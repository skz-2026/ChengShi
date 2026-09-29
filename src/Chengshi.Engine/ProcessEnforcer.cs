using System.Diagnostics;
using Chengshi.Core;

namespace Chengshi.Engine;

public class ProcessEnforcer : IProcessEnforcer
{
    private readonly AllowlistMatcher _matcher = new();
    private readonly HashSet<int> _recentlyBlocked = [];
    private readonly object _gate = new();
    private readonly Func<int>? _activeSession;

    public ProcessEnforcer(Func<int>? activeSession = null)
    {
        _activeSession = activeSession;
    }

    public event Action<ProcessIdentity>? Blocked;

    protected void OnBlocked(ProcessIdentity process) => Blocked?.Invoke(process);

    public bool TryEnforce(ProcessIdentity process, Desk desk)
    {
        if (process.Pid is 0 or 4)
        {
            return false;
        }

        // 只守护当前交互会话：服务的会话 0、其他登录用户一律放行。
        var active = _activeSession?.Invoke() ?? ActiveSession.ConsoleSessionId;
        if (active == 0 || process.SessionId != active)
        {
            return false;
        }

        // 「整个电脑」场景：软件一律放行；计时长与到点锁屏由会话状态机负责。
        // 唯一的例外是当天单独限时已用完的软件——EffectiveDesk 只把它们留在名单里
        // 交进来，名单在这里的含义从「允许」翻转成「拒绝」。系统关键进程
        // （AlwaysAllow）仍保底放行：拒绝名单匹配不能借用 IsAllowed，
        // 它的保底放行在这种模式下会被误读成「命中名单」。
        if (desk.Unrestricted)
        {
            if (desk.Apps.Count == 0
                || !_matcher.MatchesDeskRules(process, desk)
                || AlwaysAllow.IsAlwaysAllowed(process.FileName, process.ImagePath))
            {
                return false;
            }
        }
        else if (_matcher.IsAllowed(process, desk))
        {
            return false;
        }

        if (!TryKill(process.Pid))
        {
            return false;
        }

        lock (_gate)
        {
            _recentlyBlocked.Add(process.Pid);
        }

        OnBlocked(process);
        return true;
    }

    public int SweepRunning(Desk desk)
    {
        var blocked = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                var identity = ProcessIdentityFactory.FromPid(process.Id, hintName: process.ProcessName);
                if (TryEnforce(identity, desk))
                {
                    blocked++;
                }
            }
            catch (Exception)
            {
                // 个别进程瞬时退出，忽略。
            }
            finally
            {
                process.Dispose();
            }
        }

        return blocked;
    }

    protected virtual bool TryKill(int pid)
    {
        if (pid is 0 or 4 || pid == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
