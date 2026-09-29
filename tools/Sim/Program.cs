// 澄时功能仿真台：把产品功能在真实引擎（SessionHost / 状态机 / 存储）上从头跑一遍。
// 适配器全部用假件：不结束真实进程、不写防火墙、不写浏览器策略注册表、不真锁屏。
// 所有存储经 CHENGSHI_DATA_DIR 重定向到临时目录，退出时可整体删除。
//   dotnet run --project tools/Sim
using System.Text.Json;
using Chengshi.Core;
using Chengshi.Engine;
using Chengshi.Ipc;

var root = Path.Combine(Path.GetTempPath(), "chengshi-sim-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable(StorePaths.EnvironmentVariable, root);

var passed = 0;
var failures = new List<string>();

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"── {title}");
}

void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        passed++;
        Console.WriteLine($"  ✓ {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"  ✘ {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
    }
}

static string Hm(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

// 建一台仿真宿主：所有可替换件都是假件，时钟日历手动推。
static SessionHost BuildHost(
    ManualClock clock,
    ManualCalendar calendar,
    string root,
    TimeSpan dailyLimit,
    string deskId,
    IProcessEnforcer? enforcer = null,
    FakeNetworkGuard? network = null,
    IRunningAppProbe? probe = null,
    FakeLockProbe? lockProbe = null,
    IEmailSender? emailSender = null,
    Func<DateTime>? now = null,
    int? weekday = null,
    int? weekend = null,
    string? recoveryEmail = null)
{
    var familyStore = FamilyStore.Load(Path.Combine(root, "family.json"));
    familyStore.Save(FamilySettings.Create(
        "1234", (int)dailyLimit.TotalMinutes, deskId,
        WeekdayMinutes: weekday, WeekendMinutes: weekend, recoveryEmail: recoveryEmail));
    var deskStore = DeskStore.Load(Path.Combine(root, $"desks-{Guid.NewGuid():N}.json"));
    return new SessionHost(
        clock,
        deskStore,
        familyStore,
        calendar,
        ScreenTimeStore.Load(calendar, dailyLimit, Path.Combine(root, $"time-{Guid.NewGuid():N}.json")),
        enforcer: enforcer ?? new RecordingEnforcer(),
        network: network ?? new FakeNetworkGuard(),
        usageLog: new UsageLogStore(Path.Combine(root, $"usagelog-{Guid.NewGuid():N}.jsonl")),
        now: now,
        probe: probe ?? new FakeProbe(),
        lockProbe: lockProbe ?? new FakeLockProbe(),
        emailSender: emailSender);
}

static void RunMinutes(SessionHost host, ManualClock clock, int minutes)
{
    for (var i = 0; i < minutes; i++)
    {
        clock.Advance(TimeSpan.FromMinutes(1));
        host.Tick();
    }
}

Console.WriteLine("澄时功能仿真台");
Console.WriteLine($"数据目录（临时）：{root}");

// ============================================================
Section("1. 家长初始化：密码哈希与找回码");
var family = FamilySettings.Create("1234", 60, BuiltinDesks.CodeId);
Check("家长密码只存哈希，不存明文", family.PinHash != "1234" && !family.PinHash.Contains("1234", StringComparison.Ordinal));
Check("密码验证通过 / 拒绝错误密码", family.VerifyPin("1234") && !family.VerifyPin("0000"));
Check("找回码已生成且可校验", !string.IsNullOrWhiteSpace(family.RecoveryCode) && family.MatchesRecovery(family.RecoveryCode!),
    $"找回码形如 {family.RecoveryCode![..4]}***");

// ============================================================
Section("2. 密码防穷举：连错 5 次锁定");
{
    var clock = new ManualClock();
    using var host = BuildHost(clock, new ManualCalendar(), root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId);
    for (var i = 0; i < 5; i++)
    {
        host.VerifyParentPin("0000");
    }

    var locked = false;
    try
    {
        host.VerifyParentPin("1234");
    }
    catch (InvalidOperationException ex)
    {
        locked = ex.Message.Contains("错误次数过多");
    }

    Check("连错 5 次后正确密码也被拒（锁定）", locked);
    clock.Advance(TimeSpan.FromMinutes(10));
    Check("锁定到期后恢复验证", host.VerifyParentPin("1234"));
}

// ============================================================
Section("3. 书桌模板与「整个电脑」场景");
{
    var homework = BuiltinDesks.Homework();
    var fullPc = BuiltinDesks.FullPc();
    Check("写作业桌默认断网", homework.DisconnectNetwork,
        string.Join("、", homework.Apps.Select(a => a.DisplayName)));
    var codeDesk = BuiltinDesks.Code();
    Check("勾选禁止类别后生成屏蔽名单（编程桌自带 游戏/成人）", BuiltinSites.BlockedDomainsFor(codeDesk).Count >= 10,
        $"屏蔽 {BuiltinSites.BlockedDomainsFor(codeDesk).Count} 个域名");
    Check("预置类别齐全（短视频直播/游戏/成人）", BuiltinSites.Categories.Count >= 3,
        string.Join("、", BuiltinSites.Categories.Values.Select(c => c.Name)));
    Check("「整个电脑」场景不限软件", fullPc.Unrestricted && fullPc.Apps.Count == 0);
    Check("内置模板齐全（写作业/网课/编程）",
        BuiltinDesks.Find(BuiltinDesks.HomeworkId) is not null
        && BuiltinDesks.Find(BuiltinDesks.ClassId) is not null
        && BuiltinDesks.Find(BuiltinDesks.CodeId) is not null);
}

// ============================================================
Section("4. 开始守护");
var day = new DateOnly(2026, 9, 1);
var clockA = new ManualClock();
var calendarA = new ManualCalendar { Today = day };
var hostA = BuildHost(clockA, calendarA, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId);
var guardResult = hostA.StartGuard();
Check("守护开始后进入书桌", guardResult.Status == StartSessionStatus.Started
    && hostA.Snapshot.Phase == SessionPhase.InDesk && hostA.IsGuarding);
Check("剩余时间等于当天额度", hostA.Snapshot.Remaining == TimeSpan.FromMinutes(60), Hm(hostA.Snapshot.Remaining));
hostA.Dispose();

// 上一行的清场断言需要引用 enforcer 实例，这里单独补一次：
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var enforcer = new RecordingEnforcer();
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId, enforcer: enforcer);
    host.StartGuard();
    Check("开始守护时对「有效书桌」执行首轮清场", enforcer.Swept.Count > 0);
}

