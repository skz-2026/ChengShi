using System.Text.Json;
using System.Text.Json.Serialization;
using Chengshi.Core;
using Xunit;

namespace Chengshi.Core.Tests;

public class AppLimitTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void No_limit_by_default()
    {
        var app = new AllowedApp("Word", "winword");
        Assert.Null(app.DailyMinutes);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-5, 5)]
    [InlineData(1, 5)]
    [InlineData(30, 30)]
    [InlineData(9999, 600)]
    public void Limit_is_clamped_to_a_sane_range(int input, int? expected)
    {
        Assert.Equal(expected, new AllowedApp("游戏", "game", dailyMinutes: input).DailyMinutes);
    }

    [Fact]
    public void With_daily_minutes_changes_only_the_limit()
    {
        var app = new AllowedApp("Word", "winword", @"C:\Office\winword.exe");
        var limited = app.WithDailyMinutes(45);

        Assert.Equal(45, limited.DailyMinutes);
        Assert.Equal(app.DisplayName, limited.DisplayName);
        Assert.Equal(app.FileName, limited.FileName);
        Assert.Equal(app.ImagePath, limited.ImagePath);
        Assert.Null(app.DailyMinutes);
    }

    [Fact]
    public void With_daily_minutes_returns_same_instance_when_unchanged()
    {
        var app = new AllowedApp("Word", "winword", dailyMinutes: 45);
        Assert.Same(app, app.WithDailyMinutes(45));
    }

    [Fact]
    public void Desk_with_app_limit_targets_only_that_app()
    {
        var desk = new Desk(
            "test", "测试", "两款",
            [
                new AllowedApp("记事本", "notepad"),
                new AllowedApp("计算器", "calc"),
            ]);

        var limited = desk.WithAppLimit("calc.exe", 30);

        Assert.Equal(30, limited.Apps.First(a => a.FileName == "calc.exe").DailyMinutes);
        Assert.Null(limited.Apps.First(a => a.FileName == "notepad.exe").DailyMinutes);
        // 原书桌不该被就地修改。
        Assert.Null(desk.Apps[0].DailyMinutes);
    }

    [Fact]
    public void Unrestricted_desk_keeps_its_summary_when_apps_change()
    {
        // 「整个电脑」场景：名单承载单软件限时，摘要描述场景本身，不能被重写成软件名。
        var desk = BuiltinDesks.FullPc();
        var withLimit = desk.WithApps([new AllowedApp("游戏", "game", dailyMinutes: 30)]);

        Assert.Equal(desk.Summary, withLimit.Summary);
        Assert.True(withLimit.Unrestricted);
    }

    [Fact]
    public void Desk_with_app_limit_clears_the_limit_when_null()
    {
        var desk = new Desk("test", "测试", "一款", [new AllowedApp("计算器", "calc", dailyMinutes: 30)]);
        var cleared = desk.WithAppLimit("calc.exe", null);

        Assert.Null(cleared.Apps[0].DailyMinutes);
        Assert.Empty(cleared.LimitedApps);
    }

    [Fact]
    public void Limited_apps_lists_each_app_once()
    {
        var desk = new Desk(
            "test", "测试", "两款",
            [
                new AllowedApp("计算器", "calc", dailyMinutes: 30),
                new AllowedApp("Word", "winword"),
                new AllowedApp("Excel", "excel", dailyMinutes: 20),
            ]);

        var limited = desk.LimitedApps;
        Assert.Equal(2, limited.Count);
        Assert.Contains(limited, a => a.FileName == "calc.exe");
        Assert.Contains(limited, a => a.FileName == "excel.exe");
    }

    [Fact]
    public void App_limit_survives_json_roundtrip()
    {
        var desk = new Desk(
            "test", "测试", "一款",
            [new AllowedApp("计算器", "calc", dailyMinutes: 30)],
            DisconnectNetwork: true);

        var json = JsonSerializer.Serialize(desk, Json);
        var copy = JsonSerializer.Deserialize<Desk>(json, Json);

        Assert.NotNull(copy);
        Assert.Equal(30, copy.Apps[0].DailyMinutes);
        Assert.True(copy.DisconnectNetwork);
    }

    [Fact]
    public void Unlimited_app_keeps_no_limit_after_roundtrip()
    {
        var desk = new Desk("test", "测试", "一款", [new AllowedApp("计算器", "calc")]);
        var copy = JsonSerializer.Deserialize<Desk>(JsonSerializer.Serialize(desk, Json), Json);

        Assert.NotNull(copy);
        Assert.Null(copy.Apps[0].DailyMinutes);
    }

    // ===== 用量记账 =====

    [Fact]
    public void Tracker_accumulates_seconds_and_rounds_to_minutes()
    {
        var tracker = new AppUsageTracker();
        tracker.Add("calc.exe", 45);

        // 不足 1 分钟但实际在跑，应算 1 分钟。
        Assert.Equal(1, tracker.UsedMinutes("calc.exe"));

        tracker.Add("calc.exe", 45);
        Assert.Equal(2, tracker.UsedMinutes("calc.exe"));
    }

    [Fact]
    public void Tracker_ignores_empty_keys_and_non_positive_seconds()
    {
        var tracker = new AppUsageTracker();
        tracker.Add("   ", 60);
        tracker.Add("calc.exe", 0);
        tracker.Add("calc.exe", -30);

        Assert.Equal(0, tracker.UsedSeconds("calc.exe"));
    }

    [Fact]
    public void Keys_are_matched_case_insensitively()
    {
        var tracker = new AppUsageTracker();
        tracker.Add("Calc.EXE", 60);
        tracker.Add("calc.exe", 60);

        Assert.Equal(120, tracker.UsedSeconds("CALC.exe"));
    }

    [Fact]
    public void Exhausted_only_after_the_limit_is_reached()
    {
        var tracker = new AppUsageTracker();
        tracker.Add("calc.exe", 30 * 60 - 1);
        Assert.False(tracker.IsExhausted("calc.exe", 30));

        tracker.Add("calc.exe", 1);
        Assert.True(tracker.IsExhausted("calc.exe", 30));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void App_without_limit_is_never_exhausted(int? limit)
    {
        var tracker = new AppUsageTracker();
        tracker.Add("calc.exe", 60 * 60 * 24);

        Assert.False(tracker.IsExhausted("calc.exe", limit));
    }

    [Fact]
    public void Tracker_replaces_rows_and_resets()
    {
        var tracker = new AppUsageTracker();
        tracker.Add("calc.exe", 600);
        tracker.ReplaceWith([new KeyValuePair<string, double>("notepad.exe", 120)]);

        Assert.Equal(0, tracker.UsedSeconds("calc.exe"));
        Assert.Equal(120, tracker.UsedSeconds("notepad.exe"));

        tracker.Reset();
        Assert.Equal(0, tracker.UsedSeconds("notepad.exe"));
    }

    [Fact]
    public void ReplaceWith_drops_empty_rows()
    {
        var tracker = new AppUsageTracker();
        tracker.ReplaceWith([
            new KeyValuePair<string, double>("calc.exe", 0),
            new KeyValuePair<string, double>("", 60),
        ]);

        Assert.Empty(tracker.Snapshot());
    }

    // ===== 给界面用的展示模型 =====

    [Fact]
    public void App_usage_reports_fraction_and_summary()
    {
        var usage = new AppUsage("calc.exe", "计算器", 15, 30);

        Assert.True(usage.HasLimit);
        Assert.False(usage.Exhausted);
        Assert.Equal(0.5, usage.Fraction);
        Assert.Equal("已用 15 分钟 / 限 30 分钟", usage.Summary);
    }

    [Fact]
    public void App_usage_without_limit_has_no_fraction()
    {
        var usage = new AppUsage("notepad.exe", "记事本", 15, null);

        Assert.False(usage.HasLimit);
        Assert.False(usage.Exhausted);
        Assert.Equal(0d, usage.Fraction);
        Assert.Equal("已用 15 分钟", usage.Summary);
    }

    [Fact]
    public void App_usage_is_exhausted_when_used_reaches_the_limit()
    {
        var usage = new AppUsage("calc.exe", "计算器", 30, 30);

        Assert.True(usage.Exhausted);
        Assert.Equal(1d, usage.Fraction);
    }

    [Fact]
    public void App_usage_does_not_serialize_computed_fields()
    {
        var json = JsonSerializer.Serialize(new AppUsage("calc.exe", "计算器", 15, 30), Json);

        Assert.Contains("\"limitMinutes\":30", json);
        Assert.DoesNotContain("exhausted", json);
        Assert.DoesNotContain("fraction", json);
        Assert.DoesNotContain("hasLimit", json);
    }

    [Fact]
    public void Running_defaults_to_false_and_remaining_minutes_tracks_the_limit()
    {
        var usage = new AppUsage("calc.exe", "计算器", 15, 30);

        Assert.False(usage.Running);
        Assert.Equal(15, usage.RemainingMinutes);
        Assert.Equal(0, new AppUsage("calc.exe", "计算器", 30, 30).RemainingMinutes);
        Assert.Equal(0, new AppUsage("notepad.exe", "记事本", 99, null).RemainingMinutes);

        var json = JsonSerializer.Serialize(new AppUsage("calc.exe", "计算器", 15, 30, Running: true), Json);
        Assert.Contains("\"running\":true", json);
    }
}
