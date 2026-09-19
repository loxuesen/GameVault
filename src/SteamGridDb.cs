using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace GameVault;

/// <summary>
/// SteamGridDB artwork.
///
/// The Steam CDN only has art for apps Steam knows about, so a game the user imported
/// by hand has no cover source at all. SteamGridDB fills that gap and needs a free API key.
/// </summary>
public static class SteamGridDb
{
    private const string Base = "https://www.steamgriddb.com/api/v2";

    /// <summary>
    /// Calls one SteamGridDB endpoint.
    ///
    /// @param path endpoint path below the API base
    /// @param key API key; when empty the configured key is used
    /// @param timeoutMs per-attempt timeout
    /// @return the decoded body, or null when the resource does not exist
    /// </summary>
    private static async Task<JsonDocument?> CallAsync(string path, string key, int timeoutMs)
    {
        var apiKey = string.IsNullOrWhiteSpace(key) ? Library.Current.Settings.SteamGridDbKey : key;
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("还没有填写 SteamGridDB API Key，请在「设置」中填写");

        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey.Trim()}" };
        try
        {
            var json = await SteamApi.GetStringAsync(Base + path, Math.Max(timeoutMs, 20000), headers);
            return JsonDocument.Parse(json);
        }
        catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("SteamGridDB API Key 无效或没有权限");
        }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Searches SteamGridDB by title.
    ///
    /// @param term game title
    /// @param key API key, or empty to use the configured one
    /// @param timeoutMs per-attempt timeout
    /// @return matching entries as (id, name)
    /// </summary>
    public static async Task<List<(int Id, string Name)>> SearchAsync(string term, string key = "", int timeoutMs = 15000)
    {
        using var document = await CallAsync($"/search/autocomplete/{Uri.EscapeDataString(term.Trim())}", key, timeoutMs);
        var matches = new List<(int, string)>();
        if (document is null || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return matches;
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idNode) ? idNode.GetInt32() : 0;
            var name = item.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
            if (id > 0) matches.Add((id, name));
        }
        return matches;
    }

    /// <summary>
    /// Picks the best portrait cover and wide hero for a SteamGridDB game.
    ///
    /// @param gameId SteamGridDB game id
    /// @return artwork URLs, each empty when that kind has no entry
    /// </summary>
    private static async Task<(string Cover, string Hero)> ArtworkAsync(int gameId)
    {
        var cover = "";
        var hero = "";
        try
        {
            using var grids = await CallAsync($"/grids/game/{gameId}?dimensions=600x900,342x482,660x930", "", 20000);
            cover = FirstUrl(grids);
        }
        catch (Exception)
        {
            // A missing grid set is not fatal; the hero below may still exist.
        }
        try
        {
            using var heroes = await CallAsync($"/heroes/game/{gameId}?dimensions=1920x620,3840x1240", "", 20000);
            hero = FirstUrl(heroes);
        }
        catch (Exception)
        {
            // Same as above, in the other direction.
        }
        return (cover, hero);
    }

    private static string FirstUrl(JsonDocument? document) =>
        document is not null
        && document.RootElement.TryGetProperty("data", out var data)
        && data.ValueKind == JsonValueKind.Array
        && data.GetArrayLength() > 0
        && data[0].TryGetProperty("url", out var url)
            ? url.GetString() ?? ""
            : "";

    /// <summary>
    /// Resolves a title straight to artwork and stores it in the covers directory.
    ///
    /// SteamGridDB is searched by name, and its autocomplete is loose: a query for a
    /// Chinese visual novel can return unrelated best-sellers. Only an entry whose name
    /// actually overlaps the query is accepted, because a wrong match means a wrong cover.
    ///
    /// @param name game title
    /// @param prefix filename prefix, normally the game id
    /// @return stored relative paths and the name SteamGridDB matched
    /// </summary>
    public static async Task<(string Cover, string Hero, string Matched)> FetchForNameAsync(string name, string prefix)
    {
        var matches = await SearchAsync(name);
        var match = matches.FirstOrDefault(m => NamesOverlap(m.Name, name));
        if (match.Name is null) throw new InvalidOperationException($"SteamGridDB 中没有与「{name}」匹配的条目");

        var (coverUrl, heroUrl) = await ArtworkAsync(match.Id);
        if (coverUrl.Length == 0 && heroUrl.Length == 0) throw new InvalidOperationException("SteamGridDB 中没有可用的图片");

        var timeout = Library.Current.Settings.Timeout;
        var safe = new string(prefix.Where(char.IsLetterOrDigit).ToArray());
        var cover = "";
        var hero = "";
        if (coverUrl.Length > 0)
        {
            var relative = $"covers/{safe}_sgdb.jpg";
            if (await SteamApi.DownloadFileAsync(coverUrl, Paths.Media(relative), timeout)) cover = relative;
        }
        if (heroUrl.Length > 0)
        {
            var relative = $"covers/{safe}_sgdb_hero.jpg";
            if (await SteamApi.DownloadFileAsync(heroUrl, Paths.Media(relative), timeout)) hero = relative;
        }
        if (cover.Length == 0 && hero.Length == 0) throw new InvalidOperationException("SteamGridDB 中的图片下载失败");
        return (cover, hero, match.Name);
    }

    /// <summary>
    /// @return whether a SteamGridDB entry name plausibly refers to the same game
    /// </summary>
    private static bool NamesOverlap(string candidate, string query)
    {
        var left = SteamApi.NormalizeName(candidate);
        var right = SteamApi.NormalizeName(query);
        if (left.Length == 0 || right.Length == 0) return false;
        return left == right
            || left.StartsWith(right, StringComparison.Ordinal)
            || right.StartsWith(left, StringComparison.Ordinal)
            || left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal);
    }
}