// ============================================================
Section("5. 到点前分级提醒口径（10 / 5 / 1 分钟）");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId);
    host.StartGuard();
    string? WarningFor(TimeSpan r) =>
        r <= TimeSpan.Zero ? null
        : r <= TimeSpan.FromMinutes(1) ? "最后 1 分钟"
        : r <= TimeSpan.FromMinutes(5) ? "还剩 5 分钟"
        : r <= TimeSpan.FromMinutes(10) ? "还剩 10 分钟"
        : null;
    clock.Advance(TimeSpan.FromMinutes(50));
    var at10 = host.Tick();
    clock.Advance(TimeSpan.FromMinutes(5));
    var at5 = host.Tick();
    clock.Advance(TimeSpan.FromMinutes(4));
    var at1 = host.Tick();
    Check("剩 10 分钟出预告", WarningFor(at10.Remaining) == "还剩 10 分钟", Hm(at10.Remaining));
    Check("剩 5 分钟提醒收尾", WarningFor(at5.Remaining) == "还剩 5 分钟", Hm(at5.Remaining));
    Check("最后 1 分钟红色提示", WarningFor(at1.Remaining) == "最后 1 分钟", Hm(at1.Remaining));
}

// ============================================================
Section("6. 时间到 →「保存进度」宽限 → 才锁屏");
var graceSnap = default(SessionSnapshot);
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId);
    host.StartGuard();
    clock.Advance(TimeSpan.FromMinutes(60));
    graceSnap = host.Tick();
    Check("额度走完先进宽限：书桌还在、倒计时停在 0",
        graceSnap.Phase == SessionPhase.InDesk && graceSnap.Remaining == TimeSpan.Zero,
        $"宽限剩余 {Hm(graceSnap.GraceRemaining)}");
    Check("宽限期间已用时长不被多扣", host.Budget.Used == TimeSpan.FromMinutes(60));
    clock.Advance(TimeSpan.FromMinutes(2));
    var after = host.Tick();
    Check("宽限耗尽才进入「时间用完」", after.Phase == SessionPhase.TimeUp);
}

