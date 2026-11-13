using System.ComponentModel;
using System.Runtime.CompilerServices;
using SystemInformer.Client.Processes;
using SystemInformer.Client.Services;

namespace SystemInformer.Client;

internal abstract class ObservableRow : INotifyPropertyChanged
{
    private readonly HashSet<string> _pendingChanges = new(StringComparer.Ordinal);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal IReadOnlySet<string> PendingChanges => _pendingChanges;
    protected void Changed([CallerMemberName] string name = "") => _pendingChanges.Add(name);
    internal void PublishChanges()
    {
        var names = _pendingChanges.ToArray();
        _pendingChanges.Clear();
        foreach (var name in names) PropertyChanged?.Invoke(this, new(name));
    }
    protected void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
    }
}

internal sealed class ProcessRow(ProcessSample sample) : ObservableRow
{
    public const string ForegroundSection = "前台";
    public const string BackgroundSection = "后台";
    private string _section = BackgroundSection;
    public string Section => _section;
    public void SetSection(bool foreground) => Set(ref _section, foreground ? ForegroundSection : BackgroundSection, nameof(Section));
    public uint Id { get; } = sample.Id;
    public bool IsSynthetic { get; } = sample.IsSynthetic;
    public string DisplayId => sample.IsSynthetic ? "—" : Id.ToString();
    public ulong? Created { get; } = sample.Created;
    private uint _parentId = sample.ParentId;
    public uint ParentId { get => _parentId; private set => Set(ref _parentId, value); }
    private ProcessIcon? _icon = sample.Icon;
    public ProcessIcon? Icon { get => _icon; private set => Set(ref _icon, value); }
    private string _windowRole = sample.WindowRole, _executionState = sample.ExecutionState;
    private double? _disk = sample.DiskMiBPerSecond, _network = sample.NetworkMbps;
    public string WindowRole { get => _windowRole; private set => Set(ref _windowRole, value); }
    public string ExecutionState { get => _executionState; private set => Set(ref _executionState, value); }
    public double? Disk { get => _disk; private set => Set(ref _disk, value); }
    public double? Network { get => _network; private set => Set(ref _network, value); }
    private int _treeDepth, _treeOrder;
    private bool _hasChildren, _treeVisible = true, _expanded, _displayExpanded, _canExpand;
    private int _cpuHeat = -1, _memoryHeat = -1, _diskHeat = -1, _networkHeat = -1;
    public int CpuHeat => _cpuHeat;
    public int MemoryHeat => _memoryHeat;
    public int DiskHeat => _diskHeat;
    public int NetworkHeat => _networkHeat;
    public void SetHeat(int cpu, int memory, int disk, int network)
    {
        Set(ref _cpuHeat, cpu, nameof(CpuHeat));
        Set(ref _memoryHeat, memory, nameof(MemoryHeat));
        Set(ref _diskHeat, disk, nameof(DiskHeat));
        Set(ref _networkHeat, network, nameof(NetworkHeat));
    }
    private string _parentName = "";
    public int TreeDepth => _treeDepth;
    public int TreeOrder => _treeOrder;
    public bool TreeVisible => _treeVisible;
    public bool HasChildren => _hasChildren;
    public bool CanExpand => _canExpand;
    public bool IsExpanded => _expanded;
    public string ExpansionGlyph => _displayExpanded ? "▾" : "▸";
    public string ParentName => _parentName;
    public void ToggleExpanded() => Set(ref _expanded, !_expanded, nameof(IsExpanded));
    public void SetTree(int depth, int order, bool visible, bool hasChildren, bool filtering, string parentName)
    {
        Set(ref _treeDepth, depth, nameof(TreeDepth));
        Set(ref _treeOrder, order, nameof(TreeOrder));
        Set(ref _treeVisible, visible, nameof(TreeVisible));
        Set(ref _hasChildren, hasChildren, nameof(HasChildren));
        Set(ref _canExpand, hasChildren && !filtering, nameof(CanExpand));
        Set(ref _displayExpanded, filtering || _expanded, nameof(ExpansionGlyph));
        if (_parentName != parentName) Changed(nameof(NameDetails));
        Set(ref _parentName, parentName, nameof(ParentName));
    }
    private string _name = sample.Name, _path = sample.Path;
    private string _description = sample.Description, _productName = sample.ProductName, _windowTitle = sample.WindowTitle;
    public string Description => _description;
    public string ProductName => _productName;
    public string WindowTitle => _windowTitle;
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description;
    public string NameDetails => string.Join("\n", new[] { Name, WindowTitle,
        ParentName.Length == 0 ? "" : "父进程：" + ParentName }.Where(value => value.Length != 0));
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Path { get => _path; private set => Set(ref _path, value); }
    private double? _cpu = sample.CpuPercent, _workingSet = sample.WorkingSetMiB, _commit = sample.PrivateCommitMiB,
        _privateWorkingSet = sample.PrivateWorkingSetMiB;
    private string _availability = sample.Availability;
    public double? Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public double? WorkingSet { get => _workingSet; private set => Set(ref _workingSet, value); }
    public double? PrivateWorkingSet { get => _privateWorkingSet; private set => Set(ref _privateWorkingSet, value); }
    public double? PrivateCommit { get => _commit; private set => Set(ref _commit, value); }
    public string Availability { get => _availability; private set => Set(ref _availability, value); }
    public void Update(ProcessSample value)
    {
        if (Name != value.Name || Description != value.Description) Changed(nameof(DisplayName));
        if (Name != value.Name || WindowTitle != value.WindowTitle) Changed(nameof(NameDetails));
        Name = value.Name;
        Set(ref _description, value.Description, nameof(Description));
        Set(ref _productName, value.ProductName, nameof(ProductName));
        Set(ref _windowTitle, value.WindowTitle, nameof(WindowTitle));
        ParentId = value.ParentId;
        Icon = value.Icon;
        WindowRole = value.WindowRole;
        ExecutionState = value.ExecutionState;
        Disk = value.DiskMiBPerSecond;
        Network = value.NetworkMbps;
        Path = value.Path;
        Cpu = value.CpuPercent;
        WorkingSet = value.WorkingSetMiB;
        PrivateWorkingSet = value.PrivateWorkingSetMiB;
        PrivateCommit = value.PrivateCommitMiB;
        Availability = value.Availability;
    }
}

