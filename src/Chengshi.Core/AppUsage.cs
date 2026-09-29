using System.Text.Json.Serialization;

namespace Chengshi.Core;

/// <summary>
/// 单个软件今天用掉的时间。LimitMinutes 为空表示不单独限时，只受每天总时长约束。
/// Running 表示最近一次记账时它正在运行（孩子端正用它）——临近限时/刚被关的提醒只对它弹。
/// </summary>
public sealed record AppUsage(
    string Key,
    string DisplayName,
    int UsedMinutes,
    int? LimitMinutes,
    bool Running = false)
{
    [JsonIgnore]
    public bool HasLimit => LimitMinutes is > 0;

    [JsonIgnore]
    public bool Exhausted => HasLimit && UsedMinutes >= LimitMinutes!.Value;

    /// <summary>今天还剩多少分钟（不足 1 分钟按 1 分钟算，给「还剩 N 分钟」的提醒用）。</summary>
    [JsonIgnore]
    public int RemainingMinutes => HasLimit
        ? Math.Max(0, LimitMinutes!.Value - UsedMinutes)
        : 0;

    /// <summary>0–1 的额度消耗比例，没有限额时恒为 0（不画进度条）。</summary>
    [JsonIgnore]
    public double Fraction =>
        HasLimit ? Math.Clamp((double)UsedMinutes / LimitMinutes!.Value, 0d, 1d) : 0d;

    /// <summary>给家长看的一句话，例如「已用 25 分钟 / 限 30 分钟」。</summary>
    [JsonIgnore]
    public string Summary => HasLimit
        ? $"已用 {UsedMinutes} 分钟 / 限 {LimitMinutes} 分钟"
        : $"已用 {UsedMinutes} 分钟";
}

/// <summary>
/// 按软件累计使用秒数。记账单位是「真实流逝的秒」而不是「心跳次数」：
/// 守护心跳的间隔不保证稳定（界面卡顿、系统休眠、服务忙），只有按秒记账才不会被带偏。
/// </summary>
public sealed class AppUsageTracker
{
    private readonly Dictionary<string, double> _seconds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>给某个软件累加 seconds 秒；非正数和空键忽略。</summary>
    public void Add(string key, double seconds)
    {
        if (string.IsNullOrWhiteSpace(key) || seconds <= 0)
        {
            return;
        }

        _seconds.TryGetValue(key, out var current);
        _seconds[key] = current + seconds;
    }

    public double UsedSeconds(string key) =>
        _seconds.TryGetValue(key, out var seconds) ? seconds : 0d;

    /// <summary>四舍五入到分钟：差几秒到 1 分钟也算 1 分钟，避免家长看到「已用 0 分钟」却在跑。</summary>
    public int UsedMinutes(string key) =>
        (int)Math.Round(UsedSeconds(key) / 60d, MidpointRounding.AwayFromZero);

    /// <summary>该软件今天的额度是否用完了；没有单独限额时永远没用完。</summary>
    public bool IsExhausted(string key, int? limitMinutes)
    {
        if (limitMinutes is not > 0)
        {
            return false;
        }

        return UsedSeconds(key) >= limitMinutes.Value * 60d;
    }

    public IReadOnlyDictionary<string, double> Snapshot() =>
        new Dictionary<string, double>(_seconds, StringComparer.OrdinalIgnoreCase);

    /// <summary>用持久化的数据整表替换（跨天或进程重启后恢复当天用量）。</summary>
    public void ReplaceWith(IEnumerable<KeyValuePair<string, double>> rows)
    {
        _seconds.Clear();
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Key) && row.Value > 0)
            {
                _seconds[row.Key] = row.Value;
            }
        }
    }

    public void Reset() => _seconds.Clear();
}