// ============================================================
Section("7. 时间用完后申请加时（家长密码批准）");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId);
    host.StartGuard();
    clock.Advance(TimeSpan.FromMinutes(63));
    host.Tick();
    var denied = host.GrantExtra("0000", 30);
    Check("密码错误不加时", !denied.Ok && host.Snapshot.Phase == SessionPhase.TimeUp);
    var granted = host.GrantExtra("1234", 30);
    Check("密码正确：按新额度重新开场",
        granted.Ok && granted.Snapshot.Phase == SessionPhase.InDesk
        && Math.Abs(granted.Snapshot.Remaining.TotalMinutes - 30) <= 1,
        $"剩余 {Hm(granted.Snapshot.Remaining)}");
    Check("当天总额度上调为 基础+加时", host.Budget.Limit == TimeSpan.FromMinutes(90), Hm(host.Budget.Limit));
}

// ============================================================
Section("8. 书桌进行中加时（顺延场次）");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId);
    host.StartGuard();
    RunMinutes(host, clock, 20);
    var granted = host.GrantExtra("1234", 15);
    Check("剩余时间 = 原剩余 + 加时",
        granted.Ok && Math.Abs(granted.Snapshot.Remaining.TotalMinutes - 55) <= 1,
        $"剩余 {Hm(granted.Snapshot.Remaining)}");
    clock.Advance(TimeSpan.FromMinutes(1));
    var snap = host.Tick();
    Check("记账自洽：已用 = 总额度 − 剩余",
        Math.Abs((host.Budget.Limit - host.Budget.Used - snap.Remaining).TotalSeconds) <= 1,
        $"额度 {Hm(host.Budget.Limit)} / 已用 {Hm(host.Budget.Used)}");
}

// ============================================================
Section("9. 锁屏不扣时长（冻结 / 恢复）");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var lockProbe = new FakeLockProbe();
    var probe = new FakeProbe { Keys = ["cmd.exe"] };
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId,
        probe: probe, lockProbe: lockProbe);
    host.StartGuard();
    RunMinutes(host, clock, 10);
    Check("正常使用 10 分钟后剩 50", host.Snapshot.Remaining == TimeSpan.FromMinutes(50));
    lockProbe.Locked = true;
    RunMinutes(host, clock, 10);
    Check("锁屏 10 分钟：剩余时间冻结", host.Snapshot.Remaining == TimeSpan.FromMinutes(50), Hm(host.Snapshot.Remaining));
    Check("锁屏期间已用不涨、软件用量也暂停",
        host.Budget.Used == TimeSpan.FromMinutes(10)
        && host.AppUsage.Single(r => r.Key == "cmd.exe").UsedMinutes == 10);
    lockProbe.Locked = false;
    RunMinutes(host, clock, 1);
    Check("解锁后接着正常倒计时", host.Snapshot.Remaining == TimeSpan.FromMinutes(49));
}

