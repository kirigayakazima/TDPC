using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Jiaolong.App.Platform;
using Jiaolong.App.Osd;
using Jiaolong.App.Sensors;
using Jiaolong.Core.Config;
using Jiaolong.Core.Ec;
using Jiaolong.Core.Fan;
using Jiaolong.Core.Logging;
using Jiaolong.Core.Power;
using Jiaolong.Core.Smu;
using Jiaolong.Core.Wmi;

namespace Jiaolong.App;

public partial class MainWindow : Window
{
    private EcAccess? _ec;
    private JiaolongWmi? _wmi;
    private FanController? _fan;
    private SensorHub? _sensors;
    private TrayService? _tray;
    private bool _forceClose;
    private UpdateInfo? _latestUpdate;

    private DispatcherTimer? _applyTimer;   // slider debounce
    private OsdOverlay? _osd;

    private bool _syncing;      // loading state from hardware -> suppress handlers
    private bool _sliderReady;  // allow slider -> Lock auto-apply
    private bool _hwReady;

    // persisted state
    private AppConfig _cfg = new();
    private string _lastSavedJson = "";
    private int _currentMode = -1;      // 0 game / 1 burst / 2 quiet
    private int _fanUpperCurrent;       // WMI 0x15 x 100 for the active mode
    private SmuClient? _smu;            // lazy: only opened when a CO value is actually applied

    private static readonly Brush ModeOn = new SolidColorBrush(Color.FromRgb(0x2B, 0x3E, 0x5C));
    private static readonly Brush ModeOff = Brushes.Transparent;

    public MainWindow() => InitializeComponent();

    // ================================================================ lifecycle
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Dwm.EnableRoundedCorners(hwnd);
        Dwm.EnableDarkTitleBar(hwnd);
        Dwm.EnableNativeWindowAnimations(hwnd);

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        try
        {
            _tray = new TrayService(this, onActivate: ShowDashboard, onContextMenu: ShowTrayContextMenu);
        }
        catch (Exception ex)
        {
            AddEvent("托盘初始化异常: " + OneLine(ex.Message));
        }

        try
        {
            _wmi = new JiaolongWmi();
            AddEvent("WMI 通道就绪");
        }
        catch (Exception ex)
        {
            AddEvent("WMI 失败: " + OneLine(ex.Message));
            _wmi = null;
        }

