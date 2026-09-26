using System.Diagnostics;
using System.Globalization;
using Jiaolong.Core.Smu;
using Jiaolong.Core.Wmi;

namespace Jiaolong.App.Sensors;

/// <summary>Fixed-capacity ring buffer used for the history charts.</summary>
public sealed class RingBuffer
{
    private readonly float[] _v;
    private int _count;
    private int _head;

    public RingBuffer(int capacity) => _v = new float[Math.Max(2, capacity)];

    public int Count => _count;

    public void Push(float value)
    {
        _v[_head] = value;
        _head = (_head + 1) % _v.Length;
        if (_count < _v.Length) _count++;
    }

    /// <summary>Oldest first.</summary>
    public float[] Snapshot()
    {
        var outArr = new float[_count];
        for (int i = 0; i < _count; i++)
            outArr[i] = _v[(_head - _count + _v.Length + i) % _v.Length];
        return outArr;
    }
}

/// <summary>
/// Collects CPU/GPU telemetry using only things that ship with the machine:
///   CPU  - WQL against Win32_Processor (through our existing WMI bridge)
///   GPU  - nvidia-smi (bundled with the NVIDIA driver), cached for <see cref="GpuCacheMs"/>
/// Nothing here pulls a NuGet package.
/// </summary>
public sealed class SensorHub
{
    private readonly JiaolongWmi? _wmi;
    private readonly object _gate = new();
    private DateTime _lastGpuUtc = DateTime.MinValue;
    private bool _gpuProbed;

    // perf counters: real-time CPU frequency/load without WMI round-trips
    private PerformanceCounter? _cFreq, _cPerf, _cUtil;
    private bool _countersTried;
    private bool _countersOk;
    private bool _coresQueried;

    public SensorHub(JiaolongWmi? wmi) => _wmi = wmi;

    // ---- latest values -------------------------------------------------
    public double CpuFreqMhz { get; private set; }
    public double CpuMaxMhz { get; private set; }
    public double CpuLoad { get; private set; }
    public int CpuCores { get; private set; }

    /// <summary>Package power in watts from MSR 0xC001029B (0 until the second sample).</summary>
    public double CpuPowerW { get; private set; }
    public bool CpuPowerAvailable => _msr.IsAvailable;

    private readonly MsrReader _msr = new();

    public bool GpuAvailable { get; private set; }
    public double GpuTempC { get; private set; }
    public double GpuLoad { get; private set; }
    public double GpuClockMhz { get; private set; }
    public double GpuPowerW { get; private set; }

    // memory / disk (WQL, cached - these change slowly so we refresh every 5 s)
    public double MemTotalGb { get; private set; }
    public double MemAvailGb { get; private set; }
    public double MemLoadPct { get; private set; }
    public double DiskTotalGb { get; private set; }
    public double DiskAvailGb { get; private set; }
    public int MemDiskCacheMs { get; set; } = 5000;
    private DateTime _lastMemDiskUtc = DateTime.MinValue;

    public string LastError { get; private set; } = "";

    /// <summary>History for the charts (one point per Poll).</summary>
    public RingBuffer CpuFreqHistory { get; } = new(180);
    public RingBuffer CpuTempHistory { get; } = new(180);
    public RingBuffer GpuTempHistory { get; } = new(180);

    /// <summary>How long a nvidia-smi result is reused (ms).</summary>
    public int GpuCacheMs { get; set; } = 1000;

    /// <summary>
    /// CPU: performance counters (fast, boost-aware). Falls back to WQL if they are unavailable.
    /// GPU: nvidia-smi, cached for <see cref="GpuCacheMs"/>.
    /// Call this from a BACKGROUND thread - both paths can block for 100-300 ms.
    /// </summary>
    public void Poll()
    {
        lock (_gate)
        {
            PollCpu();
            PollGpu();
            CpuPowerW = _msr.SamplePackagePower();
            PollMemDisk();
        }
    }

