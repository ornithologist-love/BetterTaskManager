using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SystemInformer.Client.StartupManagement;

[SupportedOSPlatform("windows")]
internal sealed class StartupManagementService
{
    private const string RegistryRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryRunOnceSubKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    public Task<List<StartupItem>> GetAllStartupItemsAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new List<StartupItem>());

        return Task.Run(() =>
        {
            var items = new List<StartupItem>();
            items.AddRange(ReadRegistryItems(Registry.CurrentUser, RegistryRunSubKey, StartupLocation.RegistryCurrentUserRun));
            items.AddRange(ReadRegistryItems(Registry.LocalMachine, RegistryRunSubKey, StartupLocation.RegistryLocalMachineRun));
            items.AddRange(ReadRegistryItems(Registry.CurrentUser, RegistryRunOnceSubKey, StartupLocation.RegistryCurrentUserRun));
            items.AddRange(ReadRegistryItems(Registry.LocalMachine, RegistryRunOnceSubKey, StartupLocation.RegistryLocalMachineRun));
            items.AddRange(ReadStartupFolderItems(currentUser: true));
            items.AddRange(ReadStartupFolderItems(currentUser: false));
            items.AddRange(ReadScheduledLogonTasks());
            return items;
        });
    }

    private static List<StartupItem> ReadRegistryItems(RegistryKey rootKey, string subKey, StartupLocation location)
    {
        var items = new List<StartupItem>();
        try
        {
            using var key = rootKey.OpenSubKey(subKey, writable: false);
            if (key == null)
                return items;

            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName)?.ToString();
                if (value == null)
                    continue;

                items.Add(new StartupItem
                {
                    Name = string.IsNullOrEmpty(valueName) ? "(默认)" : valueName,
                    Path = value,
                    Location = location,
                    Description = $"注册表: {subKey}",
                    SourceLocationDetail = subKey
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取注册表启动项失败 {subKey}: {ex.Message}");
        }

        return items;
    }

    private static List<StartupItem> ReadStartupFolderItems(bool currentUser)
    {
        var items = new List<StartupItem>();
        try
        {
            var startupPath = GetStartupFolderPath(currentUser);
            if (!Directory.Exists(startupPath))
                return items;

            foreach (var file in Directory.GetFiles(startupPath, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                var target = GetShortcutTarget(file);
                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Path = string.IsNullOrEmpty(target) ? file : target,
                    Location = currentUser ? StartupLocation.StartupFolderCurrentUser : StartupLocation.StartupFolderAllUsers,
                    Description = $"启动文件夹: {Path.GetFileName(startupPath)}",
                    SourceLocationDetail = file
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取启动文件夹失败: {ex.Message}");
        }

        return items;
    }

    private static List<StartupItem> ReadScheduledLogonTasks()
    {
        var items = new List<StartupItem>();
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null)
                return items;

            var scheduler = Activator.CreateInstance(serviceType);
            if (scheduler == null)
                return items;

            serviceType.InvokeMember("Connect", BindingFlags.InvokeMethod, null, scheduler, null);
            var rootFolder = serviceType.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, scheduler, ["\\"]);
            if (rootFolder == null)
                return items;

            var tasks = rootFolder.GetType().InvokeMember("GetTasks", BindingFlags.InvokeMethod, null, rootFolder, [0]);
            if (tasks is System.Collections.IEnumerable enumerable)
            {
                foreach (var task in enumerable)
                {
                    if (task == null)
                        continue;

                    var taskType = task.GetType();
                    var enabled = taskType.InvokeMember("Enabled", BindingFlags.GetProperty, null, task, null) is true;
                    if (!enabled)
                        continue;

                    var definition = taskType.InvokeMember("Definition", BindingFlags.GetProperty, null, task, null);
                    var triggers = definition?.GetType().InvokeMember("Triggers", BindingFlags.GetProperty, null, definition, null);
                    var hasLogonOrBoot = false;
                    if (triggers is System.Collections.IEnumerable triggerList)
                    {
                        foreach (var trigger in triggerList)
                        {
                            var triggerTypeVal = trigger?.GetType().InvokeMember("Type", BindingFlags.GetProperty, null, trigger, null);
                            if (triggerTypeVal is int kind && (kind == 8 || kind == 9)) // TASK_TRIGGER_BOOT=8, TASK_TRIGGER_LOGON=9
                            {
                                hasLogonOrBoot = true;
                                break;
                            }
                        }
                    }

                    if (!hasLogonOrBoot)
                        continue;

                    var name = taskType.InvokeMember("Name", BindingFlags.GetProperty, null, task, null)?.ToString() ?? string.Empty;
                    var path = taskType.InvokeMember("Path", BindingFlags.GetProperty, null, task, null)?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(name))
                    {
                        items.Add(new StartupItem
                        {
                            Name = name,
                            Path = path,
                            Location = StartupLocation.TaskScheduler,
                            Description = "任务计划程序（开机/登录触发）",
                            SourceLocationDetail = path
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取任务计划程序失败: {ex.Message}");
        }

        return items;
    }

    public bool AddStartupItemToRegistry(StartupItem item, bool currentUser)
    {
        var rootKey = currentUser ? Registry.CurrentUser : Registry.LocalMachine;
        using var key = rootKey.OpenSubKey(RegistryRunSubKey, writable: true)
            ?? rootKey.CreateSubKey(RegistryRunSubKey, writable: true);
        if (key == null)
            return false;

        key.SetValue(item.Name, item.Path, RegistryValueKind.String);
        item.SourceLocationDetail = RegistryRunSubKey;
        item.Location = currentUser ? StartupLocation.RegistryCurrentUserRun : StartupLocation.RegistryLocalMachineRun;
        return true;
    }

    public bool AddStartupItemToStartupFolder(StartupItem item, bool currentUser)
    {
        var startupPath = GetStartupFolderPath(currentUser);
        if (!Directory.Exists(startupPath))
            Directory.CreateDirectory(startupPath);

        var shortcutPath = Path.Combine(startupPath, $"{item.Name}.lnk");
        CreateShortcut(shortcutPath, item.Path, item.Description);
        item.SourceLocationDetail = shortcutPath;
        item.Location = currentUser ? StartupLocation.StartupFolderCurrentUser : StartupLocation.StartupFolderAllUsers;
        return true;
    }

    public bool RemoveStartupItem(StartupItem item)
    {
        return item.Location switch
        {
            StartupLocation.RegistryCurrentUserRun => RemoveFromRegistry(item, currentUser: true),
            StartupLocation.RegistryLocalMachineRun => RemoveFromRegistry(item, currentUser: false),
            StartupLocation.StartupFolderCurrentUser => RemoveFromStartupFolder(item, currentUser: true),
            StartupLocation.StartupFolderAllUsers => RemoveFromStartupFolder(item, currentUser: false),
            _ => false
        };
    }

    private static bool RemoveFromRegistry(StartupItem item, bool currentUser)
    {
        var subKey = string.IsNullOrWhiteSpace(item.SourceLocationDetail) ? RegistryRunSubKey : item.SourceLocationDetail;
        var rootKey = currentUser ? Registry.CurrentUser : Registry.LocalMachine;
        using var key = rootKey.OpenSubKey(subKey, writable: true);
        if (key == null || key.GetValue(item.Name) == null)
            return false;

        key.DeleteValue(item.Name);
        return true;
    }

    private static bool RemoveFromStartupFolder(StartupItem item, bool currentUser)
    {
        var shortcutPath = !string.IsNullOrWhiteSpace(item.SourceLocationDetail) && File.Exists(item.SourceLocationDetail)
            ? item.SourceLocationDetail
            : Path.Combine(GetStartupFolderPath(currentUser), $"{item.Name}.lnk");

        if (!File.Exists(shortcutPath))
            return false;

        File.Delete(shortcutPath);
        return true;
    }

    private static string GetStartupFolderPath(bool currentUser) => currentUser
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "Startup")
        : Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    private static string GetShortcutTarget(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return string.Empty;

            var shell = Activator.CreateInstance(shellType);
            var shortcut = shell?.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            var target = shortcut?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
            return target?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM 组件不可用");

        var shell = Activator.CreateInstance(shellType);
        var shortcut = shell?.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])
            ?? throw new InvalidOperationException("无法创建快捷方式对象");

        shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
        shortcut.GetType().InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, [description]);
        shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    public static string? TryResolveTargetDirectory(StartupItem item)
    {
        var resolved = ResolvePathFromCommand(item.Path);
        if (!string.IsNullOrEmpty(resolved))
            return Directory.Exists(resolved) ? resolved : Path.GetDirectoryName(resolved);

        if (!string.IsNullOrWhiteSpace(item.SourceLocationDetail) && File.Exists(item.SourceLocationDetail))
            return Path.GetDirectoryName(item.SourceLocationDetail);

        return null;
    }

    private static string? ResolvePathFromCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (expanded.StartsWith('"'))
        {
            var endQuote = expanded.IndexOf('"', 1);
            if (endQuote > 1)
            {
                var quoted = expanded[1..endQuote];
                if (File.Exists(quoted) || Directory.Exists(quoted))
                    return quoted;
            }
        }

        if (File.Exists(expanded) || Directory.Exists(expanded))
            return expanded;

        var exeIndex = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            var candidate = expanded[..(exeIndex + 4)].Trim('"');
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
