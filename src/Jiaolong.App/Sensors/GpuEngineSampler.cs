using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jiaolong.App.Sensors;

/// <summary>Samples the busiest WDDM engine on the NVIDIA adapter reported by nvidia-smi.</summary>
internal sealed class GpuEngineSampler
{
    private const uint NvidiaVendorId = 0x10DE;
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private readonly PerformanceCounterCategory _category = new("GPU Engine");
    private uint? _deviceId;
    private string[] _adapterTokens = [];
    private Dictionary<string, CounterSample>? _previous;

    public double? Sample(uint deviceId)
    {
        try
        {
            if (_deviceId != deviceId)
            {
                _deviceId = deviceId;
                _adapterTokens = FindAdapterTokens(deviceId);
                _previous = null;
            }
            if (_adapterTokens.Length == 0) return null;
            var instances = _category.ReadCategory()["Utilization Percentage"];
            if (instances is null) return null;

            var current = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
            var engineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceData instance in instances.Values)
            {
                string name = instance.InstanceName;
                if (instance.Sample.CounterType != PerformanceCounterType.Timer100Ns) continue;
                string? token = _adapterTokens.FirstOrDefault(candidate => name.Contains(candidate, StringComparison.OrdinalIgnoreCase));
                if (token is null) continue;

                var sample = instance.Sample;
                current[name] = sample;
                if (_previous is null || !_previous.TryGetValue(name, out var old)) continue;
                double? busy = BusyPercent(old, sample);
                if (busy is null) continue;

                // Sum the processes scheduled on the same physical engine. Different
                // engines can be busy concurrently, so report the busiest single engine.
                int start = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                int end = name.IndexOf("_engtype_", start + token.Length, StringComparison.OrdinalIgnoreCase);
                if (end < 0) continue;
                string engine = name[start..end];
                engineTotals[engine] = engineTotals.GetValueOrDefault(engine) + busy.Value;
            }
            _previous = current;
            return engineTotals.Count > 0 ? Math.Clamp(engineTotals.Values.Max(), 0, 100) : null;
        }
        catch
        {
            _previous = null;
            return null;
        }
    }

    internal static double? BusyPercent(CounterSample previous, CounterSample current)
    {
        long elapsed = current.TimeStamp100nSec - previous.TimeStamp100nSec;
        long busy = current.RawValue - previous.RawValue;
        return elapsed > 0 && busy >= 0 ? Math.Clamp(busy * 100.0 / elapsed, 0, 100) : null;
    }

    private static string[] FindAdapterTokens(uint deviceId)
    {
        const int DxgiNotFound = unchecked((int)0x887A0002);
        if (CreateDXGIFactory1(in FactoryId, out nint factory) < 0) return [];
        try
        {
            // IDXGIFactory1::EnumAdapters1 is slot 12 after the inherited COM methods.
            var enumerate = GetMethod<EnumAdapter>(factory, 12);
            var matches = new List<string>();
            for (uint index = 0; ; index++)
            {
                int result = enumerate(factory, index, out nint adapter);
                if (result == DxgiNotFound) return matches.ToArray();
                if (result < 0) return [];
                try
                {
                    // IDXGIAdapter1::GetDesc1 is slot 10.
                    if (GetMethod<GetAdapterDescription>(adapter, 10)(adapter, out var description) < 0)
                        return [];
                    if (description.VendorId != NvidiaVendorId || description.DeviceId != deviceId) continue;
                    matches.Add($"_luid_0x{description.LuidHigh:X8}_0x{description.LuidLow:X8}_");
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
    }

    private static T GetMethod<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid id, out nint factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapter(nint factory, uint index, out nint adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDescription(nint adapter, out AdapterDescription description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow, LuidHigh, Flags;
    }
}
