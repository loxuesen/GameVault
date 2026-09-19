using System.Text.Json;

namespace GameVault;

/// <summary>Metadata resolved from a source other than the Steam store.</summary>
public sealed class ExternalMetadata
{
    /// <summary>Name of the source that produced this record, for the user-facing message.</summary>
    public string Source { get; set; } = "";

    /// <summary>A description, preferably in Chinese.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// A second description in another language, kept when the primary one came from a
    /// source that only describes the series rather than this exact title.
    /// </summary>
    public string Detailed { get; set; } = "";

    public string Developer { get; set; } = "";
    public string ReleaseDate { get; set; } = "";

    /// <summary>A Chinese title for this release, empty when the source knows none.</summary>
    public string ChineseTitle { get; set; } = "";

    /// <summary>A remote cover image URL, empty when the source has none.</summary>
    public string CoverUrl { get; set; } = "";
}

/// <summary>
/// VNDB, the visual-novel database.
///
/// It is the only reachable source that both matches visual novels by their Chinese
/// fan titles and supplies cover art, so it covers exactly the games the Steam store
/// does not have. Descriptions are English; <see cref="Moegirl"/> supplies Chinese ones.
/// </summary>
public static class Vndb
{
    private const string Endpoint = "https://api.vndb.org/kana/vn";
    private const string Fields = "id,title,alttitle,titles.title,titles.lang,description,image.url,released,developers.name";

    /// <summary>Results are reused because the artwork and metadata passes ask for the same game.</summary>
    private static readonly Dictionary<string, ExternalMetadata?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    /// <summary>
    /// Searches VNDB and picks the entry whose titles best match the query.
    ///
    /// @param term game title as the user typed it, in any language
    /// @param timeoutMs per-attempt timeout
    /// @return the best match, or null when nothing matched
    /// </summary>
    public static async Task<ExternalMetadata?> SearchAsync(string term, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        var key = term.Trim();

        await CacheLock.WaitAsync();
        try
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        finally
        {
            CacheLock.Release();
        }

        var found = await QueryAsync(key, timeoutMs);

        await CacheLock.WaitAsync();
        try
        {
            Cache[key] = found;
        }
        finally
        {
            CacheLock.Release();
        }
        return found;
    }

