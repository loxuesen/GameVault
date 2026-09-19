using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameVault;

/// <summary>
/// Everything that talks to Steam: store search, app details, artwork, and the
/// installed-library scan.
///
/// The store host is unreachable on some networks, so every read retries and the
/// appdetails API falls back to scraping the store page. Artwork comes from the CDN,
/// which stays reachable far more often than the store API does.
/// </summary>
public static class SteamApi
{
    private const string Store = "https://store.steampowered.com";
    private const string WebApi = "https://api.steampowered.com";
    private const string Cdn = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GameVault/1.0";

    private static HttpClient _client = CreateClient("");

    /// <summary>The appdetails endpoint used to decide whether the store is worth contacting.</summary>
    public const string AppDetailsProbeUrl = Store + "/api/appdetails?appids=550&l=schinese";

    /// <summary>@param appid Steam app id @return the wide header capsule URL</summary>
    public static string HeaderUrl(string appid) => $"{Cdn}/{appid}/header.jpg";

    /// <summary>@param appid Steam app id @return the portrait library capsule URL</summary>
    public static string CapsuleUrl(string appid) => $"{Cdn}/{appid}/library_600x900.jpg";

    /// <summary>@param appid Steam app id @return the wide library hero URL</summary>
    public static string HeroUrl(string appid) => $"{Cdn}/{appid}/library_hero.jpg";

    /// <summary>
    /// Rebuilds the HTTP client so a changed proxy takes effect.
    ///
    /// @param proxy proxy URL such as <c>http://127.0.0.1:7890</c>, or empty to connect directly
    /// </summary>
    public static void ApplyProxy(string proxy)
    {
        _client.Dispose();
        _client = CreateClient(proxy);
    }

