using Jiaolong.Core.Ec;
using Jiaolong.Core.Wmi;

namespace Jiaolong.Core.Fan;

public enum FanOwner
{
    /// <summary>BIOS automatic curve owns the fan (EC target registers are 0/0).</summary>
    Bios,
    /// <summary>We pinned both fans to a fixed target.</summary>
    Manual,
}

public enum FanEventKind
{
    StartupCleared,
    Locked,
    ReleasedByUs,
    ReleasedExternally,
    SafetyValve,
    WatchdogError,
}

public sealed record FanEvent(FanEventKind Kind, string Message, DateTime AtUtc);

/// <summary>
/// Owns fan control with an explicit state machine, a watchdog and a thermal safety valve.
///
/// Design rules (learned from watching the third-party tool misbehave):
///   1. On startup we ALWAYS call <see cref="EcAccess.ReleaseToBios"/> so a crash from the
///      previous run cannot leave the fans pinned.
///   2. We never blindly trust our own write - the loop reads the EC back every tick.
///   3. If someone else changes the EC (third-party tool, another instance) we ADOPT their
///      state and raise <see cref="Event"/> instead of fighting them.
///   4. If CPU temperature crosses <see cref="SafetyTempC"/> we release immediately.
///   5. Disposing / cancelling the loop always releases.
/// </summary>
public sealed class FanController : IDisposable
{
    private readonly EcAccess _ec;
    private readonly JiaolongWmi? _wmi;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loop;
    private int _lockedRpm;
    private bool _disposed;

    /// <summary>
    /// CPU temperature (WMI 0x16) at which control is handed back to the BIOS.
    /// 0 disables the valve. Default 92 ℃ ensures high-load stability without false trips.
    /// </summary>
    public int SafetyTempC { get; set; } = 92;

    /// <summary>Cool-down margin required before a lock may be armed again.</summary>
    public int SafetyHysteresisC { get; set; } = 5;

    /// <summary>True while the CPU is still too hot to safely re-arm the manual lock.</summary>
    private bool _safetyLatched;

    /// <summary>When set, the watchdog writes the interpolated target every tick instead of only observing.</summary>
    private FanCurve? _curve;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public FanOwner Owner { get; private set; } = FanOwner.Bios;
    public int LockedRpm => _lockedRpm;
    public int LastTempC { get; private set; } = -1;
    public string LastStatus { get; private set; } = "starting";

    public event Action<FanEvent>? Event;

    public FanController(EcAccess ec, JiaolongWmi? wmi = null)
    {
        _ec = ec;
        _wmi = wmi;
    }

    /// <summary>Clear any residue left by a previous crash. Call exactly once, before anything else.</summary>
    public void Startup()
    {
        _ec.ReleaseToBios();
        lock (_gate) { Owner = FanOwner.Bios; _lockedRpm = 0; }
        Raise(FanEventKind.StartupCleared, "released to BIOS (startup hygiene)");
    }

    public void StartWatchdog()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>True while the 1 Hz temperature curve is driving the fans.</summary>
    public bool CurveEnabled
    {
        get { lock (_gate) return _curve is not null; }
    }

    /// <summary>
    /// Enables (or, with null/invalid input, disables) 1 Hz temperature-curve control.
    /// <para>
    /// Disabling must NOT hand the fan back to the BIOS: the EC is pinned at the value the
    /// curve would have chosen at the current temperature, so the speed is continuous when
    /// leaving curve mode. An explicit <see cref="Release"/> still returns to BIOS.
    /// </para>
    /// </summary>
    public void SetCurve(FanCurve? curve)
    {
        bool enable = curve is { IsValid: true };
        int[]? temps = null;
        int pinned = -1;

        lock (_gate)
        {
            var previous = _curve;

            if (!enable && previous is not null)
            {
                bool tooHot = SafetyTempC > 0 && LastTempC >= SafetyTempC;
                if (LastTempC >= 0 && !tooHot)
                {
                    int hundreds = Math.Clamp(previous.Interpolate(LastTempC) / 100, 18, 58);
                    _ec.SetManualFanSpeed((byte)hundreds);
                    _lockedRpm = hundreds * 100;
                    Owner = FanOwner.Manual;
                    pinned = _lockedRpm;
                }
            }

            _curve = enable ? curve : null;
            _safetyLatched = false;
            temps = _curve?.Temps;
        }

        if (enable)
            Raise(FanEventKind.Locked,
                $"温度曲线已启用（{temps![0]}~{temps[^1]}℃，{string.Join("/", temps)}）");
        else if (pinned >= 0)
            Raise(FanEventKind.Locked,
                $"温度曲线已关闭，风扇保持在当前曲线目标 {pinned} RPM");
        else
            Raise(FanEventKind.ReleasedByUs, "温度曲线已关闭");
    }

    /// <summary>Pin both fans. rpm must be 1800..5800 in steps of 100.</summary>
    public void Lock(int rpm)
    {
        if (rpm is < 1800 or > 5800) throw new ArgumentOutOfRangeException(nameof(rpm), "1800..5800");

        int t = ReadTemp();
        if (SafetyTempC > 0 && (_safetyLatched || t >= SafetyTempC - SafetyHysteresisC))
            throw new InvalidOperationException(
                $"CPU {t}℃ 已接近安全阀 {SafetyTempC}℃，先降温再锁定（或临时调高安全阀）");

        byte value = (byte)(rpm / 100);
        lock (_gate)
        {
            _curve = null;                 // an explicit fixed lock overrides the curve
            _ec.SetManualFanSpeed(value);
            _lockedRpm = value * 100;
            Owner = FanOwner.Manual;
            _safetyLatched = false;
        }
        Raise(FanEventKind.Locked, $"locked at {_lockedRpm} RPM");
    }