    private static async Task<ExternalMetadata?> QueryAsync(string term, int timeoutMs)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["filters"] = new object[] { "search", "=", term },
            ["fields"] = Fields,
            ["results"] = 10,
        });

        using var document = JsonDocument.Parse(await SteamApi.PostJsonAsync(Endpoint, body, timeoutMs));
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        var vn = PickBest(results, term);
        if (vn is null) return null;
        var entry = vn.Value;

        var description = CleanDescription(Text(entry, "description"));
        return new ExternalMetadata
        {
            Source = "VNDB",
            Description = description,
            Developer = string.Join("、", Developers(entry)),
            ReleaseDate = Text(entry, "released"),
            ChineseTitle = ChineseTitleOf(entry),
            CoverUrl = entry.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
                ? Text(image, "url")
                : "",
        };
    }

    /// <summary>
    /// @return the Simplified Chinese title VNDB records for this release, or an empty string
    /// </summary>
    private static string ChineseTitleOf(JsonElement entry)
    {
        if (!entry.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) return "";
        foreach (var title in titles.EnumerateArray())
        {
            var language = Text(title, "lang");
            if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var value = Text(title, "title");
                if (value.Length > 0) return value;
            }
        }
        return "";
    }

    /// <summary>
    /// Chooses the entry whose known titles best match the query.
    ///
    /// A visual novel carries several titles (romaji, Japanese, Chinese, English), and the
    /// user may have typed any of them, so every title is scored rather than just the main one.
    /// </summary>
    private static JsonElement? PickBest(JsonElement results, string query)
    {
        var target = SteamApi.NormalizeName(query);
        JsonElement? best = null;
        var bestScore = -1;

        foreach (var entry in results.EnumerateArray())
        {
            var score = 0;
            foreach (var candidate in Titles(entry))
            {
                var normalized = SteamApi.NormalizeName(candidate);
                if (normalized.Length == 0) continue;
                if (normalized == target) score = Math.Max(score, 100);
                else if (normalized.StartsWith(target, StringComparison.Ordinal) || target.StartsWith(normalized, StringComparison.Ordinal))
                    score = Math.Max(score, 60);
                else if (normalized.Contains(target, StringComparison.Ordinal) || target.Contains(normalized, StringComparison.Ordinal))
                    score = Math.Max(score, 30);
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }
        return best;
    }

    /// <summary>@return every title VNDB knows for an entry, in any language or script</summary>
    private static IEnumerable<string> Titles(JsonElement entry)
    {
        var main = Text(entry, "title");
        if (main.Length > 0) yield return main;
        var alternative = Text(entry, "alttitle");
        if (alternative.Length > 0) yield return alternative;
        if (!entry.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) yield break;
        foreach (var title in titles.EnumerateArray())
        {
            var value = Text(title, "title");
            if (value.Length > 0) yield return value;
        }
    }

    private static IEnumerable<string> Developers(JsonElement entry)
    {
        if (!entry.TryGetProperty("developers", out var developers) || developers.ValueKind != JsonValueKind.Array) yield break;
        foreach (var developer in developers.EnumerateArray())
        {
            var name = Text(developer, "name");
            if (name.Length > 0) yield return name;
        }
    }

    /// <summary>
    /// Strips VNDB's BBCode markup and collapses the result into readable plain text.
    ///
    /// @param description the raw description field
    /// @return plain text, or an empty string when the entry has none
    /// </summary>
    private static string CleanDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Trim() == "-") return "";
        var text = Regex.Replace(description, @"\[/?[^\]]*\]", "");
        text = SteamApi.Decode(text);
        // Stripping the markup above leaves bare link labels such as "official website" behind.
        text = Regex.Replace(text, @"^[ \t]*(official (website|site)|web ?site)[ \t]*$", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

/// <summary>
/// 萌娘百科 (Moegirlpedia), used for Chinese game descriptions.
///
/// Steam's Chinese blurbs do not exist for games Steam has never heard of, and VNDB only
/// writes English, so this is what makes a non-Steam game readable in Chinese.
/// </summary>
public static class Moegirl
{
    private const string Api = "https://zh.moegirl.org.cn/api.php";

    /// <summary>
    /// Reads the opening section of an article.
    ///
    /// @param title article title, or a term to search for
    /// @param timeoutMs per-attempt timeout
    /// @return the plain-text intro, or an empty string when there is no article
    /// </summary>
    public static async Task<string> IntroAsync(string title, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        try
        {
            var extract = await ExtractAsync(title, timeoutMs);
            if (extract.Length > 0) return extract;

            // The article may live under a longer title, so fall back to a full-text search.
            var found = await SearchAsync(title, timeoutMs);
            if (found.Length > 0 && !string.Equals(found, title, StringComparison.Ordinal))
                return await ExtractAsync(found, timeoutMs);
        }
        catch (Exception)
        {
            // Moegirl is a bonus source; a failure here must not fail the whole lookup.
        }
        return "";
    }

    private static async Task<string> ExtractAsync(string title, int timeoutMs)
    {
        var url = $"{Api}?action=query&format=json&prop=extracts&exintro=1&explaintext=1&redirects=1" +
                  $"&titles={Uri.EscapeDataString(title)}";
        using var document = JsonDocument.Parse(await SteamApi.GetStringAsync(url, timeoutMs));
        if (!document.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Object)
            return "";

        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("extract", out var extract) && extract.ValueKind == JsonValueKind.String)
                return Clean(extract.GetString() ?? "");
        }
        return "";
    }

    private static async Task<string> SearchAsync(string term, int timeoutMs)
    {
        var url = $"{Api}?action=query&format=json&list=search&srlimit=1&srsearch={Uri.EscapeDataString(term)}";
        using var document = JsonDocument.Parse(await SteamApi.GetStringAsync(url, timeoutMs));
        if (!document.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("search", out var search) ||
            search.ValueKind != JsonValueKind.Array ||
            search.GetArrayLength() == 0)
            return "";
        return search[0].TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
    }

    private static string Clean(string text)
    {
        var cleaned = Regex.Replace(text, @"[ \t]+", " ").Trim();
        return cleaned.Length > 600 ? cleaned[..600] + "…" : cleaned;
    }
}