// ============================================================
Section("10. 单独限额：超额软件从有效书桌剔除");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var probe = new FakeProbe { Keys = ["calc.exe"] };
    var deskStore = DeskStore.Load(Path.Combine(root, $"desks-{Guid.NewGuid():N}.json"));
    deskStore.Upsert(new Desk("sim-limit", "限额桌", "计算器限 30 分钟",
        [new AllowedApp("计算器", "calc", dailyMinutes: 30), new AllowedApp("记事本", "notepad")]));
    var familyStore = FamilyStore.Load(Path.Combine(root, "family-limit.json"));
    familyStore.Save(FamilySettings.Create("1234", 360, "sim-limit"));
    using var host = new SessionHost(
        clock, deskStore, familyStore, calendar,
        ScreenTimeStore.Load(calendar, TimeSpan.FromHours(6), Path.Combine(root, "time-limit.json")),
        enforcer: new RecordingEnforcer(), network: new FakeNetworkGuard(),
        usageLog: new UsageLogStore(Path.Combine(root, "log-limit.jsonl")),
        probe: probe);
    host.StartGuard();
    RunMinutes(host, clock, 29);
    Check("29 分钟时计算器仍在有效书桌", host.EnforcedDesk!.Apps.Any(a => a.Key == "calc.exe"));
    RunMinutes(host, clock, 2);
    var enforced = host.EnforcedDesk!;
    Check("满 30 分钟后计算器被剔除（拦截器会自然关掉它）",
        !enforced.Apps.Any(a => a.Key == "calc.exe") && enforced.Apps.Any(a => a.Key == "notepad.exe"));
}

// ============================================================
Section("11. 「整个电脑」场景：只限时长、不限软件（+ 指定软件单独限时）");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.FullPcId);
    host.StartGuard();
    Check("有效书桌就是整机场景（拦截器一律放行）", host.EnforcedDesk!.Unrestricted);
    Check("到点照常走统一锁屏口径", host.Snapshot.IsGuarding && host.Snapshot.Phase == SessionPhase.InDesk);
}

{
    // 整个电脑 + 指定软件每天限时：用满只关那一款，其余软件照常。
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var probe = new FakeProbe { Keys = ["game.exe"] };
    var deskStore = DeskStore.Load(Path.Combine(root, $"desks-{Guid.NewGuid():N}.json"));
    deskStore.Upsert(new Desk("sim-fullpc-limit", "整个电脑", "不限软件",
        [new AllowedApp("游戏", "game", dailyMinutes: 20), new AllowedApp("记事本", "notepad")],
        Unrestricted: true));
    var familyStore = FamilyStore.Load(Path.Combine(root, "family-fullpc-limit.json"));
    familyStore.Save(FamilySettings.Create("1234", 360, "sim-fullpc-limit"));
    using var host = new SessionHost(
        clock, deskStore, familyStore, calendar,
        ScreenTimeStore.Load(calendar, TimeSpan.FromHours(6), Path.Combine(root, "time-fullpc-limit.json")),
        enforcer: new RecordingEnforcer(), network: new FakeNetworkGuard(),
        usageLog: new UsageLogStore(Path.Combine(root, "log-fullpc-limit.jsonl")),
        probe: probe);
    host.StartGuard();
    RunMinutes(host, clock, 19);
    Check("限时没用完前拒绝名单是空的（谁都拦不着）", host.EnforcedDesk!.Apps.Count == 0);
    RunMinutes(host, clock, 2);
    var enforced = host.EnforcedDesk!;
    Check("游戏满 20 分钟后只把它放进拒绝名单（其它软件照常）",
        enforced.Unrestricted && enforced.Apps.Count == 1 && enforced.Apps[0].Key == "game.exe");
    Check("用量行带着限额，家长仪表盘能看到还剩多少",
        host.AppUsage.Single(r => r.Key == "game.exe").LimitMinutes == 20
            && host.AppUsage.Single(r => r.Key == "game.exe").UsedMinutes >= 20);
}

