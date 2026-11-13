using System.Diagnostics;
using System.Text.Json;

namespace SystemInformer.Client.Processes;

/// <summary>Console inspection of the same sampler used by the process panel. No UI is created.</summary>
public static class ProcessDiagnostics
{
    public static string Inspect(string processName, int intervalMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        if (intervalMilliseconds is < 500 or > 10000) throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        using var sampler = new ProcessSampler();
        var rows = new SnapshotRows<ProcessSample, (uint Id, ulong? Created), ProcessRow>(
            sample => (sample.Id, sample.Created), sample => new(sample), (row, sample) => row.Update(sample));
        var hierarchy = new ProcessHierarchy();
        void Arrange(IReadOnlyList<ProcessRow> items) => hierarchy.Update(items, _ => true, false, (left, right) => left.Id.CompareTo(right.Id));
        var before = sampler.Capture(CancellationToken.None);
        rows.Apply(before, Arrange);
        var previousRows = rows.ToDictionary(row => (row.Id, row.Created));
        var baseline = before.ToDictionary(sample => sample.Id);
        var elapsed = Stopwatch.StartNew();
        Thread.Sleep(intervalMilliseconds);
        var after = sampler.Capture(CancellationToken.None);
        var batch = rows.Apply(after, Arrange);
        var currentRows = rows.ToDictionary(row => (row.Id, row.Created));
        var matches = after.Where(sample => sample.Name.Contains(processName, StringComparison.OrdinalIgnoreCase)
            || sample.Description.Contains(processName, StringComparison.OrdinalIgnoreCase)
            || sample.ProductName.Contains(processName, StringComparison.OrdinalIgnoreCase)
            || sample.WindowTitle.Contains(processName, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(sample.Path).Contains(processName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var items = matches.Select(sample =>
        {
            baseline.TryGetValue(sample.Id, out var previous);
            var key = (sample.Id, sample.Created);
            currentRows.TryGetValue(key, out var currentRow);
            var rowIdentityPreserved = previousRows.TryGetValue(key, out var previousRow)
                && ReferenceEquals(previousRow, currentRow);
            string? referenceName = null;
            double? referenceWorkingSet = null, referencePrivateCommit = null;
            string? referenceError = null;
            try
            {
                if (sample.IsSynthetic) referenceError = "系统 CPU 记账项，无进程句柄";
                else
                {
                    using var process = Process.GetProcessById(checked((int)sample.Id));
                    referenceName = process.ProcessName;
                    referenceWorkingSet = process.WorkingSet64 / 1048576d;
                    referencePrivateCommit = process.PrivateMemorySize64 / 1048576d;
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                referenceError = error.Message;
            }
            return new
            {
                sample.Id, sample.Name, sample.Path, sample.Created, sample.IsSynthetic,
                sample.Description, sample.ProductName, sample.WindowTitle,
                sample.ParentId, parent_name = currentRow?.ParentName, tree_depth = currentRow?.TreeDepth,
                icon_available = sample.Icon != null,
                sample.WindowRole, sample.ExecutionState, sample.DiskMiBPerSecond, sample.NetworkMbps,
                same_identity = previous?.Created != null && previous.Created == sample.Created,
                row_identity_preserved = rowIdentityPreserved,
                sample.CpuPercent, sample.PrivateWorkingSetMiB, sample.WorkingSetMiB, sample.PrivateCommitMiB, sample.Availability,
                reference = new { name = referenceName, working_set_mib = referenceWorkingSet,
                    private_commit_mib = referencePrivateCommit, error = referenceError }
            };
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            captured_at = DateTimeOffset.Now,
            process_name = processName,
            process_count_before = before.Count(sample => !sample.IsSynthetic),
            process_count_after = after.Count(sample => !sample.IsSynthetic),
            elapsed_seconds = elapsed.Elapsed.TotalSeconds,
            match_count = matches.Length,
            throughput_status = sampler.ThroughputStatus,
            system_resources = sampler.SystemResources,
            cpu_status = sampler.CpuStatus,
            row_sync = new
            {
                count_before = previousRows.Count, count_after = currentRows.Count,
                batch.Added, batch.Removed, batch.Updated, batch.MembershipChanged,
                retained_row_count = currentRows.Count(pair => previousRows.TryGetValue(pair.Key, out var row)
                    && ReferenceEquals(row, pair.Value)),
                changed_properties = batch.ChangedProperties.OrderBy(name => name).ToArray()
            },
            has_complete_sample = matches.Any(sample => sample.Created != null && sample.Path.Length > 0
                && sample.CpuPercent != null && sample.PrivateWorkingSetMiB != null
                && sample.WorkingSetMiB != null && sample.PrivateCommitMiB != null),
            matches = items
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }
}
