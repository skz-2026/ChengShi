using System.Diagnostics;
using System.Runtime.InteropServices;
using Chengshi.Core;

namespace Chengshi.Engine;

public sealed record InstalledApp(string DisplayName, string FileName, string? ImagePath, string Source);

public static class InstalledAppCatalog
{
    /// <summary>dailyMinutes 透传单软件限时：家长在名单里挑来挑去不该把已设的限额弄丢。</summary>
    public static AllowedApp ToAllowed(this InstalledApp app, int? dailyMinutes = null) =>
        new(app.DisplayName, app.FileName, app.ImagePath, dailyMinutes);

    public static IReadOnlyList<InstalledApp> Scan()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return ScanCore();
        }

        IReadOnlyList<InstalledApp>? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = ScanCore();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }

        return result ?? [];
    }

    private static IReadOnlyList<InstalledApp> ScanCore()
    {
        var map = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in StartMenuFolders())
        {
            ScanShortcuts(folder, map);
        }

        ProbeWellKnown(map);
        ScanRunning(map);
        return map.Values
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> StartMenuFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static void ScanShortcuts(string root, Dictionary<string, InstalledApp> map)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        Type? shellType;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
        }
        catch (Exception)
        {
            return;
        }

        if (shellType is null)
        {
            return;
        }

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            foreach (var link in EnumerateLinks(root))
            {
                TryAddShortcut(shell, link, map);
            }
        }
        finally
        {
            if (shell is not null)
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static IEnumerable<string> EnumerateLinks(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files = [];
            string[] subs = [];
            try
            {
                files = Directory.GetFiles(dir, "*.lnk");
            }
            catch (Exception)
            {
                // 个别开始菜单子目录无权限，跳过。
            }

            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                // ignore
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var sub in subs)
            {
                pending.Push(sub);
            }
        }
    }

    private static void TryAddShortcut(object shell, string link, Dictionary<string, InstalledApp> map)
    {
        object? shortcut = null;
        try
        {
            dynamic dshell = shell;
            shortcut = dshell.CreateShortcut(link);
            if (shortcut is null)
            {
                return;
            }

            dynamic sc = shortcut;
            string? target = Convert.ToString(sc.TargetPath);
            if (!IsUsefulExe(target))
            {
                return;
            }

            var name = Path.GetFileNameWithoutExtension(link);
            if (IsJunkName(name) || IsJunkName(Path.GetFileNameWithoutExtension(target)))
            {
                return;
            }

            var fileName = Path.GetFileName(target)!;
            if (AlwaysAllow.IsAlwaysAllowed(fileName, target))
            {
                return;
            }

            map[target!] = new InstalledApp(name, fileName, target, "开始菜单");
        }
        catch (Exception)
        {
            // 个别快捷方式损坏，跳过。
        }
        finally
        {
            if (shortcut is not null)
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
        }
    }

    private static void ProbeWellKnown(Dictionary<string, InstalledApp> map)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var system = Environment.SystemDirectory;
        (string Name, string Path)[] known =
        [
            ("记事本", Path.Combine(system, "notepad.exe")),
            ("画图", Path.Combine(system, "mspaint.exe")),
            ("计算器", Path.Combine(system, "calc.exe")),
            ("命令提示符", Path.Combine(system, "cmd.exe")),
            ("PowerShell", Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe")),
            ("Edge", Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe")),
            ("Edge", Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe")),
            ("Word", Path.Combine(programFiles, @"Microsoft Office\root\Office16\WINWORD.EXE")),
            ("Excel", Path.Combine(programFiles, @"Microsoft Office\root\Office16\EXCEL.EXE")),
            ("PowerPoint", Path.Combine(programFiles, @"Microsoft Office\root\Office16\POWERPNT.EXE")),
        ];

        foreach (var (name, path) in known)
        {
            if (!IsUsefulExe(path) || map.ContainsKey(path))
            {
                continue;
            }

            map[path] = new InstalledApp(name, Path.GetFileName(path), path, "本机");
        }
    }

    private static void ScanRunning(Dictionary<string, InstalledApp> map)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    continue;
                }

                if (!IsUsefulExe(path) || AlwaysAllow.IsAlwaysAllowed(Path.GetFileName(path), path))
                {
                    continue;
                }

                if (map.ContainsKey(path!))
                {
                    continue;
                }

                var fileName = Path.GetFileName(path)!;
                if (IsJunkName(Path.GetFileNameWithoutExtension(fileName)))
                {
                    continue;
                }

                var title = FriendlyName(path!, process.MainWindowTitle, fileName);
                map[path!] = new InstalledApp(title, fileName, path, "正在运行");
            }
            catch (Exception)
            {
                // ignore
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public static InstalledApp FromExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var fileName = Path.GetFileName(full);
        return new InstalledApp(FriendlyName(full, null, fileName), fileName, full, "文件");
    }

    private static string FriendlyName(string path, string? windowTitle, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(windowTitle) && windowTitle.Trim().Length is > 0 and < 40)
        {
            return windowTitle.Trim();
        }

        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static bool IsUsefulExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        var file = Path.GetFileNameWithoutExtension(path);
        return !IsJunkName(file);
    }

    private static readonly string[] JunkNames =
    [
        "uninstall", "unins000", "uninst", "setup", "install",
        "卸载", "安装程序", "help", "readme", "update", "updater",
        "crashpad", "notification_helper", "elevation_service",
    ];

    private static bool IsJunkName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return JunkNames.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