    /// <summary>@return whether the address parses as an http(s) proxy URL</summary>
    public static bool IsValidProxy(string proxy) =>
        string.IsNullOrWhiteSpace(proxy) ||
        (Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"));

    private static HttpClient CreateClient(string proxy)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxy) && Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri))
        {
            handler.Proxy = new WebProxy(uri);
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    // ---------------------------------------------------------------- transport

    /// <summary>
    /// Performs one GET with a bound timeout.
    ///
    /// @param url absolute target
    /// @param timeoutMs longest time to wait for headers and body
    /// @param headers extra request headers
    /// @return the response body
    /// </summary>
    public static async Task<byte[]> GetBytesAsync(string url, int timeoutMs, IDictionary<string, string>? headers = null)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
            foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    /// <summary>Performs one GET and decodes the body as UTF-8 text.</summary>
    public static async Task<string> GetStringAsync(string url, int timeoutMs, IDictionary<string, string>? headers = null) =>
        Encoding.UTF8.GetString(await GetBytesAsync(url, timeoutMs, headers));

    /// <summary>
    /// Performs one POST with a JSON body, used by APIs that reject GET.
    ///
    /// @param url absolute target
    /// @param json request body, already serialized
    /// @param timeoutMs longest time to wait for headers and body
    /// @param headers extra request headers
    /// @return the response body as UTF-8 text
    /// </summary>
    public static async Task<string> PostJsonAsync(string url, string json, int timeoutMs, IDictionary<string, string>? headers = null)
    {
        return await PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), timeoutMs, headers);
    }

    /// <summary>
    /// Performs one POST with an application/x-www-form-urlencoded body.
    ///
    /// @param url absolute target
    /// @param form the already-encoded form body
    /// @param timeoutMs longest time to wait for headers and body
    /// @param headers extra request headers
    /// @return the response body as UTF-8 text
    /// </summary>
    public static async Task<string> PostFormAsync(string url, string form, int timeoutMs, IDictionary<string, string>? headers = null)
    {
        return await PostAsync(url, new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"), timeoutMs, headers);
    }

    private static async Task<string> PostAsync(string url, HttpContent content, int timeoutMs, IDictionary<string, string>? headers)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (headers is not null)
            foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        return Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(cts.Token));
    }

    /// <summary>
    /// Retries an operation with linear backoff, because the Steam store host fails intermittently.
    ///
    /// @param action the operation to attempt
    /// @param attempts how many times to try before giving up
    /// @return the first successful result
    /// </summary>
    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, int attempts)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception error)
            {
                last = error;
                if (attempt < attempts - 1) await Task.Delay(700 * (attempt + 1));
            }
        }
        throw last!;
    }

    // ---------------------------------------------------------------- text helpers

    /// <summary>Decodes the HTML entity subset the Steam store pages emit.</summary>
    public static string Decode(string? text) => Regex.Replace(text ?? "", @"&(#?[0-9A-Za-z]+);", match =>
    {
        var code = match.Groups[1].Value;
        return code switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => " ",
            _ when code.StartsWith("#x", StringComparison.OrdinalIgnoreCase) =>
                int.TryParse(code[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) ? char.ConvertFromUtf32(hex) : match.Value,
            _ when code.StartsWith('#') =>
                int.TryParse(code[1..], out var dec) ? char.ConvertFromUtf32(dec) : match.Value,
            _ => match.Value,
        };
    });

    /// <summary>Strips markup from store copy and collapses it into readable plain text.</summary>
    public static string StripHtml(string? html)
    {
        var text = Regex.Replace(html ?? "", @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|li|h\d)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li>", "· ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = Decode(text);
        text = Regex.Replace(text, "[ \t]+", " ");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>@return lowercase alphanumeric form of a title, used to match names to app ids</summary>
    public static string NormalizeName(string? name) =>
        Regex.Replace((name ?? "").ToLowerInvariant().Replace("™", "").Replace("®", ""), @"[^a-z0-9\u4e00-\u9fff]+", "");

    /// <summary>
    /// Chooses the blurb shown as a game's summary.
    ///
    /// Valve localizes the long description but leaves the short one in English for a
    /// number of older apps, so an English blurb is replaced by the opening of the
    /// localized long description whenever one exists.
    ///
    /// @param short the short description, possibly untranslated
    /// @param detailed the long description
    /// @return the best available summary
    /// </summary>
    public static string PreferLocalizedSummary(string shortText, string detailed)
    {
        if (shortText.Length == 0 || HasCjk(shortText) || !HasCjk(detailed)) return shortText;
        var opening = string.Join("\n\n", Regex.Split(detailed, @"\n{2,}").Where(p => p.Length > 0).Take(2));
        return opening.Length > 360 ? opening[..360] + "…" : opening;
    }

    private static bool HasCjk(string? text) => !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"[\u4e00-\u9fff]");

    // ---------------------------------------------------------------- store

    /// <summary>
    /// Searches the Steam store for a title.
    ///
    /// @param term free-text query
    /// @param timeoutMs per-attempt timeout
    /// @return matching store entries, newest query first
    /// </summary>
    public static async Task<List<SteamSearchResult>> SearchStoreAsync(string term, int timeoutMs)
    {
        var results = new List<SteamSearchResult>();
        if (string.IsNullOrWhiteSpace(term)) return results;

        try
        {
            var url = $"{Store}/search/results/?query&start=0&count=20&term={Uri.EscapeDataString(term)}&infinite=1&cc=CN&l=schinese";
            var json = await WithRetryAsync(() => GetStringAsync(url, timeoutMs), 2);
            using var document = JsonDocument.Parse(json);
            var html = document.RootElement.TryGetProperty("results_html", out var node) ? node.GetString() ?? "" : "";
            var seen = new HashSet<string>();
            foreach (Match anchor in Regex.Matches(html, "<a\\s[^>]*data-ds-appid=\"([\\d,]+)\"[^>]*>([\\s\\S]*?)</a>"))
            {
                var appId = anchor.Groups[1].Value.Split(',')[0];
                if (!seen.Add(appId)) continue;
                var title = Regex.Match(anchor.Groups[2].Value, "<span class=\"title\">([\\s\\S]*?)</span>");
                if (!title.Success) continue;
                var image = Regex.Match(anchor.Groups[2].Value, "<img\\s+src=\"([^\"]+)\"");
                results.Add(new SteamSearchResult
                {
                    AppId = appId,
                    Name = Decode(title.Groups[1].Value).Trim(),
                    Image = image.Success ? image.Groups[1].Value : "",
                });
            }
            if (results.Count > 0) return results;
        }
        catch (Exception)
        {
            // The results endpoint refused; the JSON search API below is the documented fallback.
        }

        var fallback = await WithRetryAsync(
            () => GetStringAsync($"{Store}/api/storesearch/?term={Uri.EscapeDataString(term)}&l=schinese&cc=CN", timeoutMs), 2);
        using var fallbackDocument = JsonDocument.Parse(fallback);
        if (!fallbackDocument.RootElement.TryGetProperty("items", out var items)) return results;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() != "app") continue;
            results.Add(new SteamSearchResult
            {
                AppId = item.GetProperty("id").ToString(),
                Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                Image = item.TryGetProperty("tiny_image", out var tiny) ? (tiny.GetString() ?? "").Split('?')[0] : "",
            });
        }
        return results;
    }

    /// <summary>
    /// Reads one app's store details.
    ///
    /// @param appId Steam app id
    /// @param timeoutMs per-attempt timeout
    /// @return the normalized details
    /// </summary>
    public static async Task<SteamDetails> FetchAppDetailsAsync(string appId, int timeoutMs)
    {
        try
        {
            var json = await WithRetryAsync(
                () => GetStringAsync($"{Store}/api/appdetails?appids={appId}&l=schinese&cc=CN", timeoutMs), 3);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(appId, out var entry) ||
                !entry.TryGetProperty("success", out var success) || !success.GetBoolean() ||
                !entry.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Steam 未返回该应用的信息");
            }

            var detailed = StripHtml(Text(data, "detailed_description"));
            return new SteamDetails
            {
                AppId = appId,
                Name = Text(data, "name"),
                Description = PreferLocalizedSummary(StripHtml(Text(data, "short_description")), detailed),
                Detailed = detailed,
                Developer = string.Join("、", Names(data, "developers")),
                Publisher = string.Join("、", Names(data, "publishers")),
                ReleaseDate = data.TryGetProperty("release_date", out var release) ? Text(release, "date") : "",
                Genres = Names(data, "genres"),
            };
        }
        catch (Exception apiError)
        {
            // The store page carries the same copy; scrape it rather than losing the import.
            try
            {
                return await ScrapeAppPageAsync(appId, timeoutMs);
            }
            catch (Exception)
            {
                throw apiError;
            }
        }
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static List<string> Names(JsonElement element, string property)
    {
        var names = new List<string>();
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return names;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) names.Add(item.GetString() ?? "");
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("description", out var description))
                names.Add(description.GetString() ?? "");
        }
        return names.Where(n => n.Length > 0).ToList();
    }

    /// <summary>Reads name and blurb from a store page when the appdetails API is unavailable.</summary>
    private static async Task<SteamDetails> ScrapeAppPageAsync(string appId, int timeoutMs)
    {
        var html = await WithRetryAsync(
            () => GetStringAsync($"{Store}/app/{appId}/?l=schinese&cc=CN", timeoutMs), 2);
        var name = Regex.Match(html, "<div[^>]*class=\"apphub_AppName\"[^>]*>([\\s\\S]*?)</div>");
        var blurb = Regex.Match(html, "<div[^>]*class=\"game_description_snippet\"[^>]*>([\\s\\S]*?)</div>");
        var developer = Regex.Match(html, "<div[^>]*id=\"developers_list\"[^>]*>([\\s\\S]*?)</div>");
        var date = Regex.Match(html, "<div[^>]*class=\"date\"[^>]*>([\\s\\S]*?)</div>");
        if (!name.Success && !blurb.Success) throw new InvalidOperationException("无法解析商店页面");
        return new SteamDetails
        {
            AppId = appId,
            Name = name.Success ? StripHtml(name.Groups[1].Value) : "",
            Description = blurb.Success ? StripHtml(blurb.Groups[1].Value) : "",
            Developer = developer.Success ? StripHtml(developer.Groups[1].Value) : "",
            ReleaseDate = date.Success ? StripHtml(date.Groups[1].Value) : "",
        };
    }

    /// <summary>
    /// Resolves a game title to the most likely Steam app id.
    ///
    /// @param name game title
    /// @param timeoutMs per-attempt timeout
    /// @return the best match, or null when nothing matched
    /// </summary>
    public static async Task<SteamSearchResult?> ResolveAppIdAsync(string name, int timeoutMs)
    {
        var target = NormalizeName(name);
        if (target.Length == 0) return null;
        var results = await SearchStoreAsync(name, timeoutMs);
        // An unrelated best-seller must never be adopted as this game: a wrong app id
        // means a wrong cover and a wrong description. Only a real name overlap counts.
        return results.FirstOrDefault(r => NormalizeName(r.Name) == target)
            ?? results.FirstOrDefault(r => NormalizeName(r.Name).StartsWith(target, StringComparison.Ordinal)
                                        || target.StartsWith(NormalizeName(r.Name), StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- artwork

    /// <summary>
    /// Downloads a file to disk, treating a missing image as a normal outcome.
    ///
    /// @param url remote image
    /// @param destination absolute path to write
    /// @param timeoutMs per-attempt timeout
    /// @return whether a file was written
    /// </summary>
    public static async Task<bool> DownloadFileAsync(string url, string destination, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var bytes = await GetBytesAsync(url, Math.Max(timeoutMs, 25000));
            if (bytes.Length < 512) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, bytes);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads the best available Steam artwork for an app.
    ///
    /// @param appId Steam app id
    /// @param timeoutMs per-attempt timeout
    /// @return stored relative paths, empty when nothing could be downloaded
    /// </summary>
    public static async Task<(string Cover, string Hero)> DownloadArtworkAsync(string appId, int timeoutMs)
    {
        var cover = $"covers/{appId}.jpg";
        var hero = $"covers/{appId}_hero.jpg";
        var gotCover = await DownloadFileAsync(CapsuleUrl(appId), Paths.Media(cover), timeoutMs);
        var gotHero = await DownloadFileAsync(HeroUrl(appId), Paths.Media(hero), timeoutMs);
        return (gotCover ? cover : "", gotHero ? hero : "");
    }

    // ---------------------------------------------------------------- VDF

    /// <summary>
    /// Parses Valve's KeyValues text into a nested dictionary.
    ///
    /// @param text the VDF document
    /// @return the parsed tree; nested blocks become dictionaries
    /// </summary>
    public static Dictionary<string, object> ParseVdf(string text)
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<Dictionary<string, object>>();
        stack.Push(root);
        string? pendingKey = null;

        foreach (Match token in Regex.Matches(text, "\"(?:[^\"\\\\]|\\\\.)*\"|[{}]"))
        {
            var value = token.Value;
            if (value == "{")
            {
                var child = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (pendingKey is not null) stack.Peek()[pendingKey] = child;
                stack.Push(child);
                pendingKey = null;
            }
            else if (value == "}")
            {
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
            }
            else
            {
                var unquoted = Regex.Replace(value[1..^1], @"\\(.)", "$1");
                if (pendingKey is null) pendingKey = unquoted;
                else
                {
                    stack.Peek()[pendingKey] = unquoted;
                    pendingKey = null;
                }
            }
        }
        return root;
    }

    private static string VdfString(Dictionary<string, object> block, string key) =>
        block.TryGetValue(key, out var value) && value is string text ? text : "";

    /// <summary>
    /// Finds every Steam library root on this machine, from the registry and common paths.
    ///
    /// @return absolute library root paths
    /// </summary>
    public static List<string> SteamLibraryRoots()
    {
        var found = new List<string>();
        void Consider(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) return;
            try
            {
                var parsed = ParseVdf(File.ReadAllText(libraryFile));
                if (parsed.TryGetValue("libraryfolders", out var folders) && folders is Dictionary<string, object> map)
                {
                    foreach (var entry in map.Values)
                    {
                        var path = entry is Dictionary<string, object> block ? VdfString(block, "path") : entry as string;
                        if (!string.IsNullOrWhiteSpace(path)) found.Add(path.Replace("\\\\", "\\"));
                    }
                }
                found.Add(root);
            }
            catch (IOException)
            {
                // Steam rewrites this file while running; the next scan picks it up.
            }
        }

        foreach (var root in RegistrySteamPaths()) Consider(root);
        foreach (var drive in new[] { "C", "D", "E", "F", "G", "H" })
        {
            Consider($"{drive}:\\Steam");
            Consider($"{drive}:\\SteamLibrary");
            Consider($"{drive}:\\Program Files (x86)\\Steam");
            Consider($"{drive}:\\Program Files\\Steam");
            Consider($"{drive}:\\Games\\Steam");
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>@return the Steam install paths recorded in the registry</summary>
    private static List<string> RegistrySteamPaths()
    {
        var paths = new List<string>();
        void Read(Microsoft.Win32.RegistryKey? hive, string subKey, string value)
        {
            try
            {
                using var key = hive?.OpenSubKey(subKey);
                if (key?.GetValue(value) is string path && path.Length > 0)
                    paths.Add(path.Replace('/', '\\'));
            }
            catch (Exception)
            {
                // A missing or unreadable key simply means Steam is not registered here.
            }
        }
        Read(Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        Read(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        Read(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        return paths;
    }

    /// <summary>
    /// Lists every installed Steam app with the metadata needed to import it.
    ///
    /// @return installed apps, most recently played first
    /// </summary>
    public static List<InstalledGame> ScanInstalledGames()
    {
        var games = new Dictionary<string, InstalledGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SteamLibraryRoots())
        {
            var steamApps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var parsed = ParseVdf(File.ReadAllText(manifest));
                    if (!parsed.TryGetValue("AppState", out var state) || state is not Dictionary<string, object> appState) continue;
                    var appId = VdfString(appState, "appid");
                    if (appId.Length == 0) continue;
                    var installDir = Path.Combine(steamApps, "common", VdfString(appState, "installdir"));
                    long.TryParse(VdfString(appState, "LastPlayed"), out var lastPlayed);
                    long.TryParse(VdfString(appState, "SizeOnDisk"), out var size);
                    games[appId] = new InstalledGame
                    {
                        AppId = appId,
                        Name = VdfString(appState, "name") is { Length: > 0 } name ? name : appId,
                        InstallDir = installDir,
                        LibraryRoot = root,
                        LastPlayedMs = lastPlayed * 1000,
                        SizeOnDisk = size,
                    };
                }
                catch (IOException)
                {
                    // A manifest being rewritten by Steam can be unreadable for a moment.
                }
            }
        }
        return games.Values.OrderByDescending(g => g.LastPlayedMs).ThenBy(g => g.Name, StringComparer.CurrentCulture).ToList();
    }

    private static readonly string[] ExeSkip =
    {
        "unins", "uninstall", "vcredist", "dxsetup", "dxwebsetup", "dotnetfx", "oalinst", "setup", "installer",
        "crashpad", "crashreport", "crashhandler", "crashsender", "prereqsetup", "notification_helper", "python",
        "node", "errorreporter", "cleanup", "touchup", "helper", "report", "config", "register", "activate",
        "easyanticheat", "battleye",
    };

    private static readonly HashSet<string> DirSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "_commonredist", "redist", "directx", "dotnet", "mono", "support", "tools", "editor", "thirdparty",
        "easyanticheat", "battleye", "sound", "docs", "documentation", "__installer", "installer", "cache", "logs",
        "engine", "movies", "music", "audio",
    };

    /// <summary>
    /// Picks the most plausible launcher inside an installed game folder.
    ///
    /// Walking a full install tree is unbounded, so the search is breadth-first,
    /// depth-limited and capped in the number of directories it visits.
    ///
    /// @param installDir game installation root
    /// @param hint game or folder name used to score candidates
    /// @return the best candidate path, or an empty string when none was found
    /// </summary>
    public static string FindLaunchCandidate(string installDir, string hint)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return "";
        var target = NormalizeName(hint);
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((installDir, 0));
        var candidates = new List<(string Path, double Score)>();
        var visited = 0;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var skip = new[] { "_Data", "Engine", "Redist", "DirectX", "dotnet", "Mono", "Support", "Tools", "Editor",
                           "CrashReportClient", "EasyAntiCheat", "BattlEye", "ThirdParty", "NoDVD", "__Installer",
                           "_CommonRedist", "Antivirus" };

        while (queue.Count > 0 && visited < 400 && DateTime.UtcNow < deadline)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(dir, "*.exe");
                directories = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                continue;
            }

            if (depth < 3)
            {
                foreach (var child in directories)
                {
                    var name = Path.GetFileName(child);
                    if (DirSkip.Contains(name) || skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                    queue.Enqueue((child, depth + 1));
                }
            }

            foreach (var file in files)
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                if (ExeSkip.Any(part => baseName.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;
                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    continue;
                }
                if (size < 4096) continue;

                var normalized = NormalizeName(baseName);
                double score = size / 1e9;
                if (depth == 0) score += 40;
                if (target.Length > 0 && (normalized == target || target.Contains(normalized) || normalized.Contains(target))) score += 200;
                if (baseName.EndsWith("shipping", StringComparison.OrdinalIgnoreCase)) score += 10;
                if (baseName.EndsWith("launcher", StringComparison.OrdinalIgnoreCase)) score += 5;
                candidates.Add((file, score));
            }
        }
        return candidates.OrderByDescending(c => c.Score).Select(c => c.Path).FirstOrDefault() ?? "";
    }

    // ---------------------------------------------------------------- diagnostics

    /// <summary>
    /// Checks whether one metadata source answers through the current network settings.
    ///
    /// @param name source name shown in the settings dialog
    /// @param url endpoint to probe
    /// @param headers extra request headers, such as an API key
    /// @return the probe outcome
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(string name, string url, IDictionary<string, string>? headers = null)
    {
        var started = Environment.TickCount64;
        try
        {
            await GetBytesAsync(url, 8000, headers);
            var ms = Environment.TickCount64 - started;
            return new ProbeResult { Name = name, Ok = true, Ms = ms, Message = $"正常（{ms} 毫秒）" };
        }
        catch (Exception error)
        {
            var ms = Environment.TickCount64 - started;
            var message = error is TaskCanceledException ? "请求超时" : error.Message;
            return new ProbeResult { Name = name, Ok = false, Ms = ms, Message = message };
        }
    }
}