/// <summary>
/// Produces a Chinese description for a game, whatever it takes.
///
/// Chinese is preferred from a real source. When none exists the text is machine translated,
/// so a game never ends up described only in a language the user cannot read.
/// </summary>
public static class MetadataLocalizer
{
    /// <summary>
    /// Looks for a Chinese article under any of the titles a game is known by.
    ///
    /// A visual novel usually has several names, and the Chinese wiki may only carry one of
    /// them, so every candidate is tried before giving up.
    ///
    /// @param titles candidate titles, most likely first
    /// @param timeoutMs per-attempt timeout
    /// @return the Chinese intro, or an empty string
    /// </summary>
    public static async Task<string> FindChineseAsync(IEnumerable<string> titles, int timeoutMs)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in titles)
        {
            var candidate = (title ?? "").Trim();
            if (candidate.Length == 0 || !tried.Add(candidate)) continue;
            var text = await Moegirl.IntroAsync(candidate, timeoutMs);
            if (Translator.LooksChinese(text)) return text;
        }
        return "";
    }

    /// <summary>
    /// Makes a game's description Chinese, keeping the original text in the detail field.
    ///
    /// @param game the game to localize; its description is replaced in place
    /// @param timeoutMs per-attempt timeout
    /// @param extraTitles other names the game is known by, such as a VNDB Chinese title
    /// @return whether the description is now Chinese
    /// </summary>
    public static async Task<bool> EnsureChineseAsync(Game game, int timeoutMs, params string[] extraTitles)
    {
        if (Translator.LooksChinese(game.Description)) return true;

        // Some Steam apps ship a Chinese long description but an English short one.
        if (Translator.LooksChinese(game.Detailed))
        {
            game.Description = Summarize(game.Detailed);
            Library.Current.Touch(game);
            return true;
        }

        var candidates = new List<string> { game.Name };
        candidates.AddRange(extraTitles.Where(t => !string.IsNullOrWhiteSpace(t)));

        var chinese = await FindChineseAsync(candidates, timeoutMs);
        if (chinese.Length > 0)
        {
            if (game.Detailed.Length == 0) game.Detailed = game.Description;
            game.Description = chinese;
            Library.Current.Touch(game);
            return true;
        }

        // No Chinese text exists for this game, so translate what we have.
        var source = game.Description.Length > 0 ? game.Description : game.Detailed;
        if (source.Length == 0) return false;
        var translated = await Translator.ToChineseAsync(source, timeoutMs);
        if (translated.Length == 0) return false;

        if (game.Detailed.Length == 0 || game.Detailed == source) game.Detailed = source;
        game.Description = translated;
        Library.Current.Touch(game);
        return true;
    }

    /// <summary>@return the opening of a long description, used as the card summary</summary>
    private static string Summarize(string detailed)
    {
        var opening = string.Join("\n\n", Regex.Split(detailed, @"\n{2,}").Where(p => p.Length > 0).Take(2));
        return opening.Length > 360 ? opening[..360] + "…" : opening;
    }
}

/// <summary>
/// Looks up a game the Steam store does not cover, by combining the sources that are
/// actually reachable: VNDB for structure and cover art, Moegirl for Chinese prose,
/// and machine translation when neither has a Chinese description.
/// </summary>
public static class ExternalMetadataSources
{
    /// <summary>
    /// Resolves a non-Steam game, guaranteeing a Chinese description when one can be produced.
    ///
    /// @param name the game title as stored in the library
    /// @param timeoutMs per-attempt timeout
    /// @return the combined metadata, or null when no source matched
    /// </summary>
    public static async Task<ExternalMetadata?> LookupAsync(string name, int timeoutMs)
    {
        var vndb = await SafeVndbAsync(name, timeoutMs);

        // The game's own name first, then the Chinese title VNDB records for the same release.
        var chinese = await MetadataLocalizer.FindChineseAsync(
            new[] { name, vndb?.ChineseTitle ?? "" }, timeoutMs);

        if (vndb is null && chinese.Length == 0) return null;

        var result = vndb ?? new ExternalMetadata { Source = "萌娘百科" };
        var english = vndb?.Description ?? "";

        if (chinese.Length > 0)
        {
            result.Source = vndb is null ? "萌娘百科" : "VNDB + 萌娘百科";
            if (english.Length > 0 && english != chinese) result.Detailed = english;
            result.Description = chinese;
            return result;
        }

        // Nothing Chinese exists, so the English text is translated.
        if (english.Length > 0)
        {
            var translated = await Translator.ToChineseAsync(english, timeoutMs);
            if (translated.Length > 0)
            {
                result.Detailed = english;
                result.Description = translated;
                result.Source = "VNDB（机器翻译）";
            }
        }
        return result;
    }

    /// <summary>@return the VNDB match, or null when VNDB is unreachable or has no entry</summary>
    private static async Task<ExternalMetadata?> SafeVndbAsync(string name, int timeoutMs)
    {
        try
        {
            return await Vndb.SearchAsync(name, timeoutMs);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
