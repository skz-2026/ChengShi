using Chengshi.Engine;

namespace Chengshi.Service;

public sealed class ChengshiWorker : BackgroundService
{
    private readonly SessionHost _host;
    private readonly ILogger<ChengshiWorker> _logger;

    public ChengshiWorker(SessionHost host, ILogger<ChengshiWorker> logger)
    {
        _host = host;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StorePaths.EnsureConfigured();
        // 数据目录收紧成 Users 只读：孩子账号改不了 desks/family 配置。
        // 放在服务启动时做（SYSTEM 有权改 ACL），安装脚本的 icacls 只是第一道。
        StorePaths.EnsureDataDirHardened();
        _logger.LogInformation("澄时守护服务已启动。{EtwHint}", _host.EtwHint);
        using var pipe = new NamedPipeSessionServer(
            _host,
            log: message => _logger.LogInformation("{Message}", message));
        pipe.Start();

        // 开机守护：孩子重启电脑也逃不掉（除非家长在应用里暂停）。
        if (_host.Family?.GuardOnLaunch == true)
        {
            try
            {
                var result = _host.StartGuard();
                _logger.LogInformation("开机守护：{Status}。{GuardHint}", result.Status, _host.GuardHint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "开机守护失败。");
            }
        }

        var ticks = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                // 心跳里任何一次异常都不许逃出去：这个循环一挂，using 会把管道服务器
                // 一并 Dispose，进程却还活着——服务看起来在跑，界面却永远连不上
                // （真机上出过这种「活着但失联」的事故）。记日志、下一秒继续。
                try
                {
                    _host.Tick();

                    // 界面程序走管道时这里总是最新；只有它没连上服务、自己改磁盘时，
                    // 才需要热刷新跟上。每 5 秒核对一次，开销可忽略。
                    if (++ticks % 5 == 0)
                    {
                        _host.RefreshFromDisk();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "守护心跳第 {Ticks} 拍出错，已跳过。", ticks);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