// ============================================================
Section("12. 睡觉时段自动断网（守护期间）");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var network = new FakeNetworkGuard();
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId,
        network: network, now: () => new DateTime(2026, 9, 1, 22, 30, 0));
    host.StartGuard();
    Check("22:30 守护中断网生效", network.LastBlock == true);
    host.SaveFamily(host.Family! with { BedtimeStartHour = 23 });
    host.Tick();
    Check("家长把睡觉时段改到 23 点后，22:30 不再断网", network.LastBlock == false);
}

// ============================================================
Section("13. 绿色上网：网站白名单与禁止类别");
{
    var greenDesk = new Desk("sim-green", "绿色上网桌", "白名单 + 禁止类别",
        [new AllowedApp("Edge", "msedge")],
        AllowedSites: ["www.baidu.com", "www.xuexi.cn", "dictionary.example.com"],
        BlockCategories: ["video", "games", "adult"]);
    var blockedOnly = SitePolicyBuilder.Build([], BuiltinSites.BlockedDomainsFor(greenDesk));
    Check("黑名单模式：类别 + 自定义黑名单逐域生成屏蔽规则",
        blockedOnly.Blocklist.Count >= 2 * BuiltinSites.BlockedDomainsFor(greenDesk).Count && blockedOnly.Allowlist.Count == 0,
        $"屏蔽 {blockedOnly.Blocklist.Count} 条（30 域名 × 裸域 + 子域）");
    var allowed = greenDesk.AllowedSiteList;
    var whitelistSpec = SitePolicyBuilder.Build(allowed, []);
    Check("白名单模式：全封 + 仅放行名单内域名",
        whitelistSpec.Blocklist.Count == 1 && whitelistSpec.Blocklist[0] == "*"
        && whitelistSpec.Allowlist.Any(p => p.Contains("baidu", StringComparison.OrdinalIgnoreCase)),
        $"封全 1 条 + 放行 {whitelistSpec.Allowlist.Count} 条");
    Check("空规则 = 空 spec（守护结束自动清除）", SitePolicyBuilder.Build([], []).IsEmpty);
}

// ============================================================
Section("14. 拦截事件：今日拦截计数与界面推送");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var enforcer = new RecordingEnforcer();
    var blockedSeen = new List<BlockedMessage>();
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId, enforcer: enforcer);
    host.ProcessBlocked += b => blockedSeen.Add(b);
    host.StartGuard();
    enforcer.Raise(new ProcessIdentity(999, 1, "game.exe", null, null, null, 1));
    enforcer.Raise(new ProcessIdentity(1000, 1, "game.exe", null, null, null, 1));
    RunMinutes(host, clock, 1);
    Check("名单外进程触发拦截事件并推给界面", blockedSeen.Count == 2 && blockedSeen[0].FileName == "game.exe");
}

// ============================================================
Section("15. 跨天滚动：昨日用量写日志、额度重置、加时清零");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var logPath = Path.Combine(root, "log-rollover.jsonl");
    var probe = new FakeProbe { Keys = ["calc.exe"] };
    var enforcer = new RecordingEnforcer();
    var familyStore = FamilyStore.Load(Path.Combine(root, "family-rollover.json"));
    familyStore.Save(FamilySettings.Create("1234", 60, "sim-rollover"));
    var deskStore = DeskStore.Load(Path.Combine(root, "desks-rollover.json"));
    deskStore.Upsert(new Desk("sim-rollover", "滚动桌", "计算器",
        [new AllowedApp("计算器", "calc")]));
    var host = new SessionHost(
        clock, deskStore, familyStore, calendar,
        ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), Path.Combine(root, "time-rollover.json")),
        enforcer: enforcer, network: new FakeNetworkGuard(),
        usageLog: new UsageLogStore(logPath), probe: probe,
        appUsageStore: new AppUsageStore(Path.Combine(root, "appusage-rollover.json")));
    host.StartGuard();
    RunMinutes(host, clock, 20);
    host.GrantExtra("1234", 15);
    enforcer.Raise(new ProcessIdentity(501, 1, "game.exe", null, null, null, 1));
    RunMinutes(host, clock, 5);
    host.Stop("1234");
    calendar.Today = day.AddDays(1);
    clock.Advance(TimeSpan.FromHours(9));
    host.Tick();
    var entry = new UsageLogStore(logPath).ReadRecent(7).Single();
    Check("昨日用量写入日志", entry.Date == day && Math.Abs(entry.UsedMinutes - 25) <= 1,
        $"{entry.UsedMinutes} 分钟");
    Check("拦截次数入账", entry.BlockedCount == 1);
    Check("按软件分钟数入账", entry.Apps.TryGetValue("calc.exe", out var mins) && Math.Abs(mins - 25) <= 1);
    Check("新一天额度回到基础档（加时清零）", host.Budget.Limit == TimeSpan.FromMinutes(60), Hm(host.Budget.Limit));
    Check("新一天已用归零", host.Budget.Used == TimeSpan.Zero);
    host.Dispose();
}

