using System.Diagnostics;
using Chengshi.Core;

namespace Chengshi.Engine;

/// <summary>
/// 找出「当前书桌里被允许、而且此刻正在运行」的软件。只用于用量记账，不做拦截。
/// </summary>
public interface IRunningAppProbe
{
    /// <summary>返回正在运行的软件的 Key（= AllowedApp.Key）。</summary>
    IReadOnlyCollection<string> RunningKeys(Desk desk);
}

/// <summary>
/// 按进程名匹配允许名单。只统计当前交互会话的进程——
/// 与拦截逻辑保持一致：其他登录用户和系统服务的进程不算孩子在用。
/// </summary>
    public sealed class ProcessRunningAppProbe : IRunningAppProbe
    {
        public IReadOnlyCollection<string> RunningKeys(Desk desk)
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in desk.Apps)
            {
                byStem[Stem(app.FileName)] = app.Key;
            }

            // 「整个电脑」场景不限软件：把交互会话里所有进程都记进来（按进程名），
            // 统计页才能看到时间都花在哪些软件上；单独限时的软件按名单里的 Key 记，
            // 限额判定与设置页的增删改都以 Key 为准。
            if (desk.Unrestricted)
            {
                var activeNow = ActiveSession.ConsoleSessionId;
                if (activeNow == 0)
                {
                    return running;
                }

                var stems = new List<string>();
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId || process.SessionId != activeNow)
                        {
                            continue;
                        }

                        stems.Add(process.ProcessName);
                    }
                    catch (Exception)
                    {
                        // 进程瞬时退出，跳过。
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                return MapKeys(byStem, stems);
            }

            if (byStem.Count == 0)
            {
                return running;
            }

            var active = ActiveSession.ConsoleSessionId;
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    // 会话 0（服务）与取不到控制台会话时都不记账，避免把系统进程算到孩子头上。
                    if (active == 0 || process.SessionId != active)
                    {
                        continue;
                    }

                    if (byStem.TryGetValue(process.ProcessName, out var key))
                    {
                        running.Add(key);
                    }
                }
                catch (Exception)
                {
                    // 进程瞬时退出，跳过。
                }
                finally
                {
                    process.Dispose();
                }
            }

            return running;
        }

        /// <summary>限时名单里的软件用它的 Key（ImagePath ?? FileName）记账，其余按进程名原样记。</summary>
        public static IReadOnlyCollection<string> MapKeys(
            Dictionary<string, string> byStem,
            IEnumerable<string> processStems)
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stem in processStems)
            {
                running.Add(byStem.TryGetValue(stem, out var key) ? key : stem);
            }

            return running;
        }

        private static string Stem(string fileName) =>
            Path.GetFileNameWithoutExtension(fileName.Trim());
    }
