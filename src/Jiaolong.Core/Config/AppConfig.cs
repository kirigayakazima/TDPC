using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiaolong.Core.Config;

/// <summary>
/// Everything the user changed that we restore on the next launch.
/// Stored outside the install folder so an exe replacement does not wipe it.
/// </summary>
public sealed class AppConfig
{
    // ---- fan --------------------------------------------------------
    /// <summary>EC lower bound (0xC83C/0xC83D) in RPM. 0 = BIOS owns the fan.</summary>
    public int FanLowerRpm { get; set; }

    /// <summary>
    /// WMI 0x15 upper bound per performance mode (mode -> RPM). The firmware itself stores one
    /// value per mode, so we mirror that instead of forcing a single global value.
    /// </summary>
    public Dictionary<int, int> FanUpperRpmByMode { get; set; } = new();

    /// <summary>0 disables the valve.</summary>
    public int SafetyTempC { get; set; } = 92;

    // ---- temperature curve ------------------------------------------
    public bool CurveEnabled { get; set; }

    /// <summary>Six temperature breakpoints in ℃ (must ascend).</summary>
    public int[] CurveTemps { get; set; } = { 50, 60, 70, 80, 90, 100 };

    /// <summary>Six RPM targets, in 100-RPM steps within 1800..5800.</summary>
    public int[] CurveRpm { get; set; } = { 1984, 2189, 2311, 2513, 4311, 5703 };

    // ---- cpu --------------------------------------------------------
    public int MaxFrequencyMhz { get; set; } = 4200;
    public bool MaxFrequencyOn { get; set; } = true;
    public bool TurboDisabled { get; set; }

    /// <summary>Curve Optimiser offset, -50..0 (negative = undervolt). 0 = off.</summary>
    public int CurveOffset { get; set; } = -40;
    public bool CurveOffsetOn { get; set; }

    // ---- CPU power budget, WMI 0x17 sub 0x04 / 0x02 / 0x03.
    //      The firmware never echoes these back, so -1 means "do not touch at startup". ----
    public int TempWallC { get; set; } = -1;
    public int SplW { get; set; } = -1;
    public int SpptW { get; set; } = -1;

    // ---- lighting ------------------------------------------------------
    /// <summary>Keyboard backlight on/off; null = mirror whatever the firmware reports.</summary>
    public bool? KbdBacklight { get; set; }

    /// <summary>0 off / 1 rainbow-cyclic / 2 fixed / 3 custom. -1 = leave the firmware alone.</summary>
    public int KbdMode { get; set; } = -1;

    /// <summary>0..10 (0x80 = auto is not persisted). -1 = leave alone.</summary>
    public int KbdBrightness { get; set; } = -1;

    /// <summary>0xRRGGBB written to 0x11. -1 = leave alone.</summary>
    public int KbdColorRgb { get; set; } = -1;

    /// <summary>Software colour effect: 0 none / 1 rainbow cycle / 2 breathing.</summary>
    public int ColorLoop { get; set; }

    // ---- firmware mirrors -------------------------------------------
    /// <summary>-1 = leave whatever the machine is doing alone.</summary>
    public int PerformanceMode { get; set; } = -1;
    public bool? FnLock { get; set; }
    public bool? TouchLock { get; set; }
    public bool? DgpuDirect { get; set; }
    public bool? AmbientLight { get; set; }

    // ---- ui & system -----------------------------------------------
    public bool LightTheme { get; set; }
    public double RefreshSeconds { get; set; } = 1;
    public bool OsdOn { get; set; }
    public bool AutoStart { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TDPC", "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppConfig();
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
            cfg ??= new AppConfig();
            cfg.Clamp();
            return cfg;
        }
        catch
        {
            return new AppConfig();   // a corrupt file must never stop the app from starting
        }
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            cfg.Clamp();
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Options));
            File.Move(tmp, FilePath, overwrite: true);   // atomic enough: never a half-written file
        }
        catch
        {
            // persistence is a convenience, never fatal
        }
    }
}

public static class AppConfigExtensions
{
    private static bool IsAscending(int[] v)
    {
        for (int i = 1; i < v.Length; i++)
            if (v[i] <= v[i - 1]) return false;
        return true;
    }

    /// <summary>Keeps every value inside the range the hardware accepts.</summary>
    public static void Clamp(this AppConfig c)
    {
        if (c.FanLowerRpm is not 0 and (< 1800 or > 5800)) c.FanLowerRpm = 0;
        c.FanLowerRpm -= c.FanLowerRpm % 100;

        c.SafetyTempC = Math.Clamp(c.SafetyTempC, 0, 105);

        if (c.CurveTemps is not { Length: 6 }) c.CurveTemps = new[] { 50, 60, 70, 80, 90, 100 };
        if (c.CurveRpm is not { Length: 6 }) c.CurveRpm = new[] { 1984, 2189, 2311, 2513, 4311, 5703 };
        for (int i = 0; i < 6; i++)
        {
            c.CurveTemps[i] = Math.Clamp(c.CurveTemps[i], 30, 105);
            int r = Math.Clamp(c.CurveRpm[i], 1800, 5800);
            c.CurveRpm[i] = r - r % 100;
        }
        // a curve whose points do not ascend would interpolate to garbage -> disable it
        if (!IsAscending(c.CurveTemps)) c.CurveEnabled = false;
        c.MaxFrequencyMhz = Math.Clamp(c.MaxFrequencyMhz, 0, 9000);
        c.CurveOffset = Math.Clamp(c.CurveOffset, -50, 0);

        // -1 means "do not touch the firmware"; anything outside the sane range is reset
        if (c.TempWallC != -1) c.TempWallC = Math.Clamp(c.TempWallC, 70, 100);
        if (c.SplW != -1) c.SplW = Math.Clamp(c.SplW, 25, 100);
        if (c.SpptW != -1) c.SpptW = Math.Clamp(c.SpptW, 30, 130);
        if (c.RefreshSeconds < 1) c.RefreshSeconds = 1;
        if (c.PerformanceMode is < -1 or > 2) c.PerformanceMode = -1;

        if (c.KbdMode is < -1 or > 3) c.KbdMode = -1;
        // this panel only accepts 0..3; higher values are silently dropped by the firmware
        if (c.KbdBrightness is < -1 or > 3) c.KbdBrightness = -1;
        if (c.KbdColorRgb is < -1 or > 0xFFFFFF) c.KbdColorRgb = -1;
        if (c.ColorLoop is < 0 or > 2) c.ColorLoop = 0;

        if (c.FanUpperRpmByMode is null) c.FanUpperRpmByMode = new Dictionary<int, int>();
        foreach (var key in c.FanUpperRpmByMode.Keys.ToList())
        {
            int v = c.FanUpperRpmByMode[key];
            if (v is < 1800 or > 5800) c.FanUpperRpmByMode.Remove(key);
            else c.FanUpperRpmByMode[key] = v - v % 100;
        }
    }
}
