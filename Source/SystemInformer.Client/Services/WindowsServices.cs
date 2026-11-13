using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Services;
using SystemInformer.Client.Interop;

namespace SystemInformer.Client.Services;

public sealed record StartupOption(string Label, uint Type, bool Delayed = false)
{
    public static IReadOnlyList<StartupOption> All { get; } =
    [new("自动", 2), new("自动（延迟启动）", 2, true), new("手动", 3), new("禁用", 4)];
    public override string ToString() => Label;
}

public sealed record ServiceSettings(string Description, string Path, string Account,
    StartupOption? Startup, string[] Dependencies);
public sealed record ServiceSample(string Name, string DisplayName, uint State, uint AcceptedControls,
    uint ProcessId, ServiceSettings? Settings, string SettingsError);
public sealed record ServiceDetails(ServiceSettings Settings, string Recovery, string[] Dependents);
public enum ServiceOperation { Start, Stop, Restart, Pause, Continue }

/// <summary>Local SCM access. Calls and reusable buffers are serialized by the panel.</summary>
public sealed unsafe class WindowsServices
{
    private readonly byte[] _listBuffer = new byte[256 * 1024];
    private readonly byte[] _configBuffer = new byte[8192];
    private readonly byte[] _extraBuffer = new byte[8192];
    private readonly byte[] _dependentsBuffer = new byte[64 * 1024];
    private readonly Dictionary<string, (ServiceSettings? Settings, string Error, DateTime Expires)> _settings = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ServiceSample> Capture(bool refreshSettings, CancellationToken token)
    {
        if (refreshSettings) _settings.Clear();
        using var manager = OpenManager(1 | 4);
        var result = new List<ServiceSample>();
        uint resume = 0;
        fixed (byte* buffer = _listBuffer)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                uint needed = 0, count = 0;
                var previousResume = resume;
                var success = PInvoke.EnumServicesStatusEx(manager.Value, (SC_ENUM_TYPE)0,
                    (ENUM_SERVICE_TYPE)0x30, (ENUM_SERVICE_STATE)3, buffer, (uint)_listBuffer.Length,
                    &needed, &count, &resume, default);
                var error = success ? 0 : Marshal.GetLastWin32Error();
                if (error != 0 && error != 234) throw new Win32Exception(error, "无法读取 Windows 服务列表。");
                if ((ulong)count * (uint)sizeof(ENUM_SERVICE_STATUS_PROCESSW) > (uint)_listBuffer.Length)
                    throw new InvalidDataException("Windows 返回了无效的服务数量。");
                var items = (ENUM_SERVICE_STATUS_PROCESSW*)buffer;
                for (uint i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = items[i];
                    var name = NativeErrors.Text(item.lpServiceName, buffer, _listBuffer.Length);
                    var display = NativeErrors.Text(item.lpDisplayName, buffer, _listBuffer.Length);
                    var settings = CachedSettings(manager, name);
                    result.Add(new(name, display, (uint)item.ServiceStatusProcess.dwCurrentState,
                        item.ServiceStatusProcess.dwControlsAccepted, item.ServiceStatusProcess.dwProcessId,
                        settings.Settings, settings.Error));
                }
                if (success) break;
                if (count == 0 && resume == previousResume)
                    throw new InvalidDataException("Windows 服务枚举无法继续。");
            }
        }
        var names = result.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _settings.Keys.Where(name => !names.Contains(name)).ToArray()) _settings.Remove(name);
        return result;
    }

    public ServiceDetails ReadDetails(string name)
    {
        using var manager = OpenManager(1);
        using var service = OpenService(manager, name, 1);
        var settings = ReadSettings(service);
        _settings[name] = (settings, "", DateTime.UtcNow.AddSeconds(30));
        string recovery;
        try { recovery = ReadExtra(service, 2, ReadRecovery); }
        catch (Win32Exception error) { recovery = "恢复策略不可读取：" + error.Message; }
        string[] dependents;
        try
        {
            using var dependencyService = OpenService(manager, name, 8);
            dependents = ReadDependents(dependencyService);
        }
        catch (Win32Exception error) { dependents = ["依存关系不可读取：" + error.Message]; }
        return new(settings, recovery, dependents);
    }

    public (uint State, uint Controls, uint ProcessId, uint ExitCode) GetStatus(string name)
    {
        using var manager = OpenManager(1);
        using var service = OpenService(manager, name, 4);
        SERVICE_STATUS_PROCESS status = default;
        uint needed = 0;
        if (!PInvoke.QueryServiceStatusEx(service.Value, (SC_STATUS_TYPE)0,
            (byte*)&status, (uint)sizeof(SERVICE_STATUS_PROCESS), &needed))
            throw NativeErrors.Last("无法读取服务状态。");
        return ((uint)status.dwCurrentState, status.dwControlsAccepted, status.dwProcessId, status.dwWin32ExitCode);
    }

    internal string GetDisplayName(string name)
    {
        using var manager = OpenManager(1);
        char* display = stackalloc char[1024];
        uint length = 1024;
        fixed (char* serviceName = name)
            if (!PInvoke.GetServiceDisplayName(manager.Value, new PCWSTR(serviceName), new PWSTR(display), &length))
                throw NativeErrors.Last("读取服务显示名称失败。");
        if (length is 0 or >= 1024) throw new InvalidDataException("Windows 返回了无效的服务显示名称。");
        return new string(display, 0, (int)length);
    }

    public void Request(string name, ServiceOperation operation)
    {
        var access = operation switch
        {
            ServiceOperation.Start => 0x10u,
            ServiceOperation.Stop => 0x20u,
            ServiceOperation.Pause or ServiceOperation.Continue => 0x40u,
            _ => throw new ArgumentException("重启必须按停止、等待、启动顺序执行。", nameof(operation))
        };
        using var manager = OpenManager(1);
        using var service = OpenService(manager, name, access);
        if (operation == ServiceOperation.Start)
        {
            if (!PInvoke.StartService(service.Value, 0, null))
            {
                var error = NativeErrors.Last("启动服务失败。");
                if (error.NativeErrorCode != 1056) throw error;
            }
        }
        else
        {
            var control = operation switch { ServiceOperation.Stop => 1u, ServiceOperation.Pause => 2u, _ => 3u };
            SERVICE_STATUS status = default;
            if (!PInvoke.ControlService(service.Value, control, &status))
            {
                var error = NativeErrors.Last("控制服务失败。");
                if (operation != ServiceOperation.Stop || error.NativeErrorCode != 1062) throw error;
            }
        }
    }

    public void SetStartup(string name, StartupOption startup)
    {
        ValidateStartup(startup);
        using var manager = OpenManager(1);
        using var service = OpenService(manager, name, 1 | 2);
        var before = ReadSettings(service).Startup
            ?? throw new InvalidOperationException("当前服务启动类型不可识别，不能修改。");
        ChangeStartup(service, startup.Type);
        if (startup.Type == 2)
        {
            var delayed = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = startup.Delayed };
            if (!PInvoke.ChangeServiceConfig2W(service.Value, (SERVICE_CONFIG)3, &delayed))
            {
                var error = NativeErrors.Last("设置延迟自动启动失败。");
                try
                {
                    ChangeStartup(service, before.Type);
                    if (before.Type == 2)
                    {
                        delayed.fDelayedAutostart = before.Delayed;
                        if (!PInvoke.ChangeServiceConfig2W(service.Value, (SERVICE_CONFIG)3, &delayed))
                            throw NativeErrors.Last("恢复原延迟启动设置失败。");
                    }
                }
                catch (Win32Exception rollback)
                {
                    throw new InvalidOperationException($"{error.Message} 原设置恢复失败：{rollback.Message}，请刷新后检查服务配置。", error);
                }
                throw error;
            }
        }
        _settings.Remove(name);
    }

    public bool MatchesStartup(string name, StartupOption expected)
    {
        using var manager = OpenManager(1);
        using var service = OpenService(manager, name, 1);
        var current = ReadSettings(service).Startup;
        _settings.Remove(name);
        return current != null && current.Type == expected.Type && current.Delayed == expected.Delayed;
    }

    public static void ValidateStartup(StartupOption option)
    {
        if (option.Type is not (2 or 3 or 4) || (option.Delayed && option.Type != 2))
            throw new ArgumentException("不支持的服务启动类型。", nameof(option));
    }

    private (ServiceSettings? Settings, string Error, DateTime Expires) CachedSettings(ServiceHandle manager, string name)
    {
        if (_settings.TryGetValue(name, out var cached) && cached.Expires > DateTime.UtcNow) return cached;
        try
        {
            using var service = OpenService(manager, name, 1);
            cached = (ReadSettings(service), "", DateTime.UtcNow.AddSeconds(30));
        }
        catch (Exception error) when (error is Win32Exception or InvalidDataException)
        {
            cached = (null, "配置不可读取：" + error.Message, DateTime.UtcNow.AddSeconds(10));
        }
        _settings[name] = cached;
        return cached;
    }

    private ServiceSettings ReadSettings(ServiceHandle service)
    {
        fixed (byte* buffer = _configBuffer)
        {
            uint needed = 0;
            var config = (QUERY_SERVICE_CONFIGW*)buffer;
            if (!PInvoke.QueryServiceConfig(service.Value, config, (uint)_configBuffer.Length, &needed))
                throw NativeErrors.Last("无法读取服务配置。");
            var delayed = config->dwStartType == (SERVICE_START_TYPE)2
                && ReadExtra(service, 3, (extra, _) => ((SERVICE_DELAYED_AUTO_START_INFO*)extra)->fDelayedAutostart.Value != 0);
            var type = (uint)config->dwStartType;
            var startup = StartupOption.All.FirstOrDefault(option => option.Type == type && option.Delayed == delayed);
            var description = ReadExtra(service, 1, (extra, size) => NativeErrors.Text(((SERVICE_DESCRIPTIONW*)extra)->lpDescription, extra, size));
            return new(description, NativeErrors.Text(config->lpBinaryPathName, buffer, _configBuffer.Length),
                NativeErrors.Text(config->lpServiceStartName, buffer, _configBuffer.Length), startup,
                NativeErrors.MultiText(config->lpDependencies, buffer, _configBuffer.Length));
        }
    }

    private string[] ReadDependents(ServiceHandle service)
    {
        fixed (byte* buffer = _dependentsBuffer)
        {
            uint needed = 0, count = 0;
            if (!PInvoke.EnumDependentServices(service.Value, (ENUM_SERVICE_STATE)3, (ENUM_SERVICE_STATUSW*)buffer,
                (uint)_dependentsBuffer.Length, &needed, &count)) throw NativeErrors.Last("无法读取依存关系。");
            if ((ulong)count * (uint)sizeof(ENUM_SERVICE_STATUSW) > (uint)_dependentsBuffer.Length)
                throw new InvalidDataException("Windows 返回了无效的依赖服务数量。");
            var result = new string[count];
            var items = (ENUM_SERVICE_STATUSW*)buffer;
            for (uint i = 0; i < count; i++)
                result[i] = NativeErrors.Text(items[i].lpDisplayName, buffer, _dependentsBuffer.Length)
                    + "（" + NativeErrors.Text(items[i].lpServiceName, buffer, _dependentsBuffer.Length) + "）";
            return result;
        }
    }

    private delegate T ExtraReader<T>(byte* buffer, int size);
    private T ReadExtra<T>(ServiceHandle service, uint level, ExtraReader<T> read)
    {
        fixed (byte* buffer = _extraBuffer)
        {
            uint needed = 0;
            if (!PInvoke.QueryServiceConfig2W(service.Value, (SERVICE_CONFIG)level, buffer, (uint)_extraBuffer.Length, &needed))
                throw NativeErrors.Last("无法读取服务属性。");
            return read(buffer, _extraBuffer.Length);
        }
    }

    private static string ReadRecovery(byte* buffer, int size)
    {
        var config = (SERVICE_FAILURE_ACTIONSW*)buffer;
        if (config->cActions > 64 || (config->cActions > 0 && (config->lpsaActions == null
            || (byte*)config->lpsaActions < buffer
            || (byte*)(config->lpsaActions + config->cActions) > buffer + size)))
            throw new InvalidDataException("Windows 返回了无效的恢复策略。");
        var lines = new List<string>
        {
            config->dwResetPeriod == uint.MaxValue ? "失败计数：不重置" : $"失败计数重置：{config->dwResetPeriod / 86400d:N1} 天"
        };
        for (uint i = 0; i < config->cActions; i++)
        {
            var action = config->lpsaActions[i];
            var label = (uint)action.Type switch { 0 => "不操作", 1 => "重新启动服务", 2 => "重新启动计算机", 3 => "运行程序", _ => "未知操作" };
            lines.Add($"第 {i + 1} 次失败：{label}，延迟 {action.Delay / 1000d:N0} 秒");
        }
        if (config->cActions == 0) lines.Add("失败操作：不操作");
        var command = NativeErrors.Text(config->lpCommand, buffer, size);
        if (command.Length > 0) lines.Add("运行程序：" + command);
        var reboot = NativeErrors.Text(config->lpRebootMsg, buffer, size);
        if (reboot.Length > 0) lines.Add("重启消息：" + reboot);
        return string.Join(Environment.NewLine, lines);
    }

    private static ServiceHandle OpenManager(uint access)
    {
        var manager = new ServiceHandle(PInvoke.OpenSCManager(default, default, access));
        if (!manager.IsInvalid) return manager;
        var error = NativeErrors.Last("无法连接本机服务管理器。");
        manager.Dispose();
        throw error;
    }

    private static ServiceHandle OpenService(ServiceHandle manager, string name, uint access)
    {
        fixed (char* pointer = name)
        {
            var service = new ServiceHandle(PInvoke.OpenService(manager.Value, new PCWSTR(pointer), access));
            if (!service.IsInvalid) return service;
            var error = NativeErrors.Last($"无法访问服务 {name}。");
            service.Dispose();
            throw error;
        }
    }

    private static void ChangeStartup(ServiceHandle service, uint type)
    {
        if (!PInvoke.ChangeServiceConfig(service.Value, (ENUM_SERVICE_TYPE)uint.MaxValue,
            (SERVICE_START_TYPE)type, (SERVICE_ERROR)uint.MaxValue,
            default, default, null, default, default, default, default))
            throw NativeErrors.Last("修改启动类型失败。");
    }
}
