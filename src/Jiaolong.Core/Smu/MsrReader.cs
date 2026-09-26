using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jiaolong.Core.Smu;

/// <summary>
/// MSR access + CPU package power, discovered empirically on this machine with
/// TDPC/tools/MsrProbe (do not "fix" the register numbers without re-running that probe):
///
///   0xC0010299  AMD RAPL unit      energyUnit = 2 ^ -(bits[12:8]) joules  (= 2^-16 here)
///   0xC001029B  package energy     free-running, delta * unit / dt = watts
///   0xC0010293 / 0xC001029A        also increment, but with a different/erratic scaling
///                                   (they swung between 0 W and 65000 W, so they are rejected)
///
/// Requires WinRing0, which EcAccess already initialises at startup.
/// </summary>
public sealed class MsrReader
{
    private const string Ols = "WinRing0x64.dll";
    private const uint RaplUnit = 0xC0010299;
    private const uint PkgEnergy = 0xC001029B;

    private readonly object _gate = new();
    private bool _ready;
    private bool _tried;
    private double _energyUnit = Math.Pow(2, -16);
    private uint _lastEnergy;
    private long _lastTicks = long.MinValue;

    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool InitializeOls();

    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetDllStatus();

    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool Rdmsr(uint index, out uint eax, out uint edx);

    public bool IsAvailable
    {
        get
        {
            EnsureReady();
            return _ready;
        }
    }

    private void EnsureReady()
    {
        lock (_gate)
        {
            if (_tried) return;
            _tried = true;
            try
            {
                if (GetDllStatus() != 0) InitializeOls();
                _ready = GetDllStatus() == 0;
                if (_ready && Rdmsr(RaplUnit, out uint unit, out _))
                {
                    int exp = (int)((unit >> 8) & 0x1F);
                    _energyUnit = Math.Pow(2, -exp);
                }
            }
            catch { _ready = false; }
        }
    }

    public bool Read(uint index, out uint value)
    {
        EnsureReady();
        value = 0;
        if (!_ready) return false;
        try { return Rdmsr(index, out value, out _); }
        catch { return false; }
    }

    /// <summary>
    /// Returns CPU package power in watts by sampling the package energy counter.
    /// First call only primes the counter and returns 0; later calls return the average
    /// watts since the previous call. 0 means "not available yet".
    /// </summary>
    public double SamplePackagePower()
    {
        lock (_gate)
        {
            if (!Read(PkgEnergy, out uint energy)) return 0;

            long now = Stopwatch.GetTimestamp();
            if (_lastTicks == long.MinValue)
            {
                _lastEnergy = energy;
                _lastTicks = now;
                return 0;
            }

            double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
            uint delta = unchecked(energy - _lastEnergy);   // wraps at 2^32
            _lastEnergy = energy;
            _lastTicks = now;

            if (dt <= 0.05 || dt > 30) return 0;
            double watts = delta * _energyUnit / dt;
            return watts is > 0 and < 400 ? watts : 0;      // sanity guard
        }
    }
}
