using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Jiaolong.Core.Config;
using Jiaolong.Core.Smu;

namespace Jiaolong.App;

public partial class MainWindow
{
    private static readonly TimeSpan TrialDuration = TimeSpan.FromMinutes(15);
    private readonly CpuTrialStore _cpuTrialStore = new(Path.Combine(
        Path.GetDirectoryName(ConfigStore.FilePath)!, "cpu-trial.json"));
    private CpuTrialRecord? _cpuTrial;
    private DispatcherTimer? _trialTimer;
    private Stopwatch? _trialClock;
    private EventLogWatcher? _wheaWatcher;
    private CancellationTokenSource? _cpuSelfCheck;
    private bool _cpuRecoveryBlocked;
    private bool _savedCpuProfileApplied;
    private bool _releaseCoAfterTrial;
    private string _selfCheckStatus = "";

    private CpuTuningProfile SavedCpuProfile => CpuTuningProfile.FromConfig(_cfg, SavedFrequencyMethodIndex == 0);

    private void RestoreCpuTrialAware()
    {
        try { _cpuTrial = _cpuTrialStore.Read(); }
        catch (Exception ex)
        {
            BlockCpuTrialRecovery("无法读取试跑恢复记录", ex);
            return;
        }

        if (_cpuTrial is not null)
        {
            RollBackCpuTrial("上次试跑异常中断", instability: true);
            return;
        }

        var profile = SavedCpuProfile;
        _savedCpuProfileApplied = ApplyFrequency(profile.UseSmu, profile.EffectiveLimit);
        if (profile.EffectiveCo != 0) _savedCpuProfileApplied &= SetTrialCo(profile.EffectiveCo);
        UpdateCpuTrialControls(_savedCpuProfileApplied
            ? "修改候选值后，点击「开始试跑」应用。"
            : "已保存配置的应用未确认，详情见日志。");
    }

