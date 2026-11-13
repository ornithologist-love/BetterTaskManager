using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.WindowsProgramming;
using Windows.Wdk.System.SystemInformation;

namespace SystemInformer.Client.Processes;

internal sealed record NativeCpuProcess(uint Id, ulong Created, ulong Cycles, string Name, uint ParentId);
internal sealed record CpuFrame(IReadOnlyList<NativeCpuProcess> Processes,
    IReadOnlyDictionary<uint, double?> Percentages, double? TotalPercent, double? InterruptPercent, string Status);

/// <summary>One complete system snapshot and one cycle denominator for every displayed CPU value.</summary>
internal sealed unsafe class ProcessCpuSampler : IDisposable
{
    private const uint ProcessInformation = 5, ProcessorCycleInformation = 108;
    private const int MaximumBuffer = 64 * 1024 * 1024;
    private byte* _buffer;
    private int _capacity;
    private readonly nint _ntdll = NativeLibrary.Load("ntdll.dll");
    private readonly delegate* unmanaged[Stdcall]<uint, void*, uint, void*, uint, uint*, int> _queryGroup;
    private Dictionary<uint, NativeCpuProcess> _previous = new();
    private ulong _idle, _interrupt;
    private bool _ready;
    private bool _reported;
    private readonly Action<string>? _diagnostics;

    public ProcessCpuSampler(Action<string>? diagnostics = null)
    {
        _diagnostics = diagnostics;
        // NtQuerySystemInformationEx is absent from the SDK metadata; resolve this exact ntdll export
        // for the same per-group read-only query used upstream, without a native helper library.
        if (NativeLibrary.TryGetExport(_ntdll, "NtQuerySystemInformationEx", out var address))
            _queryGroup = (delegate* unmanaged[Stdcall]<uint, void*, uint, void*, uint, uint*, int>)address;
    }

    public void Reset() { _ready = false; _previous.Clear(); }

    public CpuFrame Capture(Func<uint, ulong, ulong?> finalCycles, CancellationToken token)
    {
        var groupsAvailable = ReadProcessorCycles(out var idle, out var interrupts);
        var processes = ReadProcesses(token);
        var current = processes.ToDictionary(process => process.Id);
        var percentages = new Dictionary<uint, double?>(processes.Count);
        double? totalPercent = null, interruptPercent = null;
        var status = groupsAvailable ? "CPU 周期等待基线采样" : "CPU 空闲或中断周期不可读取";
        if (_ready && groupsAvailable)
        {
            var valid = idle >= _idle && interrupts >= _interrupt;
            var idleDelta = valid ? idle - _idle : 0;
            var interruptDelta = valid ? interrupts - _interrupt : 0;
            var deltas = new Dictionary<uint, ulong>();
            ulong total = 0;
            var missedExits = 0;
            try
            {
                total = checked(idleDelta + interruptDelta);
                foreach (var process in processes)
                {
                    if (process.Id == 0) continue; // Idle is read across all processor groups separately.
                    var same = _previous.TryGetValue(process.Id, out var before) && before.Created == process.Created;
                    if (same && process.Cycles < before!.Cycles) { valid = false; continue; }
                    var delta = same ? process.Cycles - before!.Cycles : process.Cycles;
                    total = checked(total + delta);
                    // A new row establishes its own baseline; its cycles still contribute to the denominator.
                    if (same) deltas.Add(process.Id, delta);
                }
                foreach (var before in _previous.Values)
                {
                    if (before.Id == 0 || current.TryGetValue(before.Id, out var now) && now.Created == before.Created) continue;
                    var final = finalCycles(before.Id, before.Created);
                    if (final is ulong cycles && cycles >= before.Cycles)
                        total = checked(total + cycles - before.Cycles);
                    else missedExits++;
                }
            }
            catch (OverflowException) { valid = false; }
            if (valid && total != 0 && idleDelta <= total)
            {
                totalPercent = 100d * (1 - idleDelta / (double)total);
                interruptPercent = interruptDelta / (double)total * 100;
                foreach (var (id, delta) in deltas) percentages.Add(id, delta / (double)total * 100);
                status = missedExits == 0 ? "CPU 周期采样中"
                    : $"CPU 周期采样中；{missedExits} 个退出进程的尾段无法补记";
                if (!_reported && _diagnostics != null)
                {
                    _reported = true;
                    _diagnostics($"CPU cycle snapshot: processes={processes.Count}, total_cycles={total}, "
                        + $"idle_cycles={idleDelta}, interrupt_cycles={interruptDelta}, "
                        + $"cpu={totalPercent:F2}%, interrupts={interruptPercent:F2}%, missed_exits={missedExits}");
                }
            }
            else status = "CPU 周期计数变化异常，正在重建基线";
        }
        _previous = current;
        _idle = idle; _interrupt = interrupts;
        _ready = groupsAvailable;
        if (!groupsAvailable && !_reported && _diagnostics != null)
        {
            _reported = true;
            _diagnostics("CPU cycle snapshot unavailable: idle/interrupt processor-group query failed.");
        }
        return new(processes, percentages, totalPercent, interruptPercent, status);
    }

