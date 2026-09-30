using Chengshi.Core;
using Chengshi.Ipc;
using Xunit;

namespace Chengshi.Engine.Tests;

/// <summary>
/// 端到端：本进程内起一个带管道的 SessionHost，客户端从管道这头
/// 完成开机守护、停止、改配置、改密码，并收到推送。
/// </summary>
public class SessionClientIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chengshi-pipe-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "Chengshi-Test-" + Guid.NewGuid().ToString("N");

    public SessionClientIntegrationTests()
    {
        Directory.CreateDirectory(_dir);
    }

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
    public void Client_drives_guard_over_pipe()
    {
        var (host, server, _) = CreateServer();
        using (host)
        using (server)
        {
            using var client = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);

            Assert.True(client.IsConfigured);
            Assert.True(client.VerifyParentPin("1234"));
            Assert.False(client.IsGuarding);

            var start = client.StartGuard();
            Assert.Equal(StartSessionStatus.Started, start.Status);
            Assert.True(client.IsGuarding);
            Assert.Equal(BuiltinDesks.CodeId, client.Snapshot.DeskId);
            Assert.True(client.Snapshot.Parental);

            var rejected = client.Stop("0000");
            Assert.Equal(StopSessionStatus.PinRejected, rejected.Status);
            Assert.True(client.IsGuarding);

            var stopped = client.Stop("1234");
            Assert.Equal(StopSessionStatus.Stopped, stopped.Status);
            Assert.False(client.IsGuarding);
        }
    }

    [Fact]
    public void Client_edits_config_and_pin_over_pipe()
    {
        var (host, server, _) = CreateServer();
        using (host)
        using (server)
        {
            using var client = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);

            // 没验证家长密码前，改书桌会被门禁挡下来。
            var classDesk = client.Desks.First(d => d.Id == BuiltinDesks.ClassId);
            Assert.Throws<RemoteFaultException>(() =>
                client.SaveDesk(classDesk.WithApps(classDesk.Apps.Append(new AllowedApp("扫雷", "winmine")))));

            Assert.True(client.VerifyParentPin("1234"));
            client.SaveDesk(classDesk.WithApps(classDesk.Apps.Append(new AllowedApp("扫雷", "winmine"))));
            var reloaded = client.Desks.First(d => d.Id == BuiltinDesks.ClassId);
            Assert.Contains(reloaded.Apps, a => a.FileName.Equals("winmine.exe", StringComparison.OrdinalIgnoreCase));

            // 改密码本身带旧密码校验，成功后这条连接也视为已解锁。
            client.ChangePin("1234", "5678");
            Assert.True(client.VerifyParentPin("5678"));
            Assert.False(client.VerifyParentPin("1234"));
            Assert.Throws<RemoteFaultException>(() => client.ChangePin("0000", "9999"));
        }
    }

    [Fact]
    public void Pipe_rejects_guard_and_family_writes_without_parent_pin()
    {
        var (host, server, _) = CreateServer();
        using (host)
        using (server)
        {
            using var client = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);

            Assert.Throws<RemoteFaultException>(() => client.StartGuard());
            Assert.Throws<RemoteFaultException>(() =>
                client.SaveFamily(client.Family! with { DailyMinutes = 600, WeekendMinutes = 900 }));
            Assert.Equal(60, client.Family!.DailyMinutes);
            Assert.Null(client.Family.WeekdayMinutes);

            Assert.False(client.VerifyParentPin("0000"));
            Assert.True(client.VerifyParentPin("1234"));
            var updated = client.SaveFamily(client.Family! with
            {
                WeekdayMinutes = 40,
                WeekendMinutes = 180,
            });
            Assert.Equal(40, updated.WeekdayMinutes);
            Assert.Equal(180, updated.WeekendMinutes);

            var start = client.StartGuard();
            Assert.Equal(StartSessionStatus.Started, start.Status);
        }
    }

    [Fact]
    public async Task Client_receives_pushed_state()
    {
        var (host, server, _) = CreateServer();
        using (host)
        using (server)
        {
            using var client = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);

            var seen = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.StateChanged += snapshot =>
            {
                if (snapshot.IsGuarding)
                {
                    seen.TrySetResult(snapshot);
                }
            };

            Assert.True(client.VerifyParentPin("1234"));
            var result = client.StartGuard();
            Assert.Equal(StartSessionStatus.Started, result.Status);
            var pushed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BuiltinDesks.CodeId, pushed.DeskId);        }
    }

    [Fact]
    public async Task Client_receives_blocked_push()
    {
        var (enforcer, host, server) = CreateServerWithRaisingEnforcer();
        using (host)
        using (server)
        {
            using var client = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);

            var seen = new TaskCompletionSource<BlockedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ProcessBlocked += blocked => seen.TrySetResult(blocked);

            Assert.True(client.VerifyParentPin("1234"));
            enforcer.Raise(new ProcessIdentity(999, 1, "game.exe", null, null, null, 1));
            var blocked = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(999, blocked.Pid);
            Assert.Equal("game.exe", blocked.FileName);
        }
    }

    [Fact]
    public void A_stuck_connection_does_not_block_new_clients()
    {
        // 真机事故的回归：一条「连上后既不说话也不断开」的僵尸连接曾把单实例串行的
        // 接收循环堵死——服务进程活着，管道却永远没人应答。并行服务后新客户端必须照常工作。
        var (host, server, _) = CreateServer();
        using (host)
        using (server)
        {
            using var zombie = PipeFactory.CreateClient(_pipe);
            zombie.Connect(3000);

            using var client = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);
            Assert.True(client.IsConfigured);
            Assert.True(client.VerifyParentPin("1234"));
            var start = client.StartGuard();
            Assert.Equal(StartSessionStatus.Started, start.Status);
            Assert.True(client.IsGuarding);
        }
    }

    [Fact]
    public void Two_clients_can_connect_at_the_same_time()
    {
        var (host, server, _) = CreateServer();
        using (host)
        using (server)
        {
            using var first = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);
            using var second = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);

            Assert.True(first.IsConfigured);
            Assert.True(second.IsConfigured);

            // 各自的连接独立验证家长密码、独立操作。
            Assert.True(first.VerifyParentPin("1234"));
            Assert.True(second.VerifyParentPin("1234"));
            Assert.Equal(StartSessionStatus.Started, first.StartGuard().Status);
            Assert.Equal(StartSessionStatus.AlreadyRunning, second.StartGuard().Status);
        }
    }

    private (SessionHost Host, NamedPipeSessionServer Server, ManualClock Clock) CreateServer()
    {
        var host = NewHost(new NoopEnforcer(), out var clock);
        var server = new NamedPipeSessionServer(host, _pipe);
        server.Start();
        return (host, server, clock);
    }

    private (RaisingEnforcer Enforcer, SessionHost Host, NamedPipeSessionServer Server) CreateServerWithRaisingEnforcer()
    {
        var enforcer = new RaisingEnforcer();
        var host = NewHost(enforcer, out _);
        var server = new NamedPipeSessionServer(host, _pipe);
        server.Start();
        return (enforcer, host, server);
    }

    [Fact]
    public void Parent_grants_extra_over_pipe_after_time_up()
    {
        var (host, server, clock) = CreateServer();
        using (host)
        using (server)
        {
            using var client = SessionClient.Connect(TimeSpan.FromSeconds(5), _pipe);

            Assert.True(client.VerifyParentPin("1234"));
            Assert.Equal(StartSessionStatus.Started, client.StartGuard().Status);

            clock.Advance(TimeSpan.FromMinutes(63));
            // client.Tick() 只回本地缓存；错误的加时请求会带回服务端最新快照。
            // 63 分钟 = 60 分钟额度 + 2 分钟保存宽限 + 余量，此刻确实超时。
            var probe = client.GrantExtra("0000", 15);
            Assert.False(probe.Ok);
            Assert.Equal(SessionPhase.TimeUp, probe.Snapshot.Phase);

            var granted = client.GrantExtra("1234", 15);
            Assert.True(granted.Ok);
            Assert.Equal(SessionPhase.InDesk, granted.Snapshot.Phase);
            Assert.Equal(BuiltinDesks.CodeId, client.Snapshot.DeskId);
            Assert.True(client.IsGuarding);
            Assert.Equal(TimeSpan.FromMinutes(75), client.Budget.Limit);

            // 批过加时的连接后续配置修改也放行（家长本人）。
            client.SaveFamily(client.Family! with { DailyMinutes = 90 });
        }
    }

    private SessionHost NewHost(IProcessEnforcer enforcer, out ManualClock clock)
    {
        clock = new ManualClock();
        var calendar = new ManualCalendar();
        var familyStore = FamilyStore.Load(Path.Combine(_dir, "family.json"));
        familyStore.Save(FamilySettings.Create("1234", 60, BuiltinDesks.CodeId));
        var deskStore = DeskStore.Load(Path.Combine(_dir, "desks.json"));
        return new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), Path.Combine(_dir, "time.json")),
            enforcer: enforcer,
            network: new NoopNetworkGuard());
    }
}

public sealed class RaisingEnforcer : IProcessEnforcer
{
    public event Action<ProcessIdentity>? Blocked;

    public bool TryEnforce(ProcessIdentity process, Desk desk) => false;

    public int SweepRunning(Desk desk) => 0;

    public void Raise(ProcessIdentity process) => Blocked?.Invoke(process);
}