internal sealed class ServiceRow(ServiceSample sample) : ObservableRow
{
    private ServiceSample _sample = sample;
    public string Name => _sample.Name;
    public string DisplayName => _sample.DisplayName;
    public uint State => _sample.State;
    public uint AcceptedControls => _sample.AcceptedControls;
    public uint ProcessId => _sample.ProcessId;
    public string Startup => _sample.Settings?.Startup?.Label ?? "—";
    public string Account => _sample.Settings?.Account ?? "—";
    public string Path => _sample.Settings?.Path ?? "—";
    public string Description => _sample.Settings?.Description ?? _sample.SettingsError;
    public string Status => StateText(State);
    public void Update(ServiceSample sample)
    {
        var before = _sample;
        _sample = sample;
        if (before.Name != sample.Name) Changed(nameof(Name));
        if (before.DisplayName != sample.DisplayName) Changed(nameof(DisplayName));
        if (before.State != sample.State) { Changed(nameof(State)); Changed(nameof(Status)); }
        if (before.AcceptedControls != sample.AcceptedControls) Changed(nameof(AcceptedControls));
        if (before.ProcessId != sample.ProcessId) Changed(nameof(ProcessId));
        if (before.Settings?.Startup != sample.Settings?.Startup) Changed(nameof(Startup));
        if (before.Settings?.Account != sample.Settings?.Account) Changed(nameof(Account));
        if (before.Settings?.Path != sample.Settings?.Path) Changed(nameof(Path));
        if (before.Settings?.Description != sample.Settings?.Description || before.SettingsError != sample.SettingsError) Changed(nameof(Description));
    }
    public static string StateText(uint state) => state switch
    {
        1 => "已停止", 2 => "正在启动", 3 => "正在停止", 4 => "运行中",
        5 => "正在继续", 6 => "正在暂停", 7 => "已暂停", _ => "未知"
    };
}
