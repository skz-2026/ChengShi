using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Chengshi.Core;
using Chengshi.Engine;
using Chengshi.Ipc;
using Microsoft.Web.WebView2.Core;

namespace Chengshi.App;

/// <summary>
/// 主窗口：WPF 只提供无边框外壳，全部界面由 wwwroot\ 下的网页经 WebView2 渲染。
/// C# 侧持有守护引擎与配置（唯一事实来源），通过
///   C# → JS：PostWebMessageAsJson({type, payload}) 推状态；
///   JS → C#：postMessage({cmd, args})  发命令。
/// 网页每秒收到一次 state/dashboard；表单按节推送（form.*），避免打字时整页重排。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ISessionControl _host;
    private readonly DispatcherTimer _timer;
    private readonly List<string> _blocked = [];
    private readonly string? _startupHint;
    private const string FeedbackEmail = "sakz886@sina.com";
    private readonly UsageLogStore _usageLog = new();
    private DateTime _weekUsageNextRead = DateTime.MinValue;
    private int _weekdayMinutes = 60;
    private int _weekendMinutes = 120;
    private Dictionary<DayOfWeek, int>? _schedule;
    private readonly DayOfWeek[] _weekOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };
    private bool _parentUnlocked;
    private string _recoveryEmail = string.Empty;
    private bool _exitAllowed;
    private DateTime _lastBreakTick = DateTime.Now;
    private TimeSpan _breakAccum;
    private DateTime _lastBreakShown = DateTime.MinValue;
    private int _breakReminderMinutes;

    // ===== 家长表单的 C# 侧状态（网页只是它的视图） =====
    private string _selectedDeskId = string.Empty;
    private bool _weekendTabActive;
    private bool _startWithWindows = true;
    private bool _guardOnLaunch = true;
    private bool _bedtime = true;
    private string _mailHost = string.Empty;
    private string _mailPort = string.Empty;
    private bool _mailSsl = true;
    private string _mailUser = string.Empty;

    // 「整个电脑」场景下家长用软件选择器挑好、还没填分钟数的候选；
    // 换书桌就作废——它们只服务于给当前书桌加单软件限额。
    private readonly Dictionary<string, AllowedApp> _pendingLimitPicks = new(StringComparer.OrdinalIgnoreCase);

    // 提示语（旧 XAML 里 ParentHint / DashboardHintText / SpikeHintText 的对应物）
    private string _parentHint = string.Empty;
    private string _dashHint = string.Empty;
    private string _spikeHint = string.Empty;
    private string _recoveryHint = "忘记密码时用，请抄下来或点「复制」存好。";
    private string _childHintOverride = string.Empty;

    // ===== 统计页 =====
    private int _statsRangeDays = 7;

    // ===== WebView =====
    private bool _webReady;
    private List<object>? _weekRowsCache;
    private bool _weekEmptyCache = true;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    internal AppTray? Tray { get; set; }

    public bool IsFamilyConfigured => _host.IsConfigured;

    public MainWindow(ISessionControl host, string? startupHint = null)
    {
        InitializeComponent();
        AppIcon.Apply(this);
        _host = host;
        _startupHint = startupHint;

        var family = _host.Family;
        _weekdayMinutes = family?.WeekdayMinutes ?? family?.DailyMinutes ?? 60;
        _weekendMinutes = family?.WeekendMinutes ?? family?.DailyMinutes ?? 120;
        _schedule = family?.Schedule is { } dict && dict.Count > 0
            ? new Dictionary<DayOfWeek, int>(dict)
            : null;
        _startWithWindows = family?.StartWithWindows ?? true;
        _guardOnLaunch = family?.GuardOnLaunch ?? true;
        _bedtime = family?.BedtimeEnabled ?? true;
        _breakReminderMinutes = family?.BreakReminderMinutes ?? 0;
        _recoveryEmail = family?.RecoveryEmail ?? string.Empty;
        _selectedDeskId = family?.DeskId
            ?? _host.Desks.FirstOrDefault(d => d.Id is not BuiltinDesks.SpikeId and not BuiltinDesks.LockdownId)?.Id
            ?? string.Empty;

        _host.ProcessBlocked += OnBlocked;
        _host.ConnectionChanged += OnConnectionChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
        _lastBreakTick = DateTime.Now;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        RestoreWindowBounds();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _host.ProcessBlocked -= OnBlocked;
            _host.ConnectionChanged -= OnConnectionChanged;
            _host.Dispose();
            System.Windows.Application.Current?.Shutdown();
        };

        if (_host.IsConfigured && _host.Family?.GuardOnLaunch == true)
        {
            try
            {
                _host.StartGuard();
            }
            catch (Exception ex)
            {
                _parentHint = ex.Message;
            }
        }

        if (family is { } f)
        {
            StartupRegistration.Apply(f.StartWithWindows);
        }

        _dashHint = _startupHint ?? string.Empty;
        _parentHint = EngineHint(_startupHint);

        Loaded += async (_, _) => await InitWebAsync();
    }

    // ============================================================
    // WebView 初始化与消息通道
    // ============================================================
    private async Task InitWebAsync()
    {
        try
        {
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Chengshi", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.SetVirtualHostNameToFolderMapping("chengshi.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += (_, e) =>
                FileLog.Error("app", "WebView2 进程异常退出。", new Exception(e.ProcessFailedKind.ToString()));
            core.Navigate("https://chengshi.local/index.html");
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            FileLog.Error("app", "WebView2 运行时缺失。", ex);
            var choice = System.Windows.MessageBox.Show(
                "澄时的界面需要 Microsoft Edge WebView2 运行时（Win10/11 一般自带）。\n\n"
                + "点「确定」打开官方下载页，安装完成后重新打开澄时即可。",
                "澄时", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (choice == MessageBoxResult.OK)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
                    {
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex2)
                {
                    FileLog.Error("app", "打开 WebView2 下载页失败。", ex2);
                }
            }

            System.Windows.Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            FileLog.Error("app", "WebView2 初始化失败。", ex);
            System.Windows.MessageBox.Show("界面加载失败：" + ex.Message, "澄时", MessageBoxButton.OK, MessageBoxImage.Error);
            System.Windows.Application.Current?.Shutdown();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var cmd = root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
            var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.Clone()
                : (JsonElement?)null;
            HandleCommand(cmd, args);
        }
        catch (Exception ex)
        {
            FileLog.Error("app", "网页命令处理失败。", ex);
        }
    }

    /// <summary>向网页推一条消息。WebView 未就绪时静默丢弃（state 每秒都会补推）。</summary>
    private void Push(string type, object? payload = null)
    {
        if (!_webReady)
        {
            return;
        }

        var envelope = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["payload"] = payload,
        }, JsonOpts);
        Web.CoreWebView2.PostWebMessageAsJson(envelope);
    }

    private void PushHint(string slot, string text) => Push("hint", new { slot, text });

    /// <summary>
    /// 全局轻提示：操作成功的短确认（tone=ok）或需要马上注意的短提醒（tone=warn）。
    /// 页面底部的 hint 槽位仍然保留——那里放持久状态（断线、保存失败原因），
    /// toast 只负责「刚才那一下操作的结果」。
    /// </summary>
    private void PushToast(string text, string tone = "ok") => Push("toast", new { text, tone });

    private void PushAll()
    {
        var snapshot = _host.Snapshot;
        PushState(snapshot);
        PushDashboard(snapshot);
        PushFormAll();
        PushHelp();
    }

    // ============================================================
    // JS → C# 命令
    // ============================================================
    private void HandleCommand(string? cmd, JsonElement? args)
    {
        switch (cmd)
        {
            case null:
                break;
            case "ready":
                _webReady = true;
                PushAll();
                break;
            case "min":
                HideToTray();
                break;
            case "close":
                Close();
                break;
            case "dashboardGuard":
                DashboardGuard();
                break;
            case "startGuard":
                StartGuard(args);
                break;
            case "reward":
                Reward();
                break;
            case "deskSelect":
                SelectDesk(ArgStr(args, "id"));
                break;
            case "preset":
                ApplyPreset(ArgStr(args, "desk"), ArgInt(args, "minutes"));
                break;
            case "addApps":
                AddApps();
                break;
            case "removeApp":
                RemoveApp(ArgStr(args, "key"));
                break;
            case "addAllowedSite":
                AddAllowedSite(ArgStr(args, "text"));
                break;
            case "removeAllowedSite":
                RemoveAllowedSite(ArgStr(args, "site"));
                break;
            case "addBlockedSite":
                AddBlockedSite(ArgStr(args, "text"));
                break;
            case "removeBlockedSite":
                RemoveBlockedSite(ArgStr(args, "site"));
                break;
            case "setCategories":
                SetCategories(ArgBool(args, "video"), ArgBool(args, "games"), ArgBool(args, "adult"));
                break;
            case "dayTab":
                _weekendTabActive = ArgStr(args, "tab") == "weekend";
                PushFormDuration();
                break;
            case "setPresetDuration":
                if (ArgStr(args, "minutes") == "custom")
                {
                    PushFormDuration();
                }
                else
                {
                    ActiveMinutes = ArgInt(args, "minutes");
                    PushFormDuration();
                    PersistFamilyIfConfigured();
                }

                break;
            case "setCustomMinutes":
                ActiveMinutes = Math.Clamp(ArgInt(args, "value", ActiveMinutes), 5, 600);
                PushFormDuration();
                PersistFamilyIfConfigured();
                break;
            case "setScheduleDay":
                CommitDayMinutes(ArgInt(args, "day"), ArgInt(args, "minutes"));
                break;
            case "setFlag":
                SetFlag(ArgStr(args, "name"), args);
                break;
            case "addAppLimit":
                AddAppLimit(ArgStr(args, "key"), ArgInt(args, "minutes"));
                break;
            case "removeAppLimit":
                RemoveAppLimit(ArgStr(args, "key"));
                break;
            case "pickLimitApp":
                PickLimitApp();
                break;
            case "changePin":
                ChangePin(ArgStr(args, "old"), ArgStr(args, "new"), ArgStr(args, "confirm"));
                break;
            case "saveRecoveryEmail":
                SaveRecoveryEmail(ArgStr(args, "email"));
                break;
            case "copyRecovery":
                CopyRecovery();
                break;
            case "mailPreset":
                MailPreset(ArgStr(args, "tag"));
                break;
            case "saveMail":
                SaveMail(ArgStr(args, "host"), ArgStr(args, "port"), ArgBool(args, "ssl"),
                    ArgStr(args, "user"), ArgStr(args, "pass"));
                break;
            case "installService":
                InstallService();
                break;
            case "uninstallService":
                UninstallService();
                break;
            case "statsShow":
                _ = PushStatsAsync();
                break;
            case "statsRange":
                _statsRangeDays = ArgInt(args, "days") switch { 14 => 14, 30 => 30, _ => 7 };
                _ = PushStatsAsync();
                break;
            case "spike":
                Spike();
                break;
            case "copyFeedback":
                CopyFeedbackEmail();
                break;
            case "openMailApp":
                OpenMailApp();
                break;
            case "openLogs":
                OpenLogs();
                break;
            case "sponsor":
                new SponsorWindow { Owner = this }.ShowDialog();
                break;
            case "rerunSetup":
                RerunSetup();
                break;
            case "askParent":
                AskParent();
                break;
            case "askMore":
                AskMore();
                break;
            case "launchApp":
                LaunchApp(ArgStr(args, "key"));
                break;
            case "breakDismiss":
                DismissBreak();
                break;
        }
    }

    private static string ArgStr(JsonElement? args, string name, string def = "") =>
        args.HasValue
        && args.Value.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? def
            : def;

    private static int ArgInt(JsonElement? args, string name, int def = 0) =>
        args.HasValue && args.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : def;

    private static bool ArgBool(JsonElement? args, string name) =>
        args.HasValue && args.Value.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    // ============================================================
    // 窗口 / 托盘 / 退出
    // ============================================================
    internal void HideToTray(bool silent = false)
    {
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Hide();
        if (!silent)
        {
            Tray?.HintHidden();
        }
    }

    internal void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    internal void RequestExit()
    {
        if (!_host.IsConfigured)
        {
            _exitAllowed = true;
            Close();
            return;
        }

        ShowFromTray();
        if (!TryParentPin("退出后孩子就能打开任意软件。确定退出澄时吗？", out var pin))
        {
            return;
        }

        if (_host.Snapshot.Parental)
        {
            _host.Stop(pin);
        }

        _exitAllowed = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitAllowed)
        {
            SaveWindowBounds();
            return;
        }

        if (Tray is null)
        {
            SaveWindowBounds();
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    // ===== 窗口几何持久化：记住上次的位置和大小，每次启动不再固定居中 =====
    private static string WindowStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Chengshi", "window.json");

    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(WindowStatePath))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(WindowStatePath));
            var r = doc.RootElement;
            if (!r.TryGetProperty("left", out var l)
                || !r.TryGetProperty("top", out var t)
                || !r.TryGetProperty("width", out var w)
                || !r.TryGetProperty("height", out var h))
            {
                return;
            }

            double left = l.GetDouble(), top = t.GetDouble(), width = w.GetDouble(), height = h.GetDouble();
            if (width < MinWidth || height < MinHeight)
            {
                return;
            }

            // 记下的位置还得落在当前连着的显示器范围内：拔掉外接屏后窗口不能跑出屏幕外。
            var vx = System.Windows.SystemParameters.VirtualScreenLeft;
            var vy = System.Windows.SystemParameters.VirtualScreenTop;
            var vw = System.Windows.SystemParameters.VirtualScreenWidth;
            var vh = System.Windows.SystemParameters.VirtualScreenHeight;
            if (left < vx - 8 || top < vy - 8 || left + width > vx + vw + 8 || top + height > vy + vh + 8)
            {
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
        catch (Exception ex)
        {
            FileLog.Write("app", $"窗口位置记录读取失败（忽略，用默认位置）：{ex.Message}");
        }
    }

    private void SaveWindowBounds()
    {
        try
        {
            // 最大化时 RestoreBounds 才有值；正常态直接取当前几何。
            var rect = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(WindowStatePath)!);
            File.WriteAllText(WindowStatePath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["left"] = rect.Left,
                ["top"] = rect.Top,
                ["width"] = rect.Width,
                ["height"] = rect.Height,
            }, JsonOpts));
        }
        catch (Exception ex)
        {
            FileLog.Write("app", $"窗口位置记录保存失败（忽略）：{ex.Message}");
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && !_exitAllowed)
        {
            HideToTray();
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            var child = _host.Snapshot.Phase is SessionPhase.InDesk or SessionPhase.TimeUp;
            if (connected)
            {
                // 服务端的授权按连接算：重连后是条新管道，之前的解锁不再有效。
                // 不清掉的话，家长改设置会直接吃「设置没有保存」而不是重新弹密码框。
                _parentUnlocked = false;
                _parentHint = EngineHint(null);
                _dashHint = EngineHint(null);
            }
            else if (!child)
            {
                _parentHint = "守护服务连接中断，正在重连…（已开的守护仍由服务执行）";
                _dashHint = _parentHint;
            }

            PushHint("parent", _parentHint);
            PushHint("dashHint", _dashHint);
        });
    }

    private string EngineHint(string? extra)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(extra))
        {
            parts.Add(extra);
        }

        if (!_host.IsRemote && !string.IsNullOrWhiteSpace(_host.EtwHint))
        {
            parts.Add(_host.EtwHint);
        }

        if (!string.IsNullOrWhiteSpace(_host.GuardHint))
        {
            parts.Add(_host.GuardHint);
        }

        return string.Join(" ", parts);
    }

    private string EngineDetailText()
    {
        var lines = new List<string>
        {
            _host.IsRemote
                ? "守护服务已连接：以系统权限执行，孩子关不掉。"
                : "本机守护模式：断网和防强杀没启用，安装并启动澄时服务后自动升级。",
        };
        if (!string.IsNullOrWhiteSpace(_host.EtwHint))
        {
            lines.Add(_host.EtwHint);
        }

        if (!string.IsNullOrWhiteSpace(_host.GuardHint))
        {
            lines.Add(_host.GuardHint);
        }

        return string.Join("\n", lines);
    }

    // ============================================================
    // 心跳：每秒推一次状态
    // ============================================================
    private void OnTick(object? sender, EventArgs e)
    {
        SessionSnapshot snapshot;
        try
        {
            snapshot = _host.Tick();
        }
        catch (Exception ex)
        {
            // 单次刷新失败不许打断倒计时和界面：记日志，下一秒重试。
            FileLog.Error("app", "守护状态刷新失败，本秒跳过。", ex);
            return;
        }

        AccumulateBreak(snapshot);
        _childHintOverride = string.Empty;
        PushState(snapshot);
        PushDashboard(snapshot);
    }

    /// <summary>守护中累计连续用机时长，到「护眼休息」间隔就弹一次温柔提醒（不强制）。</summary>
    private void AccumulateBreak(SessionSnapshot snapshot)
    {
        var now = DateTime.Now;
        var dt = now - _lastBreakTick;
        _lastBreakTick = now;

        if (!snapshot.IsGuarding || _breakReminderMinutes <= 0)
        {
            _breakAccum = TimeSpan.Zero;
            return;
        }

        _breakAccum += dt;
        var interval = TimeSpan.FromMinutes(_breakReminderMinutes);
        if (_breakAccum >= interval && (now - _lastBreakShown) >= interval)
        {
            Push("overlay", new { name = "break", visible = true });
            _breakAccum = TimeSpan.Zero;
            _lastBreakShown = now;
        }
    }

    private void DismissBreak()
    {
        Push("overlay", new { name = "break", visible = false });
        _breakAccum = TimeSpan.Zero;
        _lastBreakShown = DateTime.Now;
    }

    internal void ReloadAll()
    {
        var family = _host.Family;
        _weekdayMinutes = family?.WeekdayMinutes ?? family?.DailyMinutes ?? 60;
        _weekendMinutes = family?.WeekendMinutes ?? family?.DailyMinutes ?? 120;
        _schedule = family?.Schedule is { } dict && dict.Count > 0
            ? new Dictionary<DayOfWeek, int>(dict)
            : null;
        _startWithWindows = family?.StartWithWindows ?? true;
        _guardOnLaunch = family?.GuardOnLaunch ?? true;
        _bedtime = family?.BedtimeEnabled ?? true;
        _breakReminderMinutes = family?.BreakReminderMinutes ?? 0;
        _recoveryEmail = family?.RecoveryEmail ?? string.Empty;
        _selectedDeskId = family?.DeskId ?? _selectedDeskId;
        PushAll();
    }

    // ============================================================
    // 书桌 / 时长 / 周计划
    // ============================================================
    private Desk? SelectedDesk =>
        (string.IsNullOrEmpty(_selectedDeskId) ? null : _host.FindDesk(_selectedDeskId))
        ?? _host.Desks.FirstOrDefault(d => d.Id is not BuiltinDesks.SpikeId and not BuiltinDesks.LockdownId);

    private void SelectDesk(string id)
    {
        if (string.IsNullOrEmpty(id) || _host.FindDesk(id) is null)
        {
            return;
        }

        _selectedDeskId = id;
        _pendingLimitPicks.Clear();
        PushFormDesk();
        PushFormDuration();
        PushFormLimits();
        PersistFamilyIfConfigured();
        var name = _host.FindDesk(id)?.Name;
        if (!string.IsNullOrEmpty(name))
        {
            PushToast($"已切换到「{name}」书桌。");
        }
    }

    private void ApplyPreset(string deskId, int minutes)
    {
        var desk = _host.Desks.FirstOrDefault(d =>
            string.Equals(d.Id, deskId, StringComparison.OrdinalIgnoreCase));
        if (desk is null)
        {
            return;
        }

        _selectedDeskId = desk.Id;
        ActiveMinutes = minutes;
        PushFormDesk();
        PushFormDuration();
        PushFormLimits();
        PersistFamilyIfConfigured();
        PushToast($"已套用：{desk.Name} · 每天 {DescribeMinutes(minutes)}。");
    }

    private bool WeekendTabActive => _weekendTabActive;

    private int ActiveMinutes
    {
        get => WeekendTabActive ? _weekendMinutes : _weekdayMinutes;
        set
        {
            if (WeekendTabActive)
            {
                _weekendMinutes = value;
            }
            else
            {
                _weekdayMinutes = value;
            }
        }
    }

    private void CommitDayMinutes(int dayIndex, int minutes)
    {
        if (dayIndex < 0 || dayIndex >= _weekOrder.Length)
        {
            return;
        }

        var day = _weekOrder[dayIndex];
        minutes = Math.Clamp(minutes, 5, 600);
        var baseMin = day is DayOfWeek.Saturday or DayOfWeek.Sunday ? _weekendMinutes : _weekdayMinutes;
        _schedule ??= new Dictionary<DayOfWeek, int>();
        if (minutes == baseMin)
        {
            _schedule.Remove(day);
        }
        else
        {
            _schedule[day] = minutes;
        }

        if (_schedule.Count == 0)
        {
            _schedule = null;
        }

        if (!EnsureParentUnlocked())
        {
            ReloadScheduleFromFamily();
            return;
        }

        PersistSchedule();
        PushFormSchedule();
    }

    private void ReloadScheduleFromFamily()
    {
        _schedule = _host.Family?.Schedule is { } dict && dict.Count > 0
            ? new Dictionary<DayOfWeek, int>(dict)
            : null;
        PushFormSchedule();
    }

    private void PersistSchedule()
    {
        if (_host.Family is not { } family)
        {
            return;
        }

        try
        {
            _host.SaveFamily(family with { Schedule = _schedule });
            if (_host.IsRemote)
            {
                _parentUnlocked = true;
            }
        }
        catch (Exception ex) when (ex is RemoteFaultException or UnauthorizedAccessException or IOException)
        {
            _parentHint = "周计划没有保存：" + ex.Message;
            PushHint("parent", _parentHint);
            PushToast("周计划没有保存：" + ex.Message, "warn");
        }
    }

    /// <summary>
    /// 改配置的门槛：连着守护服务时，先验证过家长密码才会被服务接受。
    /// 没有服务或还没设密码时不拦（服务端对未配置状态放行首次设置）。
    /// </summary>
    private bool EnsureParentUnlocked()
    {
        if (!_host.IsRemote || _parentUnlocked || !_host.IsConfigured)
        {
            return true;
        }

        if (!TryParentPin("修改每天时长、软件名单前，请输入家长密码。", out var pin))
        {
            return false;
        }

        try
        {
            _parentUnlocked = _host.VerifyParentPin(pin);
        }
        catch (RemoteFaultException)
        {
            _parentUnlocked = false;
        }

        if (!_parentUnlocked)
        {
            _parentHint = "密码不对，设置没有解锁。";
            PushHint("parent", _parentHint);
            PushToast("密码不对，设置没有解锁。", "warn");
        }

        return _parentUnlocked;
    }

    private void PersistFamilyIfConfigured()
    {
        if (_host.Family is not { } family || SelectedDesk is not { } desk)
        {
            return;
        }

        if (!EnsureParentUnlocked())
        {
            LoadDurationFromFamily();
            return;
        }

        try
        {
            _host.SaveFamily(family with
            {
                DailyMinutes = ActiveMinutes,
                WeekdayMinutes = _weekdayMinutes,
                WeekendMinutes = _weekendMinutes,
                DeskId = desk.Id,
                StartWithWindows = _startWithWindows,
                GuardOnLaunch = _guardOnLaunch,
                BedtimeEnabled = _bedtime,
                BreakReminderMinutes = _breakReminderMinutes,
                Schedule = _schedule,
                RecoveryEmail = string.IsNullOrWhiteSpace(_recoveryEmail) ? null : _recoveryEmail,
            });
            if (_host.IsRemote)
            {
                // 解锁只对当前连接有效，保存成功即视为本窗口已授权。
                _parentUnlocked = true;
            }
        }
        catch (Exception ex) when (ex is RemoteFaultException or UnauthorizedAccessException or IOException)
        {
            _parentHint = "设置没有保存：" + ex.Message;
            PushHint("parent", _parentHint);
            PushToast("设置没有保存：" + ex.Message, "warn");
            LoadDurationFromFamily();
            return;
        }

        StartupRegistration.Apply(_startWithWindows);
    }

    private void LoadDurationFromFamily()
    {
        var family = _host.Family;
        _weekdayMinutes = family?.WeekdayMinutes ?? family?.DailyMinutes ?? 60;
        _weekendMinutes = family?.WeekendMinutes ?? family?.DailyMinutes ?? 120;
        PushFormDuration();
    }

    private void SetFlag(string name, JsonElement? args)
    {
        switch (name)
        {
            case "startWithWindows":
                _startWithWindows = ArgBool(args, "value");
                break;
            case "guardOnLaunch":
                _guardOnLaunch = ArgBool(args, "value");
                break;
            case "bedtime":
                _bedtime = ArgBool(args, "value");
                break;
            case "breakReminder":
                _breakReminderMinutes = ArgInt(args, "value");
                _breakAccum = TimeSpan.Zero;
                break;
            default:
                return;
        }

        PersistFamilyIfConfigured();
        PushFormFlags();
    }

    // ============================================================
    // 守护开始 / 停止 / 奖励
    // ============================================================
    private void DashboardGuard()
    {
        if (!_host.IsConfigured)
        {
            Push("nav", new { page = "settings" });
            _parentHint = "先在这里设好家长密码和允许的软件，再回来开始守护。";
            PushHint("parent", _parentHint);
            return;
        }

        StartGuard(null);
    }

    private void StartGuard(JsonElement? args)
    {
        if (SelectedDesk is not { } desk)
        {
            return;
        }

        if (desk.Apps.Count == 0 && !desk.Unrestricted)
        {
            _parentHint = "先添加至少一款允许使用的软件。";
            PushHint("parent", _parentHint);
            return;
        }

        try
        {
            if (!_host.IsConfigured)
            {
                var pin = ArgStr(args, "pin");
                var confirm = ArgStr(args, "confirm");
                if (PinHasher.NormalizePin(pin) != PinHasher.NormalizePin(confirm))
                {
                    _parentHint = "两次密码不一致。";
                    PushHint("parent", _parentHint);
                    return;
                }

                var family = _host.SaveFamily(FamilySettings.Create(
                    pin, ActiveMinutes, desk.Id,
                    WeekdayMinutes: _weekdayMinutes, WeekendMinutes: _weekendMinutes) with
                {
                    StartWithWindows = _startWithWindows,
                    Schedule = _schedule,
                });
                if (_host.IsRemote && _host.VerifyParentPin(pin))
                {
                    // 刚设置的密码立刻为这条连接解锁，后续调整不再重复询问。
                    _parentUnlocked = true;
                }

                StartupRegistration.Apply(family.StartWithWindows);
                System.Windows.MessageBox.Show(
                    $"家长密码已保存。找回码是：\n\n{family.RecoveryCode}\n\n请马上抄下来。忘记密码时要用，澄时不会再通过短信找回。",
                    "澄时");
            }
            else
            {
                PersistFamilyIfConfigured();
            }

            if (_host.Snapshot.Phase is not SessionPhase.Idle && !_host.Snapshot.Parental)
            {
                _host.Stop(null);
            }

            var result = _host.StartGuard();
            _parentHint = EngineHint(null);
            _childHintOverride = ChildHintFor(result.Snapshot);
            PushAll();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RemoteFaultException)
        {
            _parentHint = ex.Message;
            PushHint("parent", _parentHint);
        }
    }

    private void Reward()
    {
        if (!TryParentPin("输入家长密码，给孩子奖励 15 分钟屏幕时间。", out var pin))
        {
            return;
        }

        try
        {
            var result = _host.GrantExtra(pin, 15);
            _dashHint = result.Ok
                ? "已奖励 15 分钟，孩子继续玩吧。" + LimitNoteIfAny()
                : result.Hint;
            PushHint("dashHint", _dashHint);
        }
        catch (Exception ex) when (ex is InvalidOperationException or RemoteFaultException)
        {
            _dashHint = ex.Message;
            PushHint("dashHint", _dashHint);
        }
    }

    private void AskParent()
    {
        // 文案要说清后果：输完密码会先停下守护、回到家长界面，再改设置。
        if (!TryParentPin(
                "输入家长密码后会先暂停守护，回到家长界面；在那里可以改时长、软件名单，或重新开始守护。",
                out var pin))
        {
            return;
        }

        if (_host.IsRemote)
        {
            try
            {
                _parentUnlocked = _host.VerifyParentPin(pin);
            }
            catch (RemoteFaultException)
            {
                _parentUnlocked = false;
            }
        }

        _host.Stop(pin);
        _selectedDeskId = _host.Family?.DeskId ?? _selectedDeskId;
        LoadDurationFromFamily();
        ReloadScheduleFromFamily();
        Push("nav", new { page = "dashboard" });
        PushAll();
    }

    private void AskMore()
    {
        var dialog = new ExtendTimeWindow(_host) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Granted)
        {
            _childHintOverride = "家长批了加时，继续吧。" + LimitNoteIfAny();
            PushState(_host.Snapshot);
        }
    }

    /// <summary>加时只加总时长；被单独限时的软件不随加时恢复——当场说明白，免得孩子以为坏了。</summary>
    private string LimitNoteIfAny() =>
        _host.AppUsage.Any(r => r.Exhausted)
            ? "（单独限时的软件不随加时恢复，明天自动恢复。）"
            : string.Empty;

    /// <summary>
    /// 孩子点磁贴启动软件：名单里的软件守护本来就放行，由界面进程替孩子拉起来，
    /// 书桌就成了孩子真正的启动器。用完单独限额的不再放行；找不到安装位置就
    /// 提示从开始菜单打开。
    /// </summary>
    private void LaunchApp(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var snapshot = _host.Snapshot;
        if (snapshot.Phase is not SessionPhase.InDesk)
        {
            return;
        }

        var desk = snapshot.DeskId is null ? null : _host.FindDesk(snapshot.DeskId);
        var app = desk is { Unrestricted: false }
            ? desk.Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
            : null;
        if (app is null)
        {
            return;
        }

        var row = _host.AppUsage.FirstOrDefault(r =>
            string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (row?.Exhausted == true)
        {
            PushToast($"「{row.DisplayName}」今天的时间用完了，明天再来。", "warn");
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(app.ImagePath ?? app.FileName)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(app.ImagePath ?? string.Empty) ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write("app", $"磁贴启动失败（{key}）：{ex.Message}");
            PushToast($"没能打开「{app.DisplayName}」，请从开始菜单或桌面打开。", "warn");
        }
    }

    private bool TryParentPin(string prompt, out string pin)
    {
        pin = string.Empty;
        if (!_host.IsConfigured)
        {
            return false;
        }

        var dialog = new PinWindow(prompt, _host) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        pin = dialog.Pin;
        return true;
    }

    /// <summary>
    /// 帮助页入口：以「重新配置」模式再走一遍引导——预填当前书桌和周内时长、
    /// 跳过密码步、不重发找回码。先验证家长密码，远程模式下顺带解锁本连接。
    /// </summary>
    private void RerunSetup()
    {
        if (!_host.IsConfigured || _host.Family is not { } family)
        {
            PushToast("先完成首次家长设置，才能重新运行引导。", "warn");
            return;
        }

        if (!TryParentPin("输入家长密码后，像第一次那样重选书桌和时长。密码、找回码和周末设置不会被改动。", out var pin))
        {
            return;
        }

        if (_host.IsRemote)
        {
            try
            {
                _parentUnlocked = _host.VerifyParentPin(pin);
            }
            catch (RemoteFaultException)
            {
                _parentUnlocked = false;
            }
        }

        var wizard = new OnboardingWindow(_host, family) { Owner = this };
        if (wizard.ShowDialog() == true)
        {
            ReloadAll();
            PushToast("设置已按引导更新。");
        }
    }

    // ============================================================
    // 允许软件 / 网站规则 / 单软件限时
    // ============================================================
    private void AddApps()
    {
        if (SelectedDesk is not { } desk)
        {
            return;
        }

        var picker = new AppsWindow(desk.Apps) { Owner = this };
        if (picker.ShowDialog() == true && picker.Result is not null)
        {
            SaveDesk(desk.WithApps(picker.Result), $"允许的软件已更新（共 {picker.Result.Count} 款）。");
        }
    }

    private void RemoveApp(string key)
    {
        if (SelectedDesk is not { } desk || string.IsNullOrEmpty(key))
        {
            return;
        }

        var name = desk.Apps.FirstOrDefault(a => a.Key == key)?.DisplayName;
        SaveDesk(desk.WithApps(desk.Apps.Where(a => a.Key != key)),
            name is null ? null : $"已从名单移除「{name}」。");
    }

    private void AddAllowedSite(string text)
    {
        if (SelectedDesk is not { } desk)
        {
            return;
        }

        var normalized = Desk.NormalizeDomains([text]);
        if (normalized.Count == 0)
        {
            PushToast("网址格式不对，例如 ke.qq.com。", "warn");
            return;
        }

        SaveDesk(desk.WithAllowedSites(desk.AllowedSiteList.Append(normalized[0])),
            $"已允许 {normalized[0]}，浏览器只能打开名单里的网站。");
    }

    private void RemoveAllowedSite(string site)
    {
        if (SelectedDesk is not { } desk || string.IsNullOrEmpty(site))
        {
            return;
        }

        SaveDesk(desk.WithAllowedSites(desk.AllowedSiteList
                .Where(s => !string.Equals(s, site, StringComparison.OrdinalIgnoreCase))),
            $"已从允许名单拿掉 {site}。");
    }

    private void AddBlockedSite(string text)
    {
        if (SelectedDesk is not { } desk)
        {
            return;
        }

        var normalized = Desk.NormalizeDomains([text]);
        if (normalized.Count == 0)
        {
            PushToast("网址格式不对，例如 youku.com。", "warn");
            return;
        }

        SaveDesk(desk.WithBlockedSites(desk.BlockedSiteList.Append(normalized[0])),
            $"已禁止 {normalized[0]}。");
    }

    private void RemoveBlockedSite(string site)
    {
        if (SelectedDesk is not { } desk || string.IsNullOrEmpty(site))
        {
            return;
        }

        SaveDesk(desk.WithBlockedSites(desk.BlockedSiteList
                .Where(s => !string.Equals(s, site, StringComparison.OrdinalIgnoreCase))),
            $"已解除禁止 {site}。");
    }

    private void SetCategories(bool video, bool games, bool adult)
    {
        if (SelectedDesk is not { } desk)
        {
            return;
        }

        var categories = new List<string>();
        if (video)
        {
            categories.Add("video");
        }

        if (games)
        {
            categories.Add("games");
        }

        if (adult)
        {
            categories.Add("adult");
        }

        SaveDesk(desk.WithBlockCategories(categories), "网站拦截类别已更新。");
    }

    /// <summary>
    /// 「整个电脑」场景下给单软件限时挑对象：打开软件选择器，挑好的先放进
    /// 下拉框当候选（不落盘），等家长填上分钟数点「加上这条限额」才写进书桌。
    /// </summary>
    private void PickLimitApp()
    {
        if (SelectedDesk is not { } desk)
        {
            return;
        }

        var picker = new AppsWindow(desk.Apps) { Owner = this };
        if (picker.ShowDialog() == true && picker.Result is not null)
        {
            _pendingLimitPicks.Clear();
            foreach (var app in picker.Result)
            {
                _pendingLimitPicks[app.Key] = app;
            }

            PushFormLimits();
        }
    }

    private void AddAppLimit(string key, int minutes)
    {
        if (SelectedDesk is not { } desk)
        {
            return;
        }

        if (string.IsNullOrEmpty(key))
        {
            PushToast("先从下拉框里挑一款软件。", "warn");
            return;
        }

        if (minutes is < 5 or > 600)
        {
            PushToast("分钟数要填数字，范围 5–600。", "warn");
            return;
        }

        // 书桌名单里已有的软件改限额；「整个电脑」场景下家长刚挑选的软件
        // 还不在名单里，连同限额一起加进去（对这种书桌名单就是限时名单）。
        Desk? limited;
        if (desk.Apps.Any(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            limited = desk.WithAppLimit(key, minutes);
        }
        else if (_pendingLimitPicks.TryGetValue(key, out var pick))
        {
            limited = desk.WithApps(desk.Apps.Append(pick.WithDailyMinutes(minutes)));
        }
        else
        {
            PushToast("先从下拉框里挑一款软件。", "warn");
            return;
        }

        if (ReferenceEquals(limited, desk))
        {
            PushToast("这条限额已经是这样了。", "warn");
            return;
        }

        var name = limited.Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? key;
        SaveDesk(limited, $"已给「{name}」设每天 {minutes} 分钟。");
    }

    private void RemoveAppLimit(string key)
    {
        if (SelectedDesk is not { } desk || string.IsNullOrEmpty(key))
        {
            return;
        }

        var name = desk.Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName;
        SaveDesk(desk.WithAppLimit(key, null),
            name is null ? null : $"已取消「{name}」的单独限时。");
    }

    private void SaveDesk(Desk desk, string? doneToast = null)
    {
        if (!EnsureParentUnlocked())
        {
            return;
        }

        try
        {
            var saved = _host.SaveDesk(desk);
            _selectedDeskId = saved.Id;
            if (!string.IsNullOrEmpty(doneToast))
            {
                PushToast(doneToast);
            }
        }
        catch (Exception ex) when (ex is RemoteFaultException or UnauthorizedAccessException or IOException)
        {
            _parentHint = "书桌没有保存：" + ex.Message;
            PushHint("parent", _parentHint);
            PushToast("书桌没有保存：" + ex.Message, "warn");
        }

        PushFormDesk();
        PushFormDuration();
        PushFormLimits();
        PersistFamilyIfConfigured();
    }

    // ============================================================
    // 密码 / 找回码 / 邮件
    // ============================================================
    private void ChangePin(string oldPin, string newPin, string confirmPin)
    {
        try
        {
            if (PinHasher.NormalizePin(newPin) != PinHasher.NormalizePin(confirmPin))
            {
                PushHint("pin", "两次新密码不一致。");
                return;
            }

            var saved = _host.ChangePin(oldPin, newPin);
            _parentUnlocked = true;
            // 改密码不动找回码；远程模式下服务不回传找回码，提醒家长沿用旧的那枚。
            Push("pinCleared");
            PushFormPin();
            PushHint("pin", saved.RecoveryCode is null ? "密码已改。找回码不变（沿用首次设置时抄下的那枚）。" : "密码已改。");
            PushToast("密码已改。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or RemoteFaultException)
        {
            PushHint("pin", ex.Message);
        }
    }

    private void CopyRecovery()
    {
        var code = _host.Family?.RecoveryCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            _recoveryHint = _host.IsRemote
                ? "守护服务不再显示找回码；请翻出首次设置时抄下的那枚。"
                : "还没有生成找回码，先完成家长设置。";
        }
        else
        {
            try
            {
                System.Windows.Clipboard.SetText(code);
                _recoveryHint = "已复制到剪贴板，请粘贴到备忘或纸质本子上。";
            }
            catch (Exception)
            {
                _recoveryHint = "复制失败，请手动抄下来。";
            }
        }

        PushFormPin();
    }

    private void SaveRecoveryEmail(string email)
    {
        if (email.Length > 0 && (!email.Contains('@') || !email.Contains('.')))
        {
            PushHint("mail", "邮箱格式看起来不太对，请检查。");
            return;
        }

        _recoveryEmail = email;
        if (!EnsureParentUnlocked())
        {
            PushHint("mail", "请先在弹出的密码框里验证家长密码。");
            LoadDurationFromFamily();
            return;
        }

        PersistFamilyIfConfigured();
        PushFormPin();
        var saved = string.IsNullOrWhiteSpace(email)
            ? "已清除备用邮箱。"
            : $"已保存备用邮箱：{email}（忘记密码时可用它收验证码）。";
        PushHint("mail", saved);
        PushToast(saved);
    }

    private void MailPreset(string tag)
    {
        var preset = SmtpConfig.Preset(tag);
        if (preset is null)
        {
            return;
        }

        _mailHost = preset.Host;
        _mailPort = preset.Port.ToString();
        _mailSsl = preset.UseSsl;
        PushFormMail();
        PushHint("mail", $"已填入 {tag.ToUpperInvariant()} 的服务器与端口，请补全邮箱账号和授权码。");
    }

    private async void SaveMail(string host, string port, bool ssl, string user, string pass)
    {
        int.TryParse(port, out var portNum);
        try
        {
            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(pass))
            {
                // 三项全空 = 清除邮件设置。
                await _host.SaveSmtpAsync(new SmtpConfig(string.Empty, 0, false, string.Empty, string.Empty));
                PushHint("mail", "已清除邮件设置。邮箱找回密码在配置好 SMTP 之前不可用。");
                return;
            }

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
            {
                PushHint("mail", "服务器和账号要一起填；授权码留空表示沿用已保存的。");
                return;
            }

            // 授权码只发往守护服务（加密落盘），界面不保存、不再读取。
            await _host.SaveSmtpAsync(new SmtpConfig(host, portNum, ssl, user, pass));
            PushHint("mail", "已保存邮件设置（授权码加密存放）。之后找回密码会真实发信到备用邮箱。");
            PushToast("已保存邮件设置。");
        }
        catch (Exception ex) when (ex is RemoteFaultException or InvalidOperationException)
        {
            PushHint("mail", "保存失败：" + ex.Message);
        }
    }

    // ============================================================
    // 守护服务
    // ============================================================
    private (bool Installed, string Status, string InstallLabel) ServiceStatus()
    {
        if (ServiceControl.IsInstalled())
        {
            var running = ServiceControl.IsRunning();
            return (true,
                running
                    ? "守护服务已安装并在运行：开机自动守护已生效，进程孩子杀不掉。"
                    : "守护服务已安装但未运行：点「重新安装/启动」会重启它。",
                "重新安装/启动");
        }

        return (false,
            "尚未安装守护服务：现在只在软件运行时守护，重启电脑后不自动生效。建议点「安装守护服务」。",
            "安装守护服务");
    }

    private void InstallService()
    {
        if (!ServiceControl.IsAdministrator())
        {
            try
            {
                ServiceControl.RunElevated("--install-service");
                ScheduleServiceStatusRefresh();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("提权失败：" + ex.Message, "澄时");
            }

            return;
        }

        try
        {
            ServiceControl.Install();
            PushFormService();
            System.Windows.MessageBox.Show("守护服务已安装并启动。", "澄时");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("安装失败：" + ex.Message, "澄时");
        }
    }

    private void UninstallService()
    {
        if (!ServiceControl.IsAdministrator())
        {
            try
            {
                ServiceControl.RunElevated("--uninstall-service");
                ScheduleServiceStatusRefresh();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("提权失败：" + ex.Message, "澄时");
            }

            return;
        }

        try
        {
            ServiceControl.Uninstall();
            PushFormService();
            System.Windows.MessageBox.Show("守护服务已卸载。", "澄时");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("卸载失败：" + ex.Message, "澄时");
        }
    }

    private void ScheduleServiceStatusRefresh()
    {
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            try
            {
                Dispatcher.Invoke(PushFormService);
            }
            catch
            {
                // 窗口已关闭等情况忽略。
            }
        }, TaskScheduler.Default);
    }

    // ============================================================
    // 拦截演示 / 帮助
    // ============================================================
    private void Spike()
    {
        if (_host.IsGuarding)
        {
            _spikeHint = "正在守护孩子，不能同时试拦截。先找家长暂停。";
            PushHint("spike", _spikeHint);
            return;
        }

        try
        {
            var result = _host.Start(BuiltinDesks.SpikeId, TimeSpan.FromMinutes(1), pinned: false, pin: null);
            _spikeHint = EngineHint(null);
            PushHint("spike", _spikeHint);
            _childHintOverride = "试拦截：只留计算器。打开记事本应被关掉。";
            PushState(result.Snapshot);
        }
        catch (Exception ex) when (ex is ArgumentException or RemoteFaultException)
        {
            _spikeHint = ex.Message;
            PushHint("spike", _spikeHint);
        }
    }

    private void CopyFeedbackEmail()
    {
        try
        {
            System.Windows.Clipboard.SetText(FeedbackEmail);
            PushHint("feedback", "邮箱已复制，粘贴到邮件里就能发。");
        }
        catch (Exception ex)
        {
            PushHint("feedback", FeedbackEmail);
            FileLog.Write("app", $"复制反馈邮箱失败：{ex.Message}");
        }
    }

    private void OpenMailApp()
    {
        var body = $"\n\n----\n澄时 {VersionText()} · Windows {Environment.OSVersion.Version}";
        try
        {
            Process.Start(new ProcessStartInfo(
                $"mailto:{FeedbackEmail}?subject={Uri.EscapeDataString("澄时问题反馈")}&body={Uri.EscapeDataString(body)}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            PushHint("feedback", "没找到邮件程序，复制邮箱后到网页邮箱发就行。");
            FileLog.Write("app", $"打开邮件应用失败：{ex.Message}");
        }
    }

    private void OpenLogs()
    {
        try
        {
            var dir = FileLog.CurrentDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                PushHint("feedback", "日志还没生成，先跑一次守护再来看。");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\""));
        }
        catch (Exception ex)
        {
            PushHint("feedback", "打不开日志文件夹，请到数据目录下的 logs 手动查看。");
            FileLog.Error("app", "打开日志文件夹失败。", ex);
        }
    }

    private void OnBlocked(BlockedMessage blocked)
    {
        Dispatcher.Invoke(() =>
        {
            // 撞上「单软件限时用完」的给出原因，别让孩子对着进程名一头雾水。
            var limitedOut = _host.AppUsage.FirstOrDefault(r =>
                r.Exhausted
                && string.Equals(
                    Path.GetFileName(r.Key),
                    Path.GetFileName(blocked.FileName),
                    StringComparison.OrdinalIgnoreCase));
            var label = limitedOut is not null
                ? $"{limitedOut.DisplayName} · 今天限时用完"
                : Path.GetFileNameWithoutExtension(blocked.FileName);
            // 带上时间：家长看到「拦了什么」也知道「什么时候拦的」。
            _blocked.Insert(0, $"{label} · {DateTime.Now:HH:mm}");
            if (_blocked.Count > 8)
            {
                _blocked.RemoveAt(_blocked.Count - 1);
            }

            PushState(_host.Snapshot);
            PushDashboard(_host.Snapshot);
        });
    }

    // ============================================================
    // C# → JS：状态构建
    // ============================================================
    private void PushState(SessionSnapshot snapshot)
    {
        var child = snapshot.Phase is SessionPhase.InDesk or SessionPhase.TimeUp;
        var timeUp = snapshot.Phase == SessionPhase.TimeUp;
        var spike = child && !timeUp && !snapshot.Parental;

        string caption;
        string childHint;
        List<object> tiles = [];
        var askMore = false;
        string? graceText = null;
        string? warningText = null;
        string? appNoticeText = null;
        string? appNoticeTone = null;
        string? remainingUntil = null;
        if (timeUp)
        {
            caption = "今天的屏幕时间用完了";
            childHint = string.IsNullOrWhiteSpace(_childHintOverride)
                ? "明天早上自动恢复；家长可以加时，或输入密码结束守护。已锁屏的话，登录后会自动弹出此界面。"
                : _childHintOverride;
            askMore = snapshot.Parental;
        }
        else if (spike)
        {
            caption = "试拦截还剩";
            childHint = string.IsNullOrWhiteSpace(_childHintOverride)
                ? "只留计算器。打开记事本应被关掉。"
                : _childHintOverride;
            tiles = BuiltinDesks.Spike().Apps
                .Select(a => a.DisplayName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Select(name => (object)new { name, key = (string?)null, launchable = false, locked = false, note = (string?)null })
                .ToList();
        }
        else if (child)
        {
            // 时间到后的「保存进度」宽限：倒计时已停在 0，让孩子把存档/文档保存完再锁屏。
            if (snapshot.GraceRemaining > TimeSpan.Zero)
            {
                caption = "保存进度";
                graceText = $"时间到啦！还有 {FormatRemaining(snapshot.GraceRemaining)} 保存你的进度，之后就会锁屏。";
            }
            else
            {
                caption = "今天还剩";
                warningText = WarningFor(snapshot.Remaining);
            }

            var desk = snapshot.DeskId is null ? null : _host.FindDesk(snapshot.DeskId);
            var usage = _host.AppUsage;
            // 磁贴是「只能用这些」的意思，只有白名单书桌才这么画；
            // 有限额的软件带上「还剩 N 分钟」，用完的置灰——哪款还能用、能用多久一目了然。
            tiles = desk is { Unrestricted: false }
                ? desk.Apps
                    .GroupBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(group => group.FirstOrDefault(a => HasLimit(a.Key, usage)) ?? group.First())
                    .Select(a => TileFor(a, usage.FirstOrDefault(r => string.Equals(r.Key, a.Key, StringComparison.OrdinalIgnoreCase))))
                    .ToList()
                : [];
            (appNoticeText, appNoticeTone) = AppNoticeFor(usage);
            childHint = string.IsNullOrWhiteSpace(_childHintOverride) ? ChildHintFor(snapshot) : _childHintOverride;
            // 「还剩 42 分钟」不如「预计 17:42 用完」直观；锁屏/睡眠期间不计时，
            // 所以只说「预计」。时间用完/宽限阶段不展示（数字已无意义）。
            if (snapshot.Remaining > TimeSpan.Zero)
            {
                remainingUntil = $"预计 {DateTime.Now.Add(snapshot.Remaining):HH:mm} 用完";
            }
        }
        else
        {
            caption = string.Empty;
            childHint = string.Empty;
        }

        string pillText;
        string pillTone;
        if (snapshot.IsGuarding)
        {
            pillText = "守护中";
            pillTone = "accent";
        }
        else if (_host.IsConfigured)
        {
            pillText = "已暂停";
            pillTone = "muted";
        }
        else
        {
            pillText = "未设置";
            pillTone = "muted";
        }

        Push("state", new
        {
            view = timeUp ? "timeup" : child ? "desk" : "parent",
            pill = new { text = pillText, tone = pillTone },
            closeHidden = snapshot.Parental && child,
            caption,
            remainingText = child ? FormatRemaining(snapshot.Remaining) : string.Empty,
            remainingUntil,
            childHint,
            graceText,
            warningText,
            appNotice = appNoticeText is null ? null : new { text = appNoticeText, tone = appNoticeTone },
            tiles,
            blocked = _blocked.ToList(),
            askParent = snapshot.Parental,
            askMore,
            engine = new
            {
                ok = _host.IsRemote,
                title = _host.IsRemote ? "守护服务已连接" : "本机守护",
                detail = EngineDetailText(),
            },
        });
    }

    private static bool HasLimit(string key, IReadOnlyList<AppUsage> usage) =>
        usage.Any(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase) && r.HasLimit);

    private static object TileFor(AllowedApp app, AppUsage? row)
    {
        var locked = row?.Exhausted == true;
        return new
        {
            name = app.DisplayName,
            key = app.Key,
            // 有完整路径直接启动；只有文件名的也交给 Shell 的 App Paths 解析，
            // 失败会有 toast 兜底提示。
            launchable = true,
            locked,
            note = row is { HasLimit: true }
                ? locked ? "今天用完" : $"还剩 {row.RemainingMinutes} 分钟"
                : null as string,
        };
    }

    // 「用完被关」的提醒只闪一秒的话孩子根本读不完：检测到就挂住 30 秒。
    private const double AppNoticeHoldSeconds = 30d;
    private string? _appOverNotice;
    private DateTime _appOverNoticeUntil = DateTime.MinValue;

    /// <summary>
    /// 正在使用的限时软件的提醒：刚被关（挂 30 秒）优先于「还剩 ≤5 分钟」。
    /// 没在运行的软件不弹——没开着它就谈不上突然袭击。
    /// </summary>
    private (string? Text, string? Tone) AppNoticeFor(IReadOnlyList<AppUsage> usage)
    {
        var justClosed = usage.FirstOrDefault(r => r.Exhausted && r.Running);
        if (justClosed is not null)
        {
            _appOverNotice = $"「{justClosed.DisplayName}」今天的 {justClosed.LimitMinutes} 分钟用完了，已自动关闭；其它软件照常，明天恢复。";
            _appOverNoticeUntil = DateTime.Now.AddSeconds(AppNoticeHoldSeconds);
        }

        if (_appOverNotice is not null && DateTime.Now < _appOverNoticeUntil)
        {
            return (_appOverNotice, "over");
        }

        var soon = usage
            .Where(r => r is { HasLimit: true, Running: true, Exhausted: false } && r.RemainingMinutes <= 5)
            .OrderBy(r => r.RemainingMinutes)
            .FirstOrDefault();
        return soon is null
            ? (null, null)
            : ($"「{soon.DisplayName}」还剩 {soon.RemainingMinutes} 分钟，准备收尾吧。", "soon");
    }

    private void PushDashboard(SessionSnapshot snapshot)
    {
        var budget = _host.Budget;
        var limit = budget.Limit;
        var used = budget.Used;
        var fraction = limit <= TimeSpan.Zero ? 0 : used.TotalMinutes / limit.TotalMinutes;

        var desk = snapshot.Phase == SessionPhase.InDesk && snapshot.DeskId is not null
            ? _host.FindDesk(snapshot.DeskId)
            : _host.FindDesk(_host.Family?.DeskId ?? string.Empty);

        var family = _host.Family;
        var guardEnabled = snapshot.Phase == SessionPhase.Idle || !snapshot.IsGuarding;
        var guardText = !_host.IsConfigured ? "完成家长设置" : snapshot.IsGuarding ? "暂停守护" : "开始守护";

        RefreshWeekUsageCache();

        Push("dashboard", new
        {
            greeting = Greeting(),
            heroLine = HeroLine(snapshot, used, limit),
            budget = new
            {
                fraction = Math.Clamp(fraction, 0d, 1d),
                remainingText = FormatRemaining(budget.Remaining),
                usedText = $"已用 {FormatMinutes(used)} / 共 {FormatMinutes(limit)}",
                hint = snapshot.IsGuarding
                    ? "正在守护。时间用完会自动锁到系统桌面。"
                    : _host.IsConfigured
                        ? "现在没有守护。点「开始守护」后开始计时。"
                        : "先完成家长设置，今天的时间额度才会生效。",
                weekdayText = $"周内每天 {DescribeMinutes(family?.WeekdayMinutes ?? family?.DailyMinutes ?? 60)}",
                weekendText = $"周末每天 {DescribeMinutes(family?.WeekendMinutes ?? family?.DailyMinutes ?? 120)}",
            },
            deskCards = new object[]
            {
                new { id = "homework", name = "写作业", summary = DeskCardSummary(BuiltinDesks.HomeworkId, "文档 + 词典 + 计算器"), selected = desk?.Id == BuiltinDesks.HomeworkId },
                new { id = "class", name = "网课", summary = DeskCardSummary(BuiltinDesks.ClassId, "浏览器 + 笔记"), selected = desk?.Id == BuiltinDesks.ClassId },
                new { id = "code", name = "编程", summary = DeskCardSummary(BuiltinDesks.CodeId, "IDE + 终端"), selected = desk?.Id == BuiltinDesks.CodeId },
            },
            week = new { empty = _weekEmptyCache, rows = _weekRowsCache ?? [] },
            appUsage = AppUsagePayload(desk),
            blocked = _blocked.ToList(),
            guardBtn = new { text = guardText, enabled = guardEnabled },
            rewardVisible = _host.IsConfigured,
            currentDesk = new
            {
                name = desk?.Name ?? "—",
                summary = desk?.Summary ?? "还没有选书桌。",
                // 「整个电脑」场景名单里只有单独限时的软件，不是「只能用这些」。
                apps = desk is { Unrestricted: false }
                    ? desk.Apps.Select(a => a.DisplayName)
                        .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray()
                    : [],
                empty = desk is null,
            },
            engineText = EngineDetailText(),
            dashHint = _dashHint,
        });
    }

    /// <summary>仪表盘三张书桌模板卡的摘要：用已配置书桌的说明和软件数，没动过就回落到内置说明。</summary>
    private string DeskCardSummary(string id, string fallback)
    {
        var desk = _host.FindDesk(id);
        // 书桌的 Summary 本身就带「…等 N 款」的信息，别再叠加一份软件数。
        return string.IsNullOrWhiteSpace(desk?.Summary) ? fallback : desk.Summary;
    }

    /// <summary>最近七天用量：读日志有 30 秒节流，缓存最近一次的行数据。</summary>
    private void RefreshWeekUsageCache()
    {
        if (DateTime.Now < _weekUsageNextRead)
        {
            return;
        }

        _weekUsageNextRead = DateTime.Now.AddSeconds(30);

        var history = _usageLog.ReadRecent(6);
        var usedToday = _host.IsConfigured ? _host.Budget.Used : TimeSpan.Zero;
        if (history.Count == 0 && usedToday <= TimeSpan.Zero)
        {
            _weekEmptyCache = true;
            _weekRowsCache = null;
            return;
        }

        var maxMinutes = Math.Max(1, Math.Max(
            usedToday.TotalMinutes,
            history.Count == 0 ? 1 : history.Max(d => d.UsedMinutes)));
        double Fraction(double minutes) => Math.Clamp(minutes / maxMinutes, 0d, 1d);

        var rows = new List<object>();
        if (usedToday > TimeSpan.Zero)
        {
            rows.Add(new
            {
                label = "今天",
                fraction = Fraction(usedToday.TotalMinutes),
                summary = $"已用 {FormatMinutes(usedToday)} / {FormatMinutes(_host.Budget.Limit)}",
            });
        }

        foreach (var day in history)
        {
            var summary = FormatMinutes(TimeSpan.FromMinutes(day.UsedMinutes))
                + (day.BlockedCount > 0 ? $" · 拦了 {day.BlockedCount} 次" : string.Empty);
            rows.Add(new
            {
                label = $"{day.Date.Month}/{day.Date.Day} {DayLabel(day.Date.DayOfWeek)}",
                fraction = Fraction(day.UsedMinutes),
                summary,
            });
        }

        _weekRowsCache = rows;
        _weekEmptyCache = false;
    }

    // ============================================================
    // 统计页（按应用的用量详情）
    // ============================================================

    /// <summary>读日志可能碰到几十 KB 的 jsonl：放到后台线程读，读完回 UI 线程推送。</summary>
    private async Task PushStatsAsync()
    {
        var range = _statsRangeDays;
        var history = await Task.Run(() => _usageLog.ReadRecent(range));
        await Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded)
            {
                return;
            }

            PushStats(history);
        });
    }

    private void PushStats(IReadOnlyList<UsageDay> history)
    {
        var range = _statsRangeDays;
        var todayDate = DateOnly.FromDateTime(DateTime.Now);

        // 应用键 → 展示名：以当前所有书桌为准，历史应用回落到文件名。
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var desk in _host.Desks)
        {
            foreach (var app in desk.Apps)
            {
                names.TryAdd(app.Key, app.DisplayName);
            }
        }

        string NameOf(string key)
        {
            if (names.TryGetValue(key, out var name))
            {
                return name;
            }

            var leaf = Path.GetFileName(key);
            return leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(leaf)
                : leaf;
        }

        // 今天的实时数据也并进来（今天还没跨天落盘）。
        var todayTotal = _host.IsConfigured ? _host.Budget.Used : TimeSpan.Zero;
        var todayMinutes = (int)Math.Round(todayTotal.TotalMinutes);
        var todayApps = (_host.AppUsage ?? [])
            .Where(r => r.UsedMinutes > 0)
            .ToDictionary(r => r.Key, r => r.UsedMinutes, StringComparer.OrdinalIgnoreCase);

        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Accumulate(IReadOnlyDictionary<string, int>? apps)
        {
            if (apps is null)
            {
                return;
            }

            foreach (var (key, minutes) in apps)
            {
                if (minutes > 0)
                {
                    totals[key] = totals.GetValueOrDefault(key) + minutes;
                }
            }
        }

        foreach (var day in history)
        {
            Accumulate(day.Apps);
        }

        Accumulate(todayApps);

        // ---- 每日柱图（旧→新，今天垫最后）----
        var ordered = history.OrderBy(d => d.Date).ToList();
        var maxMinutes = Math.Max(1, Math.Max(
            todayMinutes,
            ordered.Count == 0 ? 1 : ordered.Max(d => d.UsedMinutes)));
        double Fraction(int minutes) => Math.Clamp((double)minutes / maxMinutes, 0.02, 1d);

        var daily = new List<object>();
        foreach (var d in ordered)
        {
            daily.Add(new
            {
                label = $"{d.Date.Month}/{d.Date.Day}",
                totalText = FormatMinutes(TimeSpan.FromMinutes(d.UsedMinutes)),
                fraction = Fraction(d.UsedMinutes),
                blocked = d.BlockedCount,
            });
        }

        daily.Add(new
        {
            label = "今天",
            totalText = FormatMinutes(todayTotal),
            fraction = Fraction(todayMinutes),
            blocked = -1,
        });

        // ---- 应用排行（合计分钟降序，取前 8，其余并入「其他」）----
        var ranked = totals
            .OrderByDescending(pair => pair.Value)
            .ToList();
        var totalAppMinutes = ranked.Sum(pair => pair.Value);
        var rankingRows = new List<object>();
        foreach (var (key, minutes) in ranked.Take(8))
        {
            rankingRows.Add(new
            {
                name = NameOf(key),
                minutesText = FormatMinutes(TimeSpan.FromMinutes(minutes)),
                share = totalAppMinutes > 0 ? $"{Math.Round(100d * minutes / totalAppMinutes)}%" : "—",
                fraction = totalAppMinutes > 0 ? Math.Clamp((double)minutes / totalAppMinutes, 0.02, 1d) : 0d,
            });
        }

        if (ranked.Count > 8)
        {
            var rest = ranked.Skip(8).Sum(pair => pair.Value);
            rankingRows.Add(new
            {
                name = $"其他 {ranked.Count - 8} 款",
                minutesText = FormatMinutes(TimeSpan.FromMinutes(rest)),
                share = totalAppMinutes > 0 ? $"{Math.Round(100d * rest / totalAppMinutes)}%" : "—",
                fraction = totalAppMinutes > 0 ? Math.Clamp((double)rest / totalAppMinutes, 0.02, 1d) : 0d,
            });
        }

        // ---- 每日明细（最新在前，今天在最上）----
        var detail = new List<object>
        {
            new
            {
                dateText = $"今天 {todayDate.Month}/{todayDate.Day}",
                totalText = FormatMinutes(todayTotal),
                blockedText = "—",
                topApps = todayApps.Count == 0
                    ? "还没开始用"
                    : string.Join("、", todayApps
                        .OrderByDescending(pair => pair.Value)
                        .Take(3)
                        .Select(pair => NameOf(pair.Key))),
            },
        };
        foreach (var d in history)
        {
            var top = d.Apps is null
                ? "—"
                : d.Apps.Count == 0
                    ? "—"
                    : string.Join("、", d.Apps
                        .OrderByDescending(pair => pair.Value)
                        .Take(3)
                        .Select(pair => NameOf(pair.Key)));
            detail.Add(new
            {
                dateText = $"{d.Date.Month}/{d.Date.Day} {DayLabel(d.Date.DayOfWeek)}",
                totalText = FormatMinutes(TimeSpan.FromMinutes(d.UsedMinutes)),
                blockedText = d.BlockedCount > 0 ? $"{d.BlockedCount} 次" : "—",
                topApps = top,
            });
        }

        // ---- 汇总 ----
        var totalMinutes = ordered.Sum(d => d.UsedMinutes) + todayMinutes;
        var dayCount = ordered.Count + 1;
        var avgMinutes = dayCount > 0 ? totalMinutes / dayCount : 0;
        var blockedSum = ordered.Sum(d => d.BlockedCount);

        Push("stats", new
        {
            range,
            summary = new
            {
                totalText = FormatMinutes(TimeSpan.FromMinutes(totalMinutes)),
                avgText = FormatMinutes(TimeSpan.FromMinutes(avgMinutes)),
                appCount = totals.Count,
                blockedText = blockedSum > 0 ? $"{blockedSum} 次" : "0 次",
            },
            daily,
            ranking = rankingRows,
            detail,
        });
    }

    private string _appUsageSignature = string.Empty;

    /// <summary>
    /// 今天每个软件用了多久。用量每秒变化，但只有内容真的变了才重推列表。
    /// 「整个电脑」场景的用量行里还有一串按进程名记的系统噪音（explorer 之类），
    /// 仪表盘只摆有限额的行并指路统计页；白名单书桌照旧全列。
    /// </summary>
    private object AppUsagePayload(Desk? desk)
    {
        var rows = _host.AppUsage ?? [];
        var unrestricted = desk?.Unrestricted == true;
        var tracked = (unrestricted
                ? rows.Where(r => r.HasLimit)
                : rows.Where(r => r.UsedMinutes > 0 || r.HasLimit))
            .ToList();
        var usedCount = rows.Count(r => r.UsedMinutes > 0);
        var overCount = tracked.Count(r => r.Exhausted);
        var hint = tracked.Count == 0
            ? string.Empty
            : overCount > 0 ? $"{usedCount} 款在用 · {overCount} 款额度用完" : $"{usedCount} 款在用";
        if (unrestricted && tracked.Count > 0)
        {
            hint += " · 全部软件的明细见「使用统计」";
        }

        var signature = string.Join("|", rows.Select(r => $"{r.Key}:{r.UsedMinutes}:{r.LimitMinutes}:{r.Running}")) + "|" + hint;
        var changed = signature != _appUsageSignature;
        _appUsageSignature = signature;

        if (tracked.Count == 0)
        {
            return new { empty = true, hint = string.Empty, rows = Array.Empty<object>(), changed };
        }

        // 有限额的软件按自己的额度画条（看还剩多少），其余按彼此的相对用量画条（看谁用得多）。
        var maxUsed = Math.Max(1, tracked.Max(r => r.UsedMinutes));
        double Fraction(AppUsage row) => row.HasLimit
            ? row.Fraction
            : Math.Clamp((double)row.UsedMinutes / maxUsed, 0d, 1d);

        var payload = tracked.Select(row => new
        {
            name = row.DisplayName,
            summary = row.Summary,
            fraction = Math.Clamp(Fraction(row), 0d, 1d),
            over = row.Exhausted,
        }).ToList();
        return new { empty = false, hint, rows = payload, changed };
    }

    private void PushFormAll()
    {
        PushFormHead();
        PushPreview();
        PushFormDesk();
        PushFormDuration();
        PushFormSchedule();
        PushFormLimits();
        PushFormPin();
        PushFormMail();
        PushFormService();
        PushFormFlags();
    }

    private void PushFormHead()
    {
        var configured = _host.IsConfigured;
        Push("form.head", new
        {
            title = configured ? "家长设置" : "给孩子设屏幕时间和软件",
            lead = configured
                ? "改时长或软件后点开始守护。孩子在守护画面里改不了。"
                : "开始守护后，不在名单里的软件会被关掉；今天的时间用完就只剩系统桌面。",
        });
    }

    private void PushFormDesk()
    {
        var desk = SelectedDesk;
        // 「整个电脑」是内置书桌；给它加过单软件限额后会落进 desks.json，
        // 这里统一用下面 Insert(0) 的那张卡展示，避免出现两张一模一样的卡。
        var desks = _host.Desks
            .Where(d => d.Id is not BuiltinDesks.SpikeId
                and not BuiltinDesks.LockdownId
                and not BuiltinDesks.FullPcId)
            .Select(d => new { id = d.Id, name = d.Name, summary = d.Summary })
            .ToList();
        var names = desk?.Apps
            .Select(a => a.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList() ?? [];
        var empty = names.Count == 0;
        var allowedSites = desk?.AllowedSiteList.ToList() ?? [];
        var blockedSites = desk?.BlockedSiteList.ToList() ?? [];

        var fullPcLimited = (_host.FindDesk(BuiltinDesks.FullPcId)?.LimitedApps.Count) ?? 0;
        desks.Insert(0, new
        {
            id = BuiltinDesks.FullPcId,
            name = "整个电脑",
            summary = fullPcLimited > 0
                ? $"不限软件 · {fullPcLimited} 款单独限时"
                : "不限制软件，只按时长锁屏",
        });
        var unrestricted = desk?.Unrestricted == true;

        Push("form.desk", new
        {
            selected = desk?.Id ?? string.Empty,
            desks,
            unrestricted,
            guardEnabled = !empty || unrestricted,
            apps = desk?.Apps.Select(a => new { key = a.Key, name = a.DisplayName }).ToList() ?? [],
            sites = new
            {
                hint = allowedSites.Count > 0
                    ? "已开启白名单模式：浏览器只能打开上面这些网站。"
                    : "浏览器（Chrome / Edge）里，勾选的类别和禁止的网站会被拦掉，写作业时还可整机断网。",
                allowed = allowedSites,
                blocked = blockedSites,
                categories = new
                {
                    video = desk?.BlockCategoryList.Contains("video", StringComparer.OrdinalIgnoreCase) == true,
                    games = desk?.BlockCategoryList.Contains("games", StringComparer.OrdinalIgnoreCase) == true,
                    adult = desk?.BlockCategoryList.Contains("adult", StringComparer.OrdinalIgnoreCase) == true,
                },
            },
        });
        PushPreview();
    }

    private void PushFormDuration()
    {
        var desk = SelectedDesk;
        var unrestricted = desk?.Unrestricted == true;
        var empty = desk is null || desk.Apps.Count == 0;
        var minutes = ActiveMinutes;
        var preset = minutes is 30 or 60 or 90 or 120;
        Push("form.duration", new
        {
            tab = _weekendTabActive ? "weekend" : "weekday",
            minutesText = DescribeMinutes(minutes),
            otherDays = _weekendTabActive
                ? $"周中每天 {DescribeMinutes(_weekdayMinutes)}"
                : $"周末每天 {DescribeMinutes(_weekendMinutes)}",
            preset = preset ? minutes.ToString() : "custom",
            customValue = minutes,
            whatHappens = unrestricted
                ? "时间一到会锁定整个电脑，明天自动恢复。"
                : empty
                    ? "先加一款软件，否则孩子几乎什么都开不了。"
                    : "用完后，名单外的软件会被关掉，直到明天或输入家长密码。",
        });
        PushPreview();
    }

    private void PushFormSchedule()
    {
        var rows = _weekOrder.Select((day, index) =>
        {
            if (_schedule is not null && _schedule.TryGetValue(day, out var custom))
            {
                return new { day = index, label = DayLabel(day), minutes = custom, custom = true };
            }

            var baseMin = day is DayOfWeek.Saturday or DayOfWeek.Sunday ? _weekendMinutes : _weekdayMinutes;
            return new { day = index, label = DayLabel(day), minutes = baseMin, custom = false };
        }).ToList();
        Push("form.schedule", new { rows });
    }

    private void PushFormLimits()
    {
        var desk = SelectedDesk;
        var unrestricted = desk?.Unrestricted == true;
        var choices = (desk?.Apps ?? [])
            .GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new { key = a.Key, name = a.DisplayName })
            .ToList();

        // 「整个电脑」场景：软件选择器挑好的候选一并进下拉框（还没写进书桌）。
        if (unrestricted)
        {
            var onDesk = choices.Select(c => c.key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var pick in _pendingLimitPicks.Values
                         .Where(p => !onDesk.Contains(p.Key))
                         .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                choices.Add(new { key = pick.Key, name = pick.DisplayName });
            }
        }

        var rows = (desk?.LimitedApps ?? [])
            .Where(a => a.DailyMinutes is > 0)
            .Select(a => new { key = a.Key, name = a.DisplayName, summary = $"每天 {a.DailyMinutes} 分钟" })
            .ToList();
        Push("form.limits", new { unrestricted, choices, rows });
    }

    private void PushFormPin()
    {
        var configured = _host.IsConfigured;
        Push("form.pin", new
        {
            firstRun = !configured,
            recoveryCode = _host.IsRemote
                ? "找回码已由守护服务保管（不再显示，请用首次设置时抄下的那枚）"
                : _host.Family?.RecoveryCode ?? "进入设置后自动生成",
            recoveryHint = _recoveryHint,
            recoveryEmail = _recoveryEmail,
        });
    }

    private void PushFormMail() => Push("form.mail", new
    {
        host = _mailHost,
        port = _mailPort,
        ssl = _mailSsl,
        user = _mailUser,
    });

    private void PushFormService()
    {
        var (installed, status, label) = ServiceStatus();
        Push("form.service", new
        {
            installed,
            statusText = status,
            installLabel = label,
        });
    }

    private void PushFormFlags() => Push("form.flags", new
    {
        startWithWindows = _startWithWindows,
        guardOnLaunch = _guardOnLaunch,
        bedtime = _bedtime,
        breakReminder = _breakReminderMinutes,
    });

    /// <summary>
    /// 守护生效预览：把「能用什么 × 能用多久 × 单独限时 × 什么时候开始」
    /// 叠加后的结果讲成大白话。设置页顶部实时刷新，回答「到底什么在生效」。
    /// </summary>
    private void PushPreview()
    {
        var guarding = _host.Snapshot.IsGuarding;
        var head = guarding
            ? "正在守护中，这些规则都在生效："
            : "点「开始守护」后，以下规则会同时生效：";

        var lines = new List<string>();
        var desk = SelectedDesk;
        if (desk is null)
        {
            lines.Add("还没选书桌：先在「能用什么」里挑一个");
        }
        else if (desk.Unrestricted)
        {
            lines.Add("不限制软件：整台电脑随便用，网站也不拦，只用下面的时长来管");
        }
        else
        {
            var appCount = desk.Apps
                .Select(a => a.DisplayName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Count();
            var siteNote = desk.AllowedSiteList.Count > 0
                ? $"；浏览器只能打开 {desk.AllowedSiteList.Count} 个白名单网站"
                : string.Empty;
            lines.Add(appCount == 0
                ? "「" + desk.Name + "」书桌还没加软件：守护前至少加一款"
                : $"只能用「{desk.Name}」书桌里的 {appCount} 款软件{siteNote}，名单外的会被关掉");
        }

        var today = DateTime.Now.DayOfWeek;
        var todayMinutes = _schedule is not null && _schedule.TryGetValue(today, out var custom)
            ? custom
            : today is DayOfWeek.Saturday or DayOfWeek.Sunday ? _weekendMinutes : _weekdayMinutes;
        var scheduleNote = _schedule is { Count: > 0 } ? $"（有 { _schedule.Count } 天单独设置）" : string.Empty;
        lines.Add($"每天最多 {DescribeMinutes(todayMinutes)}{scheduleNote}；时间用完自动锁屏，次日恢复");

        var limitedCount = desk?.LimitedApps.Count(a => a.DailyMinutes is > 0) ?? 0;
        if (limitedCount > 0)
        {
            lines.Add($"其中 {limitedCount} 款软件单独限了时长，用完只关它，别的照常用");
        }

        if (_bedtime)
        {
            lines.Add("睡觉时段（22:00 – 07:00）到点断网");
        }

        Push("form.preview", new { head, lines });
    }

    private void PushHelp() => Push("help", new
    {
        engineText = EngineDetailText(),
        aboutText = $"澄时 {VersionText()} · 所有设置和记录都只存在这台电脑上，不会上传。",
        feedbackEmail = FeedbackEmail,
    });

    // ============================================================
    // 文案工具（与旧版一致）
    // ============================================================
    private string ChildHintFor(SessionSnapshot snapshot)
    {
        var desk = snapshot.DeskId is null ? null : _host.FindDesk(snapshot.DeskId);
        if (desk is { Unrestricted: true })
        {
            var limited = desk.LimitedApps.Count;
            return limited == 0
                ? "电脑上的软件都能用；今天的时间用完会自动锁屏。"
                : $"电脑上的软件都能用，其中 {limited} 款每天有时长限制，用完只关那一款。";
        }

        var names = desk?.Apps
            .Select(a => a.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToArray() ?? [];
        return names.Length == 0
            ? "现在只能用系统桌面。其它软件会被关掉。"
            : $"只能用 {string.Join("、", names)}。其它软件会被关掉。";
    }

    private static string Greeting()
    {
        var h = DateTime.Now.Hour;
        return h switch
        {
            < 6 => "夜深了",
            < 12 => "早上好",
            < 14 => "中午好",
            < 18 => "下午好",
            _ => "晚上好",
        };
    }

    private string HeroLine(SessionSnapshot snapshot, TimeSpan used, TimeSpan limit)
    {
        if (!_host.IsConfigured)
        {
            return "先完成家长设置，今天的时间额度才会生效。";
        }

        // 数字（还剩多少、已用多少）只在「电脑总时长」卡里讲一遍，
        // 这里只说当前状态，避免同一句话在页面上出现两次。
        if (snapshot.IsGuarding)
        {
            return "守护进行中 · 时间用完会自动锁屏，明天恢复。";
        }

        return "现在没有守护。点「开始守护」后，今天的时间开始计。";
    }

    private static string FormatMinutes(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分";
        }

        return $"{(int)span.TotalMinutes} 分钟";
    }

    private static string DescribeMinutes(int minutes) => minutes switch
    {
        30 => "30 分钟",
        60 => "1 小时",
        90 => "90 分钟",
        120 => "2 小时",
        _ => $"{minutes} 分钟",
    };

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }

        return remaining.ToString(@"mm\:ss");
    }

    /// <summary>到点前的分级提醒口径：10 分钟预告、5 分钟收尾、最后 1 分钟。其余时段不打扰。</summary>
    private static string? WarningFor(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(10))
        {
            return null;
        }

        if (remaining <= TimeSpan.FromMinutes(1))
        {
            return "最后 1 分钟！快把手头的事收尾。";
        }

        if (remaining <= TimeSpan.FromMinutes(5))
        {
            return "还剩 5 分钟，准备收尾啦。";
        }

        return "还剩 10 分钟，快到时间了。";
    }

    private static string DayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    private static string VersionText()
    {
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return v is null ? "开发版" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }
}