    private void PollMemDisk()
    {
        if (_wmi is null) return;
        if ((DateTime.UtcNow - _lastMemDiskUtc).TotalMilliseconds < MemDiskCacheMs) return;
        _lastMemDiskUtc = DateTime.UtcNow;
        try
        {
            if (MemTotalGb <= 0)
            {
                var cs = _wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                if (cs.Length > 0 && ParseNamedRow(cs[0]).TryGetValue("TotalPhysicalMemory", out var tot))
                    MemTotalGb = Num(tot) / (1024.0 * 1024 * 1024);
            }

            var os = _wmi.Query("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            if (os.Length > 0 && ParseNamedRow(os[0]).TryGetValue("FreePhysicalMemory", out var freeKb))
            {
                MemAvailGb = Num(freeKb) / (1024.0 * 1024);
                if (MemTotalGb > 0)
                    MemLoadPct = Math.Clamp((MemTotalGb - MemAvailGb) / MemTotalGb * 100.0, 0, 100);
            }

            var dk = _wmi.Query("SELECT Size,FreeSpace FROM Win32_LogicalDisk WHERE DeviceID='C:'");
            if (dk.Length > 0)
            {
                var m = ParseNamedRow(dk[0]);
                if (m.TryGetValue("Size", out var sz)) DiskTotalGb = Num(sz) / (1024.0 * 1024 * 1024);
                if (m.TryGetValue("FreeSpace", out var fs)) DiskAvailGb = Num(fs) / (1024.0 * 1024 * 1024);
            }
        }
        catch (Exception ex) { LastError = "mem/disk: " + ex.Message; }
    }

    private void PollCpu()
    {
        if (TryPerfCounters()) return;
        PollCpuViaWql();
    }

    /// <summary>
    /// "Processor Frequency" is the *nominal* clock, "% Processor Performance" is the live ratio
    /// against it (it can exceed 100% while boosting), so nominal * pct gives the real MHz.
    /// </summary>
    private bool TryPerfCounters()
    {
        try
        {
            if (!_countersTried)
            {
                _countersTried = true;
                _cFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
                _cPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                _cUtil = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                _ = _cFreq.NextValue();
                _ = _cPerf.NextValue();
                _ = _cUtil.NextValue();
                _countersOk = true;
            }
            if (!_countersOk) return false;
            var cf = _cFreq;
            var cp = _cPerf;
            var cu = _cUtil;
            if (cf is null || cp is null || cu is null) return false;

            double nominal = cf.NextValue();
            double perf = cp.NextValue();
            double util = cu.NextValue();

            if (nominal > 0)
            {
                CpuMaxMhz = nominal;
                CpuFreqMhz = nominal * Math.Max(perf, 0) / 100.0;
            }
            CpuLoad = Math.Max(util, 0);
            LastError = "";
            if (!_coresQueried) QueryCoresOnce();
            return true;
        }
        catch (Exception ex)
        {
            _countersOk = false;
            LastError = "CPU counter: " + ex.Message;
            return false;
        }
    }

    private void QueryCoresOnce()
    {
        try
        {
            var rows = _wmi?.Query("SELECT NumberOfCores FROM Win32_Processor");
            if (rows is { Length: > 0 } &&
                ParseNamedRow(rows[0]).TryGetValue("NumberOfCores", out var d))
            {
                CpuCores = (int)Num(d);
            }
        }
        catch { /* cosmetic only */ }
        finally { _coresQueried = true; }
    }

    private void PollCpuViaWql()
    {
        if (_wmi is null) return;
        try
        {
            // Win32_Processor lives in root\cimv2. The bridge returns "Name=Value" fields because
            // WMI hands back properties in ALPHABETICAL order, so positional mapping is wrong.
            var rows = _wmi.Query(
                "SELECT CurrentClockSpeed,MaxClockSpeed,LoadPercentage,NumberOfCores FROM Win32_Processor");
            if (rows.Length == 0) return;

            var map = ParseNamedRow(rows[0]);
            CpuFreqMhz = map.TryGetValue("CurrentClockSpeed", out var a) ? Num(a) : 0;
            CpuMaxMhz = map.TryGetValue("MaxClockSpeed", out var b) ? Num(b) : 0;
            CpuLoad = map.TryGetValue("LoadPercentage", out var c) ? Num(c) : 0;
            CpuCores = (int)(map.TryGetValue("NumberOfCores", out var d) ? Num(d) : 0);
            LastError = "";
        }
        catch (Exception ex)
        {
            LastError = "CPU sensor: " + ex.Message;
        }
    }

    /// <summary>Turns a row of "Name=Value" tokens into a lookup.</summary>
    internal static Dictionary<string, string> ParseNamedRow(string[] row)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in row)
        {
            int eq = token.IndexOf('=');
            if (eq <= 0) continue;
            map[token[..eq]] = token[(eq + 1)..];
        }
        return map;
    }

    private void PollGpu()
    {
        bool due = (DateTime.UtcNow - _lastGpuUtc).TotalMilliseconds >= GpuCacheMs;
        if (!due && _gpuProbed) return;
        _lastGpuUtc = DateTime.UtcNow;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=temperature.gpu,utilization.gpu,clocks.gr,power.draw " +
                            "--format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            string line = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(1500);
            _gpuProbed = true;

            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(line)) { GpuAvailable = false; return; }

            var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 4) { GpuAvailable = false; return; }

            GpuTempC = Num(parts[0]);
            GpuLoad = Num(parts[1]);
            GpuClockMhz = Num(parts[2]);
            GpuPowerW = Num(parts[3]);
            GpuAvailable = GpuTempC > 0;
            LastError = "";
        }
        catch
        {
            GpuAvailable = false;   // nvidia-smi missing or blocked -> silently degrade
        }
    }

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Call after each full tick to append to the chart histories.</summary>
    public void PushHistory(double cpuTemp)
    {
        lock (_gate)
        {
            if (CpuFreqMhz > 0) CpuFreqHistory.Push((float)CpuFreqMhz);
            if (cpuTemp > 0) CpuTempHistory.Push((float)cpuTemp);
            if (GpuAvailable && GpuTempC > 0) GpuTempHistory.Push((float)GpuTempC);
        }
    }
}
