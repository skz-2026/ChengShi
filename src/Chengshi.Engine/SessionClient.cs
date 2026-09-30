using System.IO.Pipes;
using Chengshi.Core;
using Chengshi.Ipc;

namespace Chengshi.Engine;

/// <summary>
/// 通过命名管道连接守护服务（Chengshi.Service）的会话客户端。
/// 连接不上（服务没装/没起）时构造失败，界面程序应回退到本机 SessionHost。
/// 读取循环是唯一读端；命令请求只写并等待带 RequestId 的应答；
/// 服务端每秒推送 StateMessage 保持快照新鲜，Tick() 只读缓存。
/// </summary>
public sealed class SessionClient : ISessionControl
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<ServerMessage>> _pending = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipeName;
    private readonly bool _verifyServer;
    private NamedPipeClientStream? _pipe;
    private Task? _readLoop;
    private int _nextRequestId;
    private volatile bool _disposed;
    private volatile bool _connected;
    private ConfigMessage? _config;
    private IReadOnlyList<AppUsage> _appUsage = [];
    private SessionSnapshot _snapshot = new(SessionPhase.Idle, null, null, TimeSpan.Zero, false, false);

    private SessionClient(string pipeName, bool verifyServer)
    {
        _pipeName = pipeName;
        _verifyServer = verifyServer;
    }

    public static SessionClient Connect(TimeSpan? timeout = null, string? pipeName = null, bool verifyServer = false)
    {
        var client = new SessionClient(pipeName ?? PipeNames.Default, verifyServer);
        try
        {
            client.ConnectCore(timeout ?? TimeSpan.FromSeconds(3), verifyServer);
        }
        catch (Exception)
        {
            // 握手失败也要把已连上的管道放干净：界面程序不 Dispose 这个半成品的话，
            // 句柄泄漏会让服务端的接收一直挂着等一个再也不说话的客户端。
            client.Dispose();
            throw;
        }

        return client;
    }

    public IReadOnlyList<Desk> Desks => _config?.Desks ?? [];

    public FamilySettings? Family => _config?.Family;

    public DailyBudget Budget => _config?.Budget
        ?? new DailyBudget(DateOnly.FromDateTime(DateTime.Now), TimeSpan.FromMinutes(60), TimeSpan.Zero);

    public IReadOnlyList<AppUsage> AppUsage => _appUsage;

    public bool IsConfigured => Family is not null;

    public bool IsGuarding => _snapshot.IsGuarding;

    public bool IsRemote => true;

    public string EtwHint => _config?.EtwHint ?? string.Empty;

    public string GuardHint => _config?.GuardHint ?? string.Empty;

    public SessionSnapshot Snapshot => _snapshot;

    public event Action<SessionSnapshot>? StateChanged;
    public event Action<BlockedMessage>? ProcessBlocked;
    public event Action<bool>? ConnectionChanged;

    private void ConnectCore(TimeSpan timeout, bool verifyServer)
    {
        _pipe = OpenPipe(timeout, verifyServer);
        _connected = true;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        var config = Request<ConfigMessage>(new GetConfigRequest(), timeout);
        ApplyConfig(config);
    }

    private NamedPipeClientStream OpenPipe(TimeSpan timeout, bool verifyServer = false)
    {
        var pipe = PipeFactory.CreateClient(_pipeName);
        try
        {
            pipe.Connect((int)Math.Max(1, timeout.TotalMilliseconds));
            if (verifyServer)
            {
                // 管道名可能被本机任意进程先占：核对对端确实是澄时服务，防钓鱼。
                PipeFactory.VerifyServerProcess(pipe, "chengshi.service.exe");
            }

            return pipe;
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            throw new InvalidOperationException("连不上澄时守护服务。", ex);
        }
    }

    public Desk? FindDesk(string id) =>
        Desks.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? BuiltinDesks.Find(id);

    public Desk SaveDesk(Desk desk)
    {
        var config = Request<ConfigMessage>(new SaveDeskRequest(desk), TimeSpan.FromSeconds(5));
        ApplyConfig(config);
        return desk;
    }

    public FamilySettings SaveFamily(FamilySettings settings)
    {
        var config = Request<ConfigMessage>(new SaveFamilyRequest(settings), TimeSpan.FromSeconds(5));
        ApplyConfig(config);
        return settings;
    }

    public bool VerifyParentPin(string? pin) =>
        Request<BoolMessage>(new VerifyPinRequest(pin ?? string.Empty), TimeSpan.FromSeconds(5)).Value;

    public FamilySettings ChangePin(string oldPin, string newPin)
    {
        var config = Request<ConfigMessage>(new ChangePinRequest(oldPin, newPin), TimeSpan.FromSeconds(5));
        ApplyConfig(config);
        return config.Family ?? throw new InvalidOperationException("还没有设置家长密码。");
    }

    public FamilySettings RecoverPin(string token, string newPin)
    {
        var config = Request<ConfigMessage>(new RecoverPinRequest(token, newPin), TimeSpan.FromSeconds(5));
        ApplyConfig(config);
        return config.Family ?? throw new InvalidOperationException("还没有设置家长密码。");
    }

    public async Task<PinResetResult> RecoverPinWithEmailAsync(string email, string code, string newPin)
    {
        var reply = Request<RecoverPinReply>(
            new EmailRecoveryResetRequest(email, code, newPin),
            TimeSpan.FromSeconds(15));
        RefreshConfigQuietly();
        return await Task.FromResult(new PinResetResult(
            _config?.Family ?? Family!,
            reply.Hint,
            reply.NewRecoveryCode)).ConfigureAwait(false);
    }

    public Task SendEmailRecoveryCodeAsync(string email)
    {
        Request<BoolMessage>(new EmailRecoveryCodeRequest(email), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task<SmtpConfig?> GetSmtpAsync()
    {
        var reply = Request<SmtpConfigReply>(new GetSmtpRequest(), TimeSpan.FromSeconds(5));
        SmtpConfig? config = reply.Host.Length == 0 && !reply.HasPassword
            ? null
            : new SmtpConfig(reply.Host, reply.Port, reply.UseSsl, reply.User, string.Empty);
        return Task.FromResult(config);
    }

    public Task SaveSmtpAsync(SmtpConfig config)
    {
        Request<BoolMessage>(new SaveSmtpRequest(config), TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    public StartSessionResult StartGuard()
    {
        var reply = Request<StartReply>(new StartGuardRequest(), TimeSpan.FromSeconds(5));
        ApplyState(reply.Snapshot);
        RefreshConfigQuietly();
        return new StartSessionResult(reply.Status, reply.Snapshot);
    }

    public StartSessionResult Start(string deskId, TimeSpan duration, bool pinned, string? pin, TimeSpan grace = default)
    {
        var reply = Request<StartReply>(
            new StartSessionRequest(
                deskId,
                (int)Math.Max(1, duration.TotalMinutes),
                pinned,
                pin,
                grace.TotalMinutes),
            TimeSpan.FromSeconds(5));
        ApplyState(reply.Snapshot);
        RefreshConfigQuietly();
        return new StartSessionResult(reply.Status, reply.Snapshot);
    }

    public StopSessionResult Stop(string? pin)
    {
        var reply = Request<StopReply>(new StopSessionRequest(pin), TimeSpan.FromSeconds(5));
        ApplyState(reply.Snapshot);
        RefreshConfigQuietly();
        return new StopSessionResult(reply.Status, reply.Snapshot);
    }

    public GrantExtraResult GrantExtra(string? pin, int minutes)
    {
        var reply = Request<GrantExtraReply>(new GrantExtraRequest(minutes, pin), TimeSpan.FromSeconds(5));
        ApplyState(reply.Snapshot);
        RefreshConfigQuietly();
        return new GrantExtraResult(reply.Ok, reply.Hint, reply.Snapshot);
    }

    public SessionSnapshot Tick() => _snapshot;

    private void RefreshConfigQuietly()
    {
        try
        {
            var config = Request<ConfigMessage>(new GetConfigRequest(), TimeSpan.FromSeconds(2));
            ApplyConfig(config);
        }
        catch (Exception)
        {
            // 下次成功请求时再刷新。
        }
    }

    private void ApplyState(SessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        StateChanged?.Invoke(snapshot);
    }

    private void ApplyConfig(ConfigMessage config)
    {
        _config = config;
        _snapshot = config.Snapshot;
        _appUsage = config.AppUsage ?? [];
        StateChanged?.Invoke(config.Snapshot);
    }

    private T Request<T>(ClientMessage message, TimeSpan timeout)
        where T : ServerMessage
    {
        var pipe = _pipe;
        if (pipe is null || !_connected)
        {
            throw new RemoteFaultException("守护服务未连接。");
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var tagged = message with { RequestId = id };
        var tcs = new TaskCompletionSource<ServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending[id] = tcs;
        }

        try
        {
            _writeLock.Wait();
            try
            {
                MessageSerializer.WriteAsync(pipe, tagged, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                _writeLock.Release();
            }

            if (!tcs.Task.Wait(timeout))
            {
                throw new TimeoutException("守护服务没有响应。");
            }

            return tcs.Task.Result switch
            {
                T typed => typed,
                ErrorMessage error => throw new RemoteFaultException(error.Message),
                _ => throw new RemoteFaultException("守护服务返回了意外的应答。"),
            };
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var pipe = _pipe;
        if (pipe is null)
        {
            return;
        }

        var broken = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var message = await MessageSerializer.ReadAsync<ServerMessage>(pipe, token).ConfigureAwait(false);
                if (message is null)
                {
                    broken = true;
                    break;
                }

                HandleMessage(message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                broken = true;
                break;
            }
        }

        if (broken && !_disposed)
        {
            HandleDisconnected();
            _ = ReconnectLoopAsync();
        }
    }

    private void HandleMessage(ServerMessage message)
    {
        if (message.RequestId > 0)
        {
            TaskCompletionSource<ServerMessage>? tcs;
            lock (_gate)
            {
                if (_pending.TryGetValue(message.RequestId, out tcs))
                {
                    _pending.Remove(message.RequestId);
                }
            }

            tcs?.TrySetResult(message);
            return;
        }

        switch (message)
        {
            case StateMessage state:
                _appUsage = state.AppUsage ?? _appUsage;
                ApplyState(state.ToSnapshot());
                break;
            case ConfigMessage config:
                ApplyConfig(config);
                break;
            case BlockedMessage blocked:
                ProcessBlocked?.Invoke(blocked);
                break;
        }
    }

    private void HandleDisconnected()
    {
        _connected = false;
        try
        {
            _pipe?.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        _pipe = null;
        lock (_gate)
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(new RemoteFaultException("守护服务连接中断。"));
            }

            _pending.Clear();
        }

        ConnectionChanged?.Invoke(false);
    }

    private async Task ReconnectLoopAsync()
    {
        while (!_disposed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                var pipe = OpenPipe(TimeSpan.FromSeconds(2.5), _verifyServer);
                _pipe = pipe;
                _connected = true;
                ConnectionChanged?.Invoke(true);
                _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
                var config = Request<ConfigMessage>(new GetConfigRequest(), TimeSpan.FromSeconds(5));
                ApplyConfig(config);
                return;
            }
            catch (Exception)
            {
                try
                {
                    _pipe?.Dispose();
                }
                catch (Exception)
                {
                    // ignore
                }

                _pipe = null;
                _connected = false;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        lock (_gate)
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetCanceled();
            }

            _pending.Clear();
        }

        try
        {
            _pipe?.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        _pipe = null;
        _connected = false;
        try
        {
            _readLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // ignore
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }
}