        try
        {
            _ec = new EcAccess();
            _fan = new FanController(_ec, _wmi);
            _fan.Event += ev => Dispatcher.BeginInvoke(() => AddEvent(ev.Message));
            _fan.Startup();
            _fan.StartWatchdog();
            AddEvent("EC 通道就绪，看门狗已启动");
        }
        catch (Exception ex)
        {
            AddEvent("EC 失败: " + OneLine(ex.Message));
            BtnFanLock.IsEnabled = false;
            BtnFanRelease.IsEnabled = false;
            OwnerPill.Text = "EC 不可用";
            OwnerPill.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x3F, 0x5E));
        }

        _sensors = new SensorHub(_wmi);
        _hwReady = _ec is not null;

        RestoreFromConfig();
        _sliderReady = true;

        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            ApplySlider();
        };

        StartTimer();

        // Silent launch support: if started with -silent or --minimized (e.g. from AutoStart Task Scheduler)
        bool startSilent = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("-silent", StringComparison.OrdinalIgnoreCase) || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (startSilent)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => HideToTray(silent: true));
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClose && _cfg.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _tray?.Dispose();
        _tray = null;
        StopColorLoop(silent: true);
        _fastTimer?.Stop();
        try { _loopCts?.Cancel(); } catch { /* already cancelled */ }
        _applyTimer?.Stop();
        try { _osd?.Close(); } catch { /* ignore */ }
        try { _fan?.Dispose(); } catch { /* last resort already inside Dispose */ }
        try { _wmi?.Dispose(); } catch { /* ignore */ }
        try { _ec?.Dispose(); } catch { /* ignore */ }
    }

    // ================================================================ title bar
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // don't start a window drag when a caption button is pressed
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;

        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg.MinimizeToTrayOnClose)
        {
            HideToTray();
        }
        else
        {
            _forceClose = true;
            Close();
        }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ERASEBKGND = 0x0014;
        if (msg == WM_ERASEBKGND)
        {
            handled = true;
            return (IntPtr)1; // Suppress GDI background erasure to eliminate resize flicker
        }
        return IntPtr.Zero;
    }

    private static T? FindParent<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ================================================================ state load / persist
    /// <summary>
    /// Restores everything from %AppData%\TDPC\config.json. Config wins over whatever the
    /// machine happens to be doing, so what you set last session is what you get back.
    /// </summary>
    private void RestoreFromConfig()
    {
        _cfg = ConfigStore.Load();
        _syncing = true;
        try
        {
            // ---- UI state -------------------------------------------------
            FreqBox.Text = _cfg.MaxFrequencyMhz.ToString();
            FreqSwitch.IsChecked = _cfg.MaxFrequencyOn;
            TurboSwitch.IsChecked = _cfg.TurboDisabled;
            SafetyBox.Text = _cfg.SafetyTempC.ToString(CultureInfo.InvariantCulture);
            IntervalBox.Text = _cfg.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
            OsdSwitch.IsChecked = _cfg.OsdOn;
            CoBox.Text = _cfg.CurveOffset.ToString(CultureInfo.InvariantCulture);
            CoSwitch.IsChecked = _cfg.CurveOffsetOn;
            AutoStartSwitch.IsChecked = AutoStartManager.IsScheduled();
            TrayCloseSwitch.IsChecked = _cfg.MinimizeToTrayOnClose;

            if (_cfg.LightTheme != _isLight)
            {
                _isLight = _cfg.LightTheme;
                ApplyTheme(_isLight);
                ThemeGlyph.Text = _isLight ? "" : "";
            }

            _intervalSeconds = _cfg.RefreshSeconds;
            if (_fan is not null) _fan.SafetyTempC = _cfg.SafetyTempC;

            // ---- power plan ----------------------------------------------
            try
            {
                PowerCfg.SetMaxFrequency(_cfg.MaxFrequencyOn ? _cfg.MaxFrequencyMhz : 0);
                PowerCfg.SetTurboBoost(!_cfg.TurboDisabled);
            }
            catch (Exception ex) { AddEvent("恢复电源设置失败: " + OneLine(ex.Message)); }

            if (_wmi is null) { AddEvent("WMI 不可用，跳过固件项恢复"); return; }

            // ---- firmware ---------------------------------------------------
            try
            {
                int mode = _wmi.Get(WmiCommand.SystemPerMode)[4];
                HighlightMode(mode);

                if (_cfg.PerformanceMode >= 0 && _cfg.PerformanceMode != mode)
                {
                    _wmi.Set(WmiCommand.SystemPerMode, (byte)_cfg.PerformanceMode);
                    HighlightMode(_cfg.PerformanceMode);
                    AddEvent($"恢复性能模式 -> {ModeName(_cfg.PerformanceMode)}");
                }

                ApplySwitch(WmiCommand.TPLock, TpSwitch, _cfg.TouchLock, "触摸板锁定");
                ApplySwitch(WmiCommand.GPUMode, DgpuSwitch, _cfg.DgpuDirect, "独显直连");
                ApplySwitch(WmiCommand.Ambientlight, AmbientSwitch, _cfg.AmbientLight, "氛围灯");
                RestoreLighting();

                // upper bound for the active mode
                _fanUpperCurrent = ReadUpperBound();
                if (_cfg.FanUpperRpmByMode.TryGetValue(_currentMode, out var up) && up > 0)
                {
                    _fanUpperCurrent = up;
                    _wmi.Set(WmiCommand.MaxFanSpeed, (byte)(up / 100));
                    AddEvent($"恢复转速上限 {_currentMode switch { 1 => "狂飙", 0 => "游戏", _ => "办公" }} = {up} RPM");
                }
                FanUpperBox.Text = _fanUpperCurrent.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { AddEvent("恢复固件设置失败: " + OneLine(ex.Message)); }

            // ---- fan lower bound last (guarded by the safety valve) ----------
            if (_cfg.FanLowerRpm >= 1800 && _fan is not null)
            {
                try
                {
                    _fan.Lock(_cfg.FanLowerRpm);
                    AddEvent($"恢复风扇锁定 {_cfg.FanLowerRpm} RPM");
                }
                catch (Exception ex) { AddEvent("恢复风扇锁定被拒绝: " + OneLine(ex.Message)); }
            }
            if (_cfg.CurveOffsetOn && _cfg.CurveOffset != 0 && PushCurveOptimizer(_cfg.CurveOffset, out var coMsg))
                AddEvent("恢复 " + coMsg);

            ApplyCurveFromConfig(true);
            RestorePowerBudget();

            if (_cfg.ColorLoop == 1) { SetChecked(RainbowSwitch, true); StartColorLoop(1); }
            else if (_cfg.ColorLoop == 2) { SetChecked(BreathSwitch, true); StartColorLoop(2); }

            UpdateOwnerPill();
        }
        finally { _syncing = false; }

        _lastSavedJson = JsonSerializer.Serialize(_cfg);
        AddEvent($"已载入配置 {ConfigStore.FilePath}");

        // Version & background update check
        VersionText.Text = UpdateService.CurrentVersionString;
        _ = Task.Run(() => CheckUpdateInBackgroundAsync());
    }

    /// <summary>Mirrors the live UI into <see cref="_cfg"/> and writes it when anything changed.</summary>
    private void PersistIfChanged()
    {
        if (_syncing || _hwReady is false && _wmi is null) return;
        SyncCfgFromUi();
        string json = JsonSerializer.Serialize(_cfg);
        if (json == _lastSavedJson) return;
        _lastSavedJson = json;
        ConfigStore.Save(_cfg);
    }

    private void SyncCfgFromUi()
    {
        _cfg.MaxFrequencyMhz = int.TryParse(FreqBox.Text, out var f) ? f : _cfg.MaxFrequencyMhz;
        _cfg.MaxFrequencyOn = FreqSwitch.IsChecked == true;
        _cfg.TurboDisabled = TurboSwitch.IsChecked == true;
        _cfg.SafetyTempC = Safety();
        _cfg.OsdOn = OsdSwitch.IsChecked == true;
        _cfg.LightTheme = _isLight;
        _cfg.RefreshSeconds = _intervalSeconds;
        _cfg.CurveOffsetOn = CoSwitch.IsChecked == true;
        if (int.TryParse(CoBox.Text, out var co) && co is <= 0 and >= -50) _cfg.CurveOffset = co;
        _cfg.PerformanceMode = _currentMode;
        _cfg.TouchLock = TpSwitch.IsChecked;
        _cfg.DgpuDirect = DgpuSwitch.IsChecked;
        _cfg.AmbientLight = AmbientSwitch.IsChecked;
        _cfg.KbdBacklight = KbdSwitch.IsChecked;
        _cfg.AutoStart = AutoStartSwitch.IsChecked == true;
        _cfg.MinimizeToTrayOnClose = TrayCloseSwitch.IsChecked == true;
        _cfg.FanLowerRpm = _fan is { Owner: FanOwner.Manual } ? _fan.LockedRpm : 0;
        if (_currentMode >= 0 && _fanUpperCurrent >= 1800)
            _cfg.FanUpperRpmByMode[_currentMode] = _fanUpperCurrent;
    }

    private int ReadUpperBound()
    {
        try { return (_wmi?.Get(WmiCommand.MaxFanSpeed)[4] ?? 0) * 100; }
        catch { return _fanUpperCurrent; }
    }

    private void ApplySwitch(WmiCommand cmd, ToggleButton box, bool? want, string label)
    {
        if (_wmi is null) return;
        if (want is null)
        {
            box.IsChecked = _wmi.Get(cmd)[4] != 0;   // not in config yet -> mirror hardware
            return;
        }
        bool now = box.IsChecked == true;
        if (now != want.Value)
        {
            box.IsChecked = want.Value;
            _wmi.Set(cmd, (byte)(want.Value ? 1 : 0));
            AddEvent($"恢复 {label} -> {(want.Value ? "开" : "关")}");
        }
    }

    private void FanUpper_Committed(object sender, RoutedEventArgs e) => ApplyUpperBound();

    private void FanUpper_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyUpperBound();
    }

    private void ApplyUpperBound()
    {
        if (_syncing || _wmi is null) return;
        int v;
        try { v = int.Parse(FanUpperBox.Text); }
        catch { FanUpperBox.Text = _fanUpperCurrent.ToString(CultureInfo.InvariantCulture); return; }

        if (v == 0)
        {
            _fanUpperCurrent = 0;
            _cfg.FanUpperRpmByMode.Remove(_currentMode);
            AddEvent($"转速上限已跟随模式默认（当前 {ReadUpperBound()} RPM）");
            FanUpperBox.Text = "0";
            return;
        }

        if (v < 1800 || v > 5800)
        {
            FanUpperBox.Text = _fanUpperCurrent.ToString(CultureInfo.InvariantCulture);
            AddEvent("转速上限必须在 1800–5800 之间");
            return;
        }

        v -= v % 100;
        try
        {
            _wmi.Set(WmiCommand.MaxFanSpeed, (byte)(v / 100));
            _fanUpperCurrent = v;
            if (_currentMode >= 0) _cfg.FanUpperRpmByMode[_currentMode] = v;
            FanUpperBox.Text = v.ToString(CultureInfo.InvariantCulture);
            AddEvent($"转速上限 = {v} RPM（回读 {ReadUpperBound()}）");
        }
        catch (Exception ex) { AddEvent("设置转速上限失败: " + OneLine(ex.Message)); }
    }

    // ================================================================ tick
    // Data collection can block for 100-300 ms (WMI bridge round-trip, nvidia-smi spawn),
    // so it runs on a background task. The UI thread only gets a cheap 150 ms timer for
    // lock-key polling (keeps the Caps/Num toast snappy) and paints cached values.
    private CancellationTokenSource? _loopCts;
    private DispatcherTimer? _fastTimer;
    private double _intervalSeconds = 1;
    private int _rpmCpu, _rpmGpu, _cpuTemp;

    private void StartTimer()
    {
        _intervalSeconds = IntervalFromUiSeconds();
        _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => CollectLoopAsync(_loopCts.Token));

        _fastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fastTimer.Tick += (_, _) => PollLockKeys();
        _fastTimer.Start();
    }

    private double IntervalFromUiSeconds()
    {
        double s = double.TryParse(IntervalBox?.Text ?? "1", NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out var v) ? v : 1;
        return s < 1 ? 1 : s;
    }

    private async Task CollectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                CollectOnce();
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => AddEvent("轮询出错: " + OneLine(ex.Message)));
            }

            try { await Dispatcher.InvokeAsync(ApplyUi); } catch { /* window may be closing */ }

            try { await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Background thread: every call that can block lives here.</summary>
    private void CollectOnce()
    {
        if (_wmi is not null)
        {
            var (cpu, gpu) = _wmi.ReadFanRpm();
            _rpmCpu = cpu;
            _rpmGpu = gpu;
            _cpuTemp = _wmi.ReadCpuTemp();
        }
        _sensors?.Poll();
        _sensors?.PushHistory(_cpuTemp);
    }

    /// <summary>UI thread: only writes cached values, never blocks.</summary>
    private void ApplyUi()
    {
        FanCpu.Text = _rpmCpu.ToString();
        FanGpu.Text = _rpmGpu.ToString();
        TempText.Text = _cpuTemp.ToString();
        UpdateSensorText();
        UpdateOwnerPill();
        PersistIfChanged();

        _tray?.SetTooltip($"TDPC · 蛟龙控制台\nCPU: {_cpuTemp}℃ | 风扇: {_rpmCpu} RPM\n模式: {ModeName(_currentMode)}");
    }

    private bool? _caps;
    private bool? _num;

    /// <summary>
    /// Toast on Caps/Num lock changes.
    /// </summary>
    private void PollLockKeys()
    {
        bool caps = Console.CapsLock;
        bool num = Console.NumberLock;
        if (_caps is not null && caps != _caps.Value)
        {
            string m = caps ? "Caps Lock 开" : "Caps Lock 关";
            Toast(m);
            AddEvent(m);
        }
        if (_num is not null && num != _num.Value)
        {
            string m = num ? "Num Lock 开" : "Num Lock 关";
            Toast(m);
            AddEvent(m);
        }
        _caps = caps;
        _num = num;
    }

    private void Toast(string text)
    {
        if (OsdSwitch.IsChecked != true) return;
        _osd ??= new OsdOverlay();
        _osd.ShowToast(text);
    }

    private void UpdateSensorText()
    {
        if (_sensors is null) return;
        var s = _sensors;
        CpuFreq.Text = s.CpuFreqMhz > 0 ? $"{s.CpuFreqMhz / 1000.0:0.00} GHz" : "--";
        CpuLoad.Text = s.CpuLoad > 0 ? $"{s.CpuLoad:0} %" : "--";
        CpuCores.Text = s.CpuCores > 0 ? s.CpuCores.ToString() : "--";
        CpuMax.Text = s.CpuMaxMhz > 0 ? $"{s.CpuMaxMhz / 1000.0:0.00} GHz" : "--";

        if (s.GpuAvailable)
        {
            GpuTemp.Text = $"{s.GpuTempC:0} ℃";
            GpuClock.Text = $"{s.GpuClockMhz / 1000.0:0.00} GHz";
            GpuLoad.Text = $"{s.GpuLoad:0} %";
            GpuPower.Text = $"{s.GpuPowerW:0} W";
        }
        else
        {
            GpuTemp.Text = GpuClock.Text = GpuLoad.Text = GpuPower.Text = "--";
        }

        MemLoad.Text = s.MemLoadPct > 0 ? $"{s.MemLoadPct:0} %" : "--";
        MemAvail.Text = s.MemAvailGb > 0 ? $"{s.MemAvailGb:0.0} GB" : "--";
        DiskAvail.Text = s.DiskAvailGb > 0 ? $"{s.DiskAvailGb:0} GB" : "--";
        DiskLoad.Text = s.DiskTotalGb > 0
            ? $"{100 - s.DiskAvailGb / s.DiskTotalGb * 100:0} %"
            : "--";

        CpuPower.Text = s.CpuPowerW > 0 ? $"{s.CpuPowerW:0.0} W" : "--";
        CoSetting.Text = CoSwitch.IsChecked == true
            ? $"{(int.TryParse(CoBox.Text, out var c) ? c : 0)} mV"
            : "关闭";
        CoAck.Text = string.IsNullOrEmpty(_coAck) ? "--" : _coAck;
        TurboState.Text = TurboSwitch.IsChecked == true ? "已关闭" : "开启";
    }

    /// <summary>Last SMU acknowledgement string for the CO row (set by PushCurveOptimizer).</summary>
    private string _coAck = "";

    private void UpdateOwnerPill()
    {
        if (_fan is null) return;
        bool curve = _fan.CurveEnabled;
        if (_fan.Owner == FanOwner.Manual)
        {
            OwnerPill.Text = $"EC 锁定 {_fan.LockedRpm} RPM";
            OwnerPill.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
            FanNote.Text = curve
                ? $"温度曲线生效：{_fan.LastTempC}℃ → {_fan.LockedRpm} RPM（看门狗每 {_fan.PollInterval.TotalSeconds:0}s 持续守护）"
                : $"EC 硬件锁定生效（锁定 {_fan.LockedRpm} RPM），看门狗每 {_fan.PollInterval.TotalSeconds:0}s 持续守护";
        }
        else
        {
            OwnerPill.Text = "BIOS 自动";
            OwnerPill.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
            FanNote.Text = curve
                ? $"温度曲线已挂起：CPU {_fan.LastTempC}℃，等降温到 {(_fan.SafetyTempC - 5)}℃ 以下自动恢复"
                : "风扇控制权已交还主板 BIOS 智能控温曲线";
        }
    }

    // ================================================================ fan card
    /// <summary>0 disables the safety valve; otherwise 50..105 ℃. Default 92.</summary>
    private int Safety() =>
        int.TryParse(SafetyBox.Text, out var v) && (v == 0 || v is >= 50 and <= 105) ? v : 92;

    private void FanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FanTarget is null) return;
        FanTarget.Text = $"{(int)FanSlider.Value} RPM";
        if (!_sliderReady || _fan is null) return;

        // debounce: apply 500ms after the user stops dragging
        _applyTimer?.Stop();
        _applyTimer?.Start();
    }

    private void ApplySlider()
    {
        if (!_sliderReady || _fan is null) return;
        try
        {
            _fan.SafetyTempC = Safety();
            int want = (int)FanSlider.Value;
            if (_fan.Owner == FanOwner.Manual && _fan.LockedRpm == want) return;
            _fan.Lock(want);
            UpdateOwnerPill();
        }
        catch (Exception ex) { AddEvent("锁定失败: " + OneLine(ex.Message)); }
    }

    private void BtnFanLock_Click(object sender, RoutedEventArgs e) => ApplySlider();

    private void BtnFanRelease_Click(object sender, RoutedEventArgs e) => ReleaseFan();

    private void ReleaseFan()
    {
        if (_fan is null) return;
        try
        {
            _fan.Release();
            UpdateOwnerPill();
            AddEvent("已交还 BIOS 托管风扇");
            Toast("BIOS 托管风扇");
        }
        catch (Exception ex) { AddEvent("释放失败: " + OneLine(ex.Message)); }
    }

    // ================================================================ cpu card
    private void FreqBox_Committed(object sender, RoutedEventArgs e)
    {
        if (_syncing || FreqSwitch.IsChecked != true) return;
        ApplyFrequency();
    }

    private void FreqBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ((FrameworkElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void FreqSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        ApplyFrequency();
    }

    private void ApplyFrequency()
    {
        try
        {
            int mhz = FreqSwitch.IsChecked == true
                ? (int.TryParse(FreqBox.Text, out var v) ? v : 4200)
                : 0;
            PowerCfg.SetMaxFrequency(mhz);
            int back = PowerCfg.GetMaxFrequency() ?? -1;
            AddEvent(mhz > 0
                ? $"CPU 限制频率 = {mhz} MHz（回读 {back}）"
                : $"CPU 限制频率已解除（回读 {back}）");
        }
        catch (Exception ex) { AddEvent("锁频失败: " + OneLine(ex.Message)); }
    }

    private void TurboSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        try
        {
            bool disable = TurboSwitch.IsChecked == true;
            PowerCfg.SetTurboBoost(!disable);
            int mode = PowerCfg.GetTurboBoostMode() ?? -1;
            AddEvent(disable ? $"睿频已关闭（回读 mode={mode}）" : $"睿频已开启（回读 mode={mode}）");
        }
        catch (Exception ex) { AddEvent("睿频设置失败: " + OneLine(ex.Message)); }
    }

    // ================================================================ refresh interval
    private void Interval_Committed(object sender, RoutedEventArgs e) => ApplyInterval();

    private void Interval_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyInterval();
    }

    private void ApplyInterval()
    {
        double s = double.TryParse(IntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1;
        if (s < 1) s = 1;
        IntervalBox.Text = s.ToString(CultureInfo.InvariantCulture);
        _intervalSeconds = s;
        AddEvent($"刷新间隔 = {s} 秒");
    }

    // ================================================================ modes
    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || _wmi is null) return;
        var b = (Button)sender;
        if (!byte.TryParse((string)b.Tag, out var mode)) return;
        SetMode(mode);
    }

    private void SetMode(byte mode)
    {
        if (_syncing || _wmi is null) return;
        try
        {
            _wmi.Set(WmiCommand.SystemPerMode, mode);
            HighlightMode(mode);
            AddEvent($"性能模式 -> {ModeName(mode)}");
            Toast(ModeName(mode) + "模式");
            PersistIfChanged();
        }
        catch (Exception ex) { AddEvent("切换模式失败: " + OneLine(ex.Message)); }
    }

    private void HighlightMode(int mode)
    {
        _currentMode = mode;

        // active segment = the lit gradient (frozen clone so one shared brush serves all three),
        // inactive = transparent, text flips to white only on the lit one
        Brush lit = Brushes.Transparent;
        if (TryFindResource("PrimaryBtnBg") is Brush b)
        {
            var c = b.Clone();
            c.Freeze();
            lit = c;
        }
        else
        {
            var fallback = new SolidColorBrush(Color.FromRgb(0x2B, 0x3E, 0x5C));
            fallback.Freeze();
            lit = fallback;
        }

        Brush dim = Brushes.Transparent;
        Brush text = TryFindResource("RowText") as Brush ?? Brushes.Gainsboro;

        ModeBurst.Background = mode == 1 ? lit : dim;
        ModeGame.Background = mode == 0 ? lit : dim;
        ModeQuiet.Background = mode == 2 ? lit : dim;
        ModeBurst.Foreground = mode == 1 ? Brushes.White : text;
        ModeGame.Foreground = mode == 0 ? Brushes.White : text;
        ModeQuiet.Foreground = mode == 2 ? Brushes.White : text;
        ModeNote.Text = mode switch
        {
            1 => "狂飙 · 风扇上限 5000 RPM",
            0 => "游戏 · 风扇上限 5000 RPM",
            2 => "办公 · 风扇上限 2700 RPM",
            _ => "",
        };
    }

    private static string ModeName(int m) => m switch { 1 => "狂飙", 0 => "游戏", 2 => "办公", _ => m.ToString() };

    // ================================================================ switches
    private void Dgpu_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _wmi is null) return;
        try
        {
            bool on = DgpuSwitch.IsChecked == true;
            _wmi.Set(WmiCommand.GPUMode, (byte)(on ? 1 : 0));
            AddEvent(on ? "独显直连已开启（需重启生效）" : "已切回混合模式（需重启生效）");
        }
        catch (Exception ex) { AddEvent("切换显卡模式失败: " + OneLine(ex.Message)); }
    }

    private void Tp_Changed(object sender, RoutedEventArgs e) => WriteSwitch(WmiCommand.TPLock, TpSwitch, "TPLock");
    private void Ambient_Changed(object sender, RoutedEventArgs e)
    {
        if (KbAmbientBar is not null) KbAmbientBar.Opacity = AmbientSwitch.IsChecked == true ? 1.0 : 0.15;
        WriteSwitch(WmiCommand.Ambientlight, AmbientSwitch, "氛围灯");
    }

    private void WriteSwitch(WmiCommand cmd, ToggleButton box, string label)
    {
        if (_syncing || _wmi is null) return;
        try
        {
            _wmi.Set(cmd, (byte)(box.IsChecked == true ? 1 : 0));
            AddEvent($"{label} -> {(box.IsChecked == true ? "开" : "关")}");
        }
        catch (Exception ex) { AddEvent($"{label} 失败: " + OneLine(ex.Message)); }
    }

    private void Osd_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (OsdSwitch.IsChecked == true)
        {
            AddEvent("OSD 事件提示已开启");
            Toast("OSD 已开启");
        }
        else
        {
            _osd?.Hide();
            AddEvent("OSD 事件提示已关闭");
        }
    }

    // ================================================================ theme
    private bool _isLight;

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        _isLight = !_isLight;
        ApplyTheme(_isLight);
        if (_currentMode >= 0) HighlightMode(_currentMode);   // refresh the active pill colour
        ThemeGlyph.Text = _isLight ? "\uE706" : "\uE708";   // sun : moon
        AddEvent(_isLight ? "已切换到浅色" : "已切换到深色");
    }

    private void ApplyTheme(bool light)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Dwm.DisableBackdrop(hwnd);
        Dwm.EnableDarkTitleBar(hwnd);

        string name = light ? "Light" : "Dark";
        var dicts = Application.Current.Resources.MergedDictionaries;
        var source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute);
        for (int i = 0; i < dicts.Count; i++)
        {
            var s = dicts[i].Source;
            if (s is not null && s.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase))
            {
                dicts[i] = new ResourceDictionary { Source = source };
                return;
            }
        }
        dicts.Add(new ResourceDictionary { Source = source });
    }

    // ================================================================ curve optimiser (SMU)
    /// <summary>
    /// Sends <c>set-coall</c> on the RSMU mailbox (msg 7) with <c>0x100000 - |offset|</c>.
    /// mv must be -50..0, 0 = off, positive rejected.
    /// </summary>
    private bool PushCurveOptimizer(int mv, out string message)
    {
        try
        {
            _smu ??= new SmuClient();
            bool ok = _smu.SetCurveOptimizer(mv, out var rsp);
            _coAck = ok ? "RSP=1 已确认" : $"RSP=0x{rsp:X}";
            message = ok
                ? $"电压调节 CO = {mv} mV（SMU 已确认 RSP={rsp}）"
                : $"SMU 未确认 CO（RSP=0x{rsp:X}）";
            return ok;
        }
        catch (Exception ex)
        {
            _coAck = "失败";
            message = "CO 失败: " + OneLine(ex.Message);
            return false;
        }
    }

    private void Co_Committed(object sender, RoutedEventArgs e) => ApplyCo();

    private void Co_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyCo();
    }

    private void Co_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        ApplyCo();
    }

    private void ApplyCo()
    {
        if (_syncing) return;

        if (!int.TryParse(CoBox.Text, out var mv) || mv > 0 || mv < -50)
        {
            CoBox.Text = _cfg.CurveOffset.ToString(CultureInfo.InvariantCulture);
            AddEvent("电压调节范围 -50 ~ 0（0 = 关闭降压，不接受正数）");
            return;
        }

        if (CoSwitch.IsChecked != true)
        {
            // turning it off must actually restore the CPU, not leave -40 latched
            if (PushCurveOptimizer(0, out var offMsg)) AddEvent("电压调节已关闭 → " + offMsg);
            _cfg.CurveOffsetOn = false;
            return;
        }

        if (PushCurveOptimizer(mv, out var msg))
        {
            _cfg.CurveOffset = mv;
            _cfg.CurveOffsetOn = true;
        }
        AddEvent(msg);
    }

    // ================================================================ lighting (0x0A/0x0F/0x10/0x11/0x12)
    private int _previewRgb = 0x00FFFF;
    private DispatcherTimer? _colorTimer;

    /// <summary>
    /// 0x0A is write-protected here (reply 0xE0) and 0x10 = Mode_Off is rejected too, so the only
    /// switch the firmware actually honours is brightness: 0 = dark, 1..3 = on.
    /// Toggling therefore gates 0x12 and remembers the previous level.
    /// </summary>
    private void Kbd_Changed(object sender, RoutedEventArgs e)
    {
        if (KbGlowHalo is not null)
        {
            KbGlowHalo.Opacity = KbdSwitch.IsChecked == true ? 0.55 : 0.05;
        }
        if (_syncing || _wmi is null) return;
        try
        {
            if (KbdSwitch.IsChecked == true)
            {
                int level = _cfg.KbdBrightness is > 0 and <= 3 ? _cfg.KbdBrightness : 3;
                SetBrightness(level);
                AddEvent($"键盘背光开启 · 亮度 {level}");
            }
            else
            {
                int prev = (int)KbdBright.Value;
                if (prev > 0) _cfg.KbdBrightness = prev;   // remember what to restore
                SetBrightness(0);
                AddEvent("键盘背光关闭（亮度归 0）");
            }
        }
        catch (Exception ex) { AddEvent("键盘背光失败: " + OneLine(ex.Message)); }
    }

    /// <summary>Writes 0x12 and verifies it stuck - out-of-range values are silently dropped.</summary>
    private void SetBrightness(int level)
    {
        if (_wmi is null) return;
        level = Math.Clamp(level, 0, 3);
        _wmi.Set(WmiCommand.RGBKeyboardBrightness, (byte)level);
        int back = _wmi.Get(WmiCommand.RGBKeyboardBrightness)[4];
        if (back != level) AddEvent($"亮度写入未生效（期望 {level}，回读 {back}）");
        _cfg.KbdBrightness = level;
        _syncing = true;
        try
        {
            KbdBright.Value = level;
            KbdBrightText.Text = level.ToString();
        }
        finally { _syncing = false; }
    }

    /// <summary>
    /// Pins the firmware to Mode_RGBFixedMode (0x10 = 2).
    /// Probes proved 0 and 4..6 are rejected and 1 has no visible effect, so "fixed" is the only
    /// mode worth using: it simply renders whatever colour we write - static or animated by the
    /// loops below. The mode picker was therefore removed from the UI.
    /// </summary>
    private void SetStaticMode(bool log = true)
    {
        if (_wmi is null) return;
        try
        {
            _wmi.Set(WmiCommand.RGBKeyboardMode, 2);
            _cfg.KbdMode = 2;
            if (log) AddEvent("背光模式 -> 常亮");
        }
        catch (Exception ex) { AddEvent("设置背光模式失败: " + OneLine(ex.Message)); }
    }

    private void KbdBright_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (KbdBrightText is null) return;
        int v = Math.Clamp((int)KbdBright.Value, 0, 3);
        KbdBrightText.Text = v.ToString();
        if (KbGlowHalo is not null)
        {
            KbGlowHalo.Opacity = v == 0 ? 0.05 : 0.2 + (v / 3.0) * 0.45;
        }
        if (_syncing || _wmi is null) return;
        try
        {
            _wmi.Set(WmiCommand.RGBKeyboardBrightness, (byte)v);
            int back = _wmi.Get(WmiCommand.RGBKeyboardBrightness)[4];
            if (back != v) AddEvent($"亮度写入未生效（期望 {v}，回读 {back}）");
            _cfg.KbdBrightness = v;
        }
        catch (Exception ex) { AddEvent("设置亮度失败: " + OneLine(ex.Message)); }
    }

    private void Hue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ColorSwatch is null || ColorText is null) return;
        var (r, g, b) = HsvToRgb(HueSlider.Value, 1, 1);
        _previewRgb = (r << 16) | (g << 8) | b;
        ColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ColorText.Text = $"#{_previewRgb:X6}";
        UpdateKeyboardPreview(r, g, b);
        if (_syncing || _wmi is null) return;

        // debounce: dragging the hue slider must not spam the WMI bridge
        if (_colorTimer is null)
        {
            _colorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _colorTimer.Tick += (_, _) =>
            {
                _colorTimer.Stop();
                PushColor(_previewRgb);
            };
        }
        _colorTimer.Stop();
        _colorTimer.Start();
    }

    private void PaletteChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var hue))
        {
            HueSlider.Value = hue;
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        EventList.Items.Clear();
        AddEvent("日志记录已清空");
    }

    private void UpdateKeyboardPreview(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        if (KbAmbientBar is not null) KbAmbientBar.Background = brush;
        if (KbGlowHalo is not null) KbGlowHalo.Color = Color.FromRgb(r, g, b);
    }

    private void PushColor(int rgb, bool log = true)
    {
        if (_wmi is null) return;
        try
        {
            _wmi.Set(WmiCommand.RGBKeyboardColor,
                     (byte)((rgb >> 16) & 0xFF),
                     (byte)((rgb >> 8) & 0xFF),
                     (byte)(rgb & 0xFF));
            _cfg.KbdColorRgb = rgb;
            if (log) AddEvent($"背光颜色 -> #{rgb:X6}");
        }
        catch (Exception ex) { AddEvent("设置背光颜色失败: " + OneLine(ex.Message)); }
    }

    /// <summary>Mirrors the firmware, then lets config win where it differs.</summary>
    private void RestoreLighting()
    {
        if (_wmi is null) return;
        try
        {
            int bright = _wmi.Get(WmiCommand.RGBKeyboardBrightness)[4];
            if (bright is >= 0 and <= 3)
            {
                KbdBright.Value = bright;
                KbdBrightText.Text = bright.ToString();
            }

            var col = _wmi.Get(WmiCommand.RGBKeyboardColor);
            ApplyPreviewRgb((col[4] << 16) | (col[5] << 8) | col[6]);

            // backlight on/off = brightness gate (0x10 mode 0 is rejected by the firmware)
            KbdSwitch.IsChecked = (int)KbdBright.Value > 0;

            if (_cfg.KbdBrightness >= 0 && _cfg.KbdBrightness != (int)KbdBright.Value)
                SetBrightness(_cfg.KbdBrightness);

            SetStaticMode(log: false);
            if (_cfg.KbdColorRgb >= 0 && _cfg.KbdColorRgb != _previewRgb)
            {
                PushColor(_cfg.KbdColorRgb, log: false);
                ApplyPreviewRgb(_cfg.KbdColorRgb);
            }
        }
        catch (Exception ex) { AddEvent("恢复灯光设置失败: " + OneLine(ex.Message)); }
    }

    private void ApplyPreviewRgb(int rgb)
    {
        byte r = (byte)((rgb >> 16) & 0xFF);
        byte g = (byte)((rgb >> 8) & 0xFF);
        byte b = (byte)(rgb & 0xFF);
        _previewRgb = rgb;
        bool prev = _syncing;
        _syncing = true;
        try { HueSlider.Value = RgbToHue(r, g, b); }
        finally { _syncing = prev; }
        ColorSwatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        ColorText.Text = $"#{rgb:X6}";
        UpdateKeyboardPreview(r, g, b);
    }

    /// <summary>Pure hue (S = 1, V = 1) color conversion for 0x11 packet.</summary>
    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double hp = (((h % 360) + 360) % 360) / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        double r1 = 0, g1 = 0, b1 = 0;
        switch ((int)hp)
        {
            case 0: r1 = c; g1 = x; break;
            case 1: r1 = x; g1 = c; break;
            case 2: g1 = c; b1 = x; break;
            case 3: g1 = x; b1 = c; break;
            case 4: r1 = x; b1 = c; break;
            default: r1 = c; b1 = x; break;
        }
        double m = v - c;
        return ((byte)Math.Clamp((r1 + m) * 255, 0, 255),
                (byte)Math.Clamp((g1 + m) * 255, 0, 255),
                (byte)Math.Clamp((b1 + m) * 255, 0, 255));
    }

    private static int RgbToHue(byte r, byte g, byte b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        if (d <= 0) return 0;
        double h = max == r ? 60 * (((g - b) / d) % 6)
                 : max == g ? 60 * ((b - r) / d + 2)
                            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (int)Math.Round(h);
    }

    // ================================================================ software colour effects
    // The firmware mode (0x10) is a mode label; smooth animation is produced by writing 0x11
    // repeatedly. We run at 10 fps to balance visual smoothness and WMI bus headroom.
    private CancellationTokenSource? _colorCts;
    private int _loopMode;                       // 0 none / 1 rainbow / 2 breathing

    private void Rainbow_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (RainbowSwitch.IsChecked == true)
        {
            if (BreathSwitch.IsChecked == true) SetChecked(BreathSwitch, false);
            StartColorLoop(1);
        }
        else if (_loopMode == 1) StopColorLoop();
        PersistEffect();
    }

    private void Breath_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (BreathSwitch.IsChecked == true)
        {
            if (RainbowSwitch.IsChecked == true) SetChecked(RainbowSwitch, false);
            StartColorLoop(2);
        }
        else if (_loopMode == 2) StopColorLoop();
        PersistEffect();
    }

    private void SetChecked(ToggleButton box, bool value)
    {
        bool prev = _syncing;      // restore, so a nested call does not clear an outer _syncing
        _syncing = true;
        try { box.IsChecked = value; }
        finally { _syncing = prev; }
    }

    private void StartColorLoop(int kind)
    {
        StopColorLoop(silent: true);
        _loopMode = kind;
        _colorCts = new CancellationTokenSource();
        var ct = _colorCts.Token;
        SetStaticMode(log: false);   // firmware must be in fixed mode to render our animated colours
        _ = Task.Run(() => ColorLoopAsync(kind, ct));
        AddEvent(kind == 1 ? "彩虹循环已启动 · 10 FPS" : "呼吸效果已启动 · 5s 周期");
    }

    private void StopColorLoop(bool silent = false)
    {
        if (_colorCts is null) return;
        int was = _loopMode;
        _colorCts.Cancel();
        _colorCts.Dispose();
        _colorCts = null;
        _loopMode = 0;
        SetStaticMode(log: false);   // back to a solid colour
        if (!silent) AddEvent(was == 1 ? "彩虹循环已停止" : "呼吸效果已停止");
    }

    private void PersistEffect()
    {
        _cfg.ColorLoop = RainbowSwitch.IsChecked == true ? 1
                       : BreathSwitch.IsChecked == true ? 2 : 0;
        PersistIfChanged();
    }

    private async Task ColorLoopAsync(int kind, CancellationToken ct)
    {
        int baseRgb = _previewRgb <= 0 ? 0x00FFFF : _previewRgb;
        double hue = kind == 1
            ? RgbToHue((byte)((baseRgb >> 16) & 0xFF), (byte)((baseRgb >> 8) & 0xFF), (byte)(baseRgb & 0xFF))
            : 0;
        long t0 = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            int rgb;
            if (kind == 1)
            {
                rgb = Pack(HsvToRgb(hue, 1, 1));
                hue += 6;                                  // 6°/frame @10 fps -> 6 s per full cycle
                if (hue >= 360) hue -= 360;
            }
            else
            {
                double phase = ((Environment.TickCount64 - t0) % 5000) / 5000.0;
                double k = 0.15 + 0.85 * (0.5 + 0.5 * Math.Sin(phase * Math.PI * 2));
                rgb = ScaleRgb(baseRgb, k);
            }

            try
            {
                _wmi?.Set(WmiCommand.RGBKeyboardColor,
                          (byte)((rgb >> 16) & 0xFF),
                          (byte)((rgb >> 8) & 0xFF),
                          (byte)(rgb & 0xFF));
            }
            catch
            {
                // bridge busy or already torn down - keep the loop alive, it recovers
            }

            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static int Pack((byte R, byte G, byte B) c) => (c.R << 16) | (c.G << 8) | c.B;

    private static int ScaleRgb(int rgb, double k)
    {
        int r = (int)Math.Clamp((rgb >> 16 & 0xFF) * k, 0, 255);
        int g = (int)Math.Clamp((rgb >> 8 & 0xFF) * k, 0, 255);
        int b = (int)Math.Clamp((rgb & 0xFF) * k, 0, 255);
        return (r << 16) | (g << 8) | b;
    }

    // ================================================================ temperature curve
    private void BtnCurve_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new CurveWindow(_cfg) { Owner = this };
        dlg.ShowDialog();
        ApplyCurveFromConfig();
        PersistIfChanged();
    }

    /// <summary>Pushes the config's curve into the controller (single owner of EC writes).</summary>
    private void ApplyCurveFromConfig(bool quiet = false)
    {
        if (_fan is null) return;
        var curve = new FanCurve { Temps = _cfg.CurveTemps, Rpm = _cfg.CurveRpm };
        _fan.SetCurve(_cfg.CurveEnabled && curve.IsValid ? curve : null);
        if (!quiet) AddEvent(_cfg.CurveEnabled ? "温度曲线已启用" : "温度曲线已停用");
        UpdateOwnerPill();
    }

    // ================================================================ CPU power budget (0x17)
    // Hardware power budget sub-commands:
    //   [0x17][sub][value]  sub: 0x04 temperature wall, 0x02 SPL, 0x03 SPPT
    private const byte SubSpl = 0x02;
    private const byte SubSppt = 0x03;
    private const byte SubTempWall = 0x04;

    private void TempWall_Committed(object sender, RoutedEventArgs e)
        => CommitPower(SubTempWall, TempWallBox, "温度墙", 70, 100, "℃");

    private void Spl_Committed(object sender, RoutedEventArgs e)
        => CommitPower(SubSpl, SplBox, "长时功耗 SPL", 25, 100, "W");

    private void Sppt_Committed(object sender, RoutedEventArgs e)
        => CommitPower(SubSppt, SpptBox, "短时功耗 SPPT", 30, 130, "W");

    private void TempWall_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitPower(SubTempWall, TempWallBox, "温度墙", 70, 100, "℃");
    }

    private void Spl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitPower(SubSpl, SplBox, "长时功耗 SPL", 25, 100, "W");
    }

    private void Sppt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitPower(SubSppt, SpptBox, "短时功耗 SPPT", 30, 130, "W");
    }

    private void CommitPower(byte sub, TextBox box, string label, int min, int max, string unit)
    {
        if (_syncing || _wmi is null) return;
        if (!int.TryParse(box.Text, out var v))
        {
            AddEvent($"{label} 不是有效数字，已忽略");
            return;
        }
        if (v < min || v > max)
        {
            AddEvent($"{label} 允许 {min}–{max} {unit}，已忽略（当前 {v}）");
            return;
        }

        try
        {
            _wmi.Set(WmiCommand.CPUPower, sub, (byte)v);
            switch (sub)
            {
                case SubTempWall: _cfg.TempWallC = v; break;
                case SubSpl: _cfg.SplW = v; break;
                case SubSppt: _cfg.SpptW = v; break;
            }
            AddEvent($"{label} = {v} {unit}（固件已应用 · 功耗协议生效）");
        }
        catch (Exception ex) { AddEvent($"{label} 失败: " + OneLine(ex.Message)); }
    }

    private void RestorePowerBudget()
    {
        if (_wmi is null) return;
        void Apply(byte sub, int value, string label, string unit)
        {
            if (value < 0) return;
            try
            {
                _wmi.Set(WmiCommand.CPUPower, sub, (byte)value);
                AddEvent($"恢复 {label} = {value} {unit}");
            }
            catch (Exception ex) { AddEvent($"恢复 {label} 失败: " + OneLine(ex.Message)); }
        }

        if (_cfg.TempWallC >= 0) TempWallBox.Text = _cfg.TempWallC.ToString(CultureInfo.InvariantCulture);
        if (_cfg.SplW >= 0) SplBox.Text = _cfg.SplW.ToString(CultureInfo.InvariantCulture);
        if (_cfg.SpptW >= 0) SpptBox.Text = _cfg.SpptW.ToString(CultureInfo.InvariantCulture);

        Apply(SubTempWall, _cfg.TempWallC, "温度墙", "℃");
        Apply(SubSpl, _cfg.SplW, "长时功耗 SPL", "W");
        Apply(SubSppt, _cfg.SpptW, "短时功耗 SPPT", "W");
    }

    // ================================================================ log
    private void AddEvent(string message)
    {
        // 1. Thread-safe log to rolling daily file (%AppData%\TDPC\logs\tdpc_yyyyMMdd.log, retained 30 days)
        FileLogger.Info(message);

        // 2. Display in UI
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddEvent(message));
            return;
        }
        var tb = new TextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss}  {message}",
            Foreground = TryFindResource("EventText") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x8E, 0x9D, 0xB2)),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5),
        };
        EventList.Items.Insert(0, tb);
        while (EventList.Items.Count > 150) EventList.Items.RemoveAt(EventList.Items.Count - 1);
    }

    private void LogHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;
        bool isVisible = LogContainer.Visibility == Visibility.Visible;
        LogContainer.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        LogExpandChevron.Text = isVisible ? "▾" : "▴";
        LogSummaryText.Text = isVisible ? "（点击展开 · 本地保留30天）" : "（本地日志保留 30 天）";
    }

    private void BtnOpenLogDir_Click(object sender, RoutedEventArgs e) => FileLogger.OpenLogDirectory();

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    // ================================================================ tray & autostart
    private void ShowDashboard()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowDashboard);
            return;
        }
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void HideToTray(bool silent = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => HideToTray(silent));
            return;
        }
        Hide();
        ShowInTaskbar = false;
        if (!silent)
        {
            _tray?.ShowToast("TDPC 已缩入系统托盘", "后台硬件保护与风扇看门狗持续运行中，点击托盘图标可重新呼出。");
        }
    }

    private void ShowTrayContextMenu()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowTrayContextMenu);
            return;
        }
        if (FindResource("TrayContextMenu") is ContextMenu cm)
        {
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
        }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool want = AutoStartSwitch.IsChecked == true;
        bool ok = AutoStartManager.SetAutoStart(want, out var msg);
        AddEvent(ok ? $"开机自启: {msg}" : $"开机自启失败: {msg}");
        _cfg.AutoStart = want;
        PersistIfChanged();
    }

    private void TrayClose_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _cfg.MinimizeToTrayOnClose = TrayCloseSwitch.IsChecked == true;
        AddEvent($"关闭窗口行为 -> {(_cfg.MinimizeToTrayOnClose ? "缩入托盘常驻" : "直接退出")}");
        PersistIfChanged();
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e) => ShowDashboard();

    private void TrayModeBurst_Click(object sender, RoutedEventArgs e) => SetMode(1);

    private void TrayModeGame_Click(object sender, RoutedEventArgs e) => SetMode(0);

    private void TrayModeQuiet_Click(object sender, RoutedEventArgs e) => SetMode(2);

    private void TrayReleaseFan_Click(object sender, RoutedEventArgs e) => ReleaseFan();

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    // ================================================================ auto-update & version check
    private async Task CheckUpdateInBackgroundAsync()
    {
        try
        {
            var info = await UpdateService.CheckForUpdateAsync();
            if (info?.HasUpdate == true)
            {
                _latestUpdate = info;
                Dispatcher.Invoke(() =>
                {
                    UpdateDot.Fill = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                    VersionText.Text = $"{UpdateService.CurrentVersionString} (新版本)";
                    VersionBadge.ToolTip = $"发现新版本 {info.LatestVersionStr}！点击查看并更新";
                    AddEvent($"[更新提示] 发现新版本 {info.LatestVersionStr}，点击顶部版本号即可一键升级");
                });
            }
        }
        catch { /* silent background check */ }
    }

    private async void VersionBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (_latestUpdate?.HasUpdate == true)
        {
            ShowUpdateModal(_latestUpdate);
            return;
        }

        VersionText.Text = "检查中…";
        try
        {
            var info = await UpdateService.CheckForUpdateAsync();
            if (info?.HasUpdate == true)
            {
                _latestUpdate = info;
                UpdateDot.Fill = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                VersionText.Text = $"{UpdateService.CurrentVersionString} (新版本)";
                VersionBadge.ToolTip = $"发现新版本 {info.LatestVersionStr}！点击更新";
                ShowUpdateModal(info);
            }
            else
            {
                VersionText.Text = UpdateService.CurrentVersionString;
                Toast($"已是最新版本 ({UpdateService.CurrentVersionString})");
                AddEvent($"[更新检查] 当前已是最新版本 ({UpdateService.CurrentVersionString})");
            }
        }
        catch (Exception ex)
        {
            VersionText.Text = UpdateService.CurrentVersionString;
            Toast("检查更新超时，请稍后重试");
            AddEvent($"[更新检查失败] {ex.Message}");
        }
    }

    private void ShowUpdateModal(UpdateInfo info)
    {
        UpdateVersionSubtitle.Text = $"{info.CurrentVersionStr}  ➔  {info.LatestVersionStr} (最新)";
        UpdateNotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "包含最新性能优化与稳定性更新。"
            : info.ReleaseNotes;
        UpdateProgressPanel.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Value = 0;
        UpdateProgressPct.Text = "0%";
        UpdateApplyBtn.IsEnabled = true;
        UpdateCancelBtn.IsEnabled = true;
        UpdateModal.Visibility = Visibility.Visible;
    }

    private void UpdateModalClose_Click(object sender, RoutedEventArgs e)
    {
        UpdateModal.Visibility = Visibility.Collapsed;
    }

    private void UpdateBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/kirigayakazima/TDPC/releases/latest",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void UpdateApply_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdate == null || string.IsNullOrEmpty(_latestUpdate.DownloadUrl))
        {
            Toast("未找到可用下载链接");
            return;
        }

        UpdateApplyBtn.IsEnabled = false;
        UpdateCancelBtn.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressStatus.Text = "正在下载更新包 (290 KB)...";

        var progress = new Progress<double>(pct =>
        {
            UpdateProgressBar.Value = pct;
            UpdateProgressPct.Text = $"{pct:F0}%";
            if (pct >= 100)
            {
                UpdateProgressStatus.Text = "下载解压完毕，正在重启升级...";
            }
        });

        try
        {
            AddEvent($"[在线更新] 开始下载 {_latestUpdate.LatestVersionStr}...");
            await UpdateService.DownloadAndApplyUpdateAsync(_latestUpdate, progress);
        }
        catch (Exception ex)
        {
            Toast("更新失败：" + ex.Message);
            AddEvent($"[在线更新失败] {ex.Message}");
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            UpdateApplyBtn.IsEnabled = true;
            UpdateCancelBtn.IsEnabled = true;
        }
    }
}
