using System.Text.Json;

namespace Jiaolong.Core.Config;

public sealed record CpuTuningProfile(
    bool UseSmu, bool LimitEnabled, int LimitMhz, bool CoEnabled, int CoOffset)
{
    public int EffectiveLimit => LimitEnabled ? LimitMhz : 0;
    public int EffectiveCo => CoEnabled ? CoOffset : 0;

    public static CpuTuningProfile FromConfig(AppConfig config, bool useSmu) =>
        new(useSmu, config.MaxFrequencyOn, config.MaxFrequencyMhz, config.CurveOffsetOn, config.CurveOffset);

    public void CopyTo(AppConfig config)
    {
        config.UseSmuFrequencyLimit = UseSmu;
        config.MaxFrequencyOn = LimitEnabled;
        config.MaxFrequencyMhz = LimitMhz;
        config.CurveOffsetOn = CoEnabled;
        config.CurveOffset = CoOffset;
    }

    public bool IsValid => LimitMhz is > 0 and <= 9000 && CoOffset is >= -50 and <= 0;
}

public sealed record CpuTrialRecord(CpuTuningProfile Baseline, CpuTuningProfile Candidate)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesSavedProfile => Baseline == Candidate;
}

/// <summary>Write the recovery point before applying any candidate CPU setting.</summary>
public sealed class CpuTrialStore(string path)
{
    public CpuTrialRecord? Read()
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var record = JsonSerializer.Deserialize<CpuTrialRecord>(json);
        if (record?.Baseline is not { IsValid: true } || record.Candidate is not { IsValid: true })
            throw new InvalidDataException("CPU 试跑恢复记录无效");
        return record;
    }

    public void Begin(CpuTrialRecord record)
    {
        if (Read() is not null) throw new InvalidOperationException("已有未完成的 CPU 试跑");
        if (!record.Baseline.IsValid || !record.Candidate.IsValid)
            throw new ArgumentException("CPU 试跑参数无效", nameof(record));
        ConfigStore.WriteAtomic(path, JsonSerializer.Serialize(record));
    }

    public void Finish() => File.Delete(path);
}
