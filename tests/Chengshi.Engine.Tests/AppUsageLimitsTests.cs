using Chengshi.Core;
using Xunit;

namespace Chengshi.Engine.Tests;

public class AppUsageLimitsTests : IDisposable
{
    private const string TestDeskId = "per-app-limits";
    private static readonly DateOnly Wednesday = new(2026, 8, 19);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chengshi-appusage-" + Guid.NewGuid().ToString("N"));

    public AppUsageLimitsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    private string FamilyPath => Path.Combine(_dir, "family.json");
    private string DeskPath => Path.Combine(_dir, "desks.json");
    private string TimePath => Path.Combine(_dir, "screentime.json");
    private string AppUsagePath => Path.Combine(_dir, "appusage.json");

    /// <summary>只报告「正在运行」的软件，不碰真实进程。</summary>
    private sealed class FakeProbe : IRunningAppProbe
    {
        public List<string> Keys { get; set; } = [];

        public IReadOnlyCollection<string> RunningKeys(Desk desk) => Keys.ToArray();
    }

    /// <summary>只记录交进来的书桌，绝不真的结束进程——测试里碰真进程太危险。</summary>
    private sealed class RecordingEnforcer : IProcessEnforcer
    {
        public List<Desk> Swept { get; } = [];

        event Action<ProcessIdentity>? IProcessEnforcer.Blocked
        {
            add { }
            remove { }
        }

        public bool TryEnforce(ProcessIdentity process, Desk desk) => false;

        public int SweepRunning(Desk desk)
        {
            Swept.Add(desk);
            return 0;
        }
    }

    private sealed class NoopSiteGuard : SitePolicyGuard
    {
        public override bool Apply(PolicySpec spec) => true;
    }

    private Fixture Build(int? calcLimit, TimeSpan daily) => BuildDesk(
        new Desk(
            TestDeskId,
            "测试桌",
            "记事本与计算器",
            [
                new AllowedApp("记事本", "notepad"),
                new AllowedApp("计算器", "calc", dailyMinutes: calcLimit),
            ]),
        daily);

    /// <summary>「整个电脑」场景：不限软件，名单里只有单独限时的软件。</summary>
    private Fixture BuildFullPc(int? gameLimit, TimeSpan daily) => BuildDesk(
        new Desk(
            TestDeskId,
            "整个电脑",
            "不限软件",
            [
                new AllowedApp("游戏", "game", dailyMinutes: gameLimit),
                new AllowedApp("记事本", "notepad"),
            ],
            Unrestricted: true),
        daily);