    private int ReadTemp()
    {
        try { return _wmi?.ReadCpuTemp() ?? LastTempC; }
        catch { return LastTempC; }
    }

    /// <summary>Hand the fans back to the BIOS. Always safe, always idempotent.</summary>
    public void Release()
    {
        lock (_gate)
        {
            bool wasManual = Owner == FanOwner.Manual || _curve is not null;
            _curve = null;                 // "hand back to BIOS" also stops the curve
            _ec.ReleaseToBios();
            Owner = FanOwner.Bios;
            _lockedRpm = 0;
            if (!wasManual) return;               // releasing a BIOS-owned fan is a no-op
        }
        Raise(FanEventKind.ReleasedByUs, "released to BIOS");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                LastStatus = "watchdog error: " + ex.Message;
                Raise(FanEventKind.WatchdogError, ex.Message);
                // Fail safe: we lost visibility of the EC -> do not keep the fans pinned.
                try { Release(); } catch { /* nothing more we can do */ }
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }

        // fail-safe on shutdown no matter how we got here
        try { _ec.ReleaseToBios(); } catch { /* best effort */ }
    }

    private void Tick()
    {
        byte cpuTarget = _ec.Read16(EcAccess.FanSpeedCpu);
        byte gpuTarget = _ec.Read16(EcAccess.FanSpeedGpu);
        int temp = _wmi?.ReadCpuTemp() ?? -1;
        LastTempC = temp;

        lock (_gate)
        {
            // 0) temperature curve owns the fan while enabled: write interpolated target every tick
            if (_curve is not null)
            {
                if (temp < 0) { LastStatus = "curve: waiting for temperature"; return; }

                // the safety valve still wins, with hysteresis so the two never oscillate
                if (SafetyTempC > 0 && temp >= SafetyTempC)
                {
                    if (!_safetyLatched)
                    {
                        _ec.ReleaseToBios();
                        Owner = FanOwner.Bios;
                        _lockedRpm = 0;
                        _safetyLatched = true;
                        Raise(FanEventKind.SafetyValve,
                            $"CPU {temp}℃ >= {SafetyTempC}℃，温度曲线暂停并交还 BIOS");
                    }
                    LastStatus = $"curve paused (CPU {temp}℃)";
                    return;
                }
                if (_safetyLatched)
                {
                    if (temp >= SafetyTempC - SafetyHysteresisC)
                    {
                        LastStatus = $"curve waiting to cool (CPU {temp}℃)";
                        return;
                    }
                    _safetyLatched = false;
                }

                int want = _curve.Interpolate(temp);
                int hundreds = Math.Clamp(want / 100, 18, 58);   // EC unit = 100 RPM, range 1800..5800
                _ec.SetManualFanSpeed((byte)hundreds);
                _lockedRpm = hundreds * 100;
                Owner = FanOwner.Manual;
                LastStatus = $"curve {temp}℃ -> {_lockedRpm} RPM";
                return;
            }

            if (Owner == FanOwner.Manual)
            {
                // 4) thermal safety valve wins over everything
                if (SafetyTempC > 0 && temp >= SafetyTempC)
                {
                    _ec.ReleaseToBios();
                    Owner = FanOwner.Bios;
                    _lockedRpm = 0;
                    _safetyLatched = true;
                    Raise(FanEventKind.SafetyValve,
                        $"CPU {temp}℃ >= {SafetyTempC}℃，已交还 BIOS（降温 {SafetyHysteresisC}℃ 后才允许重新锁定）");
                    LastStatus = $"safety valve (CPU {temp}℃)";
                    return;
                }

                if (SafetyTempC > 0 && temp < SafetyTempC - SafetyHysteresisC)
                    _safetyLatched = false;

                // 3) someone else changed the EC -> adopt, do not fight
                if (cpuTarget == 0 && gpuTarget == 0)
                {
                    Owner = FanOwner.Bios;
                    _lockedRpm = 0;
                    Raise(FanEventKind.ReleasedExternally,
                        "EC targets cleared by another program; adopted BIOS ownership");
                    LastStatus = "released externally";
                    return;
                }

                // 2) our write is still in effect
                LastStatus = $"manual {_lockedRpm} RPM, CPU {temp}C";
            }
            else
            {
                if (cpuTarget != 0 || gpuTarget != 0)
                {
                    Owner = FanOwner.Manual;
                    _lockedRpm = cpuTarget * 100;
                    Raise(FanEventKind.Locked,
                        $"another program pinned the fans to {_lockedRpm} RPM; adopting");
                }
                LastStatus = $"BIOS auto, CPU {temp}C";
            }
        }
    }

    private void Raise(FanEventKind kind, string message) =>
        Event?.Invoke(new FanEvent(kind, message, DateTime.UtcNow));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* already cancelled */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* observed below */ }
        try { _ec.ReleaseToBios(); } catch { /* last resort */ }
        _cts.Dispose();
    }
}
