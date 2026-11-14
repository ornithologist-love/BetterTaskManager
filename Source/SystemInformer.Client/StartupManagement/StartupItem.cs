namespace SystemInformer.Client.StartupManagement;

public enum StartupLocation
{
    RegistryCurrentUserRun,
    RegistryLocalMachineRun,
    StartupFolderCurrentUser,
    StartupFolderAllUsers,
    TaskScheduler
}

public sealed class StartupItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public StartupLocation Location { get; set; }
    public string Description { get; set; } = string.Empty;
    public string SourceLocationDetail { get; set; } = string.Empty;

    public string LocationDisplayName => Location switch
    {
        StartupLocation.RegistryCurrentUserRun => "当前用户注册表",
        StartupLocation.RegistryLocalMachineRun => "系统注册表",
        StartupLocation.StartupFolderCurrentUser => "当前用户启动文件夹",
        StartupLocation.StartupFolderAllUsers => "所有用户启动文件夹",
        StartupLocation.TaskScheduler => "任务计划程序",
        _ => Location.ToString()
    };

    public bool CanRemove => Location is StartupLocation.RegistryCurrentUserRun
        or StartupLocation.RegistryLocalMachineRun
        or StartupLocation.StartupFolderCurrentUser
        or StartupLocation.StartupFolderAllUsers;
}
