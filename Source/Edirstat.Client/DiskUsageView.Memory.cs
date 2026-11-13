using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Edirstat.Client;

public sealed partial class DiskUsageView
{
    private static readonly TimeSpan ResultRetention = TimeSpan.FromMinutes(5);
    // Poll effective visibility too: the host can hide an ancestor without detaching this page.
    private readonly DispatcherTimer _retentionTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Stopwatch _hiddenClock = new();

    private void CheckResultRetention()
    {
        if (_stopping || _snapshot is null) { _retentionTimer.Stop(); return; }
        if (this.IsAttachedToVisualTree() && IsEffectivelyVisible && Opacity > 0
            && this.GetVisualAncestors().All(visual => visual.Opacity > 0))
        {
            _hiddenClock.Reset();
            return;
        }
        if (!_hiddenClock.IsRunning) _hiddenClock.Start();
        // Use the same operation gate as navigation, deletion and allocation-size reads.
        if (!_busy && _hiddenClock.Elapsed >= ResultRetention)
            Run(() => ClearResultsAsync("磁盘页闲置五分钟，扫描结果已释放，可重新扫描"));
    }

    private async Task ClearResultsAsync(string message)
    {
        await ReleaseSnapshotAsync();
        _status.Text = message;
    }
}
