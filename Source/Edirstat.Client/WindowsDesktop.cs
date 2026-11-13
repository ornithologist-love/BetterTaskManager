using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Edirstat.Client;

internal sealed record VolumeSpace(string Root, string Label, ulong Total, ulong Used, ulong Free, ulong? Reserved)
{
    public string Name => $"[{Root.TrimEnd('\\')}] {Label}";
    public double UsedPercent => Total == 0 ? 0 : 100.0 * Used / Total;
    public double FreePercent => Total == 0 ? 0 : 100.0 * Free / Total;
    public static string Format(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.0} {units[unit]}";
    }
}
internal static class WindowsDesktop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DiskSpace
    {
        public ulong ActualTotal, ActualAvailable, ActualPoolUnavailable, CallerTotal, CallerAvailable,
            CallerPoolUnavailable, Used, TotalReserved, StorageReserved, AvailableCommitted, PoolAvailable;
        public uint SectorsPerUnit, BytesPerSector;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetDiskSpaceInformationW(string root, out DiskSpace space);

    internal static VolumeSpace Volume(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("路径没有卷信息");
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException("磁盘未就绪");
        var total = (ulong)drive.TotalSize;
        var free = (ulong)drive.TotalFreeSpace;
        var used = total - Math.Min(total, free);
        ulong? reserved = null;
        try
        {
            if (GetDiskSpaceInformationW(root, out var space) == 0)
            {
                var cluster = checked((ulong)space.SectorsPerUnit * space.BytesPerSector);
                total = checked(space.ActualTotal * cluster);
                free = checked(space.ActualAvailable * cluster);
                used = checked(space.Used * cluster);
                reserved = checked(space.TotalReserved * cluster);
            }
        }
        catch (EntryPointNotFoundException) { }
        return new(root, drive.VolumeLabel, total, used, free, reserved);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Owner;
        public uint Operation;
        public nint From, To;
        public ushort Flags;
        public int Aborted;
        public nint Mappings, Title;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref FileOperation operation);

    // Called only after the UI's confirmation for this exact path. Keep shell
    // confirmations enabled, including a warning if recycling is unavailable.
    internal static Task<bool> Recycle(string path, nint owner)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("回收站删除仅支持 Windows");
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint paths = 0;
            try
            {
                paths = Marshal.StringToHGlobalUni(Path.GetFullPath(path) + "\0\0");
                var operation = new FileOperation { Owner = owner, Operation = 3, From = paths, Flags = 0x0040 | 0x4000 | 0x2000 };
                var error = SHFileOperationW(ref operation);
                if (operation.Aborted != 0) result.SetResult(false);
                else if (error != 0) result.SetException(new Win32Exception(error));
                else result.SetResult(true);
            }
            catch (Exception e) { result.SetException(e); }
            finally { if (paths != 0) Marshal.FreeHGlobal(paths); }
        }) { IsBackground = true, Name = "edirstat-recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}
