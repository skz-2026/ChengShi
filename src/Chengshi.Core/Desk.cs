using System.Text.Json.Serialization;

namespace Chengshi.Core;

public sealed record Desk(
    string Id,
    string Name,
    string Summary,
    IReadOnlyList<AllowedApp> Apps,
    bool DisconnectNetwork = false,
    IReadOnlyList<string>? AllowedSites = null,
    IReadOnlyList<string>? BlockedSites = null,
    IReadOnlyList<string>? BlockCategories = null,
    bool Unrestricted = false)
{
    /// <summary>
    /// 不限软件模式（「整个电脑」场景）：所有软件、网站一律放行，
    /// 守护只负责计时长和到点锁屏。时间用完的 Lockdown 清场不受本标志影响。
    /// </summary>
    [JsonIgnore]
    public bool RestrictsApps => !Unrestricted;

    [JsonIgnore]
    public IReadOnlyList<AllowRule> Rules => Expand(Apps);

    [JsonIgnore]
    public IReadOnlyList<string> AllowedSiteList => AllowedSites ?? [];

    [JsonIgnore]
    public IReadOnlyList<string> BlockedSiteList => BlockedSites ?? [];

    [JsonIgnore]
    public IReadOnlyList<string> BlockCategoryList => BlockCategories ?? [];

    /// <summary>允许名单非空 = 白名单模式：浏览器只能打开名单里的网站。</summary>
    [JsonIgnore]
    public bool HasSiteRules =>
        AllowedSiteList.Count > 0
        || BlockedSiteList.Count > 0
        || BlockCategoryList.Count > 0;

    public Desk WithApps(IEnumerable<AllowedApp> apps)
    {
        var distinct = Distinct(apps);
        // 「整个电脑」场景的名单不是白名单（承载的是单软件限时），摘要描述的是场景本身，
        // 不随名单重写成软件名列表。
        return this with
        {
            Apps = distinct,
            Summary = Unrestricted ? Summary : Summarize(distinct),
        };
    }

    /// <summary>
    /// 给某一款软件单独限时（minutes 为空 = 不限时）。按 Key 匹配，
    /// 同一款软件在名单里有多条规则时一起改，不影响书桌的摘要文案。
    /// </summary>
    public Desk WithAppLimit(string key, int? minutes)
    {
        var limit = AllowedApp.NormalizeLimit(minutes);
        var apps = new List<AllowedApp>(Apps.Count);
        var changed = false;
        foreach (var app in Apps)
        {
            if (string.Equals(app.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                changed |= app.DailyMinutes != limit;
                apps.Add(app.WithDailyMinutes(limit));
            }
            else
            {
                apps.Add(app);
            }
        }

        return changed ? this with { Apps = Distinct(apps) } : this;
    }

    /// <summary>今天有单独限额的软件（给设置页列出来）。</summary>
    [JsonIgnore]
    public IReadOnlyList<AllowedApp> LimitedApps =>
        Apps.Where(a => a.DailyMinutes is > 0)
            .GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public Desk WithAllowedSites(IEnumerable<string> sites) =>
        this with { AllowedSites = NormalizeDomains(sites) };

    public Desk WithBlockedSites(IEnumerable<string> sites) =>
        this with { BlockedSites = NormalizeDomains(sites) };

    public Desk WithBlockCategories(IEnumerable<string> categories) =>
        this with { BlockCategories = categories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };

    /// <summary>把用户输入整理成裸域名：去协议、去路径、去空白、小写、去重。</summary>
    public static IReadOnlyList<string> NormalizeDomains(IEnumerable<string> domains)
    {
        var result = new List<string>();
        foreach (var raw in domains)
        {
            var text = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (text.Length == 0)
            {
                continue;
            }

            text = text.Replace('\\', '/');
            if (text.Contains("://", StringComparison.Ordinal))
            {
                text = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
            }

            var slash = text.IndexOf('/');
            if (slash >= 0)
            {
                text = text[..slash];
            }

            text = text.TrimEnd('.').Trim();
            if (text.Length == 0 || !text.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            if (!result.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(text);
            }
        }

        return result;
    }

    public static string Summarize(IReadOnlyList<AllowedApp> apps)
    {
        if (apps.Count == 0)
        {
            return "还没选软件";
        }

        var names = apps
            .Select(a => a.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(4)
            .ToArray();
        var joined = string.Join("、", names);
        return apps.Select(a => a.DisplayName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() > 4
            ? $"{joined} 等 {apps.Count} 款"
            : joined;
    }

    public static IReadOnlyList<AllowedApp> Distinct(IEnumerable<AllowedApp> apps)
    {
        var map = new Dictionary<string, AllowedApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            map[app.Key] = app;
        }

        return map.Values
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<AllowRule> Expand(IReadOnlyList<AllowedApp> apps) =>
        apps.SelectMany(a => a.ToRules()).ToArray();
}
