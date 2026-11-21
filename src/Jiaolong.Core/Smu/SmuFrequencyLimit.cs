using System.Globalization;
using Microsoft.Win32;

namespace Jiaolong.Core.Smu;

/// <summary>Owns the firmware limit only while it matches a value that TDPC wrote.</summary>
public static class SmuFrequencyLimit
{
    private const string StateKey = @"Software\TDPC\SmuFrequency";
    private const string StateName = "Ryzen7745HX_V35";

    public static void Apply(SmuClient smu, int target)
    {
        EnsureSupported();
        if (target is < SmuClient.MinBoostLimitMhz or > SmuClient.MaxBoostLimitMhz)
            throw new ArgumentOutOfRangeException(nameof(target), "SMU 上限需在 3600–5150 MHz 之间");

        int current = smu.ReadBoostLimit();
        if (current == target)
        {
            if (smu.ReadBoostLimit() != target)
                throw new InvalidOperationException("SMU 睿频上限两次回读不一致");
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(StateKey);
        string? originalState = key.GetValue(StateName) as string;
        var previous = Parse(originalState);
        int baseline = previous is { } saved && saved.Owns(current) ? saved.Baseline : current;

        // Journal before sending the command so a crash between the write and its
        // verification still leaves enough information to restore the previous limit.
        key.SetValue(StateName, FormattableString.Invariant($"{baseline},{current},{target}"));
        key.Flush();
        try
        {
            smu.WriteBoostLimitVerified(target);
        }
        catch (Exception writeError)
        {
            try
            {
                if (smu.ReadBoostLimit() != current)
                    smu.WriteBoostLimitVerified(current);
                if (originalState is null)
                    key.DeleteValue(StateName, throwOnMissingValue: false);
                else
                    key.SetValue(StateName, originalState);
            }
            catch (Exception restoreError)
            {
                throw new InvalidOperationException(
                    $"SMU 写入失败，恢复 {current} MHz 也未确认。{writeError.Message}。{restoreError.Message}",
                    restoreError);
            }
            throw;
        }
    }

    public static bool HasSavedLimit()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StateKey);
        return key?.GetValue(StateName) is not null;
    }

    public static (int Mhz, bool Restored)? Release(SmuClient smu)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StateKey, writable: true);
        var saved = Parse(key?.GetValue(StateName) as string);
        if (key is null || saved is not { } state) return null;

        EnsureSupported();
        int current = smu.ReadBoostLimit();
        if (!state.Owns(current))
        {
            // Firmware or another tool has already replaced our value.
            key.DeleteValue(StateName, throwOnMissingValue: false);
            return (current, false);
        }

        if (current != state.Baseline)
            smu.WriteBoostLimitVerified(state.Baseline);
        key.DeleteValue(StateName, throwOnMissingValue: false);
        return (state.Baseline, true);
    }

    private static void EnsureSupported()
    {
        if (!SmuClient.SupportsBoostLimit())
            throw new InvalidOperationException("此 CPU / BIOS 尚未验证 SMU 睿频上限");
    }

    private static SavedLimit? Parse(string? state)
    {
        if (state is null) return null;
        string[] parts = state.Split(',');
        if (parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int baseline)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int previous)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int target)
            && baseline is > 0 and <= 9000
            && previous is > 0 and <= 9000
            && target is >= SmuClient.MinBoostLimitMhz and <= SmuClient.MaxBoostLimitMhz)
            return new SavedLimit(baseline, previous, target);

        throw new InvalidOperationException("无法读取 TDPC 保存的 SMU 原始上限");
    }

    private readonly record struct SavedLimit(int Baseline, int Previous, int Target)
    {
        public bool Owns(int current) => current == Target || current == Previous;
    }
}