    private Fixture BuildDesk(Desk desk, TimeSpan daily)
    {
        var calendar = new ManualCalendar { Today = Wednesday };
        var clock = new ManualClock();

        var familyStore = FamilyStore.Load(FamilyPath);
        familyStore.Save(FamilySettings.Create("1234", (int)daily.TotalMinutes, TestDeskId));

        var deskStore = DeskStore.Load(DeskPath);
        deskStore.Upsert(desk);

        var probe = new FakeProbe();
        var enforcer = new RecordingEnforcer();
        var store = new AppUsageStore(AppUsagePath);
        var host = new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, daily, TimePath),
            enforcer,
            new NoopNetworkGuard(),
            new NoopSiteGuard(),
            new UsageLogStore(Path.Combine(_dir, "usagelog.jsonl")),
            () => DateTime.Now,
            probe,
            store);

        return new Fixture(host, clock, calendar, probe, enforcer, store);
    }

    private sealed record Fixture(
        SessionHost Host,
        ManualClock Clock,
        ManualCalendar Calendar,
        FakeProbe Probe,
        RecordingEnforcer Enforcer,
        AppUsageStore Store) : IDisposable
    {
        public void Dispose() => Host.Dispose();
        /// <summary>按 1 分钟一步推进：记账有 5 分钟的单次上限，一次跳太久会被丢弃。</summary>
        public void RunMinutes(int minutes)
        {
            for (var i = 0; i < minutes; i++)
            {
                Clock.Advance(TimeSpan.FromMinutes(1));
                Host.Tick();
            }
        }

        public AppUsage Usage(string key) => Host.AppUsage.Single(row => row.Key == key);
    }

    [Fact]
    public void Only_running_apps_accumulate_usage()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        f.RunMinutes(10);

        Assert.Equal(10, f.Usage("calc.exe").UsedMinutes);
        Assert.Equal(0, f.Usage("notepad.exe").UsedMinutes);
    }

    [Fact]
    public void Usage_keeps_accruing_while_the_app_stays_open()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe", "notepad.exe"];
        f.Host.StartGuard();

        f.RunMinutes(5);
        Assert.Equal(5, f.Usage("calc.exe").UsedMinutes);

        // 记事本关掉后只累计计算器。
        f.Probe.Keys = ["calc.exe"];
        f.RunMinutes(5);

        Assert.Equal(10, f.Usage("calc.exe").UsedMinutes);
        Assert.Equal(5, f.Usage("notepad.exe").UsedMinutes);
    }

    [Fact]
    public void Nothing_is_counted_when_no_desk_session_is_running()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];

        f.RunMinutes(10);

        Assert.Equal(0, f.Usage("calc.exe").UsedMinutes);
    }

    [Fact]
    public void App_over_its_own_limit_is_dropped_from_the_enforced_desk()
    {
        using var f = Build(calcLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        f.RunMinutes(29);
        Assert.False(f.Usage("calc.exe").Exhausted);
        Assert.Contains(f.Host.EnforcedDesk!.Apps, a => a.Key == "calc.exe");

        f.RunMinutes(2);
        Assert.True(f.Usage("calc.exe").Exhausted);

        var enforced = f.Host.EnforcedDesk!;
        Assert.DoesNotContain(enforced.Apps, a => a.Key == "calc.exe");
        Assert.Contains(enforced.Apps, a => a.Key == "notepad.exe");
    }

    [Fact]
    public void App_without_its_own_limit_is_never_dropped()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        f.RunMinutes(120);

        Assert.False(f.Usage("calc.exe").Exhausted);
        Assert.Contains(f.Host.EnforcedDesk!.Apps, a => a.Key == "calc.exe");
    }

    [Fact]
    public void Yesterday_exhausted_app_stays_blocked_when_guard_starts()
    {
        var calendar = new ManualCalendar { Today = Wednesday };
        var store = new AppUsageStore(AppUsagePath);
        store.Save(calendar.Today, new Dictionary<string, double> { ["calc.exe"] = 31 * 60 });

        using var f = Build(calcLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        // 首轮清场用的就必须是「有效书桌」，否则额度用完的软件一重启澄时就复活。
        Assert.NotEmpty(f.Enforcer.Swept);
        Assert.DoesNotContain(f.Enforcer.Swept[0].Apps, a => a.Key == "calc.exe");
    }

    [Fact]
    public void A_gap_longer_than_five_minutes_is_not_counted()
    {
        using var f = Build(calcLimit: null, TimeSpan.FromHours(24));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        // 休眠或服务长时间挂起后，中间那段不能算到孩子头上。
        f.Clock.Advance(TimeSpan.FromHours(8));
        f.Host.Tick();

        Assert.Equal(0, f.Usage("calc.exe").UsedMinutes);
    }

    [Fact]
    public void Usage_resets_on_a_new_day()
    {
        using var f = Build(calcLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();

        f.RunMinutes(10);
        Assert.Equal(10, f.Usage("calc.exe").UsedMinutes);

        f.Calendar.Today = Wednesday.AddDays(1);
        f.Clock.Advance(TimeSpan.FromHours(12));
        f.Host.Tick();

        Assert.Equal(0, f.Usage("calc.exe").UsedMinutes);
        Assert.Contains(f.Host.EnforcedDesk!.Apps, a => a.Key == "calc.exe");
    }

    [Fact]
    public void Usage_survives_a_restart_within_the_same_day()
    {
        var f = Build(calcLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();
        f.RunMinutes(10);
        f.Host.Dispose();

        var reloaded = new AppUsageStore(AppUsagePath).Load(Wednesday);
        Assert.True(reloaded.TryGetValue("calc.exe", out var seconds));
        Assert.Equal(10, (int)Math.Round(seconds / 60d));
    }

    [Fact]
    public void Changing_the_limit_takes_effect_immediately()
    {
        using var f = Build(calcLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["calc.exe"];
        f.Host.StartGuard();
        f.RunMinutes(10);

        // 家长把 30 分钟改成 5 分钟：已经用了 10 分钟，应立刻算超额。
        var desk = f.Host.Desks.Single(d => d.Id == TestDeskId);
        f.Host.SaveDesk(desk.WithAppLimit("calc.exe", 5));

        Assert.True(f.Usage("calc.exe").Exhausted);
        Assert.DoesNotContain(f.Host.EnforcedDesk!.Apps, a => a.Key == "calc.exe");
    }

    // ===== 「整个电脑」场景：不限软件，但可以给指定软件单独限时 =====

    [Fact]
    public void FullPc_exhausted_app_lands_in_the_deny_list()
    {
        using var f = BuildFullPc(gameLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["game.exe"];
        f.Host.StartGuard();

        f.RunMinutes(29);
        // 没用完之前谁都拦不着：拦截名单必须是空的，绝不能把「限时名单」当「拒绝名单」。
        Assert.Empty(f.Host.EnforcedDesk!.Apps);

        f.RunMinutes(2);
        Assert.True(f.Usage("game.exe").Exhausted);

        // 「整个电脑」书桌的名单语义翻转：里面只剩用完的软件（= 拒绝名单）。
        var enforced = f.Host.EnforcedDesk!;
        Assert.Contains(enforced.Apps, a => a.Key == "game.exe");
        Assert.DoesNotContain(enforced.Apps, a => a.Key == "notepad.exe");
    }

    [Fact]
    public void FullPc_app_without_a_limit_never_appears_in_the_deny_list()
    {
        using var f = BuildFullPc(gameLimit: null, TimeSpan.FromHours(6));
        f.Probe.Keys = ["game.exe", "notepad.exe"];
        f.Host.StartGuard();

        f.RunMinutes(120);

        Assert.Empty(f.Host.EnforcedDesk!.Apps);
        Assert.Equal(120, f.Usage("game.exe").UsedMinutes);
    }

    [Fact]
    public void FullPc_yesterday_exhausted_app_is_swept_when_guard_starts()
    {
        var calendar = new ManualCalendar { Today = Wednesday };
        var store = new AppUsageStore(AppUsagePath);
        store.Save(calendar.Today, new Dictionary<string, double> { ["game.exe"] = 31 * 60 });

        using var f = BuildFullPc(gameLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["game.exe"];
        f.Host.StartGuard();

        // 开守护的首轮清场拿的就是拒绝名单：昨天额度用完的软件不能因为重启澄时复活。
        Assert.NotEmpty(f.Enforcer.Swept);
        var swept = f.Enforcer.Swept[0];
        Assert.True(swept.Unrestricted);
        Assert.Contains(swept.Apps, a => a.Key == "game.exe");
        Assert.DoesNotContain(swept.Apps, a => a.Key == "notepad.exe");
    }

    [Fact]
    public void FullPc_removing_the_limit_re_allows_the_app()
    {
        using var f = BuildFullPc(gameLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["game.exe"];
        f.Host.StartGuard();
        f.RunMinutes(31);
        Assert.NotEmpty(f.Host.EnforcedDesk!.Apps);

        var desk = f.Host.Desks.Single(d => d.Id == TestDeskId);
        f.Host.SaveDesk(desk.WithAppLimit("game.exe", null));

        Assert.Empty(f.Host.EnforcedDesk!.Apps);
        Assert.False(f.Usage("game.exe").Exhausted);
    }

    [Fact]
    public void FullPc_usage_rows_carry_the_limit_for_the_dashboard()
    {
        using var f = BuildFullPc(gameLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["game.exe"];
        f.Host.StartGuard();
        f.RunMinutes(10);

        var row = f.Usage("game.exe");
        Assert.Equal("游戏", row.DisplayName);
        Assert.Equal(30, row.LimitMinutes);
        Assert.Equal(10, row.UsedMinutes);
        Assert.Equal("已用 10 分钟 / 限 30 分钟", row.Summary);
    }

    [Fact]
    public void FullPc_new_day_re_allows_the_app_and_resets_usage()
    {
        using var f = BuildFullPc(gameLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["game.exe"];
        f.Host.StartGuard();
        f.RunMinutes(31);
        Assert.NotEmpty(f.Host.EnforcedDesk!.Apps);

        f.Calendar.Today = Wednesday.AddDays(1);
        f.Clock.Advance(TimeSpan.FromHours(12));
        f.Host.Tick();

        Assert.Empty(f.Host.EnforcedDesk!.Apps);
        Assert.Equal(0, f.Usage("game.exe").UsedMinutes);
    }

    [Fact]
    public void FullPc_running_flag_follows_the_probe_and_clears_when_guard_stops()
    {
        using var f = BuildFullPc(gameLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["game.exe"];
        f.Host.StartGuard();
        f.RunMinutes(3);
        Assert.True(f.Usage("game.exe").Running);

        // 软件关掉后 Running 跟着灭——孩子端的临近提醒只对开着的软件弹。
        f.Probe.Keys = [];
        f.RunMinutes(1);
        Assert.False(f.Usage("game.exe").Running);

        // 守护结束后也不该有「正在使用」的残留。
        f.Probe.Keys = ["game.exe"];
        f.RunMinutes(1);
        f.Host.Stop("1234");
        Assert.False(f.Usage("game.exe").Running);
    }

    [Fact]
    public void FullPc_usage_lists_unlisted_apps_by_process_name()
    {
        using var f = BuildFullPc(gameLimit: 30, TimeSpan.FromHours(6));
        f.Probe.Keys = ["game.exe", "mspaint"];
        f.Host.StartGuard();
        f.RunMinutes(5);

        // 名单外的软件按进程名入列（无限额），统计页「今天」才和历史天数同口径。
        var row = f.Host.AppUsage.Single(r => r.Key == "mspaint");
        Assert.Equal(5, row.UsedMinutes);
        Assert.Null(row.LimitMinutes);
        Assert.Equal("mspaint", row.DisplayName);
    }
}

/// <summary>「整个电脑」场景的记账键映射：限时软件用 Key，其余用进程名。</summary>
public class ProcessRunningAppProbeKeyTests
{
    [Fact]
    public void Limited_apps_are_recorded_under_their_key()
    {
        var desk = BuiltinDesks.FullPc().WithApps(
        [
            new AllowedApp("游戏", "game", @"C:\Games\Bin\game.exe", 30),
            new AllowedApp("记事本", "notepad"),
        ]);
        var byStem = desk.Apps.ToDictionary(
            a => Path.GetFileNameWithoutExtension(a.FileName.Trim()),
            a => a.Key,
            StringComparer.OrdinalIgnoreCase);

        var keys = ProcessRunningAppProbe.MapKeys(byStem, ["game", "notepad", "mspaint"]);

        // 名单里的软件按自己的 Key 记账（限时判定认这个），名单外的按进程名。
        Assert.Contains(@"C:\Games\Bin\game.exe", keys);
        Assert.Contains("notepad.exe", keys);
        Assert.Contains("mspaint", keys);
        Assert.DoesNotContain("game", keys);
    }
}

public class AppUsageStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "chengshi-appusage-store-" + Guid.NewGuid().ToString("N"));

    public AppUsageStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    [Fact]
    public void Roundtrip_keeps_seconds_per_app()
    {
        var path = Path.Combine(_dir, "appusage.json");
        var store = new AppUsageStore(path);
        var date = new DateOnly(2026, 8, 19);
        store.Save(date, new Dictionary<string, double> { ["calc.exe"] = 600, ["notepad.exe"] = 90 });

        var loaded = new AppUsageStore(path).Load(date);

        Assert.Equal(600, loaded["calc.exe"]);
        Assert.Equal(90, loaded["notepad.exe"]);
    }

    [Fact]
    public void Another_day_reads_as_empty()
    {
        var path = Path.Combine(_dir, "appusage.json");
        var store = new AppUsageStore(path);
        var date = new DateOnly(2026, 8, 19);
        store.Save(date, new Dictionary<string, double> { ["calc.exe"] = 600 });

        Assert.Empty(new AppUsageStore(path).Load(date.AddDays(1)));
    }

    [Fact]
    public void Missing_file_reads_as_empty()
    {
        var store = new AppUsageStore(Path.Combine(_dir, "nope.json"));
        Assert.Empty(store.Load(new DateOnly(2026, 8, 19)));
    }

    [Fact]
    public void Broken_file_reads_as_empty_instead_of_throwing()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new AppUsageStore(path);
        Assert.Empty(store.Load(new DateOnly(2026, 8, 19)));
    }

    [Fact]
    public void Zero_seconds_are_dropped_on_save()
    {
        var path = Path.Combine(_dir, "appusage.json");
        var date = new DateOnly(2026, 8, 19);
        var store = new AppUsageStore(path);
        store.Save(date, new Dictionary<string, double> { ["calc.exe"] = 0, ["notepad.exe"] = 120 });

        var loaded = new AppUsageStore(path).Load(date);

        Assert.Single(loaded);
        Assert.Equal(120, loaded["notepad.exe"]);
    }
}
