using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Edirstat.Client;

internal static class NativeScanner
{
    private const string Library = "edirstat_bridge";
    private static readonly Lazy<nint> LibraryHandle = new(() => NativeLibrary.Load(
        Path.Combine(Path.GetDirectoryName(typeof(NativeScanner).Assembly.Location)!, "edirstat_bridge.dll")));
    static NativeScanner()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeScanner).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? paths) =>
        name == Library ? LibraryHandle.Value : 0;

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Buffer { public readonly nint Data; public readonly nuint Length; }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern uint edirstat_abi_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_scan_start(byte[] path, nuint length, out nint scan);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_scan_state(ScanHandle scan);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void edirstat_scan_cancel(ScanHandle scan);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void edirstat_scan_release(nint scan);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint edirstat_snapshot_acquire(ScanHandle scan);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_snapshot_page(SnapshotHandle snapshot, uint parent, uint offset, uint count, uint sort, uint descending);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_snapshot_node(SnapshotHandle snapshot, uint id, uint sort, uint descending);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_snapshot_extensions(SnapshotHandle snapshot, uint root);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_snapshot_files(SnapshotHandle snapshot, uint root, byte[] filter, nuint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_snapshot_map(SnapshotHandle snapshot, uint root, double width, double height, double renderScale, uint allocated, ulong volumeUsed, ulong volumeFree);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_allocation_start(SnapshotHandle snapshot);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern Buffer edirstat_allocation_state(SnapshotHandle snapshot);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void edirstat_allocation_cancel(SnapshotHandle snapshot);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void edirstat_snapshot_release(nint snapshot);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void edirstat_buffer_release(Buffer buffer);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static T Read<T>(Buffer buffer)
    {
        try
        {
            var bytes = new byte[checked((int)buffer.Length)];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException(error.GetString());
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidOperationException("扫描后端返回了空结果");
        }
        finally { edirstat_buffer_release(buffer); }
    }

    internal static ScanHandle Start(string path)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("当前入口支持 Windows x64");
        if (edirstat_abi_version() != 4) throw new InvalidOperationException("扫描后端版本不匹配");
        var bytes = Encoding.UTF8.GetBytes(path);
        var result = edirstat_scan_start(bytes, (nuint)bytes.Length, out var pointer);
        using var handle = pointer == 0 ? null : new ScanHandle(pointer);
        Read<JsonElement>(result);
        // Transfer ownership only after checking the result buffer.
        if (handle is null) throw new InvalidOperationException("扫描后端没有返回句柄");
        var owned = new ScanHandle(pointer);
        handle.SetHandleAsInvalid();
        return owned;
    }

    internal static ScanProgress Progress(ScanHandle scan) => Read<ScanProgress>(edirstat_scan_state(scan));
    internal static void Cancel(ScanHandle scan) => edirstat_scan_cancel(scan);
    internal static SnapshotHandle Snapshot(ScanHandle scan)
    {
        var pointer = edirstat_snapshot_acquire(scan);
        return pointer != 0 ? new SnapshotHandle(pointer) : throw new InvalidOperationException("没有可用的扫描结果");
    }
    internal static DirectoryPage Page(SnapshotHandle snapshot, uint id, uint offset, uint sort = 1, bool descending = true) => Read<DirectoryPage>(edirstat_snapshot_page(snapshot, id, offset, 512, sort, descending ? 1u : 0u));
    internal static NodeInfo Node(SnapshotHandle snapshot, uint id, uint sort = 1, bool descending = true) => Read<NodeInfo>(edirstat_snapshot_node(snapshot, id, sort, descending ? 1u : 0u));
    internal static ExtensionInfo[] Extensions(SnapshotHandle snapshot, uint root) => Read<ExtensionInfo[]>(edirstat_snapshot_extensions(snapshot, root));
    internal static FilePage Files(SnapshotHandle snapshot, uint root, string? extension, string name)
    {
        var filter = JsonSerializer.SerializeToUtf8Bytes(new { extension, name });
        return Read<FilePage>(edirstat_snapshot_files(snapshot, root, filter, (nuint)filter.Length));
    }
    internal static MapBlock[] Map(SnapshotHandle snapshot, uint root, double width, double height, double renderScale, bool allocated, ulong volumeUsed, ulong volumeFree, bool names) => Read<MapBlock[]>(edirstat_snapshot_map(snapshot, root, width, height, renderScale, (allocated ? 1u : 0u) | (names ? 2u : 0u), volumeUsed, volumeFree));
    internal static AllocationProgress StartAllocation(SnapshotHandle snapshot) => Read<AllocationProgress>(edirstat_allocation_start(snapshot));
    internal static AllocationProgress AllocationState(SnapshotHandle snapshot) => Read<AllocationProgress>(edirstat_allocation_state(snapshot));
    internal static void CancelAllocation(SnapshotHandle snapshot) => edirstat_allocation_cancel(snapshot);
}

internal sealed class ScanHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ScanHandle(nint handle) : base(true) => SetHandle(handle);
    protected override bool ReleaseHandle() { NativeScanner.edirstat_scan_release(handle); return true; }
}
internal sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SnapshotHandle(nint handle) : base(true) => SetHandle(handle);
    protected override bool ReleaseHandle() { NativeScanner.edirstat_snapshot_release(handle); return true; }
}
internal sealed record ScanProgress(bool Done, bool Cancelled, ulong Files, ulong Directories, ulong Bytes);
internal sealed record AllocationProgress(bool Done, bool Cancelled, ulong Processed, ulong Total, ulong Failed);
internal sealed record DirectoryPage(uint Id, uint Parent, string Path, ulong Size, bool Denied, uint Total, uint Offset, ulong OtherSize, DiskEntry[] Items);
public sealed record DiskEntry(uint Id, uint Parent, string Name, ulong Size, bool Directory, bool Denied,
    ulong Files, ulong Directories, uint Modified, ulong ParentSize, string Extension, string? Path = null,
    ulong? AllocatedSize = null, bool AllocationIncomplete = false) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isDeleted;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDeleted
    {
        get => _isDeleted;
        internal set
        {
            if (_isDeleted == value) return;
            _isDeleted = value;
            PropertyChanged?.Invoke(this, new(nameof(IsDeleted)));
        }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string SizeText => DiskSize.Format(Size);
    public double Percentage => ParentSize == 0 ? 0 : 100.0 * Size / ParentSize;
    public string PercentageText => $"{Percentage:0.0}%";
    public string FilesText => Files.ToString("N0");
    public string DirectoriesText => Directory ? Directories.ToString("N0") : "";
    public string ModifiedText => Modified == 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(Modified).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string Attributes => Denied ? "无权限" : "";
}
internal static class DiskSize
{
    internal static string Format(ulong size)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)size;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
