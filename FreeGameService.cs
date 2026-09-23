using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace BFV_FPS_Monitor;

/// <summary>限免游戏情报条目（来自官方公开接口，仅供展示 + 跳转，不代登录）。</summary>
public sealed class FreeGame
{
    public string Title = "";
    public string Platform = "";      // Epic / Steam
    public string Seller = "";
    public string Url = "";
    public string ImageUrl = "";
    public long OriginPriceCents;
    public string FmtPrice = "";      // 官方格式化价格（如 ¥68.00）
    public DateTime? EndLocal;        // null = 官方未公布
    public DateTime? StartLocal;
    public bool Upcoming;             // 即将开始
}

/// <summary>聚合 Epic（freeGamesPromotions）与 Steam（featuredcategories 100% 折扣）限免情报。</summary>
public static class FreeGameService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static FreeGameService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("GameMonitor/1.0 (+free-games-widget)");
    }

    public static async Task<List<FreeGame>> FetchAllAsync(CancellationToken ct = default)
    {
        var list = new List<FreeGame>();
        var epicTask = FetchEpicAsync(ct);
        var steamTask = FetchSteamAsync(ct);
        var gogTask = FetchCheapSharkDealsAsync(ct, storeId: 7, platform: "GOG", maxPrice: 0, top: 8);
        var humbleTask = FetchCheapSharkDealsAsync(ct, storeId: 11, platform: "Humble", maxPrice: 0, top: 8);
        try { list.AddRange(await epicTask.ConfigureAwait(false)); } catch { }
        try { list.AddRange(await steamTask.ConfigureAwait(false)); } catch { }
        try { list.AddRange(await gogTask.ConfigureAwait(false)); } catch { }
        try { list.AddRange(await humbleTask.ConfigureAwait(false)); } catch { }
        return list;
    }

    // ================= Epic =================

    /// <summary>Epic 限免（公开接口，直连可用）。</summary>
    public static async Task<List<FreeGame>> FetchEpicAsync(CancellationToken ct)
    {
        const string url = "https://store-site-backend-static.ak.epicgames.com/freeGamesPromotions?locale=zh-CN&country=CN&allowCountries=CN";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var result = new List<FreeGame>();
        var now = DateTime.UtcNow;
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Catalog", out var catalog) ||
            !catalog.TryGetProperty("searchStore", out var store) ||
            !store.TryGetProperty("elements", out var elements))
            return result;

        foreach (var el in elements.EnumerateArray())
        {
            try
            {
                string title = el.T("title");
                if (title.Length == 0) continue;
                string seller = el.TryGetProperty("seller", out var sel) ? sel.T("name") : "";
                string desc = el.T("description");

                long origin = 0; string cur = ""; string fmtPrice = "";
                if (el.TryGetProperty("price", out var price) && price.TryGetProperty("totalPrice", out var tot))
                {
                    origin = tot.L("originalPrice");
                    cur = tot.T("currencyCode");
                    if (tot.TryGetProperty("fmtPrice", out var fmt)) fmtPrice = fmt.T("originalPrice");
                }

                string img = "";
                if (el.TryGetProperty("keyImages", out var images))
                {
                    foreach (var im in images.EnumerateArray())
                    {
                        string type = im.T("type");
                        if (type is "OfferImageWide" or "DieselStoreFrontWide")
                        {
                            img = im.T("url");
                            if (img.Length > 0) break;
                        }
                    }
                    if (img.Length == 0 && images.GetArrayLength() > 0) img = images[0].T("url");
                }

                string slug = el.T("productSlug");
                if (slug.Length == 0 && el.TryGetProperty("catalogNs", out var cns) &&
                    cns.TryGetProperty("mappings", out var maps) && maps.GetArrayLength() > 0)
                    slug = maps[0].T("pageSlug");
                string pageUrl = slug.Length > 0
                    ? $"https://store.epicgames.com/zh-CN/p/{slug}"
                    : "https://store.epicgames.com/zh-CN/free-games";

                DateTime? activeEnd = null, upStart = null;
                bool upcoming = false;
                if (el.TryGetProperty("promotions", out var promo))
                {
                    if (activeEnd == null && promo.TryGetProperty("promotionalOffers", out var po))
                    {
                        foreach (var w in po.EnumerateArray())
                        {
                            if (!w.TryGetProperty("promotionalOffers", out var offs)) continue;
                            foreach (var off in offs.EnumerateArray())
                            {
                                int pct = off.TryGetProperty("discountSetting", out var ds) ? ds.I("discountPercentage") : -1;
                                if (pct != 0) continue;
                                var st = ParseIso(off.T("startDate"));
                                var en = ParseIso(off.T("endDate"));
                                if (st.HasValue && en.HasValue && now >= st.Value && now < en.Value)
                                {
                                    activeEnd = en.Value.ToLocalTime();
                                    break;
                                }
                            }
                            if (activeEnd != null) break;
                        }
                    }
                    if (activeEnd == null && promo.TryGetProperty("upcomingPromotions", out var upo))
                    {
                        foreach (var w in upo.EnumerateArray())
                        {
                            if (!w.TryGetProperty("upcomingPromotions", out var offs)) continue;
                            foreach (var off in offs.EnumerateArray())
                            {
                                int pct = off.TryGetProperty("discountSetting", out var ds) ? ds.I("discountPercentage") : -1;
                                if (pct != 0) continue;
                                var st = ParseIso(off.T("startDate"));
                                if (st.HasValue && st.Value > now)
                                {
                                    upStart = st.Value.ToLocalTime();
                                    upcoming = true;
                                    break;
                                }
                            }
                            if (upcoming) break;
                        }
                    }
                }

                if (activeEnd == null && !upcoming) continue;
                result.Add(new FreeGame
                {
                    Title = title,
                    Platform = "Epic",
                    Seller = seller,
                    Url = pageUrl,
                    ImageUrl = img,
                    OriginPriceCents = origin,
                    FmtPrice = fmtPrice,
                    EndLocal = activeEnd,
                    StartLocal = upStart,
                    Upcoming = upcoming,
                });
            }
            catch { }
        }
        return result;
    }

    // ================= Steam =================

    /// <summary>Steam 限免（featuredcategories 100% 折扣；部分地区网络不可达，调用方需容错）。</summary>
    public static async Task<List<FreeGame>> FetchSteamAsync(CancellationToken ct)
    {
        const string url = "https://store.steampowered.com/api/featuredcategories/?cc=CN&l=schinese";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var result = new List<FreeGame>();
        if (!doc.RootElement.TryGetProperty("specials", out var specials) ||
            !specials.TryGetProperty("items", out var items))
            return result;

        foreach (var it in items.EnumerateArray())
        {
            try
            {
                if (it.I("discount_percent") != 100) continue;
                long origin = it.L("original_price");
                string img = it.T("large_capsule_image");
                if (img.Length == 0) img = it.T("header_image");
                result.Add(new FreeGame
                {
                    Title = it.T("name"),
                    Platform = "Steam",
                    Url = $"https://store.steampowered.com/app/{it.L("id")}",
                    ImageUrl = img,
                    OriginPriceCents = origin,
                    EndLocal = null,
                });
            }
            catch { }
        }
        return result;
    }

    // ================= GOG / Humble（CheapShark 聚合特价） =================

    /// <summary>
    /// CheapShark 聚合特价（公开 API，需自定义 UA）。GOG storeID=7、Humble Store=11、Fanatical=15。
    /// maxPrice=0 时不限价，按折扣力度排序取前 N 条；URL 跳转到 CheapShark redirect（再转商店页）。
    /// </summary>
    public static async Task<List<FreeGame>> FetchCheapSharkDealsAsync(CancellationToken ct, int storeId, string platform, double maxPrice, int top = 8)
    {
        var url = $"https://www.cheapshark.com/api/1.0/deals?storeID={storeId}&sortBy=Savings&pageSize={top}";
        if (maxPrice > 0) url += $"&upperPrice={maxPrice.ToString("F0", CultureInfo.InvariantCulture)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("GameMonitor/1.0 (personal-hardware-monitor)");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var result = new List<FreeGame>();
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            try
            {
                string title = d.T("title");
                if (title.Length == 0) continue;
                double normal = ParseMoney(d.T("normalPrice"));
                double sale = ParseMoney(d.T("salePrice"));
                if (normal <= 0 || sale <= 0) continue;
                int steamApp = (int)d.L("steamAppID");
                string dealId = d.T("dealID");
                string url2 = steamApp > 0
                    ? $"https://store.steampowered.com/app/{steamApp}"
                    : (dealId.Length > 0 ? $"https://www.cheapshark.com/redirect?dealID={Uri.EscapeDataString(dealId)}" : "");
                double rating = d.L("steamRatingPercent");
                result.Add(new FreeGame
                {
                    Title = title,
                    Platform = platform,
                    Seller = $"-{100 - (int)Math.Round(sale / normal * 100)}%",
                    Url = url2,
                    ImageUrl = d.T("thumb"),
                    OriginPriceCents = (long)(normal * 100),
                    FmtPrice = $"${sale:F2}",
                    EndLocal = null,
                });
            }
            catch { }
        }
        return result;
    }
    // ================= helpers =================

    private static DateTime? ParseIso(string s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static double ParseMoney(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string T(this JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long L(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        try { return v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0; }
        catch { return 0; }
    }

    private static int I(this JsonElement e, string name)
        => (int)L(e, name);
}
