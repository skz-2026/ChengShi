using System.Text.Json;
using Chengshi.Core;
using Chengshi.Ipc;

namespace Chengshi.Engine;

public sealed class SessionHost : ISessionControl
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SessionStateMachine _machine;
    private readonly IProcessEnforcer _enforcer;
    private readonly PollProcessWatcher _poll;
    private readonly EtwProcessWatcher _etw;
    private readonly DeskStore _store;
    private readonly FamilyStore _family;
    private readonly ScreenTimeStore _time;
    private readonly ILocalCalendar _calendar;
    private readonly IUnbiasedClock _clock;
    private readonly NetworkGuard _network;
    private readonly SitePolicyGuard _sites;
    private readonly UsageLogStore _usageLog;
    private readonly IRunningAppProbe _probe;
    private readonly AppUsageStore _appUsageStore;
    private readonly AppUsageTracker _appUsage = new();
    private readonly PinGate _pinGate;
    private readonly SmtpStore _smtpStore;
    private readonly PinRecoveryService _emailRecovery;
    private readonly IWorkstationLockProbe _lockProbe;
    private readonly Func<DateTime> _now;
    private int _blockedToday;
    private DateOnly _appUsageDate;
    private double _lastUsageSample;
    private double _nextUsagePersist;
    private double _nextEtwRetrySeconds;

    /// <summary>两次记账间隔超过这个值就丢弃：多半是系统休眠或守护被长时间挂起，补记会把用量算爆。</summary>
    private const double MaxUsageSampleSeconds = 300d;

    /// <summary>用量落盘的最短间隔，避免每秒写一次磁盘。</summary>
    private const double UsagePersistIntervalSeconds = 30d;

    /// <summary>时间到之后锁屏的节流间隔：避免每秒都弹锁屏，给家长解锁操作留时间。</summary>
    private const double LockRetrySeconds = 45d;

    /// <summary>家长验证过密码后的“宽限期”：期内不再强制锁屏，给家长从容加时或结束守护。</summary>
    private const double UnlockGraceMinutes = 3d;

    /// <summary>
    /// 时间到后留给孩子的「保存进度」宽限：期内书桌软件还能用（把游戏存档、文档保存完），
    /// 额度不再多扣（Remaining 已为 0），宽限结束才清场 + 锁屏。
    /// </summary>
    private static readonly TimeSpan SaveGrace = TimeSpan.FromMinutes(2);

    /// <summary>ETW 监听挂掉后的重试间隔。</summary>
    private const double EtwRetrySeconds = 30d;

    private DateTimeOffset _lastLockAttempt;
    private DateTimeOffset _unlockGraceUntil;

    public SessionHost(
        IUnbiasedClock? clock = null,
        DeskStore? store = null,
        FamilyStore? family = null,
        ILocalCalendar? calendar = null,
        ScreenTimeStore? time = null,
        IProcessEnforcer? enforcer = null,
        NetworkGuard? network = null,
        SitePolicyGuard? sites = null,
        UsageLogStore? usageLog = null,
        Func<DateTime>? now = null,
        IRunningAppProbe? probe = null,
        AppUsageStore? appUsageStore = null,
        SmtpStore? smtpStore = null,
        IEmailSender? emailSender = null,
        IWorkstationLockProbe? lockProbe = null)
    {
        _clock = clock ?? new QueryUnbiasedInterruptClock();
        _calendar = calendar ?? new SystemCalendar();
        _store = store ?? DeskStore.Load();
        _family = family ?? FamilyStore.Load();
        _network = network ?? new NetworkGuard();
        _sites = sites ?? new SitePolicyGuard();
        _usageLog = usageLog ?? new UsageLogStore();
        _probe = probe ?? new ProcessRunningAppProbe();
        _appUsageStore = appUsageStore ?? new AppUsageStore();
        _smtpStore = smtpStore ?? new SmtpStore();
        _lockProbe = lockProbe ?? new NullWorkstationLockProbe();
        // 测试注入发信器时直接用；否则从 SMTP 配置现取（家长中途改配置也能生效）。
        _emailRecovery = emailSender is null
            ? new PinRecoveryService(new ResolvingEmailSender(ResolveEmailSender))
            : new PinRecoveryService(emailSender);
        _pinGate = new PinGate(() => _clock.Elapsed.TotalSeconds);
        _now = now ?? (() => DateTime.Now);
        var limit = _family.Settings?.LimitFor(_calendar.Today) ?? TimeSpan.FromMinutes(60);
        _time = time ?? ScreenTimeStore.Load(_calendar, limit);
        _machine = new SessionStateMachine(_clock);
        _enforcer = enforcer ?? new ProcessEnforcer();
        _poll = new PollProcessWatcher(_enforcer, EffectiveDesk);
        _etw = new EtwProcessWatcher(_enforcer, EffectiveDesk);
        _appUsageDate = _calendar.Today;
        _appUsage.ReplaceWith(_appUsageStore.Load(_appUsageDate));
        _lastUsageSample = _clock.Elapsed.TotalSeconds;
        _nextUsagePersist = _lastUsageSample + UsagePersistIntervalSeconds;
        _enforcer.Blocked += process =>
        {
            Interlocked.Increment(ref _blockedToday);
            ProcessBlocked?.Invoke(new BlockedMessage(process.Pid, process.FileName, process.ImagePath));
        };
        _poll.Start();
        EtwEnabled = _etw.TryStart();
        EtwHint = EtwEnabled
            ? "ETW 进程创建已打开。"
            : (_etw.LastError ?? "ETW 未启用，使用轮询补漏。");
        RefreshNetwork();
        RefreshSitePolicy();
    }

    /// <summary>发信出口每次用时现取：家长中途改 SMTP 配置也能立即生效。</summary>
    private IEmailSender ResolveEmailSender()
    {
        var config = _smtpStore.Load();
        if (config is { IsComplete: true })
        {
            return new SmtpEmailSender(config);
        }

        throw new InvalidOperationException("还没有配置发信邮箱：请在家长设置里填好 SMTP（QQ/163/新浪）再使用邮箱找回。");
    }

    private sealed class ResolvingEmailSender(Func<IEmailSender> resolve) : IEmailSender
    {
        public Task SendVerificationCodeAsync(string toEmail, string code, CancellationToken ct = default) =>
            resolve().SendVerificationCodeAsync(toEmail, code, ct);
    }

    public IReadOnlyList<Desk> Desks => _store.Desks;
    public FamilySettings? Family => _family.Settings;
    public DailyBudget Budget => _time.Budget;

    /// <summary>今天每个软件用了多久；没在守护时按家长选中的书桌展示（通常是空用量）。</summary>
    public IReadOnlyList<AppUsage> AppUsage => BuildAppUsage();

    public bool IsConfigured => _family.Settings is not null;
    public bool IsGuarding => Snapshot.IsGuarding;
    public bool IsRemote => false;
    public bool EtwEnabled { get; private set; }
    public string EtwHint { get; private set; } = string.Empty;
    public string GuardHint { get; private set; } = string.Empty;
    public event Action<SessionSnapshot>? StateChanged;
    public event Action<BlockedMessage>? ProcessBlocked;

    // 本机宿主始终“连着”，不会断，所以这个事件永远不触发。
    event Action<bool>? ISessionControl.ConnectionChanged
    {
        add { }
        remove { }
    }

    public SessionSnapshot Snapshot => _machine.Snapshot();

    /// <summary>
    /// 真正交给拦截器的书桌（已剔除今天额度用完的软件）。
    /// 与 <see cref="Desks"/> 里的原始书桌区分开：诊断和测试用它能看清拦截口径。
    /// </summary>
    public Desk? EnforcedDesk => EffectiveDesk();

    public Desk? FindDesk(string id) => _store.Find(id) ?? BuiltinDesks.Find(id);

    public Desk SaveDesk(Desk desk)
    {
        var previous = _store.Find(desk.Id);
        var saved = _store.Upsert(desk);

        // 家长在守护中改了书桌（增删软件、改单独限额、改网站规则）时，
        // 正在进行的场次还抱着旧的书桌快照。按新配置重新开场，孩子的剩余时间不受影响。
        if (Snapshot.IsGuarding
            && Family is not null
            && _machine.Current?.Desk.Id == saved.Id
            && DeskSignature(previous) != DeskSignature(saved))
        {
            PersistUsage(Snapshot);
            _machine.Abandon();
            StartGuard();
        }

        return saved;
    }

    private static string DeskSignature(Desk? desk) =>
        desk is null ? "null" : JsonSerializer.Serialize(desk, Json);

    public FamilySettings SaveFamily(FamilySettings settings)
    {
        // 界面程序拿到的配置不含密码哈希/找回码；回存时用已存的补齐，
        // 避免一次普通设置修改把家长密码清掉。
        if (_family.Settings is { } current)
        {
            settings = settings.WithPreservedSecrets(current);
        }

        var saved = _family.Save(settings);
        _time.SyncLimit(saved.LimitFor(_calendar.Today));
        return saved;
    }

    public bool VerifyParentPin(string? pin)
    {
        if (_pinGate.IsLocked(out var retry))
        {
            throw new InvalidOperationException($"错误次数过多，请 {retry} 秒后再试。");
        }

        if (Family?.VerifyPin(pin) != true)
        {
            var locked = _pinGate.OnFailure();
            FileLog.Write("service", $"家长密码验证失败（锁定 {locked}s）。");
            return false;
        }

        _pinGate.OnSuccess();
        GrantUnlockGrace();
        return true;
    }

    public FamilySettings ChangePin(string oldPin, string newPin)
    {
        if (_pinGate.IsLocked(out var retry))
        {
            throw new InvalidOperationException($"错误次数过多，请 {retry} 秒后再试。");
        }

        if (Family is null)
        {
            throw new InvalidOperationException("还没有设置家长密码。");
        }

        if (!Family.VerifyPin(oldPin))
        {
            _pinGate.OnFailure();
            throw new ArgumentException("当前密码不对。");
        }

        _pinGate.OnSuccess();
        GrantUnlockGrace();
        return SaveFamilyAndRefreshSession(Family.WithNewPin(newPin));
    }

    public FamilySettings RecoverPin(string token, string newPin)
    {
        if (_pinGate.IsLocked(out var retry))
        {
            throw new InvalidOperationException($"错误次数过多，请 {retry} 秒后再试。");
        }

        if (Family is null)
        {
            throw new InvalidOperationException("还没有设置家长密码。");
        }

        if (!Family.MatchesRecovery(token))
        {
            _pinGate.OnFailure();
            throw new ArgumentException("找回码不对。");
        }

        _pinGate.OnSuccess();
        GrantUnlockGrace();
        var next = Family.WithNewPin(newPin);
        if (string.IsNullOrWhiteSpace(next.RecoveryCode))
        {
            next = next with { RecoveryCode = FamilySettings.NewRecoveryCode() };
        }

        return SaveFamilyAndRefreshSession(next);
    }

    public async Task SendEmailRecoveryCodeAsync(string email)
    {
        var reserved = Family?.RecoveryEmail;
        if (string.IsNullOrWhiteSpace(reserved))
        {
            throw new InvalidOperationException("还没有预留找回邮箱：请家长在设置里先填备用邮箱。");
        }

        if (!string.Equals(email.Trim(), reserved.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("这个邮箱和设置里预留的备用邮箱不一致。");
        }

        await _emailRecovery.RequestCodeAsync(reserved);
        FileLog.Write("service", $"找回验证码已发往预留邮箱（{MaskEmail(reserved)}）。");
    }

    public async Task<PinResetResult> RecoverPinWithEmailAsync(string email, string code, string newPin)
    {
        if (_pinGate.IsLocked(out var retry))
        {
            throw new InvalidOperationException($"错误次数过多，请 {retry} 秒后再试。");
        }

        var reserved = Family?.RecoveryEmail;
        if (string.IsNullOrWhiteSpace(reserved)
            || !string.Equals(email.Trim(), reserved.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("这个邮箱和设置里预留的备用邮箱不一致。");
        }

        if (!_emailRecovery.VerifyCode(reserved, code))
        {
            _pinGate.OnFailure();
            throw new ArgumentException("验证码不对或已过期（10 分钟有效）。");
        }

        if (Family is null)
        {
            throw new InvalidOperationException("还没有设置家长密码。");
        }

        _pinGate.OnSuccess();
        GrantUnlockGrace();

        // 走邮箱重置说明旧找回码可能已丢失，重设一枚新的，只在本次应答里给家长看。
        var next = Family.WithNewPin(newPin) with { RecoveryCode = FamilySettings.NewRecoveryCode() };
        var saved = SaveFamilyAndRefreshSession(next);
        FileLog.Write("service", $"家长密码已通过邮箱验证码重置（{MaskEmail(reserved)}）。");
        return new PinResetResult(saved, "密码已重置。", saved.RecoveryCode);
    }

    public Task<SmtpConfig?> GetSmtpAsync()
    {
        var config = _smtpStore.Load();
        // 授权码不下发：界面只看到「已存过密码」。
        return Task.FromResult<SmtpConfig?>(config is null
            ? null
            : config with { Password = string.Empty });
    }

    public Task SaveSmtpAsync(SmtpConfig config)
    {
        if (string.IsNullOrEmpty(config.Password))
        {
            // 密码留空 = 沿用已存的授权码（界面编辑时不回显密码）。
            var existing = _smtpStore.Load()?.Password ?? string.Empty;
            config = config with { Password = existing };
        }

        _smtpStore.Save(config);
        if (_smtpStore.LastError is { } error)
        {
            throw new InvalidOperationException("SMTP 配置没能写进磁盘：" + error);
        }

        FileLog.Write("service", "SMTP 发信配置已更新。");
        return Task.CompletedTask;
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        var name = email[..at];
        var head = name[..Math.Min(2, name.Length)];
        return $"{head}***{email[at..]}";
    }

    /// <summary>
    /// 磁盘上的配置被别的进程（如没连上服务的界面程序）改过后，重新读入；
    /// 变化大到影响守护时，按新配置重新开始守护。返回是否有变化。
    /// 读不到有效配置（文件被删/写坏）时保留内存里的设置继续守护——
    /// 守护不能因为孩子碰了配置文件就自己消失。
    /// </summary>
    public bool RefreshFromDisk()
    {
        var before = Signature();
        var previousFamily = _family.Settings;
        _family.Reload();
        _store.Reload();
        if (_family.Settings is null && previousFamily is not null)
        {
            // 磁盘配置没了（被删/写坏）但内存里还有：按内存配置把文件补回去，
            // 守护不能因为配置文件失踪就自己消失。
            _family.Save(previousFamily);
        }

        if (_family.Settings is { } family)
        {
            _time.SyncLimit(family.LimitFor(_calendar.Today));
        }

        var after = Signature();
        if (before == after)
        {
            return false;
        }

        if (Snapshot.IsGuarding)
        {
            PersistUsage(Snapshot);
            _machine.Abandon();
            if (_family.Settings is not null)
            {
                StartGuard();
            }
            else
            {
                RefreshNetwork();
                RefreshSitePolicy();
                StateChanged?.Invoke(Snapshot);
            }
        }
        else
        {
            RefreshNetwork();
            RefreshSitePolicy();
            StateChanged?.Invoke(Snapshot);
        }

        return true;
    }

    private string Signature()
    {
        var family = Family is null ? "null" : JsonSerializer.Serialize(Family, Json);
        return family + "|" + JsonSerializer.Serialize(Desks, Json);
    }

    private FamilySettings SaveFamilyAndRefreshSession(FamilySettings settings)
    {
        var saved = SaveFamily(settings);
        if (Snapshot.IsGuarding)
        {
            _machine.Abandon();
            StartGuard();
        }

        return saved;
    }

    public StartSessionResult StartGuard()
    {
        if (Family is null)
        {
            throw new InvalidOperationException("还没有设置家长密码。");
        }

        RolloverIfNewDay();
        var desk = FindDesk(Family.DeskId) ?? _store.Desks.FirstOrDefault() ?? BuiltinDesks.Homework();
        var result = _machine.StartParental(desk, _time.Budget.Remaining, Family.PinHash, SaveGrace);
        if (result.Status == StartSessionStatus.Started)
        {
            // 用「有效书桌」做首轮清场：昨天额度就用完的软件不该因为重启澄时而复活。
            var enforce = EffectiveDesk() ?? desk;
            _enforcer.SweepRunning(enforce);
        }

        RefreshNetwork();
        RefreshSitePolicy();
        StateChanged?.Invoke(result.Snapshot);
        if (result.Snapshot.IsGuarding && result.Snapshot.Phase == SessionPhase.TimeUp)
        {
            TryEnforceSessionLock();
        }

        return result;
    }

    /// <summary>时间到且正在守护时锁屏；节流避免反复弹锁，给家长解锁留操作时间。</summary>
    private void TryEnforceSessionLock()
    {
        var now = DateTimeOffset.UtcNow;
        // 宽限期内不锁屏：家长刚解锁，给足时间输入密码加时/结束守护，避免立刻又被弹回锁屏。
        if (now < _unlockGraceUntil)
        {
            return;
        }

        if ((now - _lastLockAttempt).TotalSeconds < LockRetrySeconds)
        {
            return;
        }

        _lastLockAttempt = now;
        SessionLocker.LockActiveSession();
    }

    /// <summary>家长密码验证通过后进入“宽限期”：期间不再自动锁屏，方便家长加时或结束守护。
    /// 只信服务端自己验证过的密码，不信任何客户端上报的「我解锁了」。</summary>
    private void GrantUnlockGrace() =>
        _unlockGraceUntil = DateTimeOffset.UtcNow.AddMinutes(UnlockGraceMinutes);

    public StartSessionResult Start(string deskId, TimeSpan duration, bool pinned, string? pin, TimeSpan grace = default)
    {
        var desk = FindDesk(deskId);
        if (desk is null)
        {
            return new StartSessionResult(StartSessionStatus.UnknownDesk, _machine.Snapshot());
        }

        if (desk.Apps.Count == 0 && !desk.Unrestricted)
        {
            throw new ArgumentException("先添加至少一款允许使用的软件。");
        }

        var result = _machine.Start(desk, duration, pinned, pin, grace);
        if (result.Status == StartSessionStatus.Started)
        {
            _enforcer.SweepRunning(desk);
        }

        RefreshNetwork();
        RefreshSitePolicy();
        StateChanged?.Invoke(result.Snapshot);
        return result;
    }

    public StopSessionResult Stop(string? pin)
    {
        PersistUsage(_machine.Snapshot());
        var result = _machine.Stop(pin);
        RefreshNetwork();
        RefreshSitePolicy();
        StateChanged?.Invoke(result.Snapshot);
        return result;
    }

    public SessionSnapshot Tick()
    {
        RolloverIfNewDay();
        var previous = _machine.Current;
        var snapshot = _machine.Tick();
        var elapsed = _clock.Elapsed.TotalSeconds;
        var delta = elapsed - _lastUsageSample;
        _lastUsageSample = elapsed;

        // 锁屏期间冻结记账：总时长把流逝的秒数还给场次，各软件用量也暂停累计。
        // 睡眠/休眠本来就由单调时钟天然排除，这里补上「锁屏人不在」的口径。
        // 退款要在快照/落账之前做，界面与「已用」看到的才是冻结后的口径。
        var locked = previous is { LockedOut: false }
            && snapshot.Phase == SessionPhase.InDesk
            && delta > 0
            && delta <= MaxUsageSampleSeconds
            && _lockProbe.IsLocked(ActiveSession.ConsoleSessionId);
        if (locked)
        {
            _machine.Extend(TimeSpan.FromSeconds(delta));
            snapshot = _machine.Snapshot();
        }

        SampleAppUsage(snapshot, delta, elapsed, locked);
        PersistUsage(snapshot);
        if (snapshot.Phase == SessionPhase.TimeUp
            && previous is { LockedOut: false }
            && _machine.Current?.Desk is { } lockdown)
        {
            _enforcer.SweepRunning(lockdown);
        }

        // 守护中且已超时：由服务直接锁屏，孩子杀掉客户端也绕不过去（节流重锁）。
        if (snapshot.IsGuarding && snapshot.Phase == SessionPhase.TimeUp)
        {
            TryEnforceSessionLock();
        }

        EnsureEtwAlive();
        RefreshNetwork();
        RefreshSitePolicy();
        StateChanged?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>ETW 内核监听可能中途挂掉（系统策略、资源压力）；挂了就周期性重开，并把提示刷新。</summary>
    private void EnsureEtwAlive()
    {
        if (!EtwEnabled || _etw.IsRunning)
        {
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        if (now < _nextEtwRetrySeconds)
        {
            return;
        }

        _nextEtwRetrySeconds = now + EtwRetrySeconds;
        if (_etw.TryStart())
        {
            EtwHint = "ETW 进程创建已重新打开。";
            FileLog.Write("service", "ETW 进程监听中断后已自动重启。");
        }
        else
        {
            EtwHint = _etw.LastError ?? "ETW 未启用，使用轮询补漏。";
        }
    }

    private void RefreshNetwork()
    {
        var snapshot = _machine.Snapshot();
        var desk = snapshot.Phase == SessionPhase.InDesk ? CurrentDesk() : null;

        // 断网 = 书桌要求断网，或守护期间处于睡觉时段（默认 22:00–07:00）。
        var bedtime = Family is { } familySettings
            && snapshot.IsGuarding
            && familySettings.IsBedtime(_now());
        var shouldBlock = desk?.DisconnectNetwork == true || bedtime;

        var hints = new List<string>();
        if (_network.Apply(shouldBlock))
        {
            if (bedtime)
            {
                hints.Add($"睡觉时段（{Family!.BedtimeStartHour}:00–{Family.BedtimeEndHour}:00）断网已生效。");
            }
            else if (shouldBlock)
            {
                hints.Add("断网已生效：网络出口已关闭，只留允许的软件。");
            }
        }
        else if (shouldBlock)
        {
            hints.Add($"断网没生效：{_network.LastError}。请安装并启动澄时守护服务。");
        }

        foreach (var writeError in new[] { _family.LastError, _store.LastError, _time.LastError }
                     .Where(e => e is not null)
                     .Select(e => e!))
        {
            hints.Add("配置没能写进磁盘（当前权限只读）：" + writeError);
        }

        GuardHint = string.Join(" ", hints);
    }

    /// <summary>把当前书桌的网站规则写进 Chrome/Edge 策略；会话结束即清除。</summary>
    private void RefreshSitePolicy()
    {
        var snapshot = _machine.Snapshot();
        var desk = snapshot.Phase == SessionPhase.InDesk ? CurrentDesk() : null;
        var allowed = desk?.AllowedSiteList ?? [];
        var blocked = desk is null ? [] : BuiltinSites.BlockedDomainsFor(desk);
        var spec = SitePolicyBuilder.Build(allowed, blocked);

        string? siteHint;
        if (_sites.Apply(spec))
        {
            siteHint = spec.IsEmpty
                ? null
                : allowed.Count > 0
                    ? $"网站白名单已生效：浏览器只能打开 {string.Join("、", allowed.Take(4))} 等 {allowed.Count} 个网站。"
                    : $"绿色上网已生效：已屏蔽 {blocked.Count} 个不良网站（Chrome / Edge）。";
        }
        else
        {
            siteHint = spec.IsEmpty
                ? null
                : $"网站规则没生效：{_sites.LastError}。请安装并启动澄时守护服务。";
        }

        if (siteHint is not null)
        {
            GuardHint = string.Join(" ", new[] { GuardHint, siteHint }.Where(h => !string.IsNullOrWhiteSpace(h)));
        }
    }

    private void RolloverIfNewDay()
    {
        if (Family is null)
        {
            return;
        }

        if (_time.Budget.Date == _calendar.Today)
        {
            return;
        }

        LogYesterday();
        var wasGuarding = _machine.Snapshot().IsGuarding;
        if (wasGuarding)
        {
            _machine.Abandon();
        }

        Interlocked.Exchange(ref _blockedToday, 0);
        ResetAppUsage();
        _time.Rollover(Family.LimitFor(_calendar.Today));
        if (wasGuarding)
        {
            StartGuard();
        }
    }

    /// <summary>跨天时把昨天用掉的时间、拦下的次数和各软件的分钟数写进用量日志。</summary>
    private void LogYesterday()
    {
        var yesterday = _time.Budget;
        var blockedCount = Volatile.Read(ref _blockedToday);
        var usedMinutes = (int)Math.Round(yesterday.Used.TotalMinutes);
        var apps = AppMinutesSnapshot();
        if (usedMinutes <= 0 && blockedCount <= 0)
        {
            return;
        }

        // 跨天瞬间 calendar 已指向今天，昨天的日期要从预算行拿。
        _usageLog.Append(new UsageDay(yesterday.Date, usedMinutes, blockedCount, apps));
    }

    /// <summary>当天各软件的分钟数快照（写进用量日志，统计页看历史分布用）。</summary>
    private IReadOnlyDictionary<string, int> AppMinutesSnapshot() =>
        _appUsage.Snapshot()
            .Where(pair => pair.Value > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => (int)Math.Round(pair.Value / 60d, MidpointRounding.AwayFromZero),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 家长批准加时：孩子来申请，家长当场输密码。正在书桌里就顺延场次；
    /// 已经锁到桌面就按新额度重新开场。加时的部分只在今天有效。
    /// </summary>
    public GrantExtraResult GrantExtra(string? pin, int minutes)
    {
        if (_pinGate.IsLocked(out var retry))
        {
            throw new InvalidOperationException($"错误次数过多，请 {retry} 秒后再试。");
        }

        if (Family is null)
        {
            throw new InvalidOperationException("还没有设置家长密码。");
        }

        if (!Family.VerifyPin(pin))
        {
            _pinGate.OnFailure();
            return new GrantExtraResult(false, "家长密码不对，没有加时。", _machine.Snapshot());
        }

        _pinGate.OnSuccess();
        GrantUnlockGrace();

        var amount = TimeSpan.FromMinutes(Math.Clamp(minutes, 5, ScreenTimeStore.MaxExtraMinutesPerGrant));
        var snapshot = _machine.Snapshot();
        if (snapshot.Phase == SessionPhase.TimeUp)
        {
            _time.GrantExtra(amount);
            _machine.Abandon();
            var desk = FindDesk(Family.DeskId) ?? BuiltinDesks.Homework();
            // 锁死状态下额度已耗尽，加时后的剩余时间就是刚批的部分。
            var remaining = _time.Budget.Remaining;
            var duration = remaining > amount ? remaining : amount;
            var result = _machine.StartParental(desk, duration, Family.PinHash, SaveGrace);
            if (result.Status != StartSessionStatus.Started)
            {
                return new GrantExtraResult(false, "没能重新开始守护，再试一次。", _machine.Snapshot());
            }
        }
        else if (snapshot.Phase == SessionPhase.InDesk)
        {
            // 场次时长与当天总额度同步上调，已用时间的记账公式保持一致。
            _machine.Extend(amount);
            _time.GrantExtra(amount);
        }
        else
        {
            return new GrantExtraResult(false, "现在没有在守护，不需要加时。", snapshot);
        }

        var granted = _machine.Snapshot();
        StateChanged?.Invoke(granted);
        return new GrantExtraResult(true, $"已经加了 {(int)amount.TotalMinutes} 分钟。", granted);
    }

    private void PersistUsage(SessionSnapshot snapshot)
    {
        if (Family is null)
        {
            return;
        }

        // 当天总额度 = 基础档 + 已批的加时；跨天瞬间的边界回落到基础档。
        var totalToday = _time.Budget.Date == _calendar.Today
            ? _time.Budget.Limit
            : Family.LimitFor(_calendar.Today);
        if (snapshot.Phase == SessionPhase.TimeUp)
        {
            _time.SaveUsed(totalToday);
            return;
        }

        if (snapshot.Phase == SessionPhase.InDesk)
        {
            var used = totalToday - snapshot.Remaining;
            _time.SaveUsed(used < TimeSpan.Zero ? TimeSpan.Zero : used);
        }
    }

    private Desk? CurrentDesk() => _machine.Current?.Desk;

    /// <summary>
    /// 交给拦截器的书桌，把「当天单独限时已用完」的软件处理成拦截器认识的口径：
    /// 白名单书桌把它们从允许名单里剔除（轮询/ETW 的既有逻辑会自然关掉名单外的）；
    /// 「整个电脑」书桌反过来只把它们留下——对这种书桌拦截器把名单当拒绝名单用，
    /// 其余软件照常放行。两边的记账（RunningKeys）都用原始书桌，不受这里影响。
    /// </summary>
    private Desk? EffectiveDesk()
    {
        var desk = _machine.Current?.Desk;
        if (desk is null)
        {
            return null;
        }

        var exhausted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in desk.Apps)
        {
            if (_appUsage.IsExhausted(app.Key, app.DailyMinutes))
            {
                exhausted.Add(app.Key);
            }
        }

        // 「整个电脑」书桌：名单翻转成拒绝名单——只留当天用完的软件，
        // 一个都没用完就交空名单（全放行）。注意不能把原始书桌直接交出去：
        // 它的名单里还有没用完的限时软件，拦截器会当成要关的。
        if (desk.Unrestricted)
        {
            return exhausted.Count == 0
                ? desk.WithApps([])
                : desk.WithApps(desk.Apps.Where(app => exhausted.Contains(app.Key)));
        }

        if (exhausted.Count == 0)
        {
            return desk;
        }

        return desk.WithApps(desk.Apps.Where(app => !exhausted.Contains(app.Key)));
    }

    private IReadOnlyList<AppUsage> BuildAppUsage()
    {
        var desk = _machine.Current?.Desk
            ?? (Family is null ? null : FindDesk(Family.DeskId));
        if (desk is null)
        {
            return [];
        }

        return desk.Apps
            .GroupBy(app => app.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var app = group.First();
                return new AppUsage(
                    app.Key,
                    app.DisplayName,
                    _appUsage.UsedMinutes(app.Key),
                    app.DailyMinutes);
            })
            .OrderByDescending(row => row.UsedMinutes)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 按真实流逝时间给「正在运行的被允许软件」记账。只在书桌会话进行中计，
    /// 与每天总额度的口径保持一致；锁屏期间暂停。
    /// </summary>
    private void SampleAppUsage(SessionSnapshot snapshot, double delta, double elapsed, bool locked)
    {
        if (delta <= 0)
        {
            return;
        }

        if (snapshot.Phase != SessionPhase.InDesk || locked || delta > MaxUsageSampleSeconds)
        {
            return;
        }

        var desk = snapshot.DeskId is null ? null : CurrentDesk();
        if (desk is null)
        {
            return;
        }

        foreach (var key in _probe.RunningKeys(desk))
        {
            _appUsage.Add(key, delta);
        }

        if (elapsed >= _nextUsagePersist)
        {
            PersistAppUsage();
            _nextUsagePersist = elapsed + UsagePersistIntervalSeconds;
        }
    }

    private void PersistAppUsage() =>
        _appUsageStore.Save(_appUsageDate, _appUsage.Snapshot());

    private void ResetAppUsage()
    {
        _appUsage.Reset();
        _appUsageDate = _calendar.Today;
        PersistAppUsage();
    }

    public void Dispose()
    {
        PersistUsage(_machine.Snapshot());
        PersistAppUsage();
        _poll.Dispose();
        _etw.Dispose();
        _network.Dispose();
        _sites.Dispose();
    }
}
