using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CharacterCardManager.Services;

public sealed class BotbooruSyncService
{
    private const string BaseUrl = "https://botbooru.com";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private string? _sessionCookie;
    private string? _userAgent;
    private string? _bearerToken;

    public bool HasSession => !string.IsNullOrWhiteSpace(_bearerToken) || !string.IsNullOrWhiteSpace(_sessionCookie);

    public void SetSession(string? cookieHeader, string? userAgent = null, string? bearerToken = null)
    {
        _sessionCookie = string.IsNullOrWhiteSpace(cookieHeader) ? null : cookieHeader.Trim();
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim();
        _bearerToken = string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken.Trim();
    }

    static BotbooruSyncService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("CharacterCardManager/1.0");
    }

    public sealed record BotbooruTagInfo(string Name, string Category);

    public sealed record BotbooruGalleryImage(
        long Id,
        int Position,
        int Width,
        int Height,
        string PreviewUrl,
        string MainViewUrl,
        string DownloadUrl);

    public sealed record BotbooruLinkedPost(long Id, string CharacterName, string Relation);

    public sealed record BotbooruPostDetails(
        List<BotbooruTagInfo> Tags,
        string? Tagline,
        string? MainImageUrl,
        List<BotbooruGalleryImage> GalleryImages,
        bool IsFork,
        BotbooruLinkedPost? ForkMaster,
        List<BotbooruLinkedPost> Forks,
        List<BotbooruLinkedPost> Siblings,
        double? SlopScore);

    public sealed record BotbooruSearchItem(
        long Id,
        string CharacterName,
        string Tagline,
        string DescriptionExcerpt,
        string Filename,
        string ThumbnailUrl,
        int Downloads,
        int Views,
        int FavoriteCount,
        int TokenCount,
        bool IsFork,
        int ForkCount,
        int SiblingCount,
        List<BotbooruTagInfo> Tags);

    public sealed record BotbooruSearchResult(
        List<BotbooruSearchItem> Posts,
        int Total,
        bool TotalIsCapped);

    public async Task<List<BotbooruTagInfo>> FetchPostTagsAsync(long postId, CancellationToken ct = default)
    {
        var details = await FetchPostDetailsAsync(postId, ct);
        return details.Tags;
    }

    public async Task<BotbooruPostDetails> FetchPostDetailsAsync(long postId, CancellationToken ct = default)
    {
        string url = $"{BaseUrl}/post/{postId}";
        using var req = CreateRequest(url);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tags = new List<BotbooruTagInfo>();
        if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tagsEl.EnumerateArray())
            {
                string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? cat = item.TryGetProperty("category", out var c) ? c.GetString() : "General";
                if (!string.IsNullOrWhiteSpace(name))
                    tags.Add(new BotbooruTagInfo(name, string.IsNullOrWhiteSpace(cat) ? "General" : cat));
            }
        }

        string? tagline = root.TryGetProperty("tagline", out var tl) && tl.ValueKind == JsonValueKind.String
            ? tl.GetString()
            : null;

        string? mainImageUrl = root.TryGetProperty("image_link", out var il) && il.ValueKind == JsonValueKind.String
            ? ToAbsoluteUrl(il.GetString())
            : null;

        var gallery = new List<BotbooruGalleryImage>();
        if (root.TryGetProperty("mini_gallery", out var mg) &&
            mg.ValueKind == JsonValueKind.Object &&
            mg.TryGetProperty("images", out var imgs) &&
            imgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in imgs.EnumerateArray())
            {
                long id = img.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var parsedId) ? parsedId : 0;
                int pos = img.TryGetProperty("position", out var posEl) && posEl.TryGetInt32(out var p) ? p : 0;
                int w = img.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var wi) ? wi : 0;
                int h = img.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out var hi) ? hi : 0;
                string preview = ToAbsoluteUrl(img.TryGetProperty("preview_url", out var pv) ? pv.GetString() : null) ?? string.Empty;
                string mainView = ToAbsoluteUrl(img.TryGetProperty("main_view_url", out var mv) ? mv.GetString() : null) ?? preview;
                string download = ToAbsoluteUrl(img.TryGetProperty("download_url", out var dl) ? dl.GetString() : null) ?? string.Empty;

                if (!string.IsNullOrEmpty(preview) && !string.IsNullOrEmpty(download))
                    gallery.Add(new BotbooruGalleryImage(id, pos, w, h, preview, mainView, download));
            }
        }

        bool isFork = root.TryGetProperty("is_fork", out var ifEl) && ifEl.ValueKind == JsonValueKind.True;
        var forkMaster = ParseLinkedList(root, "fork_master", "Master").FirstOrDefault();
        var forks = ParseLinkedList(root, "fork_posts", "Fork");
        var siblings = ParseLinkedList(root, "sibling_posts", "Sibling");
        double? slopScore = root.TryGetProperty("slop_score", out var ssEl) && ssEl.ValueKind == JsonValueKind.Number && ssEl.TryGetDouble(out var ss)
            ? ss
            : null;

        return new BotbooruPostDetails(tags, tagline, mainImageUrl, gallery, isFork, forkMaster, forks, siblings, slopScore);
    }

    private static List<BotbooruLinkedPost> ParseLinkedList(JsonElement root, string propName, string relation)
    {
        var list = new List<BotbooruLinkedPost>();
        if (!root.TryGetProperty(propName, out var el))
            return list;

        if (el.ValueKind == JsonValueKind.Object)
        {
            var item = ParseSingleLinked(el, relation);
            if (item is not null) list.Add(item);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var sub in el.EnumerateArray())
            {
                if (sub.ValueKind == JsonValueKind.Object)
                {
                    var item = ParseSingleLinked(sub, relation);
                    if (item is not null) list.Add(item);
                }
            }
        }
        return list;
    }

    private static BotbooruLinkedPost? ParseSingleLinked(JsonElement el, string relation)
    {
        if (!el.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
            return null;
        string name = el.TryGetProperty("character_name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? $"#{id}"
            : $"#{id}";
        return new BotbooruLinkedPost(id, name, relation);
    }

    public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct = default)
    {
        string abs = ToAbsoluteUrl(url) ?? url;
        using var req = CreateRequest(abs);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<BotbooruSearchResult> SearchPostsAsync(
        string query,
        string sort,
        int limit,
        int offset,
        bool sfwOnly,
        bool hideAi,
        CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            $"sort={Uri.EscapeDataString(sort)}",
            $"limit={limit}",
            $"offset={offset}"
        };
        if (!string.IsNullOrWhiteSpace(query))
            qs.Add($"q={Uri.EscapeDataString(query.Trim())}");
        if (sfwOnly)
            qs.Add("sfw_only=true");
        if (hideAi)
            qs.Add("hide_ai=true");

        string url = $"{BaseUrl}/posts/?{string.Join("&", qs)}";
        using var req = CreateRequest(url);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        int total = root.TryGetProperty("total", out var totEl) && totEl.TryGetInt32(out var tot) ? tot : 0;
        bool capped = root.TryGetProperty("total_is_capped", out var capEl) && capEl.ValueKind == JsonValueKind.True;
        var posts = new List<BotbooruSearchItem>();

        if (root.TryGetProperty("posts", out var postsEl) && postsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in postsEl.EnumerateArray())
            {
                if (!p.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
                    continue;

                string name = GetJsonStr(p, "character_name");
                string tagline = GetJsonStr(p, "tagline");
                string desc = GetJsonStr(p, "description_excerpt");
                string filename = GetJsonStr(p, "filename");
                string thumbUrl = string.IsNullOrWhiteSpace(filename)
                    ? string.Empty
                    : $"{BaseUrl}/images/{Uri.EscapeDataString(filename)}";

                int downloads = GetJsonInt(p, "downloads");
                int views = GetJsonInt(p, "views");
                int favs = GetJsonInt(p, "favorite_count");
                int tokens = GetJsonInt(p, "token_count");
                bool isFork = p.TryGetProperty("is_fork", out var ifEl) && ifEl.ValueKind == JsonValueKind.True;
                int forkCount = GetJsonInt(p, "fork_count");
                int siblingCount = GetJsonInt(p, "sibling_count");

                var tags = new List<BotbooruTagInfo>();
                if (p.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tagsEl.EnumerateArray())
                    {
                        string? tn = t.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? tc = t.TryGetProperty("category", out var c) ? c.GetString() : "General";
                        if (!string.IsNullOrWhiteSpace(tn))
                            tags.Add(new BotbooruTagInfo(tn, string.IsNullOrWhiteSpace(tc) ? "General" : tc));
                    }
                }

                posts.Add(new BotbooruSearchItem(
                    id, name, tagline, desc, filename, thumbUrl,
                    downloads, views, favs, tokens, isFork, forkCount, siblingCount, tags));
            }
        }

        return new BotbooruSearchResult(posts, total, capped);
    }

    private static string GetJsonStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty) : string.Empty;

    private static int GetJsonInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    public async Task<(byte[] Bytes, string FileName)> DownloadCardPngAsync(long postId, string fallbackName, CancellationToken ct = default)
    {
        string url = $"{BaseUrl}/download/png/{postId}";
        using var req = CreateRequest(url);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        string? fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{fallbackName}-{postId}.png";
        }

        // Replace invalid Windows filename characters (e.g. '|', ':', '?', '*') with '_' just like Chrome
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        fileName = string.Concat(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch)).Trim();
        if (string.IsNullOrWhiteSpace(fileName) || fileName == ".png")
            fileName = $"card-{postId}.png";

        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return (bytes, fileName);
    }

    private HttpRequestMessage CreateRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_userAgent))
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        req.Headers.TryAddWithoutValidation("Referer", $"{BaseUrl}/");
        if (!string.IsNullOrWhiteSpace(_bearerToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        if (!string.IsNullOrWhiteSpace(_sessionCookie))
            req.Headers.TryAddWithoutValidation("Cookie", _sessionCookie);
        return req;
    }

    private static string? ToAbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        return url.StartsWith('/') ? $"{BaseUrl}{url}" : $"{BaseUrl}/{url}";
    }

    public static string GetCategoryColorHex(string category) => category.ToLowerInvariant() switch
    {
        "artist" => "#EF4444",
        "writer" => "#F97316",
        "character" or "characters" => "#22C55E",
        "scenarios" => "#10B981",
        "copyright" => "#EC4899",
        "meta" => "#A855F7",
        "auto" => "#94A3B8",
        "settings" => "#F59E0B",
        "activities" => "#84CC16",
        "nsfl" => "#78716C",
        "language" => "#14B8A6",
        "general" => "#3B82F6",
        _ => "#6E6E8C"
    };

    public static int GetCategorySortOrder(string category) => category.ToLowerInvariant() switch
    {
        "writer" => 0,
        "artist" => 1,
        "copyright" => 2,
        "character" or "characters" => 3,
        "general" => 4,
        "scenarios" => 5,
        "settings" => 6,
        "activities" => 7,
        "meta" => 8,
        "language" => 9,
        "auto" => 10,
        "nsfl" => 11,
        _ => 99
    };
}