    private void StartCpuTrial_Click(object sender, RoutedEventArgs e)
    {
        if (_cpuTrial is not null || _cpuRecoveryBlocked) return;
        bool useSmu = FreqMethod.SelectedIndex == 0;
        bool limitOn = FreqSwitch.IsChecked == true;
        bool coOn = CoSwitch.IsChecked == true;
        if (!int.TryParse(FreqBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mhz)
            || !int.TryParse(CoBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int co))
        {
            UpdateCpuTrialControls("请输入整数频率和 CO 值。");
            return;
        }
        var candidate = new CpuTuningProfile(useSmu, limitOn, mhz, coOn, co);
        if (!candidate.IsValid || (limitOn && useSmu
            && mhz is < SmuClient.MinBoostLimitMhz or > SmuClient.MaxBoostLimitMhz))
        {
            UpdateCpuTrialControls("频率或 CO 超出适用范围。SMU 3600–5150 MHz，CO -50～0。");
            return;
        }

        var baseline = SavedCpuProfile;
        if (!_savedCpuProfileApplied)
        {
            _savedCpuProfileApplied = ApplyCpuTrialProfile(baseline, baseline, restoring: true);
            if (!_savedCpuProfileApplied)
            {
                UpdateCpuTrialControls("已保存配置未能恢复，详情见日志。");
                return;
            }
        }
        var trial = new CpuTrialRecord(baseline, candidate);
        try { _cpuTrialStore.Begin(trial); }
        catch (Exception ex)
        {
            BlockCpuTrialRecovery("无法保存试跑前的配置", ex);
            return;
        }

        _cpuTrial = trial;
        _releaseCoAfterTrial = false;
        _selfCheckStatus = "";
        try
        {
            StartWheaWatch(trial);
            AddEvent(trial.UsesSavedProfile
                ? $"开始监测试跑，使用已保存配置 {DescribeCpuProfile(baseline)}"
                : $"开始 CPU 试跑，候选 {DescribeCpuProfile(candidate)}");
            if (!trial.UsesSavedProfile && !ApplyCpuTrialProfile(candidate, baseline))
            {
                RollBackCpuTrial("候选设置未能确认");
                return;
            }
            _trialClock = Stopwatch.StartNew();
            _trialTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _trialTimer.Tick += CpuTrialTick;
            _trialTimer.Start();
            CpuTrialTick(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AddEvent("无法开始 CPU 试跑: " + OneLine(ex.Message));
            RollBackCpuTrial("试跑准备失败");
        }
    }

    private bool ApplyCpuTrialProfile(CpuTuningProfile requested, CpuTuningProfile previous, bool restoring = false)
    {
        bool setCo = requested.EffectiveCo != previous.EffectiveCo
            || (restoring && requested.EffectiveCo != 0);
        bool setLimit = restoring || requested.EffectiveLimit != previous.EffectiveLimit
            || requested.UseSmu != previous.UseSmu;
        bool relaxCoFirst = requested.EffectiveCo > previous.EffectiveCo;

        if (setCo && relaxCoFirst && !SetTrialCo(requested.EffectiveCo)) return false;
        if (setLimit && !ApplyFrequency(requested.UseSmu, requested.EffectiveLimit, strict: true)) return false;
        return !setCo || relaxCoFirst || SetTrialCo(requested.EffectiveCo);
    }

    private bool SetTrialCo(int offset)
    {
        bool ok = PushCurveOptimizer(offset, out var message);
        AddEvent(message);
        return ok;
    }

    private void SaveCpuTrial_Click(object sender, RoutedEventArgs e)
    {
        if (_cpuTrial is not { UsesSavedProfile: false } trial || _trialClock is null || _cpuSelfCheck is not null) return;
        try
        {
            trial.Candidate.CopyTo(_cfg);
            ConfigStore.SaveConfirmed(_cfg);
            _cpuTrialStore.Finish();
            _lastSavedJson = JsonSerializer.Serialize(_cfg);
        }
        catch (Exception ex)
        {
            trial.Baseline.CopyTo(_cfg);
            AddEvent("保存 CPU 试跑配置失败: " + OneLine(ex.Message));
            RollBackCpuTrial("无法保存候选");
            return;
        }
        StopTrialMonitoring();
        _cpuTrial = null;
        _savedCpuProfileApplied = true;
        AddEvent($"已保留 CPU 试跑配置，{DescribeCpuProfile(trial.Candidate)}");
        UpdateCpuTrialControls("已保留这组设置，下次启动会应用。");
    }

    private void RollBackCpuTrial_Click(object sender, RoutedEventArgs e)
    {
        if (_cpuTrial is { } trial)
            RollBackCpuTrial(_cpuRecoveryBlocked ? "手动重试恢复" : trial.UsesSavedProfile ? "用户结束试跑" : "用户要求回退");
        else if (_cpuRecoveryBlocked)
        {
            _cpuRecoveryBlocked = false;
            RestoreCpuTrialAware();
            SetCpuDraft(SavedCpuProfile);
        }
    }

    private void RollBackCpuTrial(string reason, bool instability = false)
    {
        if (_cpuTrial is not { } trial) return;
        StopTrialMonitoring();

        _releaseCoAfterTrial |= trial.UsesSavedProfile && instability;
        var target = _releaseCoAfterTrial ? trial.Baseline with { CoEnabled = false } : trial.Baseline;
        bool restore = !trial.UsesSavedProfile || _releaseCoAfterTrial;
        try
        {
            if (restore)
            {
                AddEvent($"CPU 试跑回退，{reason}");
                // The recovery configuration wins even after an interruption while saving.
                target.CopyTo(_cfg);
                SetCpuDraft(target);
                if (!ApplyCpuTrialProfile(target, trial.Candidate, restoring: true))
                    throw new InvalidOperationException("CPU 恢复设置未能确认");
                ConfigStore.SaveConfirmed(_cfg);
                _lastSavedJson = JsonSerializer.Serialize(_cfg);
            }
            _cpuTrialStore.Finish();
            _cpuTrial = null;
            _cpuRecoveryBlocked = false;
            _savedCpuProfileApplied = true;
            AddEvent(restore
                ? $"CPU 试跑回退成功，{DescribeCpuProfile(target)}"
                : $"当前配置试跑结束，{reason}");
            UpdateCpuTrialControls(_releaseCoAfterTrial
                ? $"已结束试跑并关闭 CO。{reason}。"
                : restore ? $"已回退到已保存配置。{reason}。" : $"试跑已结束，沿用当前配置。{reason}。");
            _releaseCoAfterTrial = false;
        }
        catch (Exception ex)
        {
            BlockCpuTrialRecovery("回退未确认，已暂停 CPU 试跑", ex);
        }
    }

    private void BlockCpuTrialRecovery(string label, Exception error)
    {
        _cpuRecoveryBlocked = true;
        _savedCpuProfileApplied = false;
        AddEvent($"{label}: {OneLine(error.Message)}");
        UpdateCpuTrialControls(label + "，详情见日志。");
    }

    private void StartWheaWatch(CpuTrialRecord trial)
    {
        const string source = "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=18 or EventID=19 or EventID=20)]]";
        _wheaWatcher = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, source), null, readExistingEvents: false);
        _wheaWatcher.EventRecordWritten += (_, e) =>
        {
            using var record = e.EventRecord;
            int? number = record?.Id;
            string? error = e.EventException?.Message;
            if (number is null && error is null) return;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_cpuTrial, trial) || _cpuRecoveryBlocked) return;
                RollBackCpuTrial(number is { } id ? $"检测到 WHEA 硬件错误 {id}" : "WHEA 监测中断",
                    instability: number is not null);
                if (error is not null) AddEvent("WHEA 监测异常: " + OneLine(error));
            }, DispatcherPriority.Send);
        };
        _wheaWatcher.Enabled = true;
    }

    private void CpuTrialTick(object? sender, EventArgs e)
    {
        if (_trialClock is null) return;
        int temperatureLimit = Safety();
        if (temperatureLimit > 0 && _cpuTemp >= temperatureLimit)
        {
            RollBackCpuTrial($"CPU 温度达到保护阈值 {temperatureLimit} ℃");
            return;
        }
        TimeSpan remaining = TrialDuration - _trialClock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            RollBackCpuTrial("试跑到时未确认");
            return;
        }
        string profileStatus = _cpuTrial?.UsesSavedProfile == true ? "当前配置 · " : "";
        UpdateCpuTrialControls($"{profileStatus}剩余 {remaining:mm\\:ss} · 监测 WHEA{_selfCheckStatus}");
    }

    private void StopTrialMonitoring()
    {
        _trialTimer?.Stop();
        _trialTimer = null;
        _trialClock = null;
        _cpuSelfCheck?.Cancel();
        try { _wheaWatcher?.Dispose(); }
        catch (EventLogException ex) { AddEvent("停止 WHEA 监测失败: " + OneLine(ex.Message)); }
        _wheaWatcher = null;
    }

    private async void CpuSelfCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_cpuTrial is not { } trial || _trialClock is null || _cpuSelfCheck is not null) return;
        using var cts = new CancellationTokenSource();
        _cpuSelfCheck = cts;
        _selfCheckStatus = " · CPU 自检进行中";
        CpuTrialTick(null, EventArgs.Empty);
        try
        {
            bool passed = await Task.Run(() => RunCpuSelfCheck(cts.Token), cts.Token);
            if (ReferenceEquals(_cpuTrial, trial) && _trialClock is not null)
            {
                if (!passed) RollBackCpuTrial("CPU 自检计算结果不一致", instability: true);
                else
                {
                    _selfCheckStatus = " · 60 秒自检已通过";
                    AddEvent("CPU 60 秒自检通过");
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AddEvent("CPU 自检异常: " + OneLine(ex.Message));
            if (ReferenceEquals(_cpuTrial, trial) && _trialClock is not null)
                RollBackCpuTrial("CPU 自检异常");
        }
        finally
        {
            _cpuSelfCheck = null;
            if (_trialClock is not null) CpuTrialTick(null, EventArgs.Empty);
            else BtnStartCpuTrial.IsEnabled = !_cpuRecoveryBlocked;
        }
    }

    private static bool RunCpuSelfCheck(CancellationToken token)
    {
        var input = new byte[16_384];
        var expected = Convert.FromHexString("4FE7B59AF6DE3B665B67788CC2F99892AB827EFAE3A467342B3BB4E3BC8E5BFE");
        var stopwatch = Stopwatch.StartNew();
        bool passed = true;
        Parallel.For(0, Environment.ProcessorCount, new ParallelOptions { CancellationToken = token }, (_, state) =>
        {
            Span<byte> digest = stackalloc byte[32];
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(60) && !state.IsStopped)
            {
                token.ThrowIfCancellationRequested();
                SHA256.HashData(input, digest);
                if (digest.SequenceEqual(expected)) continue;
                Volatile.Write(ref passed, false);
                state.Stop();
            }
        });
        return passed;
    }

    private void SetCpuDraft(CpuTuningProfile profile)
    {
        FreqMethod.SelectedIndex = profile.UseSmu && _smuFrequencySupported ? 0 : 1;
        FreqBox.Text = profile.LimitMhz.ToString(CultureInfo.InvariantCulture);
        FreqSwitch.IsChecked = profile.LimitEnabled;
        CoBox.Text = profile.CoOffset.ToString(CultureInfo.InvariantCulture);
        CoSwitch.IsChecked = profile.CoEnabled;
    }

    private void UpdateCpuTrialControls(string status)
    {
        bool running = _trialClock is not null;
        bool usingSavedProfile = _cpuTrial?.UsesSavedProfile == true;
        CpuDraftPanel.IsEnabled = !running && !_cpuRecoveryBlocked;
        BtnStartCpuTrial.IsEnabled = !running && !_cpuRecoveryBlocked && _cpuSelfCheck is null;
        BtnCpuSelfCheck.IsEnabled = running && _cpuSelfCheck is null;
        BtnSaveCpuTrial.IsEnabled = running && !usingSavedProfile && _cpuSelfCheck is null;
        BtnRollbackCpuTrial.IsEnabled = running || _cpuRecoveryBlocked;
        BtnRollbackCpuTrial.Content = _cpuRecoveryBlocked ? "重试恢复" : usingSavedProfile ? "结束试跑" : "回退";
        CpuSavedText.Text = DescribeCpuProfile(SavedCpuProfile);
        CpuTrialState.Text = _cpuRecoveryBlocked ? "等待恢复" : running ? "试跑中" : "待命";
        CpuTrialState.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _cpuRecoveryBlocked ? "Danger" : running ? "Warn" : "NeonEmerald");
        CpuTrialNote.Text = status;
    }

    private static string DescribeCpuProfile(CpuTuningProfile profile)
    {
        string frequency = profile.LimitEnabled
            ? $"{(profile.UseSmu ? "SMU" : "Windows")} {profile.LimitMhz} MHz"
            : "限频关闭";
        return $"{frequency}  ·  {(profile.CoEnabled ? $"CO {profile.CoOffset}" : "CO 关闭")}";
    }
}
