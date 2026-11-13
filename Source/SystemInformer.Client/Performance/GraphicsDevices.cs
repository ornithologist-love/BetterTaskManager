using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DXCore;

namespace SystemInformer.Client.Performance;

internal sealed record GraphicsDevice(string Luid, ResourceKind Kind, string Name, double? DedicatedLimit,
    double? SharedLimit, string Driver);

// DXCore attributes identify NPUs explicitly; a generic compute engine is never guessed to be an NPU.
internal sealed unsafe class GraphicsDevices : IDisposable
{
    private readonly List<GraphicsDevice> _devices = new();
    private long _lastDiscovery;
    public string Status { get; private set; } = "图形设备尚未枚举";

    public IReadOnlyList<GraphicsDevice> Capture()
    {
        if (_lastDiscovery != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastDiscovery) < TimeSpan.FromSeconds(15)) return _devices;
        _lastDiscovery = System.Diagnostics.Stopwatch.GetTimestamp();
        var next = new Dictionary<string, GraphicsDevice>(StringComparer.OrdinalIgnoreCase);
        IDXCoreAdapterFactory* factory = null;
        try
        {
            var factoryId = typeof(IDXCoreAdapterFactory).GUID;
            Marshal.ThrowExceptionForHR(PInvoke.DXCoreCreateAdapterFactory(&factoryId, (void**)&factory).Value);
            Enumerate(factory, PInvoke.DXCORE_ADAPTER_ATTRIBUTE_D3D11_GRAPHICS, ResourceKind.Gpu, next);
            Enumerate(factory, PInvoke.DXCORE_HARDWARE_TYPE_ATTRIBUTE_GPU, ResourceKind.Gpu, next);
            Enumerate(factory, PInvoke.DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU, ResourceKind.Npu, next);
            _devices.Clear();
            _devices.AddRange(next.Values.OrderBy(device => device.Kind).ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase));
            Status = _devices.Count == 0 ? "未发现支持的 GPU/NPU 设备" : "设备已识别";
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            _devices.Clear(); Status = "GPU/NPU 设备信息不可读取";
        }
        finally { if (factory != null) factory->Release(); }
        return _devices;
    }

    private static void Enumerate(IDXCoreAdapterFactory* factory, Guid attribute, ResourceKind kind,
        Dictionary<string, GraphicsDevice> devices)
    {
        IDXCoreAdapterList* list = null;
        var listId = typeof(IDXCoreAdapterList).GUID;
        try
        {
            factory->CreateAdapterList(1, &attribute, &listId, (void**)&list);
            var count = Math.Min(list->GetAdapterCount(), 256u);
            var adapterId = typeof(IDXCoreAdapter).GUID;
            for (uint index = 0; index < count; index++)
            {
                IDXCoreAdapter* adapter = null;
                try
                {
                    list->GetAdapter(index, &adapterId, (void**)&adapter);
                    byte hardware = 1;
                    if (adapter->IsPropertySupported(DXCoreAdapterProperty.IsHardware))
                        adapter->GetProperty(DXCoreAdapterProperty.IsHardware, 1, &hardware);
                    if (hardware == 0 || !adapter->IsValid()) continue;
                    LUID luid = default;
                    adapter->GetProperty(DXCoreAdapterProperty.InstanceLuid, (nuint)sizeof(LUID), &luid);
                    var id = $"0x{(uint)luid.HighPart:x8}_0x{luid.LowPart:x8}";
                    // Classify by the native attribute-filtered list, not a second bool ABI call.
                    // Compute/ML support alone does not identify NPU hardware.
                    var name = Description(adapter);
                    var version = Property(adapter, DXCoreAdapterProperty.DriverVersion);
                    var driver = version is ulong v ? $"{v >> 48}.{(v >> 32) & 65535}.{(v >> 16) & 65535}.{v & 65535}" : "—";
                    devices[id] = new(id, kind, name.Length > 0 ? name : kind.ToString(),
                        Property(adapter, DXCoreAdapterProperty.DedicatedAdapterMemory),
                        Property(adapter, DXCoreAdapterProperty.SharedSystemMemory), driver);
                }
                catch (COMException) { /* Device disappeared or a required property is unavailable. */ }
                finally { if (adapter != null) adapter->Release(); }
            }
        }
        catch (COMException) { /* This driver does not expose the requested adapter class. */ }
        finally { if (list != null) list->Release(); }
    }

    private static ulong? Property(IDXCoreAdapter* adapter, DXCoreAdapterProperty property)
    {
        ulong value = 0;
        if (!adapter->IsPropertySupported(property)) return null;
        try { adapter->GetProperty(property, sizeof(ulong), &value); return value; }
        catch (COMException) { return null; }
    }

    private static string Description(IDXCoreAdapter* adapter)
    {
        nuint length = 0;
        try { adapter->GetPropertySize(DXCoreAdapterProperty.DriverDescription, &length); }
        catch (COMException) { return ""; }
        if (length is 0 or > 65536) return "";
        var buffer = (byte*)NativeMemory.Alloc(length);
        try
        {
            adapter->GetProperty(DXCoreAdapterProperty.DriverDescription, length, buffer);
            var bytes = new ReadOnlySpan<byte>(buffer, (int)length);
            var end = bytes.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]).Trim();
        }
        catch (COMException) { return ""; }
        finally { NativeMemory.Free(buffer); }
    }

    public void Reset() { _lastDiscovery = 0; _devices.Clear(); }
    public void Dispose() => Reset();
}
