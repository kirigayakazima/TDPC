using System.Globalization;
using Microsoft.Win32;

namespace Jiaolong.Core.Power;

internal static class GameModeFrequency
{
    private const string Profile = "d4140c81-ebba-4e60-8561-6918290359cd";
    private const string AcOverride = "OverrideACSettingIndex";
    private const string DcOverride = "OverrideDCSettingIndex";
    private const string BackupPath = @"Software\TDPC\GameModeFrequency";

    private static readonly string ProfilePath =
        $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{PowerCfg.SubProcessor}\{PowerCfg.MaxProcessorFrequency}\{Profile}";

    public static bool Apply(Guid scheme, int mhz)
    {
        using var profile = Registry.LocalMachine.OpenSubKey(ProfilePath);
        if (profile is null) return false;

        string schemeName = scheme.ToString();
        using var settings = profile.OpenSubKey(schemeName);
        int? ac = ReadOverride(settings, AcOverride);
        int? dc = ReadOverride(settings, DcOverride);
        if (ac == mhz && dc == mhz) return true;

        // Keep the original overrides across restarts and save them before either write.
        using var backups = Registry.CurrentUser.CreateSubKey(BackupPath);
        if (backups.GetValue(schemeName) is null)
            backups.SetValue(schemeName, FormattableString.Invariant($"{ac},{dc}"));

        string value = mhz.ToString(CultureInfo.InvariantCulture);
        if (ac != mhz)
            PowerCfg.RunPowerCfg("/setacprofileindex", schemeName, Profile, PowerCfg.MaxProcessorFrequency, value);
        if (dc != mhz)
            PowerCfg.RunPowerCfg("/setdcprofileindex", schemeName, Profile, PowerCfg.MaxProcessorFrequency, value);

        // powercfg /queryprofile can omit user overrides even when Windows is applying them.
        using var written = profile.OpenSubKey(schemeName);
        if (ReadOverride(written, AcOverride) != mhz || ReadOverride(written, DcOverride) != mhz)
            throw new InvalidOperationException("Windows 游戏模式限频回读不一致");
        return true;
    }

    public static bool Restore(Action reload)
    {
        using var backups = Registry.CurrentUser.OpenSubKey(BackupPath, writable: true);
        if (backups is null) return false;

        var restored = new List<string>();
        bool reloadRequired = false;
        foreach (string name in backups.GetValueNames())
        {
            if (!Guid.TryParseExact(name, "D", out var scheme)) continue;
            var values = (backups.GetValue(name) as string)?.Split(',');
            if (values is not { Length: 2 })
                throw new InvalidOperationException("无法读取原有游戏模式限频备份");

            int? ac = ParseOverride(values[0]);
            int? dc = ParseOverride(values[1]);
            using var settings = Registry.LocalMachine.OpenSubKey($@"{ProfilePath}\{scheme}", writable: true);
            if (settings is not null)
            {
                RestoreOverride(settings, AcOverride, ac);
                RestoreOverride(settings, DcOverride, dc);
                if (ReadOverride(settings, AcOverride) != ac || ReadOverride(settings, DcOverride) != dc)
                    throw new InvalidOperationException($"Windows 游戏模式原值恢复后回读不一致（{scheme}）");
                reloadRequired = true;
            }
            else if (ac is not null || dc is not null)
                throw new InvalidOperationException($"找不到有原值备份的 Windows 游戏模式配置（{scheme}）");

            // A failed first apply can leave a backup with two absent overrides and no subkey.
            restored.Add(name);
        }

        if (reloadRequired) reload();
        foreach (string name in restored)
            backups.DeleteValue(name, throwOnMissingValue: false);
        return reloadRequired;
    }

    private static int? ReadOverride(RegistryKey? settings, string name) =>
        settings?.GetValue(name) is int value ? value : null;

    private static int? ParseOverride(string value) =>
        value.Length == 0 ? null : int.Parse(value, CultureInfo.InvariantCulture);

    private static void RestoreOverride(RegistryKey settings, string name, int? value)
    {
        if (value is int index)
            settings.SetValue(name, index, RegistryValueKind.DWord);
        else
            settings.DeleteValue(name, throwOnMissingValue: false);
    }
}