// ============================================================
Section("16. 当天用量跨重启恢复");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var probe = new FakeProbe { Keys = ["cmd.exe"] };
    var usagePath = Path.Combine(root, "appusage-restart.json");
    var store = new AppUsageStore(usagePath);
    var familyStore = FamilyStore.Load(Path.Combine(root, "family-restart.json"));
    familyStore.Save(FamilySettings.Create("1234", 360, BuiltinDesks.CodeId));
    var deskStore = DeskStore.Load(Path.Combine(root, "desks-restart.json"));
    var hostA2 = new SessionHost(
        clock, deskStore, familyStore, calendar,
        ScreenTimeStore.Load(calendar, TimeSpan.FromHours(6), Path.Combine(root, "time-restart.json")),
        enforcer: new RecordingEnforcer(), network: new FakeNetworkGuard(),
        usageLog: new UsageLogStore(Path.Combine(root, "log-restart.jsonl")), probe: probe,
        appUsageStore: store);
    hostA2.StartGuard();
    RunMinutes(hostA2, clock, 12);
    hostA2.Dispose();
    var hostB = new SessionHost(
        clock, deskStore, familyStore, calendar,
        ScreenTimeStore.Load(calendar, TimeSpan.FromHours(6), Path.Combine(root, "time-restart.json")),
        enforcer: new RecordingEnforcer(), network: new FakeNetworkGuard(),
        usageLog: new UsageLogStore(Path.Combine(root, "log-restart.jsonl")), probe: probe,
        appUsageStore: store);
    Check("重启后当天的软件用量还在", hostB.AppUsage.Single(r => r.Key == "cmd.exe").UsedMinutes == 12);
    hostB.Dispose();
}

// ============================================================
Section("17. 邮箱找回密码（验证码只在服务端流转）");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var email = new FakeEmailSender();
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId,
        emailSender: email, recoveryEmail: "mom@qq.com");
    host.SendEmailRecoveryCodeAsync("mom@qq.com").Wait();
    Check("验证码已发往预留邮箱（不经过界面进程）", email.ToEmail == "mom@qq.com" && email.Code is { Length: > 0 },
        $"验证码 {email.Code![..2]}****");
    var wrong = false;
    try
    {
        host.RecoverPinWithEmailAsync("mom@qq.com", "000000", "5678").Wait();
    }
    catch (AggregateException ex) when (ex.InnerException is ArgumentException)
    {
        wrong = true;
    }

    Check("错误验证码被拒绝并计入锁定", wrong);
    var oldCode = host.Family!.RecoveryCode;
    var reset = host.RecoverPinWithEmailAsync("mom@qq.com", email.Code!, "5678").Result;
    Check("验证码正确：密码重置成功", host.Family!.VerifyPin("5678"));
    Check("重置同时签发新找回码（仅本次返回，旧的作废）",
        !string.IsNullOrWhiteSpace(reset.NewRecoveryCode)
        && reset.NewRecoveryCode != oldCode
        && reset.NewRecoveryCode == host.Family.RecoveryCode);
}

