using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Edirstat.Client;
using SystemInformer.Client.Processes;
using SystemInformer.Client.Performance;
using SystemInformer.Client.Services;
using SystemInformer.Client.Themes;

namespace SystemInformer.Client;

/// <summary>Local monitoring panels. Sampling runs only while visible; all native work is serialized.</summary>
public sealed class SystemInformerView : UserControl
{
    private const int ProcessTab = 0, PerformanceTab = 1, ServiceTab = 2, DiskTab = 3;
    private readonly TabItem _diskTab = new() { Header = "磁盘空间" };
    private readonly TabItem _performanceTab = new() { Header = "性能" };
    private DiskUsageView? _diskUsage;
    public Action<string>? CpuDiagnostics { set => _sampler.CpuDiagnostics = value; }
    private readonly ProcessSampler _sampler = new();
    private PerformanceSampler? _performanceSampler;
    private PerformanceView? _performanceView;
    public void ShowSystemInformation() => _tabs.SelectedIndex = PerformanceTab;
    private readonly ProcessHierarchy _hierarchy = new();
    private StringComparer _processNameComparer = StringComparer.CurrentCultureIgnoreCase;
    private readonly ProcessIconConverter _processIcons = new();
    private readonly ProcessHeatmap _heatmap = new();
    private readonly Dictionary<DataGridColumn, string> _processHeaders = new();
    private readonly Dictionary<string, TextBlock> _resourceTotals = new();
    private readonly Dictionary<string, TextBlock> _resourceLabels = new();
    private readonly TextBlock _memoryRatio = new() { Text = "— / — GiB",
        HorizontalAlignment = HorizontalAlignment.Right };
    private string _processSortPath = nameof(ProcessRow.DisplayName);
    private ListSortDirection _processSortDirection = ListSortDirection.Ascending;
    private readonly DataGridPathGroupDescription _processSections = new(nameof(ProcessRow.Section));
    private readonly WindowsServices _services = new();
    private readonly ServiceOperations _operations;
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly SemaphoreSlim _samplingSignal = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly SnapshotRows<ProcessSample, (uint Id, ulong? Created), ProcessRow> _processRows =
        new(sample => (sample.Id, sample.Created), sample => new(sample), (row, sample) => row.Update(sample));
    private readonly SnapshotRows<ServiceSample, string, ServiceRow> _serviceRows =
        new(sample => sample.Name, sample => new(sample), (row, sample) => row.Update(sample), StringComparer.OrdinalIgnoreCase);
    private readonly DataGridCollectionView _processData, _serviceData;
    private readonly DataGrid _processGrid = Table(), _serviceGrid = Table();
    private readonly MenuItem _terminateProcess = new() { Header = "强制结束进程", IsEnabled = false },
        _openProcessLocation = new() { Header = "打开文件所在位置", IsEnabled = false },
        _processProperties = new() { Header = "属性", IsEnabled = false };
    private enum ProcessAction { Terminate, OpenLocation, Properties }
    private ProcessTarget? _processMenuTarget;
    private readonly TextBox _processSearch = Search("搜索应用名、窗口标题、PID 或路径"), _serviceSearch = Search("搜索名称、路径、描述或账户");
    private readonly TextBox _serviceImagePath = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        PlaceholderText = "选中服务后显示完整路径和启动参数", MaxHeight = 100, MinHeight = 32 };
    private readonly ToggleButton _runningServices = new() { Content = "运行中的服务" };
    private readonly ToggleButton _memoryDiagnostics = new() { Content = "内存诊断列" };
    private readonly ComboBox _processMode = new() { ItemsSource = new[] { "全部进程", "父子进程树" }, SelectedIndex = 1, MinWidth = 150 };
    private readonly Button _refresh = ActionButton("刷新", "buttonSecondary");
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    private readonly Flyout _feedbackFlyout = new();
    private readonly ThemeVariantScope _themeScope = new() { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default };
    private readonly TabControl _tabs = new();
    private readonly Button _start = ActionButton("启动", "buttonSuccess"), _stop = ActionButton("停止", "buttonDanger"),
        _restart = ActionButton("重启", "buttonSecondary"), _serviceProperties = ActionButton("属性", "buttonSecondary");
    private readonly MenuItem _serviceStart = new() { Header = "启动" }, _serviceStop = new() { Header = "停止" },
        _serviceRestart = new() { Header = "重启" }, _nativeServiceProperties = new() { Header = "属性" };
    private string? _serviceMenuName;
    private Task _samplingTask = Task.CompletedTask, _actionTask = Task.CompletedTask;
    private bool _started, _stopping, _busy, _updatingRows, _serviceDataStale = true;
    private bool _attached;
    private Window? _window;
    private long _refreshVersion = 1, _settingsVersion = 1, _cpuVersion = 1;
    private SamplingRequest _samplingRequest = new(false, 0, 0, 0, 0);
    private SamplingUpdate? _pendingUpdate;
    private int _activeTab;

    private sealed record SamplingRequest(bool Visible, int Tab, long RefreshVersion, long SettingsVersion,
        long CpuVersion);
    private sealed record SamplingUpdate(SamplingRequest Request, IReadOnlyList<ProcessSample>? Processes = null,
        IReadOnlyList<ServiceSample>? Services = null,
        Exception? Error = null, string? ThroughputStatus = null, SystemResourceSample? SystemResources = null,
        string? CpuStatus = null, PerformanceSnapshot? Performance = null);

    public SystemInformerView()
    {
        Styles.Add(MonitorTheme.Styles());
        _memoryRatio.Classes.Add("textSecondary");
        _feedback.Classes.Add("textBody");
        _feedbackFlyout.Content = _feedback;
        _serviceImagePath.Classes.Add("servicePath");
        _runningServices.Classes.Add("monitorToggle");
        _memoryDiagnostics.Classes.Add("monitorToggle");
        _themeScope.AttachedToVisualTree += (_, _) => UpdateHeatPalette();
        _themeScope.ActualThemeVariantChanged += (_, _) => UpdateHeatPalette();
        _operations = new(_services);
        _processData = new(_processRows) { Filter = item => ((ProcessRow)item).TreeVisible };
        _serviceData = new(_serviceRows) { Filter = item => MatchesService((ServiceRow)item) };
        _processSections.GroupKeys.Add(ProcessRow.ForegroundSection);
        _processSections.GroupKeys.Add(ProcessRow.BackgroundSection);
        _processData.GroupDescriptions.Add(_processSections);
        _processGrid.LoadingRowGroup += (_, args) =>
        {
            args.RowGroupHeader.IsPropertyNameVisible = false;
            args.RowGroupHeader.IsItemCountVisible = true;
            args.RowGroupHeader.ItemCountFormat = "（{0}）";
        };
        _processData.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(ProcessRow.TreeOrder), ListSortDirection.Ascending));
        _serviceData.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(ServiceRow.DisplayName), ListSortDirection.Ascending));
        _processGrid.ItemsSource = _processData;
        _serviceGrid.ItemsSource = _serviceData;
        ProcessColumn();
        Column(_processGrid, "窗口", nameof(ProcessRow.WindowRole), 86, visible: false);
        Column(_processGrid, "状态", nameof(ProcessRow.ExecutionState), 115, visible: false);
        ResourceColumn("CPU (%)", nameof(ProcessRow.Cpu), nameof(ProcessRow.CpuHeat), 105, "{0:N1}");
        ResourceColumn("私有工作集 (MiB)", nameof(ProcessRow.PrivateWorkingSet), nameof(ProcessRow.MemoryHeat), 190, "{0:N1}");
        ResourceColumn("磁盘 (MiB/s)", nameof(ProcessRow.Disk), nameof(ProcessRow.DiskHeat), 145, "{0:N2}");
        ResourceColumn("网络 (Mbps)", nameof(ProcessRow.Network), nameof(ProcessRow.NetworkHeat), 145, "{0:N2}");
        Column(_processGrid, "PID", nameof(ProcessRow.Id), 76);
        ((DataGridTextColumn)_processGrid.Columns.Last()).Binding = new Binding(nameof(ProcessRow.DisplayId));
        Column(_processGrid, "父 PID", nameof(ProcessRow.ParentId), 76, visible: false);
        Column(_processGrid, "总工作集 (MiB)", nameof(ProcessRow.WorkingSet), 150, "{0:N1}", visible: false);
        Column(_processGrid, "私有提交 (MiB)", nameof(ProcessRow.PrivateCommit), 125, "{0:N1}", visible: false);
        Column(_processGrid, "路径", nameof(ProcessRow.Path), 260, star: true);
        Column(_processGrid, "读取情况", nameof(ProcessRow.Availability), 170, visible: false);
        ProcessMenu();
        Column(_serviceGrid, "服务", nameof(ServiceRow.DisplayName), 170, star: true);
        _serviceGrid.Columns[0].MinWidth = 220;
        Column(_serviceGrid, "名称", nameof(ServiceRow.Name), 155);
        Column(_serviceGrid, "状态", nameof(ServiceRow.Status), 90);
        Column(_serviceGrid, "启动类型", nameof(ServiceRow.Startup), 130);
        Column(_serviceGrid, "PID", nameof(ServiceRow.ProcessId), 65);
        Column(_serviceGrid, "登录账户", nameof(ServiceRow.Account), 160);
        Column(_serviceGrid, "可执行文件路径", nameof(ServiceRow.Path), 320);
        Column(_serviceGrid, "描述", nameof(ServiceRow.Description), 230);
        ServiceMenu();

        foreach (var column in _processGrid.Columns)
            _processHeaders.Add(column, _resourceLabels.TryGetValue(column.SortMemberPath ?? "", out var label)
                ? label.Text ?? "" : column.Header?.ToString() ?? "");
        UpdateProcessSortHeaders();
        _processGrid.Sorting += (_, args) =>
        {
            args.Handled = true;
            var path = args.Column.SortMemberPath;
            if (string.IsNullOrEmpty(path)) return;
            _processSortDirection = path == _processSortPath
                ? (_processSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : path is nameof(ProcessRow.Cpu) or nameof(ProcessRow.PrivateWorkingSet)
                    or nameof(ProcessRow.WorkingSet) or nameof(ProcessRow.PrivateCommit)
                    or nameof(ProcessRow.Disk) or nameof(ProcessRow.Network) ? ListSortDirection.Descending : ListSortDirection.Ascending;
            _processSortPath = path;
            UpdateProcessSortHeaders();
            RefreshProcessTree();
        };
        _processSearch.TextChanged += (_, _) => RefreshProcessTree();
        _processMode.SelectionChanged += (_, _) => RefreshProcessTree();
        ToolTip.SetTip(_memoryDiagnostics, "显示总工作集（含可共享驻留页）和私有提交（含未驻留内存）；所有进程行只显示本 PID，不含子进程合计");
        _memoryDiagnostics.IsCheckedChanged += (_, _) =>
        {
            var visible = _memoryDiagnostics.IsChecked == true;
            foreach (var column in _processGrid.Columns)
                if (column.SortMemberPath is nameof(ProcessRow.WorkingSet) or nameof(ProcessRow.PrivateCommit))
                    column.IsVisible = visible;
            if (!visible && _processSortPath is nameof(ProcessRow.WorkingSet) or nameof(ProcessRow.PrivateCommit))
            {
                _processSortPath = nameof(ProcessRow.PrivateWorkingSet);
                UpdateProcessSortHeaders();
                RefreshProcessTree();
            }
        };
        _serviceSearch.TextChanged += (_, _) => RefreshServiceFilter();
        ToolTip.SetTip(_runningServices, "仅显示正在运行的服务；可叠加搜索，再次点击恢复全部服务。");
        _runningServices.IsCheckedChanged += (_, _) => RefreshServiceFilter();
        _serviceGrid.SelectionChanged += (_, _) =>
        {
            if (_updatingRows) return;
            UpdateButtons();
        };
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_activeTab == _tabs.SelectedIndex) return;
            var wasService = _activeTab == ServiceTab;
            _activeTab = _tabs.SelectedIndex;
            _refresh.IsVisible = _activeTab != DiskTab;
            if (_activeTab == PerformanceTab) _performanceTab.Content = _performanceView ??= new PerformanceView();
            if (_activeTab == DiskTab) _diskTab.Content = _diskUsage ??= new DiskUsageView(useHostTheme: true);
            _refreshVersion++;
            if (wasService || _activeTab == ServiceTab) _cpuVersion++;
            _feedbackFlyout.Hide();
            ToolTip.SetTip(_refresh, "刷新当前页面");
            UpdateMonitoringVisibility();
        };
        _start.Click += (_, _) => BeginOperation(ServiceOperation.Start);
        _stop.Click += (_, _) => BeginOperation(ServiceOperation.Stop);
        _restart.Click += (_, _) => BeginOperation(ServiceOperation.Restart);
        _serviceProperties.Click += (_, _) => BeginServiceProperties();
        _serviceGrid.DoubleTapped += (_, args) =>
        {
            if ((args.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is ServiceRow row)
                BeginServiceProperties(row.Name);
        };

        _refresh.Click += (_, _) =>
        {
            _refreshVersion++;
            _settingsVersion++;
            _feedbackFlyout.Hide();
            ToolTip.SetTip(_refresh, "刷新当前页面");
            Tick();
        };
        ToolTip.SetTip(_refresh, "刷新当前页面");
        _tabs.Classes.Add("monitorTabs");
        _tabs.ItemsSource = new[]
        {
            new TabItem { Header = "进程", Content = ProcessPanel() },
            _performanceTab,
            new TabItem { Header = "服务", Content = ServicePanel() },
            _diskTab
        };
        _tabs.SelectedIndex = ProcessTab;
        var root = new Grid();
        root.Children.Add(_tabs);
        // The tab header reserves a trailing slot; content still spans the full panel width.
        _refresh.HorizontalAlignment = HorizontalAlignment.Right;
        _refresh.VerticalAlignment = VerticalAlignment.Top;
        _refresh.Margin = new Thickness(0, 2, 0, 0);
        root.Children.Add(_refresh);
        var surface = new Border { Padding = new Thickness(12, 8, 12, 8), Child = root };
        MonitorTheme.Bind(surface, Border.BackgroundProperty, "BgContentBrush");
        _themeScope.Child = surface;
        Content = _themeScope;
        UpdateButtons();
        _timer.Tick += (_, _) => Tick();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null) _window.PropertyChanged += WindowVisibilityChanged;
            UpdateMonitoringVisibility();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            if (_window is not null) _window.PropertyChanged -= WindowVisibilityChanged;
            _window = null;
            UpdateMonitoringVisibility();
        };
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty || change.Property == OpacityProperty)
                UpdateMonitoringVisibility();
        };
    }

    public void Start()
    {
        if (_stopping) throw new InvalidOperationException("系统监控已停用，请重启客户端后启用。");
        if (_started) return;
        _started = true;
        _samplingTask = Task.Run(SampleAsync);
        UpdateMonitoringVisibility();
    }

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _timer.Stop();
            if (_window is not null) _window.PropertyChanged -= WindowVisibilityChanged;
            _window = null;
        });
        _lifetime.Cancel();
        await Dispatcher.UIThread.InvokeAsync(() => _diskUsage?.StopAsync() ?? Task.CompletedTask);
        await Task.WhenAll(_samplingTask, _actionTask).ConfigureAwait(false);
        Interlocked.Exchange(ref _pendingUpdate, null);
        _sampler.Dispose();
        _performanceSampler?.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => _processIcons.Dispose());
        _work.Dispose();
        _samplingSignal.Dispose();
        _lifetime.Dispose();
    }

    private bool MonitoringVisible => _attached && IsEffectivelyVisible && Opacity > 0
        && _window is not { WindowState: WindowState.Minimized }
        && this.GetVisualAncestors().All(visual => visual.Opacity > 0)
        && _tabs.SelectedIndex is ProcessTab or PerformanceTab or ServiceTab;

    private void WindowVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Window.WindowStateProperty || change.Property == IsVisibleProperty)
            Dispatcher.UIThread.Post(UpdateMonitoringVisibility);
    }

    private void UpdateMonitoringVisibility()
    {
        if (!_started || _stopping) return;
        if (MonitoringVisible) _timer.Start();
        else _timer.Stop();
        Tick();
    }

    private void Tick()
    {
        if (_stopping || !_started) return;
        var visible = MonitoringVisible;
        var previous = Volatile.Read(ref _samplingRequest);
        if (visible != previous.Visible) { _cpuVersion++; _refreshVersion++; }
        var request = new SamplingRequest(visible, _tabs.SelectedIndex, _refreshVersion, _settingsVersion,
            _cpuVersion);
        Volatile.Write(ref _samplingRequest, request);
        if (request != previous && _samplingSignal.CurrentCount == 0) _samplingSignal.Release();
        var update = Interlocked.Exchange(ref _pendingUpdate, null);
        if (!visible || update == null || update.Request.Tab != request.Tab) return;
        // Discard snapshots from before a visibility/tab change, explicit refresh or service operation.
        if (update.Request.CpuVersion != request.CpuVersion || update.Request.RefreshVersion != request.RefreshVersion) return;
        try
        {
            if (update.Error != null) throw update.Error;
            if (update.Processes != null)
            {
                ApplyProcesses(update.Processes);
                UpdateResourceTotals(update.SystemResources);
                ToolTip.SetTip(_resourceTotals[nameof(ProcessRow.Cpu)], update.CpuStatus);
                ToolTip.SetTip(_resourceTotals[nameof(ProcessRow.Disk)], update.ThroughputStatus);
                ToolTip.SetTip(_resourceTotals[nameof(ProcessRow.Network)], update.ThroughputStatus);
            }
            else if (update.Performance != null)
            {
                _performanceView?.Apply(update.Performance);
            }
            else if (update.Services != null)
            {
                ApplyServices(update.Services);
            }
        }
        catch (Exception error)
        {
            _refreshVersion++;
            ReportStatus("刷新失败：" + Friendly(error));
            if (request.Tab == ServiceTab) _serviceDataStale = true;
            UpdateButtons();
        }
    }

    private async Task SampleAsync()
    {
        var token = _lifetime.Token;
        var resetCpu = true;
        long cpuVersion = -1, refreshVersion = -1, settingsVersion = -1;
        long lastServiceRefresh = 0;
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                var request = Volatile.Read(ref _samplingRequest);
                if (!request.Visible || request.Tab == ServiceTab) _sampler.PauseMonitoring();
                if (!request.Visible || request.Tab != PerformanceTab) _performanceSampler?.Pause();
                if (!request.Visible) { resetCpu = true; continue; }
                if (request.Tab == ServiceTab) resetCpu = true;
                if (!await _work.WaitAsync(0, token).ConfigureAwait(false)) continue;
                try
                {
                    if (!Volatile.Read(ref _samplingRequest).Visible) continue;
                    SamplingUpdate update;
                    if (request.Tab is ProcessTab or PerformanceTab)
                    {
                        if (resetCpu || cpuVersion != request.CpuVersion) _sampler.ResetCpuBaselines();
                        var samples = _sampler.Capture(token);
                        if (!Volatile.Read(ref _samplingRequest).Visible) continue;
                        resetCpu = false;
                        cpuVersion = request.CpuVersion;
                        if (request.Tab == PerformanceTab)
                        {
                            _performanceSampler ??= new();
                            var performance = _performanceSampler.Capture(samples, _sampler.SystemResources, _sampler.CpuStatus, token);
                            update = new(request, Performance: performance, SystemResources: _sampler.SystemResources);
                        }
                        else update = new(request, Processes: samples, ThroughputStatus: _sampler.ThroughputStatus,
                            SystemResources: _sampler.SystemResources, CpuStatus: _sampler.CpuStatus);
                    }
                    else
                    {
                        if (refreshVersion == request.RefreshVersion && lastServiceRefresh != 0
                            && Stopwatch.GetElapsedTime(lastServiceRefresh) < TimeSpan.FromSeconds(3)) continue;
                        var samples = _services.Capture(settingsVersion != request.SettingsVersion, token);
                        refreshVersion = request.RefreshVersion;
                        settingsVersion = request.SettingsVersion;
                        lastServiceRefresh = Stopwatch.GetTimestamp();
                        update = new(request, Services: samples);
                    }
                    // Only complete snapshots may replace the pending one, so skipped UI frames cannot lose removals.
                    Interlocked.Exchange(ref _pendingUpdate, update);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { Interlocked.Exchange(ref _pendingUpdate, new SamplingUpdate(request, Error: error)); }
                finally { _work.Release(); }
            }
            while (await WaitForSamplingAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<bool> WaitForSamplingAsync(CancellationToken token)
    {
        // Detach/tab changes wake this wait immediately; hidden pages have no periodic polling.
        if (Volatile.Read(ref _samplingRequest).Visible)
            await _samplingSignal.WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        else
            await _samplingSignal.WaitAsync(token).ConfigureAwait(false);
        return true;
    }

    private void ApplyProcesses(IReadOnlyList<ProcessSample> samples)
    {
        var selected = _processGrid.SelectedItem;
        var batch = _processRows.Apply(samples, ArrangeProcesses);
        RefreshView(_processData, batch, true, nameof(ProcessRow.TreeVisible));
        _processGrid.SelectedItem = selected is ProcessRow previous && _processData.Contains(previous) ? previous : null;
        _processIcons.Trim(samples.Select(sample => sample.Icon));
    }

    private void ArrangeProcesses(IReadOnlyList<ProcessRow> rows)
    {
        _processNameComparer = StringComparer.CurrentCultureIgnoreCase;
        var nameSorted = _processSortPath == nameof(ProcessRow.DisplayName);
        _hierarchy.Update(rows, MatchesProcess, !string.IsNullOrWhiteSpace(_processSearch.Text), CompareProcesses,
            tree: nameSorted && _processMode.SelectedIndex == 1, sections: nameSorted);
        _heatmap.Update(rows);
    }

    private void UpdateHeatPalette()
    {
        _heatmap.SetPalette(MonitorTheme.Brush(_themeScope, "BgSurfaceSubtleBrush"), MonitorTheme.Brush(_themeScope, "NavSelectedBgBrush"));
        _tabs.Resources["TabItemLinePipeSelectedBackground"] = MonitorTheme.Brush(_themeScope, "AccentBrush");
        _tabs.Resources["TabItemLineHeaderSelectedForeground"] = MonitorTheme.Brush(_themeScope, "AccentBrush");
        _tabs.Resources["TabItemLineHeaderPointeroverForeground"] = MonitorTheme.Brush(_themeScope, "AccentHoverBrush");
        _tabs.Resources["TabItemLinePipePointeroverBorderBrush"] = MonitorTheme.Brush(_themeScope, "AccentHoverBrush");
        _tabs.Resources["TabItemLinePipePressedBorderBrush"] = MonitorTheme.Brush(_themeScope, "AccentPressedBrush");
        // Semi's row template draws its own background; update its scoped resource keys as well.
        foreach (var grid in new[] { _processGrid, _serviceGrid })
        {
            grid.Resources["DataGridRowPointeroverBackground"] = MonitorTheme.Brush(_themeScope, "NavHoverBgBrush");
            grid.Resources["DataGridRowSelectedBackground"] = MonitorTheme.Brush(_themeScope, "NavSelectedBgBrush");
            grid.Resources["DataGridRowSelectedPointeroverBackground"] = MonitorTheme.Brush(_themeScope, "NavSelectedBgBrush");
            grid.Resources["DataGridCellCurrentBorderBrush"] = MonitorTheme.Brush(_themeScope, "NavIndicatorBrush");
            grid.Resources["DataGridCellFocusBorderBrush"] = MonitorTheme.Brush(_themeScope, "NavIndicatorBrush");
        }
    }

    private void RefreshProcessTree()
    {
        var selected = _processGrid.SelectedItem;
        var batch = _processRows.Arrange(ArrangeProcesses);
        var nameSorted = _processSortPath == nameof(ProcessRow.DisplayName);
        _processMode.IsEnabled = nameSorted;
        // Changing group descriptions refreshes the view itself, after all row state is final.
        if (nameSorted && _processData.GroupDescriptions.Count == 0)
            _processData.GroupDescriptions.Add(_processSections);
        else if (!nameSorted && _processData.GroupDescriptions.Count != 0)
            _processData.GroupDescriptions.Clear();
        else RefreshView(_processData, batch, true, nameof(ProcessRow.TreeVisible));
        _processGrid.SelectedItem = selected is ProcessRow row && _processData.Contains(row) ? row : null;
    }

    private int CompareProcesses(ProcessRow left, ProcessRow right)
    {
        // Unreadable values stay below readable measurements in either direction.
        var values = _processSortPath switch
        {
            nameof(ProcessRow.Cpu) => (left.Cpu, right.Cpu),
            nameof(ProcessRow.PrivateWorkingSet) => (left.PrivateWorkingSet, right.PrivateWorkingSet),
            nameof(ProcessRow.WorkingSet) => (left.WorkingSet, right.WorkingSet),
            nameof(ProcessRow.PrivateCommit) => (left.PrivateCommit, right.PrivateCommit),
            nameof(ProcessRow.Disk) => (left.Disk, right.Disk),
            nameof(ProcessRow.Network) => (left.Network, right.Network),
            _ => ((double?)null, (double?)null)
        };
        if (values.Item1.HasValue != values.Item2.HasValue) return values.Item1.HasValue ? -1 : 1;
        var result = _processSortPath switch
        {
            nameof(ProcessRow.Id) => left.Id.CompareTo(right.Id),
            nameof(ProcessRow.ParentId) => left.ParentId.CompareTo(right.ParentId),
            nameof(ProcessRow.Cpu) => Nullable.Compare(left.Cpu, right.Cpu),
            nameof(ProcessRow.PrivateWorkingSet) => Nullable.Compare(left.PrivateWorkingSet, right.PrivateWorkingSet),
            nameof(ProcessRow.WorkingSet) => Nullable.Compare(left.WorkingSet, right.WorkingSet),
            nameof(ProcessRow.PrivateCommit) => Nullable.Compare(left.PrivateCommit, right.PrivateCommit),
            nameof(ProcessRow.Disk) => Nullable.Compare(left.Disk, right.Disk),
            nameof(ProcessRow.Network) => Nullable.Compare(left.Network, right.Network),
            nameof(ProcessRow.WindowRole) => _processNameComparer.Compare(left.WindowRole, right.WindowRole),
            nameof(ProcessRow.ExecutionState) => _processNameComparer.Compare(left.ExecutionState, right.ExecutionState),
            nameof(ProcessRow.Path) => _processNameComparer.Compare(left.Path, right.Path),
            nameof(ProcessRow.Availability) => _processNameComparer.Compare(left.Availability, right.Availability),
            _ => _processNameComparer.Compare(left.DisplayName, right.DisplayName)
        };
        if (result != 0) return _processSortDirection == ListSortDirection.Ascending ? Math.Sign(result) : -Math.Sign(result);
        return left.Id.CompareTo(right.Id);
    }

    private void UpdateProcessSortHeaders()
    {
        foreach (var (column, title) in _processHeaders)
        {
            var arrow = column.SortMemberPath == _processSortPath
                ? (_processSortDirection == ListSortDirection.Ascending ? " ↑" : " ↓") : "";
            if (_resourceLabels.TryGetValue(column.SortMemberPath ?? "", out var label)) label.Text = title + arrow;
            else column.Header = title + arrow;
        }
    }

    private void UpdateResourceTotals(SystemResourceSample? sample)
    {
        Set(nameof(ProcessRow.Cpu), sample?.CpuPercent);
        Set(nameof(ProcessRow.PrivateWorkingSet), sample?.MemoryPercent);
        _memoryRatio.Text = sample?.UsedPhysicalBytes is ulong used && sample?.TotalPhysicalBytes is ulong total && total > 0
            ? $"{used / 1073741824d:N1} / {total / 1073741824d:N1} GiB" : "— / — GiB";
        Set(nameof(ProcessRow.Disk), sample?.DiskPercent);
        Set(nameof(ProcessRow.Network), sample?.NetworkPercent);
        void Set(string path, double? value) => _resourceTotals[path].Text = value is double percent ? $"{percent:N0}%" : "—";
    }

    private void ProcessColumn()
    {
        _processGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "进程", SortMemberPath = nameof(ProcessRow.DisplayName), Width = new DataGridLength(270), MinWidth = 220,
            CellTemplate = new FuncDataTemplate<ProcessRow>((_, _) =>
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                panel.Bind(MarginProperty, new Binding(nameof(ProcessRow.TreeDepth)) { Converter = new ProcessIndentConverter() });
                var expand = new Button
                {
                    Width = 18, Height = 22, MinHeight = 0, Padding = new Thickness(0),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0)
                };
                expand.Classes.Add("processExpander");
                expand.Bind(ContentControl.ContentProperty, new Binding(nameof(ProcessRow.ExpansionGlyph)));
                expand.Bind(IsVisibleProperty, new Binding(nameof(ProcessRow.HasChildren)));
                expand.Bind(IsEnabledProperty, new Binding(nameof(ProcessRow.CanExpand)));
                expand.Click += (_, _) =>
                {
                    if (expand.DataContext is not ProcessRow row) return;
                    row.ToggleExpanded();
                    RefreshProcessTree();
                };
                var expanderSlot = new Border { Width = 18, Child = expand };
                panel.Children.Add(expanderSlot);
                var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                icon.Bind(Image.SourceProperty, new Binding(nameof(ProcessRow.Icon)) { Converter = _processIcons });
                panel.Children.Add(icon);
                var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(ProcessRow.DisplayName)));
                name.Bind(ToolTip.TipProperty, new Binding(nameof(ProcessRow.NameDetails)));
                panel.Children.Add(name);
                return panel;
            }, supportsRecycling: true)
        });
    }

    private void ResourceColumn(string title, string path, string heatPath, double width, string format)
    {
        var total = MonitorTheme.Text("textSectionTitle", "—");
        total.HorizontalAlignment = HorizontalAlignment.Right;
        var label = MonitorTheme.Text("textFieldLabel", title);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.Children.Add(total);
        if (path == nameof(ProcessRow.PrivateWorkingSet)) header.Children.Add(_memoryRatio);
        header.Children.Add(label);
        ToolTip.SetTip(header, path switch
        {
            nameof(ProcessRow.Cpu) => "CPU 按周期增量统计；整机与进程共用包含空闲、中断/DPC及可补记退出进程的分母",
            nameof(ProcessRow.PrivateWorkingSet) => "表头为整机物理内存使用量；进程行为本 PID 的私有工作集（MiB），不含可共享页或子进程。总工作集与私有提交可通过“内存诊断列”查看",
            nameof(ProcessRow.Disk) => "最忙物理磁盘的活动时间比例",
            _ => "活动网络接口收发速率占链路带宽的最高比例"
        });
        _resourceTotals.Add(path, total);
        _resourceLabels.Add(path, label);
        _processGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = header, SortMemberPath = path, Width = new DataGridLength(width),
            MinWidth = path == nameof(ProcessRow.PrivateWorkingSet) ? 170 : 60,
            CellTemplate = new FuncDataTemplate<ProcessRow>((_, _) =>
            {
                var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                text.Bind(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay, StringFormat = format, TargetNullValue = "—" });
                MonitorTheme.Bind(text, TextBlock.ForegroundProperty, "FgPrimaryBrush");
                var cell = new Border { Padding = new Thickness(8, 0), Child = text, HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch };
                cell.Bind(Border.BackgroundProperty, new Binding(heatPath) { Converter = _heatmap });
                return cell;
            }, supportsRecycling: true)
        });
    }

    private void ApplyServices(IReadOnlyList<ServiceSample> samples)
    {
        _serviceDataStale = false;
        var selected = _serviceGrid.SelectedItem as ServiceRow;
        _updatingRows = true;
        try
        {
            var batch = _serviceRows.Apply(samples);
            RefreshView(_serviceData, batch, !string.IsNullOrWhiteSpace(_serviceSearch.Text),
                nameof(ServiceRow.Name), nameof(ServiceRow.DisplayName), nameof(ServiceRow.Description), nameof(ServiceRow.Account), nameof(ServiceRow.Path));
            if (selected != null && _serviceData.Contains(selected)) _serviceGrid.SelectedItem = selected;
            else _serviceGrid.SelectedItem = null;
        }
        finally { _updatingRows = false; }
        UpdateButtons();
    }

    private void RefreshServiceFilter()
    {
        _serviceData.Refresh();
        if (_serviceGrid.SelectedItem is ServiceRow row && !MatchesService(row)) _serviceGrid.SelectedItem = null;
        UpdateButtons();
    }

    private static void RefreshView(DataGridCollectionView view, RowBatch batch, bool filtering, params string[] filterProperties)
    {
        // A source Reset already refreshes sorting/filtering once. Never refresh twice for the same batch.
        if (batch.MembershipChanged || batch.ChangedProperties.Count == 0) return;
        var sortChanged = view.SortDescriptions.Any(sort => string.IsNullOrEmpty(sort.PropertyPath)
            || batch.ChangedProperties.Contains(sort.PropertyPath));
        var groupChanged = view.GroupDescriptions.Any(group => batch.ChangedProperties.Contains(group.PropertyName));
        if (sortChanged || groupChanged || (filtering && filterProperties.Any(batch.ChangedProperties.Contains))) view.Refresh();
    }

    private void BeginOperation(ServiceOperation operation, string? name = null)
    {
        var row = ServiceTarget(name);
        if (_busy || _stopping || _serviceDataStale || row == null || !CanOperate(row, operation)) return;
        var label = operation switch
        {
            ServiceOperation.Start => "启动", ServiceOperation.Stop => "停止", ServiceOperation.Restart => "重启",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        _actionTask = RunActionAsync(row.Name, label, token => _operations.ExecuteAsync(row.Name, operation, token));
    }

    private ServiceRow? ServiceTarget(string? name) => name == null ? _serviceGrid.SelectedItem as ServiceRow
        : _serviceRows.FirstOrDefault(row => StringComparer.OrdinalIgnoreCase.Equals(row.Name, name));

    private static bool CanOperate(ServiceRow row, ServiceOperation operation) => operation switch
    {
        ServiceOperation.Start => row.State == 1 && row.Startup != "禁用",
        ServiceOperation.Stop => row.State is 4 or 7 && (row.AcceptedControls & 1) != 0,
        ServiceOperation.Restart => row.State is 4 or 7 && (row.AcceptedControls & 1) != 0 && row.Startup != "禁用",
        _ => false
    };

    private void BeginServiceProperties(string? name = null)
    {
        var row = ServiceTarget(name);
        if (_busy || _stopping || _serviceDataStale || row == null) return;
        var target = row.Name;
        _actionTask = RunActionAsync(target, "打开原生属性", token =>
            NativeServiceProperties.OpenAsync(_services, target, token), serviceChanged: false);
    }

    private void ServiceMenu()
    {
        var menu = new ContextMenu { ItemsSource = new[] { _serviceStart, _serviceStop, _serviceRestart, _nativeServiceProperties } };
        _serviceGrid.ContextMenu = menu;
        _serviceGrid.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(_serviceGrid).Properties.IsRightButtonPressed) return;
            _serviceGrid.SelectedItem = (args.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as ServiceRow;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.Opening += (_, _) => { _serviceMenuName = (_serviceGrid.SelectedItem as ServiceRow)?.Name; UpdateButtons(); };
        _serviceStart.Click += (_, _) => { if (_serviceMenuName != null) BeginOperation(ServiceOperation.Start, _serviceMenuName); };
        _serviceStop.Click += (_, _) => { if (_serviceMenuName != null) BeginOperation(ServiceOperation.Stop, _serviceMenuName); };
        _serviceRestart.Click += (_, _) => { if (_serviceMenuName != null) BeginOperation(ServiceOperation.Restart, _serviceMenuName); };
        _nativeServiceProperties.Click += (_, _) => { if (_serviceMenuName != null) BeginServiceProperties(_serviceMenuName); };
    }

    private void ProcessMenu()
    {
        ToolTip.SetTip(_terminateProcess, "强制结束所选 PID，不结束子进程；未保存的数据会丢失。");
        var menu = new ContextMenu { ItemsSource = new[] { _terminateProcess, _openProcessLocation, _processProperties } };
        _processGrid.ContextMenu = menu;
        _processGrid.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(_processGrid).Properties.IsRightButtonPressed) return;
            var hit = (args.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
            // A right click on another row selects that row; blank space and group headers clear the target.
            _processGrid.SelectedItem = hit?.DataContext as ProcessRow;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.Opening += (_, _) =>
        {
            _processMenuTarget = _processGrid.SelectedItem is ProcessRow row
                ? new(row.Id, row.Created, row.DisplayName, row.Path, row.IsSynthetic) : null;
            UpdateProcessMenu();
        };
        _terminateProcess.Click += (_, _) => BeginProcessAction(ProcessAction.Terminate);
        _openProcessLocation.Click += (_, _) => BeginProcessAction(ProcessAction.OpenLocation);
        _processProperties.Click += (_, _) => BeginProcessAction(ProcessAction.Properties);
    }

    private void UpdateProcessMenu()
    {
        var ready = !_busy && !_stopping;
        _terminateProcess.IsEnabled = ready && _processMenuTarget?.CanTerminate == true;
        _openProcessLocation.IsEnabled = ready && _processMenuTarget?.CanOpen == true;
        _processProperties.IsEnabled = _openProcessLocation.IsEnabled;
    }

    private void BeginProcessAction(ProcessAction action)
    {
        var target = _processMenuTarget;
        if (_busy || _stopping || target == null || (action == ProcessAction.Terminate ? !target.CanTerminate : !target.CanOpen)) return;
        var label = action switch
        {
            ProcessAction.Terminate => "强制结束进程", ProcessAction.OpenLocation => "打开文件所在位置", _ => "打开文件属性"
        };
        _actionTask = RunActionAsync($"{target.Name}（PID {target.Id}）",
            label, token => action switch
            {
                ProcessAction.Terminate => ProcessOperations.TerminateAsync(target, token),
                ProcessAction.OpenLocation => ProcessOperations.OpenLocationAsync(target, token),
                _ => ProcessOperations.OpenPropertiesAsync(target, token)
            },
            serviceChanged: false);
    }

    private async Task RunActionAsync(string name, string label, Func<CancellationToken, Task> action, bool serviceChanged = true)
    {
        _busy = true;
        ReportStatus($"正在{label}：{name}…");
        UpdateButtons();
        await _work.WaitAsync();
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            await action(_lifetime.Token);
            if (!_stopping) ReportStatus($"{name}：{label}完成。");
        }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception error)
        {
            if (!_stopping) ReportStatus($"{name}：{label}失败，{Friendly(error)}", notify: true);
        }
        finally
        {
            _work.Release();
            _busy = false;
            if (serviceChanged) _settingsVersion++;
            _refreshVersion++;
            if (!_stopping) { UpdateButtons(); Tick(); }
        }
    }

    private void ReportStatus(string message, bool notify = false)
    {
        ToolTip.SetTip(_refresh, message);
        if (!notify || !IsEffectivelyVisible) return;
        _feedback.Text = message;
        _feedbackFlyout.ShowAt(_refresh);
    }

    private void UpdateButtons()
    {
        UpdateProcessMenu();
        var row = _serviceGrid.SelectedItem as ServiceRow;
        _serviceImagePath.Text = row == null ? "" : row.Path == "—" ? "路径不可读取：" + row.Description : row.Path;
        var ready = row != null && !_busy && !_stopping && !_serviceDataStale;
        _start.IsEnabled = ready && CanOperate(row!, ServiceOperation.Start);
        _stop.IsEnabled = ready && CanOperate(row!, ServiceOperation.Stop);
        _restart.IsEnabled = ready && CanOperate(row!, ServiceOperation.Restart);
        _serviceProperties.IsEnabled = ready;
        var target = _serviceMenuName == null ? null : ServiceTarget(_serviceMenuName);
        var menuReady = target != null && !_busy && !_stopping && !_serviceDataStale;
        _serviceStart.IsEnabled = menuReady && CanOperate(target!, ServiceOperation.Start);
        _serviceStop.IsEnabled = menuReady && CanOperate(target!, ServiceOperation.Stop);
        _serviceRestart.IsEnabled = menuReady && CanOperate(target!, ServiceOperation.Restart);
        _nativeServiceProperties.IsEnabled = menuReady;
    }

    private Control ProcessPanel()
    {
        var panel = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        var toolbar = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_processMode, Dock.Right);
        toolbar.Children.Add(_processMode);
        DockPanel.SetDock(_memoryDiagnostics, Dock.Right);
        _memoryDiagnostics.Margin = new Thickness(8, 0);
        toolbar.Children.Add(_memoryDiagnostics);
        toolbar.Children.Add(_processSearch);
        panel.Children.Add(toolbar);
        Grid.SetRow(_processGrid, 1); panel.Children.Add(_processGrid);
        return panel;
    }

    private Control ServicePanel()
    {
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { _serviceSearch, _runningServices, _start, _stop, _restart, _serviceProperties })
        {
            control.Margin = new Thickness(0, 0, 6, 6);
            toolbar.Children.Add(control);
        }
        var pathPanel = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        var pathLabel = MonitorTheme.Text("textFieldLabel", "可执行文件路径");
        pathLabel.VerticalAlignment = VerticalAlignment.Center;
        pathPanel.Children.Add(pathLabel);
        Grid.SetColumn(_serviceImagePath, 1); pathPanel.Children.Add(_serviceImagePath);
        var panel = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 6 };
        panel.Children.Add(toolbar);
        Grid.SetRow(pathPanel, 1); panel.Children.Add(pathPanel);
        Grid.SetRow(_serviceGrid, 2); panel.Children.Add(_serviceGrid);
        return panel;
    }

    private bool MatchesProcess(ProcessRow row)
    {
        var text = _processSearch.Text?.Trim() ?? "";
        return text.Length == 0 || Contains(row.Name, text) || Contains(row.DisplayName, text)
            || Contains(row.ProductName, text) || Contains(row.WindowTitle, text)
            || Contains(row.Path, text) || Contains(row.Id.ToString(), text);
    }
    private bool MatchesService(ServiceRow row)
    {
        if (_runningServices.IsChecked == true && row.State != 4) return false;
        var text = _serviceSearch.Text?.Trim() ?? "";
        return text.Length == 0 || Contains(row.Name, text) || Contains(row.DisplayName, text) || Contains(row.Description, text)
            || Contains(row.Account, text) || Contains(row.Path, text);
    }
    private static bool Contains(string source, string text) => source.Contains(text, StringComparison.OrdinalIgnoreCase);
    private static string Friendly(Exception error) => error is System.Runtime.InteropServices.COMException { ErrorCode: unchecked((int)0x80070005) }
        ? "权限不足，无法打开 Windows 原生属性；请以管理员身份打开系统监控后重试。"
        : error is Win32Exception windows
        ? windows.NativeErrorCode switch { 5 => "权限不足。", 1223 => "已取消管理员授权。", 1051 => "仍有依赖此服务的服务在运行。", 1058 => "服务已被禁用。", _ => $"{windows.Message}（{windows.NativeErrorCode}）" }
        : error.Message;

    private static DataGrid Table() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserSortColumns = true,
        CanUserReorderColumns = false, CanUserResizeColumns = true, SelectionMode = DataGridSelectionMode.Single,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, RowHeight = 32, MinHeight = 180
    };
    private static void Column(DataGrid grid, string title, string path, double width, string? format = null, bool star = false, bool visible = true)
        => grid.Columns.Add(new DataGridTextColumn
        {
            Header = title, Binding = new Binding(path) { Mode = BindingMode.OneWay, StringFormat = format, TargetNullValue = "—" },
            SortMemberPath = path, Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width), MinWidth = 60,
            IsVisible = visible
        });
    private static TextBox Search(string text) => new() { PlaceholderText = text, MinWidth = 230, MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
    private static Button ActionButton(string text, string semantic)
    {
        var button = new Button { Content = text };
        button.Classes.Add("contentButton"); button.Classes.Add(semantic); button.Classes.Add("buttonSmall");
        return button;
    }

}