    private bool ReadProcessorCycles(out ulong idle, out ulong interrupts)
    {
        idle = interrupts = 0;
        var groups = PInvoke.GetActiveProcessorGroupCount();
        if (groups == 0 || _queryGroup == null) return false;
        // These APIs return one UInt64 counter per logical processor, not a frequency estimate.
        ulong* idleValues = stackalloc ulong[64];
        ulong* interruptValues = stackalloc ulong[64];
        try
        {
            for (ushort group = 0; group < groups; group++)
            {
                var count = PInvoke.GetActiveProcessorCount(group);
                if (count is 0 or > 64) return false;
                uint bytes = count * sizeof(ulong), returned = 0;
                new Span<ulong>(idleValues, 64).Clear();
                new Span<ulong>(interruptValues, 64).Clear();
                if (!PInvoke.QueryIdleProcessorCycleTimeEx(group, &bytes, idleValues)
                    || _queryGroup(ProcessorCycleInformation, &group, sizeof(ushort), interruptValues,
                        count * sizeof(ulong), &returned) < 0 || returned != count * sizeof(ulong)) return false;
                for (var i = 0; i < count; i++)
                {
                    idle = checked(idle + idleValues[i]);
                    interrupts = checked(interrupts + interruptValues[i]);
                }
            }
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private List<NativeCpuProcess> ReadProcesses(CancellationToken token)
    {
        uint returned = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_capacity == 0) Resize(64 * 1024);
            var status = Windows.Wdk.PInvoke.NtQuerySystemInformation((SYSTEM_INFORMATION_CLASS)ProcessInformation,
                _buffer, (uint)_capacity, &returned);
            if (status.Value >= 0) break;
            if ((uint)status.Value is not (0xC0000004 or 0xC0000023))
                throw new InvalidOperationException($"无法读取 CPU 进程快照（NTSTATUS 0x{(uint)status.Value:X8}）。");
            Resize(checked((int)Math.Max(returned, (uint)_capacity * 2)));
        }
        if (returned == 0 || returned > _capacity) throw new InvalidDataException("CPU 进程快照长度无效。");
        var result = new List<NativeCpuProcess>();
        var offset = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if ((uint)offset > returned || returned - offset < sizeof(SYSTEM_PROCESS_INFORMATION))
                throw new InvalidDataException("CPU 进程快照结构不完整。");
            var info = (SYSTEM_PROCESS_INFORMATION*)(_buffer + offset);
            var idValue = (nuint)info->UniqueProcessId.Value;
            var parentValue = (nuint)info->Reserved2;
            if (idValue > uint.MaxValue || parentValue > uint.MaxValue) throw new InvalidDataException("进程 ID 无效。");
            // Windows 10/11 x64 SYSTEM_PROCESS_INFORMATION: the SDK hides these fields inside Reserved1.
            // The stable prefix has CycleTime at byte 24 and CreateTime at byte 32; no phnt structure is copied.
            var cycles = *(ulong*)((byte*)info + 24);
            var created = *(ulong*)((byte*)info + 32);
            var id = (uint)idValue;
            var image = info->ImageName;
            var name = id == 0 ? "System Idle Process" : id == 4 ? "System" : "";
            if (image.Length != 0)
            {
                var text = (byte*)image.Buffer.Value;
                if (image.Length % sizeof(char) != 0 || image.Length > image.MaximumLength
                    || text < _buffer || text > _buffer + returned || image.Length > _buffer + returned - text)
                    throw new InvalidDataException("CPU 进程快照名称无效。");
                name = new string((char*)text, 0, image.Length / sizeof(char));
            }
            result.Add(new(id, created, cycles, name.Length == 0 ? $"进程 {id}" : name, (uint)parentValue));
            if (info->NextEntryOffset == 0) break;
            if (info->NextEntryOffset < sizeof(SYSTEM_PROCESS_INFORMATION) || info->NextEntryOffset > returned - offset)
                throw new InvalidDataException("CPU 进程快照链接无效。");
            offset = checked(offset + (int)info->NextEntryOffset);
        }
        return result;
    }

    private void Resize(int size)
    {
        if (size > MaximumBuffer) throw new InvalidDataException("CPU 进程快照超过读取上限。");
        var replacement = (byte*)NativeMemory.Alloc((nuint)size);
        NativeMemory.Free(_buffer);
        _buffer = replacement; _capacity = size;
    }

    public void Dispose()
    {
        NativeMemory.Free(_buffer); _buffer = null; _capacity = 0;
        NativeLibrary.Free(_ntdll);
        Reset();
    }
}