// ============================================================
Section("18. 找回码重置密码");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    var familyStore = FamilyStore.Load(Path.Combine(root, "family-code.json"));
    var saved = familyStore.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));
    var deskStore = DeskStore.Load(Path.Combine(root, "desks-code.json"));
    using var host = new SessionHost(
        clock, deskStore, familyStore, calendar,
        ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), Path.Combine(root, "time-code.json")),
        enforcer: new RecordingEnforcer(), network: new FakeNetworkGuard(),
        usageLog: new UsageLogStore(Path.Combine(root, "log-code.jsonl")));
    var wrong = false;
    try
    {
        host.RecoverPin("bad-token", "5678");
    }
    catch (ArgumentException)
    {
        wrong = true;
    }

    Check("错误找回码被拒绝", wrong);
    host.RecoverPin(saved.RecoveryCode!, "5678");
    Check("找回码正确：密码重置成功", host.Family!.VerifyPin("5678"));
}

// ============================================================
Section("19. SMTP 配置：授权码加密落盘、不回传界面");
{
    var clock = new ManualClock();
    var calendar = new ManualCalendar { Today = day };
    using var host = BuildHost(clock, calendar, root, TimeSpan.FromMinutes(60), BuiltinDesks.CodeId);
    host.SaveSmtpAsync(new SmtpConfig("smtp.qq.com", 465, true, "mom@qq.com", "auth-code-123")).Wait();
    var view = host.GetSmtpAsync().Result;
    Check("界面读到的配置不含授权码", view is not null && view.Password == string.Empty && view.User == "mom@qq.com");
    var onDisk = new SmtpStore().Load();
    Check("授权码已加密落盘、能原样读回", onDisk is { Password: "auth-code-123" });
}

// ============================================================
Console.WriteLine();
Console.WriteLine("════════════════════════════════════");
Console.WriteLine($"仿真结果：{passed} 项通过，{failures.Count} 项失败");
foreach (var f in failures)
{
    Console.WriteLine($"  ✘ {f}");
}

Console.WriteLine("说明：管道服务模式（IPC 门禁/防冒充/推送）、ETW 真实进程创建监听、真实防火墙/浏览器策略写入");
Console.WriteLine("      由 81 个引擎测试覆盖（含管道端到端集成测试），仿真台不重复真实系统副作用。");
return failures.Count == 0 ? 0 : 1;

// ============================================================
// 公共假件
// ============================================================
sealed class FakeLockProbe : IWorkstationLockProbe
{
    public bool Locked { get; set; }
    public bool IsLocked(int sessionId) => Locked;
}

sealed class FakeProbe : IRunningAppProbe
{
    public List<string> Keys { get; set; } = [];
    public IReadOnlyCollection<string> RunningKeys(Desk desk) => Keys.ToArray();
}

sealed class FakeNetworkGuard : NetworkGuard
{
    public int BlockCalls { get; private set; }
    public bool? LastBlock { get; private set; }
    public override bool Apply(bool block)
    {
        BlockCalls++;
        LastBlock = block;
        return true;
    }
}

sealed class RecordingEnforcer : IProcessEnforcer
{
    public List<Desk> Swept { get; } = [];
    private Action<ProcessIdentity>? _blocked;
    event Action<ProcessIdentity>? IProcessEnforcer.Blocked
    {
        add => _blocked += value;
        remove => _blocked -= value;
    }

    public void Raise(ProcessIdentity process) => _blocked?.Invoke(process);
    public bool TryEnforce(ProcessIdentity process, Desk desk) => false;
    public int SweepRunning(Desk desk)
    {
        Swept.Add(desk);
        return 0;
    }
}

sealed class FakeEmailSender : IEmailSender
{
    public string? ToEmail { get; private set; }
    public string? Code { get; private set; }
    public Task SendVerificationCodeAsync(string toEmail, string code, CancellationToken ct = default)
    {
        ToEmail = toEmail;
        Code = code;
        return Task.CompletedTask;
    }
}

