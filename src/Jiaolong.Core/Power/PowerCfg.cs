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
    internal const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";

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
    public static (int Ac, int Dc)? ReadIndexes(string settingGuid) =>
        ReadIndexes(GetActiveScheme(), settingGuid);

    private static (int Ac, int Dc)? ReadIndexes(string scheme, string settingGuid)
    {
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
        var scheme = Guid.Parse(GetActiveScheme());
        WriteIndexes(scheme, settingGuid, value, ac, dc);
        RefreshScheme(scheme);
    }

    private static void WriteIndexes(Guid scheme, string settingGuid, int value, bool ac = true, bool dc = true)
    {
        var sub = Guid.Parse(SubProcessor);
        var setting = Guid.Parse(settingGuid);
        string index = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (ac && PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value) != ERROR_SUCCESS)
            RunPowerCfg("/setacvalueindex", scheme.ToString(), SubProcessor, settingGuid, index);
        if (dc && PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value) != ERROR_SUCCESS)
            RunPowerCfg("/setdcvalueindex", scheme.ToString(), SubProcessor, settingGuid, index);
    }

    private static void RefreshScheme(Guid scheme)
    {
        CheckActiveScheme(scheme);
        ActivateScheme(scheme);
    }

    private static void CheckActiveScheme(Guid scheme)
    {
        if (Guid.Parse(GetActiveScheme()) != scheme)
            throw new InvalidOperationException("应用电源设置时方案发生切换，请重新应用");
    }

    private static void ActivateScheme(Guid scheme)
    {
        if (PowerSetActiveScheme(IntPtr.Zero, ref scheme) != ERROR_SUCCESS)
            RunPowerCfg("/setactive", scheme.ToString());
    }

    private static void ReloadGameModeScheme(Guid scheme)
    {
        // Reactivating the same GUID can leave Windows using a cached Game Mode profile.
        var temporary = Guid.NewGuid();
        RunPowerCfg("/duplicatescheme", scheme.ToString(), temporary.ToString());
        try
        {
            CheckActiveScheme(scheme);
            RunPowerCfg("/setactive", temporary.ToString());
            Thread.Sleep(700); // Allow Windows to activate the copied processor policies.
        }
        finally
        {
            if (Guid.Parse(GetActiveScheme()) == temporary)
                RunPowerCfg("/setactive", scheme.ToString());
            RunPowerCfg("/delete", temporary.ToString());
        }
        CheckActiveScheme(scheme);
    }

    /// <summary>Cap CPU clock. 0 = remove the cap.</summary>
    public static (int Ac, int Dc, bool GameModeChanged, string? GameModeError) SetMaxFrequency(int mhz)
    {
        if (mhz < 0) throw new ArgumentOutOfRangeException(nameof(mhz));
        var scheme = Guid.Parse(GetActiveScheme());
        WriteIndexes(scheme, MaxProcessorFrequency, mhz);

        bool gameModeChanged = false;
        string? gameModeError = null;
        try
        {
            if (mhz > 0)
            {
                if (GameModeFrequency.Apply(scheme, mhz))
                {
                    ReloadGameModeScheme(scheme);
                    gameModeChanged = true;
                }
            }
            else
            {
                gameModeChanged = GameModeFrequency.Restore(() => ReloadGameModeScheme(scheme));
            }
        }
        catch (Exception ex)
        {
            gameModeError = ex.Message;
        }

        if (!gameModeChanged) RefreshScheme(scheme);
        var (ac, dc) = ReadIndexes(scheme.ToString(), MaxProcessorFrequency) ?? (-1, -1);
        if (ac != mhz || dc != mhz)
            throw new InvalidOperationException($"普通电源方案限频回读不一致（交流 {ac} / 电池 {dc} MHz）");

        return (ac, dc, gameModeChanged, gameModeError);
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

    internal static void RunPowerCfg(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start powercfg");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"powercfg {args[0]} failed ({p.ExitCode}): {(error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult()).Trim()}");
    }
}
