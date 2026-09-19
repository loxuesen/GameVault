using System.Text.Json;

namespace GameVault;

/// <summary>
/// Machine translation into Chinese, used only when no Chinese description exists anywhere.
///
/// Google Translate and DeepL are unreachable from mainland networks, so the chain is built
/// from services that are: 彩云小译 first (best quality and no practical rate limit), then
/// 有道, then MyMemory. All three work without an account; the 彩云 token is configurable
/// in settings for anyone who has their own.
/// </summary>
public static class Translator
{
    private const string CaiyunEndpoint = "https://api.interpreter.caiyunai.com/v1/translator";
    private const string YoudaoEndpoint = "https://aidemo.youdao.com/trans";
    private const string MyMemoryEndpoint = "https://api.mymemory.translated.net/get";

    /// <summary>
    /// Public token published for the 彩云小译 translator demo. It is a shared free quota,
    /// which is why the user's own token can be configured instead.
    /// </summary>
    private const string CaiyunSharedToken = "3975l6lr5pcbvidl6jl2";

    /// <summary>MyMemory rejects anonymous queries longer than 500 characters.</summary>
    private const int MyMemoryChunkSize = 440;

    /// <summary>Translations are reused because a game is examined more than once per run.</summary>
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    /// <summary>
    /// Decides whether a text is Chinese prose.
    ///
    /// Han characters alone are ambiguous: Chinese store copy routinely embeds Japanese
    /// titles in katakana, so katakana is deliberately not treated as a Japanese signal.
    /// Hiragana and Hangul are, because Chinese prose never contains them in quantity.
    ///
    /// @param text the text to classify
    /// @return whether it reads as Chinese
    /// </summary>
    public static bool LooksChinese(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int han = 0, hiragana = 0, hangul = 0;
        foreach (var character in text)
        {
            if (character >= '\u3040' && character <= '\u309F') hiragana++;
            else if (character >= '\uAC00' && character <= '\uD7AF') hangul++;
            else if (character >= '\u4E00' && character <= '\u9FFF') han++;
        }
        if (hiragana > 2 || hangul > 2) return false;
        return han >= 4;
    }

    /// <summary>
    /// Translates a text into Chinese, trying each service until one answers.
    ///
    /// @param text the source text, in any language
    /// @param timeoutMs per-attempt timeout
    /// @return the Chinese text, or an empty string when every service failed
    /// </summary>
    public static async Task<string> ToChineseAsync(string text, int timeoutMs)
    {
        var source = (text ?? "").Trim();
        if (source.Length == 0) return "";
        if (LooksChinese(source)) return source;

        await CacheLock.WaitAsync();
        try
        {
            if (Cache.TryGetValue(source, out var cached)) return cached;
        }
        finally
        {
            CacheLock.Release();
        }

        var translated = "";
        foreach (var service in new Func<Task<string>>[]
        {
            () => ViaCaiyunAsync(source, timeoutMs),
            () => ViaYoudaoAsync(source, timeoutMs),
            () => ViaMyMemoryAsync(source, timeoutMs),
        })
        {
            try
            {
                translated = await service();
            }
            catch (Exception)
            {
                // This service is down or rate limited; the next one is tried instead.
                continue;
            }
            if (translated.Length > 0) break;
        }

        if (translated.Length == 0) return "";

        await CacheLock.WaitAsync();
        try
        {
            Cache[source] = translated;
        }
        finally
        {
            CacheLock.Release();
        }
        return translated;
    }

    /// <summary>@return the translation from 彩云小译, or an empty string</summary>
    private static async Task<string> ViaCaiyunAsync(string text, int timeoutMs)
    {
        var token = Library.Current.Settings.TranslateToken.Trim();
        if (token.Length == 0) token = CaiyunSharedToken;
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["source"] = new[] { text },
            ["trans_type"] = "auto2zh",
            ["request_id"] = "gamevault",
            ["detect"] = true,
        });
        var headers = new Dictionary<string, string> { ["x-authorization"] = $"token {token}" };
        using var document = JsonDocument.Parse(await SteamApi.PostJsonAsync(CaiyunEndpoint, body, timeoutMs, headers));
        if (!document.RootElement.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(target.EnumerateArray().Select(entry => entry.GetString() ?? "")).Trim();
    }

    /// <summary>@return the translation from 有道, or an empty string when it is rate limited</summary>
    private static async Task<string> ViaYoudaoAsync(string text, int timeoutMs)
    {
        var body = $"q={Uri.EscapeDataString(text)}&from={GuessSourceLanguage(text)}&to=zh-CHS";
        using var document = JsonDocument.Parse(await SteamApi.PostFormAsync(YoudaoEndpoint, body, timeoutMs));
        // errorCode 411 means the shared demo quota is temporarily exhausted.
        if (!document.RootElement.TryGetProperty("errorCode", out var code) || code.GetString() != "0") return "";
        if (!document.RootElement.TryGetProperty("translation", out var translation) || translation.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(translation.EnumerateArray().Select(entry => entry.GetString() ?? "")).Trim();
    }

    /// <summary>@return the translation from MyMemory, or an empty string</summary>
    private static async Task<string> ViaMyMemoryAsync(string text, int timeoutMs)
    {
        // MyMemory only translates well from Latin-script languages into Chinese.
        var language = GuessSourceLanguage(text);
        if (language != "en") return "";

        var parts = new List<string>();
        foreach (var chunk in Chunk(text, MyMemoryChunkSize))
        {
            var url = $"{MyMemoryEndpoint}?q={Uri.EscapeDataString(chunk)}&langpair=en|zh-CN";
            using var document = JsonDocument.Parse(await SteamApi.GetStringAsync(url, timeoutMs));
            if (!document.RootElement.TryGetProperty("responseData", out var data)) return "";
            var translated = data.TryGetProperty("translatedText", out var value) ? value.GetString() ?? "" : "";
            if (translated.Length == 0 || translated.StartsWith("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase)) return "";
            parts.Add(translated.Trim());
        }
        return string.Join("", parts);
    }

    /// <summary>@return a rough source language tag for services that require one</summary>
    private static string GuessSourceLanguage(string text)
    {
        foreach (var character in text)
        {
            if (character >= '\u3040' && character <= '\u30FF') return "ja";
            if (character >= '\uAC00' && character <= '\uD7AF') return "ko";
        }
        return "en";
    }

    /// <summary>
    /// Splits a text into pieces that fit a service's request limit, preferring sentence breaks.
    ///
    /// @param text the text to split
    /// @param size the largest piece to produce
    /// @return the pieces, in order
    /// </summary>
    private static IEnumerable<string> Chunk(string text, int size)
    {
        var remaining = text.Trim();
        while (remaining.Length > size)
        {
            var cut = remaining.LastIndexOfAny(new[] { '.', '!', '?', '。', '！', '？', '\n' }, size);
            if (cut < size / 3) cut = remaining.LastIndexOf(' ', size);
            if (cut <= 0) cut = size;
            yield return remaining[..(cut + 1)];
            remaining = remaining[(cut + 1)..].TrimStart();
        }
        if (remaining.Length > 0) yield return remaining;
    }
}
