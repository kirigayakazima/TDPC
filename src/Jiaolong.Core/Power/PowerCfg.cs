using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Jiaolong.Core.Power;

/// <summary>
/// Windows-native CPU tuning via powercfg. Zero drivers, fully reversible.
/// Target GUIDs confirmed present on Jiaolong 16PRO 2023 (registry verified 2026-09-25).
/// </summary>
public static class PowerCfg
{
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";

    /// <summary>Maximum processor frequency, unit = MHz (hidden Windows setting).</summary>
    public const string MaxProcessorFrequency = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";

    /// <summary>Processor performance boost mode: 0 disabled / 1 enabled / 2 efficient.</summary>
    public const string BoostMode = "be337238-0d82-4146-a960-4f3749d470c7";

    /// <summary>Processor performance core parking max cores, 0..100 percent.</summary>
    public const string CoreParkingMaxCores = "ea062031-0e34-4ff1-9b6d-eb1059334028";

    private static readonly string SchemeKey =
        @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    // ---- powrprof: writing the index is not enough, Windows only re-evaluates the power
    //      settings when the scheme is (re)activated, otherwise the value sits in the registry
    //      while the CPU happily keeps boosting. Verified empirically: index read back 4000
    //      while the cores ran at 5.04 GHz.
    private const uint ERROR_SUCCESS = 0;

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup,
                                                       ref Guid setting, int value);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup,
                                                       ref Guid setting, int value);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    public static string GetActiveScheme()
    {
        using var k = Registry.LocalMachine.OpenSubKey(SchemeKey);
        var v = k?.GetValue("ActivePowerScheme") as string;
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException("cannot read ActivePowerScheme");
        return v;
    }

    /// <summary>Read the raw AC/DC indexes of a power setting. Returns null when absent.</summary>
    public static (int Ac, int Dc)? ReadIndexes(string settingGuid)
    {
        var scheme = GetActiveScheme();
        using var k = Registry.LocalMachine.OpenSubKey($@"{SchemeKey}\{scheme}\{SubProcessor}\{settingGuid}");
        if (k is null) return null;
        var ac = k.GetValue("ACSettingIndex");
        var dc = k.GetValue("DCSettingIndex");
        if (ac is null && dc is null) return null;
        return (ToInt(ac), ToInt(dc));
    }

    private static int ToInt(object? o) => o switch
    {
        int i => i,
        byte b => b,
        byte[] { Length: > 0 } a => BitConverter.ToInt32(a.Length >= 4 ? a : new byte[4].Concat(a).ToArray(), 0),
        string s when int.TryParse(s, out var v) => v,
        _ => -1,
    };

    public static void SetIndex(string settingGuid, int value, bool ac = true, bool dc = true)
    {
        var schemeStr = GetActiveScheme();
        var scheme = Guid.Parse(schemeStr);
        var sub = Guid.Parse(SubProcessor);
        var setting = Guid.Parse(settingGuid);

        uint hr = 0;
        if (ac) hr |= PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (dc) hr |= PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (hr != ERROR_SUCCESS)
            Run("powercfg", $"/setacvalueindex {schemeStr} {SubProcessor} {settingGuid} {value}");

        // force Windows to re-apply the scheme so the new index actually takes effect
        uint reapply = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (reapply != ERROR_SUCCESS)
            Run("powercfg", $"/setactive {schemeStr}");
    }

    /// <summary>Cap CPU clock. 0 = remove the cap.</summary>
    public static void SetMaxFrequency(int mhz)
    {
        if (mhz < 0) throw new ArgumentOutOfRangeException(nameof(mhz));
        SetIndex(MaxProcessorFrequency, mhz);
    }

    public static int? GetMaxFrequency() => ReadIndexes(MaxProcessorFrequency)?.Ac;

    /// <summary>Raw boost-mode index: 0 = disabled, 1 = enabled, 2 = efficient. Null when absent.</summary>
    public static int? GetTurboBoostMode()
    {
        var v = ReadIndexes(BoostMode)?.Ac;
        return v is null or < 0 ? null : v;
    }

    /// <summary>true = turbo boost is enabled (i.e. the index is not 0).</summary>
    public static bool? GetTurboBoostEnabled()
    {
        var v = GetTurboBoostMode();
        return v is null ? null : v != 0;
    }

    /// <summary>Enable (index 1) or disable (index 0) turbo boost.</summary>
    public static void SetTurboBoost(bool enabled) => SetIndex(BoostMode, enabled ? 1 : 0);

    public static void SetCoreParkingMaxPercent(int percent)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        SetIndex(CoreParkingMaxCores, percent);
    }

    private static void Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + file);
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"powercfg failed ({p.ExitCode}): {err.Trim()}");
    }
}
