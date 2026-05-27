using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Data.Enums;                     // BaseItemKind
using MediaBrowser.Controller.Entities;        // BaseItem
using MediaBrowser.Controller.Entities.Movies; // Movie
using MediaBrowser.Controller.Library;         // ILibraryManager, InternalItemsQuery
using MediaBrowser.Controller.Providers;       // IProviderManager, IRemoteImageProvider
using MediaBrowser.Model.Entities;             // ImageType
using MediaBrowser.Model.Providers;            // RemoteImageInfo
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator
{
    public class PosterRotatorService
    {
        private readonly ILibraryManager _library;
        private readonly IProviderManager _providers;
        private readonly IServiceProvider _services;
        private readonly ILogger<PosterRotatorService> _log;

        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        });

        public PosterRotatorService(
            ILibraryManager library,
            IProviderManager providers,
            IServiceProvider services,
            ILogger<PosterRotatorService> log)
        {
            _library = library;
            _providers = providers;
            _services = services;
            _log = log;
        }

        public async Task RunAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
        {
            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            };

            var movies = (await _library.GetItemsAsync(q).ConfigureAwait(false)).OfType<Movie>().ToList();

            if (cfg.Libraries is { Count: > 0 })
            {
                _log.LogInformation("PosterRotator: library filtering by name is not applied in this build; processing all movies.");
            }

            // Build a quick map: directory -> number of movies in that directory.
            // Used to detect "mixed" folders (many movies in one folder).
            var dirCounts = movies
                .Select(m => Path.GetDirectoryName(m.Path ?? string.Empty) ?? string.Empty)
                .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var total = movies.Count;
            var done = 0;

            // Gather library roots once; nudge each only if anything in that root rotated.
            var libraryRoots = GetLibraryRootPaths();
            var rootsToNudge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var movie in movies)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var rotated = await ProcessMovieAsync(movie, cfg, ct, dirCounts).ConfigureAwait(false);
                    if (rotated)
                    {
                        var path = movie.Path ?? string.Empty;
                        var root = libraryRoots.FirstOrDefault(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(root))
                        {
                            rootsToNudge.Add(root);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "PosterRotator: error processing \"{Name}\" ({Path})", movie.Name, movie.Path);
                }

                progress?.Report(++done * 100.0 / Math.Max(1, total));
            }

            // Nudge each affected root once
            foreach (var root in rootsToNudge)
            {
                NudgeLibraryRoot(root);
            }
        }

        // returns true if we actually overwrote the current poster
        private async Task<bool> ProcessMovieAsync(Movie movie, Configuration cfg, CancellationToken ct, IDictionary<string,int> dirCounts)
        {
            var movieDir = GetMovieDir(movie);
            if (string.IsNullOrEmpty(movieDir) || !Directory.Exists(movieDir))
                return false;

            var mixedFolder = IsMixedFolder(movie, dirCounts);

            // Use a per-movie pool inside a shared ".poster_pool" when in mixed folders.
            // Keep the original "poster_pool" in one-movie-per-folder setups.
            var poolDir = mixedFolder
                ? GetPerMoviePoolDir(movieDir, movie)
                : Path.Combine(movieDir, "poster_pool");
            Directory.CreateDirectory(poolDir);

            // ---- read existing pool ------------------------------------------------
            var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pat in GetPoolPatterns(cfg))
                foreach (var f in Directory.GetFiles(poolDir, pat))
                    local.Add(f);

            var lockFile = Path.Combine(poolDir, "pool.lock");
            var poolIsLocked = File.Exists(lockFile);

            // ---- cooldown state (gates TOP-UP only, not rotation) ------------------
            var statePath = Path.Combine(poolDir, "rotation_state.json");
            var state = LoadState(statePath);
            var key = movie.Id.ToString();
            var now = DateTimeOffset.UtcNow;

            bool haveLast = state.LastRotatedUtcByItem.TryGetValue(key, out var lastEpoch);
            var elapsed = haveLast ? (now - DateTimeOffset.FromUnixTimeSeconds(lastEpoch)) : TimeSpan.MaxValue;
            var minHours = Math.Max(1, cfg.MinHoursBetweenSwitches);

            // allow top-up if: never rotated OR past cooldown OR pool is empty
            bool allowTopUp = !haveLast || elapsed.TotalHours >= minHours || local.Count == 0;

            _log.LogDebug("PosterRotator: \"{Movie}\" pool has {Count}/{Target}. Locked:{Locked}. AllowTopUp:{Allow} (elapsed {H:0.0}h, min {Min}h)",
                movie.Name, local.Count, cfg.PoolSize, poolIsLocked, allowTopUp, haveLast ? elapsed.TotalHours : -1, minHours);

            // ---- TOP-UP only if allowed by cooldown --------------------------------
            if (!poolIsLocked && local.Count < cfg.PoolSize && allowTopUp)
            {
                var need = cfg.PoolSize - local.Count;
                _log.LogDebug("PosterRotator: attempting top-up of {Need} for \"{Movie}\"", need, movie.Name);

                var added = await TryTopUpFromProvidersDIAsync(movie, poolDir, need, cfg, ct).ConfigureAwait(false);
                if (added.Count < need)
                {
                    var more = await TryTopUpFromProvidersReflectionAsync(movie, poolDir, need - added.Count, cfg, ct).ConfigureAwait(false);
                    added.AddRange(more);
                }

                foreach (var f in added) local.Add(f);

                // If user wants to lock after fill, create the lock file when target reached.
                if (!poolIsLocked && cfg.LockImagesAfterFill && local.Count >= cfg.PoolSize)
                {
                    try { File.WriteAllText(lockFile, "locked"); } catch { }
                    poolIsLocked = true;
                    _log.LogInformation("PosterRotator: locked pool for \"{Movie}\" at size {Size}.", movie.Name, local.Count);
                }
            }
            else if (!poolIsLocked && local.Count < cfg.PoolSize && !allowTopUp)
            {
                _log.LogDebug("PosterRotator: skipping top-up for \"{Movie}\" due to cooldown (elapsed {H:0.0}h < {Min}h); will still rotate.", movie.Name, elapsed.TotalHours, minHours);
            }
            else if (poolIsLocked && !cfg.LockImagesAfterFill)
            {
                // Unlock if config changed.
                try { File.Delete(lockFile); } catch { }
                poolIsLocked = false;
                _log.LogInformation("PosterRotator: unlocked pool for \"{Movie}\" (config changed).", movie.Name);
            }

            // ---- bootstrap pool from current primary (or existing per-movie poster) if still empty ----
            if (local.Count == 0)
            {
                var primaryPath = TryCopyCurrentPrimaryToPool(movie, poolDir, mixedFolder);
                if (primaryPath != null) local.Add(primaryPath);
            }

            if (local.Count == 0)
            {
                _log.LogDebug("PosterRotator: no candidates available for {Name}; nothing to rotate.", movie.Name);
                return false;
            }

            // ---- ROTATE -------------------------------------------------------------
            var files = local.ToList();
            var chosen = PickNextFor(files, movie, cfg, poolDir, state);

            // Determine destination path:
            //  - If Jellyfin returns a primary path, prefer that (unique per item).
            //  - Else: in mixed folders, write a per-movie poster filename (avoid shared poster.jpg).
            //          in single-movie folders, keep the original poster.jpg fallback.
            bool rotated = false;
            var currentPrimary = movie.GetImagePath(ImageType.Primary);

            string? destinationPath = null;
            var chosenExt = Path.GetExtension(chosen);
            if (string.IsNullOrEmpty(chosenExt)) chosenExt = ".jpg";

            if (!string.IsNullOrEmpty(currentPrimary))
            {
                destinationPath = currentPrimary;
            }
            else
            {
                if (mixedFolder)
                {
                    destinationPath = GetPreferredPerMoviePosterPath(movie, movieDir, chosenExt);
                }
                else
                {
                    destinationPath = Path.Combine(movieDir, "poster.jpg");
                }
            }

            if (!string.IsNullOrEmpty(destinationPath))
            {
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                SafeOverwrite(chosen, destinationPath);
                rotated = true;

                _log.LogInformation("PosterRotator: rotated \"{Movie}\" to {Poster} ({Dest})",
                    movie.Name, Path.GetFileName(chosen), Path.GetFileName(destinationPath));
            }

            // record last-rotated time
            state.LastRotatedUtcByItem[key] = now.ToUnixTimeSeconds();
            SaveState(statePath, state);

            return rotated;
        }

        // Build a minimal MetadataRefreshOptions using reflection (kept if you ever want to refresh metadata)
        private static object? CreateDefaultRefreshOptions(Type mroType)
        {
            try
            {
                var mro = Activator.CreateInstance(mroType);
                mroType.GetProperty("ReplaceAllImages")?.SetValue(mro, false);
                mroType.GetProperty("ImageRefreshMode")?.SetValue(mro, Enum.Parse(mroType.GetProperty("ImageRefreshMode")!.PropertyType, "FullRefresh", ignoreCase: true));
                mroType.GetProperty("MetadataRefreshMode")?.SetValue(mro, Enum.Parse(mroType.GetProperty("MetadataRefreshMode")!.PropertyType, "None", ignoreCase: true));
                return mro;
            }
            catch
            {
                return null;
            }
        }

        // --- Provider top-up via DI (instrumented + smarter) ---
        private async Task<List<string>> TryTopUpFromProvidersDIAsync(
            Movie movie, string poolDir, int needed, Configuration cfg, CancellationToken ct)
        {
            var added = new List<string>();
            try
            {
                var provList = ResolveImageProviders().ToList();

                if (provList.Count == 0)
                {
                    provList = EnumerateRemoteProvidersReflection().ToList();
                    _log.LogDebug("PosterRotator: DI returned 0 providers; reflection enumeration found {Count}: {Names}",
                        provList.Count, string.Join(", ", provList.Select(p => p.GetType().Name)));
                }
                else
                {
                    _log.LogDebug("PosterRotator: DI provider top-up target {Needed} for \"{Movie}\" (providers: {Count}: {Names})",
                        needed, movie.Name, provList.Count, string.Join(", ", provList.Select(p => p.GetType().Name)));
                }

                foreach (var provider in provList)
                {
                    if (added.Count >= needed) break;

                    try
                    {
                        bool supports = true;
                        IEnumerable<ImageType>? supportedTypes = null;

                        try { supports = provider.Supports(movie); } catch { /* ignore */ }
                        try { supportedTypes = provider.GetSupportedImages(movie); } catch { /* ignore */ }

                        if (!supports)
                        {
                            _log.LogDebug("PosterRotator: provider {Prov} does not support \"{Movie}\"", provider.GetType().Name, movie.Name);
                            continue;
                        }

                        var prefersPrimary = supportedTypes?.Contains(ImageType.Primary) == true;

                        // 1) normal call
                        var images = await provider.GetImages(movie, ct).ConfigureAwait(false);
                        var gotAny = await Harvest(images, preferPrimary: prefersPrimary).ConfigureAwait(false);

                        // 2) per-type overload via reflection (if nothing yet)
                        if (!gotAny && added.Count < needed)
                        {
                            var pType = provider.GetType();
                            var overload = pType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .FirstOrDefault(m =>
                                {
                                    if (m.Name != "GetImages") return false;
                                    var ps = m.GetParameters();
                                    return ps.Length == 3
                                        && typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType)
                                        && ps[1].ParameterType.IsEnum
                                        && ps[2].ParameterType == typeof(CancellationToken);
                                });

                            if (overload != null)
                            {
                                async Task TryType(ImageType t)
                                {
                                    if (added.Count >= needed) return;
                                    try
                                    {
                                        var task = (Task)overload.Invoke(provider, new object[] { movie, t, ct })!;
                                        await task.ConfigureAwait(false);
                                        var res = task.GetType().GetProperty("Result")?.GetValue(task) as IEnumerable<RemoteImageInfo>;
                                        await Harvest(res, preferPrimary: (t == ImageType.Primary)).ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.LogDebug(ex, "PosterRotator: {Prov}.GetImages(item, {Type}, ct) failed for \"{Movie}\"",
                                            pType.Name, t, movie.Name);
                                    }
                                }

                                await TryType(ImageType.Primary).ConfigureAwait(false);
                                if (added.Count < needed) await TryType(ImageType.Thumb).ConfigureAwait(false);
                                if (added.Count < needed) await TryType(ImageType.Backdrop).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "PosterRotator: provider {Provider} failed for \"{Movie}\"",
                            provider.GetType().Name, movie.Name);
                    }
                }

                _log.LogInformation("PosterRotator: DI/providers added {Count} image(s) for \"{Movie}\"", added.Count, movie.Name);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: DI provider top-up failed for {Name}", movie.Name);
            }

            return added;

            // order + download a batch
            async Task<bool> Harvest(IEnumerable<RemoteImageInfo>? images, bool preferPrimary)
            {
                if (images == null) return false;

                var ordered = (preferPrimary
                        ? images.OrderByDescending(i => i.Type == ImageType.Primary).ThenBy(i => i.ProviderName)
                        : images.OrderBy(i => i.ProviderName))
                    .ToList();

                var gotAny = false;

                // Primary first
                foreach (var info in ordered.Where(i => i.Type == ImageType.Primary))
                {
                    if (added.Count >= needed) break;
                    await TryDownloadRemote(info, movie, poolDir, cfg, ct, added).ConfigureAwait(false);
                    gotAny = true;
                }

                // then others
                if (added.Count < needed)
                {
                    foreach (var info in ordered.Where(i => i.Type == ImageType.Thumb || i == null || i.Type == ImageType.Backdrop))
                    {
                        if (added.Count >= needed) break;
                        await TryDownloadRemote(info, movie, poolDir, cfg, ct, added).ConfigureAwait(false);
                        gotAny = true;
                    }
                }

                return gotAny;
            }

            async Task TryDownloadRemote(RemoteImageInfo info,
                                        Movie mv, string dir, Configuration c,
                                        CancellationToken token, List<string> bucket)
            {
                if (info == null) return;
                var url = info.Url;
                if (string.IsNullOrWhiteSpace(url)) return;

                var ext = GuessExtFromUrl(url) ?? ".jpg";
                var name = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
                var full = Path.Combine(dir, name);

                try
                {
                    using var resp = await _http.GetAsync(url, token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    await using var s = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var f = File.Create(full);
                    await s.CopyToAsync(f, token).ConfigureAwait(false);

                    bucket.Add(full);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PosterRotator: download failed for {Url} ({Movie})", url, mv.Name);
                }
            }
        }

        private IEnumerable<IRemoteImageProvider> ResolveImageProviders()
        {
            try
            {
                return (_services.GetService(typeof(IEnumerable<IRemoteImageProvider>))
                        as IEnumerable<IRemoteImageProvider>)
                       ?? Array.Empty<IRemoteImageProvider>();
            }
            catch
            {
                return Array.Empty<IRemoteImageProvider>();
            }
        }

        private IEnumerable<IRemoteImageProvider> EnumerateRemoteProvidersReflection()
        {
            var found = new List<IRemoteImageProvider>();
            try
            {
                var pm = _providers;
                var t = pm.GetType();

                // 1) generic GetProviders<T>()
                var generic = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                               .FirstOrDefault(m =>
                                    m.IsGenericMethodDefinition &&
                                    m.GetGenericArguments().Length == 1 &&
                                    (m.Name.Contains("GetProviders", StringComparison.OrdinalIgnoreCase) ||
                                     m.Name.Contains("GetAllProviders", StringComparison.OrdinalIgnoreCase)) &&
                                    m.GetParameters().Length == 0);
                if (generic != null)
                {
                    try
                    {
                        var closed = generic.MakeGenericMethod(typeof(IRemoteImageProvider));
                        var res = closed.Invoke(pm, null) as System.Collections.IEnumerable;
                        if (res != null)
                        {
                            foreach (var p in res)
                                if (p is IRemoteImageProvider rp)
                                    found.Add(rp);
                        }
                    }
                    catch { /* ignore */ }
                }

                // 2) properties/fields holding IEnumerable<IRemoteImageProvider>
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { AddIfEnumerableOfRemote(p.GetValue(pm), found); } catch { }
                }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { AddIfEnumerableOfRemote(f.GetValue(pm), found); } catch { }
                }

                return found
                    .GroupBy(x => x.GetType().FullName)
                    .Select(g => g.First())
                    .ToList();
            }
            catch
            {
                // swallow
            }
            return Array.Empty<IRemoteImageProvider>();

            static void AddIfEnumerableOfRemote(object? obj, List<IRemoteImageProvider> bucket)
            {
                if (obj is System.Collections.IEnumerable e)
                {
                    foreach (var item in e)
                    {
                        if (item is IRemoteImageProvider rp)
                            bucket.Add(rp);
                    }
                }
            }
        }

        // --- Provider top-up via REFLECTION (robust + logging) ---
        private async Task<List<string>> TryTopUpFromProvidersReflectionAsync(
            Movie movie, string poolDir, int needed, Configuration cfg, CancellationToken ct)
        {
            var added = new List<string>();
            try
            {
                var pm = _providers;
                var pmType = pm.GetType();

                // PATH A: ProviderManager.GetRemoteImages(item, query, ct)
                MethodInfo? getRemoteImages = pmType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "GetRemoteImages") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 3 && typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType);
                    });

                if (getRemoteImages != null)
                {
                    _log.LogDebug("PosterRotator: using GetRemoteImages on ProviderManager for \"{Movie}\"", movie.Name);

                    var queryType = getRemoteImages.GetParameters()[1].ParameterType;

                    async Task<int> HarvestWithQuery(object imageTypeEnumValue)
                    {
                        var query = Activator.CreateInstance(queryType)!;
                        queryType.GetProperty("IncludeAllLanguages")?.SetValue(query, true);
                        queryType.GetProperty("ImageType")?.SetValue(query, imageTypeEnumValue);

                        var t = (Task)getRemoteImages.Invoke(pm, new object[] { movie, query, ct })!;
                        await t.ConfigureAwait(false);

                        var result = t.GetType().GetProperty("Result")?.GetValue(t) as System.Collections.IEnumerable;
                        return await DownloadFromEnumerable(result, movie, poolDir, needed - added.Count, cfg, ct, added).ConfigureAwait(false);
                    }

                    await HarvestWithQuery(ImageType.Primary).ConfigureAwait(false);
                    if (added.Count < needed) await HarvestWithQuery(ImageType.Thumb).ConfigureAwait(false);
                    if (added.Count < needed) await HarvestWithQuery(ImageType.Backdrop).ConfigureAwait(false);

                    _log.LogInformation("PosterRotator: added {Count} images via GetRemoteImages for \"{Movie}\"", added.Count, movie.Name);
                    return added;
                }

                // PATH B: enumerate remote providers → provider.GetImages(...)
                MethodInfo? getRemoteImageProviders = pmType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "GetRemoteImageProviders" && m.Name != "GetImageProviders") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 1 && (typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType) || ps[0].ParameterType.Name.Contains("IHasImages"));
                    });

                if (getRemoteImageProviders == null)
                {
                    _log.LogDebug("PosterRotator: no way to enumerate remote image providers on this server; skipping top-up for \"{Movie}\".", movie.Name);
                    return added;
                }

                var providersObj = getRemoteImageProviders.Invoke(pm, new object[] { movie });
                if (providersObj is not System.Collections.IEnumerable providers)
                {
                    _log.LogDebug("PosterRotator: provider enumeration returned null/invalid; skipping top-up for \"{Movie}\".", movie.Name);
                    return added;
                }

                _log.LogDebug("PosterRotator: using provider.GetImages reflection for \"{Movie}\"", movie.Name);

                async Task HarvestProviderAsync(object provider, object imageTypeEnumValue)
                {
                    if (added.Count >= needed) return;

                    var pType = provider.GetType();
                    var getImagesCandidates = pType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "GetImages" && typeof(Task).IsAssignableFrom(m.ReturnType))
                        .ToList();

                    foreach (var m in getImagesCandidates)
                    {
                        if (added.Count >= needed) break;

                        var ps = m.GetParameters();
                        object? taskObj = null;

                        try
                        {
                            if (ps.Length == 3 &&
                                typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType) &&
                                ps[1].ParameterType.IsEnum &&
                                ps[2].ParameterType == typeof(CancellationToken))
                            {
                                taskObj = m.Invoke(provider, new object[] { movie, imageTypeEnumValue, ct });
                            }
                            else if (ps.Length == 2 &&
                                     typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType) &&
                                     ps[1].ParameterType == typeof(CancellationToken))
                            {
                                taskObj = m.Invoke(provider, new object[] { movie, ct });
                            }
                            else if (ps.Length == 3 &&
                                     typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType) &&
                                     ps[2].ParameterType == typeof(CancellationToken))
                            {
                                var queryObj = Activator.CreateInstance(ps[1].ParameterType)!;
                                ps[1].ParameterType.GetProperty("IncludeAllLanguages")?.SetValue(queryObj, true);
                                var imgProp = ps[1].ParameterType.GetProperty("ImageType");
                                if (imgProp != null) imgProp.SetValue(queryObj, imageTypeEnumValue);
                                taskObj = m.Invoke(provider, new object[] { movie, queryObj, ct });
                            }
                            else
                            {
                                continue;
                            }

                            if (taskObj is Task t)
                            {
                                await t.ConfigureAwait(false);
                                var result = t.GetType().GetProperty("Result")?.GetValue(t) as System.Collections.IEnumerable;
                                var harvested = await DownloadFromEnumerable(result, movie, poolDir, needed - added.Count, cfg, ct, added).ConfigureAwait(false);
                                if (harvested > 0) break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogDebug(ex, "PosterRotator: provider {Provider} GetImages failed for \"{Movie}\"", pType.Name, movie.Name);
                        }
                    }
                }

                foreach (var p in providers)
                {
                    await HarvestProviderAsync(p, ImageType.Primary).ConfigureAwait(false);
                    if (added.Count >= needed) break;
                }
                if (added.Count < needed)
                {
                    foreach (var p in providers)
                    {
                        await HarvestProviderAsync(p, ImageType.Thumb).ConfigureAwait(false);
                        if (added.Count >= needed) break;
                    }
                }
                if (added.Count < needed)
                {
                    foreach (var p in providers)
                    {
                        await HarvestProviderAsync(p, ImageType.Backdrop).ConfigureAwait(false);
                        if (added.Count >= needed) break;
                    }
                }

                _log.LogInformation("PosterRotator: added {Count} images via providers for \"{Movie}\"", added.Count, movie.Name);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: reflection-based provider top-up failed for {Name}", movie.Name);
            }

            return added;

            async Task<int> DownloadFromEnumerable(
                System.Collections.IEnumerable? result,
                Movie movie2,
                string poolDir2,
                int toTake,
                Configuration cfg2,
                CancellationToken ct2,
                List<string> added2)
            {
                if (result == null || toTake <= 0) return 0;
                var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var count = 0;

                foreach (var info in result.Cast<object>())
                {
                    if (count >= toTake) break;

                    var t = info.GetType();
                    var url = t.GetProperty("Url")?.GetValue(info) as string;
                    var mime = t.GetProperty("MimeType")?.GetValue(info) as string;

                    if (string.IsNullOrWhiteSpace(url) || !urls.Add(url)) continue;

                    var ext = GuessExt(mime) ?? GuessExtFromUrl(url) ?? ".jpg";
                    var name = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
                    var full = Path.Combine(poolDir2, name);

                    try
                    {
                        using var resp = await _http.GetAsync(url, ct2).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        await using var s = await resp.Content.ReadAsStreamAsync(ct2).ConfigureAwait(false);
                        await using var f = File.Create(full);
                        await s.CopyToAsync(f, ct2).ConfigureAwait(false);

                        added2.Add(full);
                        count++;
                    }
                    catch
                    {
                        // continue
                    }
                }
                return count;
            }
        }

        // ---- helpers --------------------------------------------------------

        private static string ResolveMovieDirectory(Movie movie)
        {
            try
            {
                if (string.IsNullOrEmpty(movie.Path))
                    return string.Empty;

                return Directory.Exists(movie.Path)
                    ? movie.Path
                    : (Path.GetDirectoryName(movie.Path) ?? string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        // New helpers for mixed-folder handling

        private static string GetMovieDir(Movie movie) => ResolveMovieDirectory(movie);

        private static bool IsMixedFolder(Movie movie, IDictionary<string,int> dirCounts)
        {
            var dir = Path.GetDirectoryName(movie.Path ?? string.Empty) ?? string.Empty;
            return !string.IsNullOrEmpty(dir)
                && dirCounts.TryGetValue(dir, out var n)
                && n > 1;
        }

        private static string GetPerMoviePoolDir(string movieDir, Movie movie)
        {
            var root = Path.Combine(movieDir, ".poster_pool");
            Directory.CreateDirectory(root);
            return Path.Combine(root, movie.Id.ToString("N"));
        }

        private static string GetPreferredPerMoviePosterPath(Movie movie, string movieDir, string preferredExt)
        {
            var src = movie.Path ?? "poster";
            var baseName = Path.GetFileNameWithoutExtension(src) ?? "poster";
            var ext = string.IsNullOrWhiteSpace(preferredExt) ? ".jpg" : preferredExt.ToLowerInvariant();

            // Prefer existing per-movie poster if present (any ext), else choose a good conventional name.
            foreach (var stem in new[] { $"{baseName}-poster", baseName })
            {
                var existing = Directory.GetFiles(movieDir, stem + ".*")
                    .FirstOrDefault(f =>
                        f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(existing))
                    return existing;
            }

            return Path.Combine(movieDir, $"{baseName}-poster{ext}");
        }

        private static IEnumerable<string> GetPoolPatterns(Configuration cfg)
        {
            var patterns = new List<string>();
            if (cfg.ExtraPosterPatterns != null)
                patterns.AddRange(cfg.ExtraPosterPatterns);

            patterns.AddRange(new[]
            {
                "*.jpg","*.jpeg","*.png","*.webp","*.gif",
                "poster*.jpg","poster*.jpeg","poster*.png","poster*.webp","poster*.gif",
                "*-poster*.jpg","*-poster*.jpeg","*-poster*.png","*-poster*.webp","*-poster*.gif" 
            });

            return patterns.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string? TryCopyCurrentPrimaryToPool(Movie movie, string poolDir, bool mixedFolder = false)
        {
            try
            {
                var primary = movie.GetImagePath(ImageType.Primary);
                if (!string.IsNullOrEmpty(primary) && File.Exists(primary))
                {
                    var name = "pool_currentprimary" + Path.GetExtension(primary);
                    var dest = Path.Combine(poolDir, name);
                    File.Copy(primary, dest, overwrite: true);
                    return dest;
                }

                // In mixed folders, also look for an existing per-movie poster beside the file.
                if (mixedFolder && !string.IsNullOrEmpty(movie.Path))
                {
                    var dir = Path.GetDirectoryName(movie.Path)!;
                    var baseName = Path.GetFileNameWithoutExtension(movie.Path)!;

                    var candidates = Directory.GetFiles(dir, $"{baseName}-poster.*")
                        .Concat(Directory.GetFiles(dir, $"{baseName}.*"))
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var existing = candidates.FirstOrDefault();
                    if (!string.IsNullOrEmpty(existing))
                    {
                        var name = "pool_currentprimary" + Path.GetExtension(existing);
                        var dest = Path.Combine(poolDir, name);
                        File.Copy(existing, dest, overwrite: true);
                        return dest;
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        // Skip pool_currentprimary.* when alternatives exist; on first rotation start at a non-current image
        private static string PickNextFor(
            List<string> files,
            Movie movie,
            Configuration cfg,
            string poolDir,
            RotationState state)
        {
            // deterministic order: push pool_currentprimary.* to the end, then sort by filename
            var reordered = files
                .OrderBy(f => Path.GetFileName(f).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var key = movie.Id.ToString();
            int idx;

            if (cfg.SequentialRotation)
            {
                // First time: if we have >1 image, skip the snapshot
                if (!state.LastIndexByItem.ContainsKey(key) && reordered.Count > 1)
                {
                    idx = 1;                        // use first non-snapshot
                    state.LastIndexByItem[key] = 2; // next time continue with index 2
                }
                else
                {
                    var last = state.LastIndexByItem.TryGetValue(key, out var v) ? v : 0;
                    idx = last % reordered.Count;
                    state.LastIndexByItem[key] = last + 1; // advance for next run
                }
            }
            else
            {
                // Random: prefer non-snapshot when possible
                if (reordered.Count > 1)
                {
                    var nonCurrent = reordered.Where(f =>
                        !Path.GetFileName(f).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (nonCurrent.Count > 0)
                    {
                        var pick = nonCurrent[Random.Shared.Next(nonCurrent.Count)];
                        idx = reordered.IndexOf(pick);
                    }
                    else
                    {
                        idx = Random.Shared.Next(reordered.Count);
                    }
                }
                else
                {
                    idx = 0;
                }

                // (We don’t touch LastIndexByItem for random mode.)
            }

            return reordered[idx];
        }

        private static void SafeOverwrite(string src, string dst)
        {
            try
            {
                if (File.Exists(dst))
                {
                    var attrs = File.GetAttributes(dst);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(dst, attrs & ~FileAttributes.ReadOnly);
                }
                File.Copy(src, dst, overwrite: true);
                try { File.SetLastWriteTimeUtc(dst, DateTime.UtcNow); } catch { }
            }
            catch
            {
                // Swallow; caller logs the intent already.
            }
        }

        private sealed class RotationState
        {
            public Dictionary<string, int> LastIndexByItem { get; set; } = new();
            public Dictionary<string, long> LastRotatedUtcByItem { get; set; } = new();
        }

        private static RotationState LoadState(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return System.Text.Json.JsonSerializer.Deserialize<RotationState>(json) ?? new RotationState();
                }
            }
            catch { /* ignore */ }

            return new RotationState();
        }

        private static void SaveState(string path, RotationState state)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(state);
                File.WriteAllText(path, json);
            }
            catch { /* ignore */ }
        }

        private static string? GuessExt(string? mime) =>
            mime switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/jpeg" => ".jpg",
                "image/gif"  => ".gif",  
                _ => null
            };

        private static string? GuessExtFromUrl(string url)
        {
            try
            {
                var ext = Path.GetExtension(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) return null;
                if (ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
                return ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
            }
            catch { return null; }
        }

        // ---- library root helpers (nudge once per root) ----------------------

        private List<string> GetLibraryRootPaths()
        {
            try
            {
                var roots = new List<string>();

                // Reflect GetVirtualFolders() to read Locations/Paths
                var lmType = _library.GetType();
                var getVf = lmType.GetMethod("GetVirtualFolders");
                if (getVf != null)
                {
                    var vfResult = getVf.Invoke(_library, null) as System.Collections.IEnumerable;
                    if (vfResult != null)
                    {
                        foreach (var vf in vfResult)
                        {
                            var locProp = vf.GetType().GetProperty("Locations") ?? vf.GetType().GetProperty("Paths");
                            var locVal = locProp?.GetValue(vf) as System.Collections.IEnumerable;
                            if (locVal != null)
                            {
                                foreach (var p in locVal)
                                {
                                    if (p is string s && !string.IsNullOrWhiteSpace(s))
                                        roots.Add(s);
                                }
                            }
                        }
                    }
                }

                return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private void NudgeLibraryRoot(string rootPath)
        {
            // Best-effort: touch a persistent file in the root so file watchers notice one change per run.
            try
            {
                _log.LogDebug("PosterRotator: nudging library root {Root}", rootPath);

                var touch = Path.Combine(rootPath, ".posterrotator.touch");
                if (!File.Exists(touch))
                {
                    File.WriteAllText(touch, "posterrotator");
                }
                else
                {
                    File.SetLastWriteTimeUtc(touch, DateTime.UtcNow);
                }
            }
            catch
            {
                // ignore; this is just a cache-bust hint
            }
        }
    }
}
