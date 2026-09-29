using Chengshi.Core;
using Xunit;

namespace Chengshi.Engine.Tests;

/// <summary>
/// 「整个电脑」场景（Unrestricted 书桌）：所有软件放行，守护只计时长和到点锁屏。
/// </summary>
public class UnrestrictedDeskTests
{
    private sealed class RecordingEnforcer : ProcessEnforcer
    {
        public RecordingEnforcer(Func<int>? activeSession = null)
            : base(activeSession)
        {
        }

        public List<int> Killed { get; } = [];

        protected override bool TryKill(int pid)
        {
            Killed.Add(pid);
            return true;
        }
    }

    private static ProcessIdentity Process(string fileName, int sessionId) =>
        new(1234 + sessionId, 1, fileName, null, null, null, sessionId);

    [Fact]
    public void Unrestricted_desk_never_kills_any_process()
    {
        var enforcer = new RecordingEnforcer(() => 42);
        var desk = BuiltinDesks.FullPc();

        Assert.True(desk.Unrestricted);
        Assert.False(enforcer.TryEnforce(Process("game.exe", 42), desk));
        Assert.False(enforcer.TryEnforce(Process("notepad.exe", 42), desk));
        Assert.Equal(0, enforcer.SweepRunning(desk));
        Assert.Empty(enforcer.Killed);
    }

    [Fact]
    public void Unrestricted_desk_kills_only_apps_left_in_its_deny_list()
    {
        // EffectiveDesk 交给拦截器的「整个电脑」书桌：名单里只剩当天限时用完的软件。
        var enforcer = new RecordingEnforcer(() => 42);
        var desk = BuiltinDesks.FullPc().WithApps([new AllowedApp("游戏", "game")]);

        Assert.True(enforcer.TryEnforce(Process("game.exe", 42), desk));
        Assert.False(enforcer.TryEnforce(Process("notepad.exe", 42), desk));
        Assert.Single(enforcer.Killed);
    }

    [Fact]
    public void Unrestricted_deny_list_respects_other_sessions()
    {
        var enforcer = new RecordingEnforcer(() => 42);
        var desk = BuiltinDesks.FullPc().WithApps([new AllowedApp("游戏", "game")]);

        // 别的登录用户 / 系统会话里的同名进程不碰。
        Assert.False(enforcer.TryEnforce(Process("game.exe", 7), desk));
        Assert.Empty(enforcer.Killed);
    }

    [Fact]
    public void Unrestricted_deny_list_spares_system_critical_processes()
    {
        var enforcer = new RecordingEnforcer(() => 42);
        var desk = BuiltinDesks.FullPc().WithApps([new AllowedApp("资源管理器", "explorer")]);

        // AlwaysAllow 的系统关键进程永远放行，家长就算点名也拦不掉。
        var explorer = new ProcessIdentity(
            1234, 1, "explorer.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            null, null, 42);
        Assert.False(enforcer.TryEnforce(explorer, desk));
        Assert.Empty(enforcer.Killed);
    }

    [Fact]
    public void FullPc_desk_is_resolvable_as_builtin()
    {
        var desk = BuiltinDesks.Find(BuiltinDesks.FullPcId);
        Assert.NotNull(desk);
        Assert.Equal("fullpc", desk!.Id);
        Assert.True(desk.Unrestricted);
        Assert.Empty(desk.Apps);
        Assert.False(desk.HasSiteRules);
    }

    [Fact]
    public void Desk_deserialization_defaults_to_restricted()
    {
        // 旧 desks.json 没有 unrestricted 字段：反序列化后必须保持白名单模式。
        const string json = """{"id":"d1","name":"书桌","summary":"…","apps":[]}""";
        var desk = System.Text.Json.JsonSerializer.Deserialize<Desk>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.NotNull(desk);
        Assert.False(desk!.Unrestricted);
    }
}
