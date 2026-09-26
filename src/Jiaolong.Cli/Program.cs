using Jiaolong.Core.Ec;
using Jiaolong.Core.Power;
using Jiaolong.Core.Wmi;

namespace Jiaolong.Cli;

internal static class Program
{
    private static readonly Dictionary<byte, string> Names = Enum.GetValues<WmiCommand>()
        .ToDictionary(c => (byte)c, c => c.ToString());

    private static int Main(string[] args)
    {
        try
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "wmi";
            return cmd switch
            {
                "wmi" or "read" => ReadAll(),
                "get" => GetOne(args),
                "set" => SetOne(args),
                "watch" => Watch(args),
                "power" => Power(),
                "ec" => EcRead(),
                "ectest" => EcTest(args),
                "help" or "-h" or "--help" => Help(),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            jiatool - independent MECHREVO Jiaolong control probe
              wmi               read every firmware register (needs Administrator)
              get  <hex>        read one command, e.g.  get 0D
              set  <hex> <byte> write one command      e.g.  set 15 21
              watch [seconds]   poll rpm+temp every second (default 20)
              power             show current powercfg values (no admin needed)
              ec                read the EC directly via WinRing0 (admin; reveals who owns the fan)
              ectest [rpm] [s]  REVERSIBLE EC write test: lock -> observe -> FanClear -> observe
                                default 2500 RPM, 15s per phase; BIOS is always restored
            """);
        return 0;
    }

    private static int ReadAll()
    {
        using var wmi = new JiaolongWmi();
        Console.WriteLine($"{"CMD",-6} {"NAME",-24} {"OUT[0..7]",-26} DECODED");
        Console.WriteLine(new string('-', 92));
        foreach (WmiCommand c in Enum.GetValues<WmiCommand>())
        {
            var d = wmi.Get(c);
            string hex = string.Join(' ', d.Take(8).Select(b => b.ToString("X2")));
            Console.WriteLine($"0x{(byte)c:X2}  {c,-24} {hex,-26} {Decode(c, d)}");
        }
        return 0;
    }

    private static string Decode(WmiCommand c, byte[] d)
    {
        if (d.Length < 8) return "(short reply)";
        int u1 = WmiProtocol.ReadU16(d, 4);
        int u2 = WmiProtocol.ReadU16(d, 6);
        return c switch
        {
            WmiCommand.CPUGPUFanSpeed => $"CPU={u1} RPM  GPU={u2} RPM",
            WmiCommand.CPUThermometer => $"T={d[4]} C",
            WmiCommand.MaxFanSpeed => $"value={d[4]} (x100={d[4] * 100} RPM)",
            _ => $"val={d[4]}",
        };
    }

    private static int GetOne(string[] args)
    {
        if (args.Length < 2) return Help();
        var cmd = ParseCmd(args[1]);
        using var wmi = new JiaolongWmi();
        var d = wmi.Get(cmd);
        Console.WriteLine(string.Join(' ', d.Select(b => b.ToString("X2"))));
        Console.WriteLine(Decode(cmd, d));
        return 0;
    }

    private static int SetOne(string[] args)
    {
        if (args.Length < 3) return Help();
        var cmd = ParseCmd(args[1]);
        var payload = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => Convert.ToByte(s.StartsWith("0x") ? s : s, 10)).ToArray();
        using var wmi = new JiaolongWmi();
        var reply = wmi.Exchange(WmiProtocol.MakePacket(WmiProtocol.Set, (byte)cmd, payload));
        var back = wmi.Get(cmd);
        Console.WriteLine($"write ok. reply[0..7] = {string.Join(' ', reply.Take(8).Select(b => b.ToString("X2")))}");
        Console.WriteLine($"readback          = {string.Join(' ', back.Take(8).Select(b => b.ToString("X2")))}  -> {Decode(cmd, back)}");
        return 0;
    }

    private static int Watch(string[] args)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 20;
        using var wmi = new JiaolongWmi();
        Console.WriteLine(" t | CPUrpm | GPUrpm | Tcpu | 0x15 | 0x14 | mode");
        Console.WriteLine("---+--------+--------+------+------+------+-----");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        int t = 0;
        while (DateTime.UtcNow < until)
        {
            var (cpu, gpu) = wmi.ReadFanRpm();
            int temp = wmi.ReadCpuTemp();
            int m15 = wmi.Get(WmiCommand.MaxFanSpeed)[4];
            int m14 = wmi.Get(WmiCommand.MaxFanSpeedSwitch)[4];
            int mode = wmi.Get(WmiCommand.SystemPerMode)[4];
            Console.WriteLine($"{t,3} | {cpu,6} | {gpu,6} | {temp,4} | {m15,4} | {m14,4} | {mode}");
            t++;
            Thread.Sleep(1000);
        }
        return 0;
    }

    /// <summary>
    /// Reads the EC directly. Validates the read protocol against ChipId1 (must be 0x55),
    /// then dumps the fan registers to reveal who currently owns the fan.
    /// </summary>
    private static int EcRead()
    {
        using var ec = new EcAccess();
        Console.WriteLine($"WinRing0 DllStatus = 0x{ec.DllStatus:X}   DriverReady = {ec.DriverReady}");
        Console.WriteLine();

        byte id1 = ec.Read16(EcAccess.ChipId1);
        byte id2 = ec.Read16(EcAccess.ChipId2);
        byte ver = ec.Read16(EcAccess.ChipVer);
        Console.WriteLine($"CHIP_ID1  0x2000 = 0x{id1:X2}   (expect 0x55 = read protocol valid)");
        Console.WriteLine($"CHIP_ID2  0x2001 = 0x{id2:X2}");
        Console.WriteLine($"CHIP_VER  0x1060 = 0x{ver:X2}");
        bool ok = id1 == 0x55;
        Console.WriteLine(ok ? "READ PROTOCOL : VALIDATED" : "READ PROTOCOL : FAILED (id1 != 0x55)");
        Console.WriteLine();

        byte fc = ec.Read16(EcAccess.FanSpeedCpu);
        byte fg = ec.Read16(EcAccess.FanSpeedGpu);
        byte ctl = ec.Read16(EcAccess.FanControl);
        Console.WriteLine($"FAN_CPU   0xC83C = 0x{fc:X2} ({fc,3})  -> {fc * 100} RPM");
        Console.WriteLine($"FAN_GPU   0xC83D = 0x{fg:X2} ({fg,3})  -> {fg * 100} RPM");
        Console.WriteLine($"FAN_CTRL  0x0B20 = 0x{ctl:X2}   manual-bit = {(ctl & 2) != 0}");
        Console.WriteLine();

        try
        {
            using var wmi = new JiaolongWmi();
            var (cpu, gpu) = wmi.ReadFanRpm();
            Console.WriteLine($"WMI 0x0D live      CPU={cpu} RPM   GPU={gpu} RPM");
            Console.WriteLine($"fan owner          {(ec.IsManualFanMode() ? "EC MANUAL  <- third-party / our lock owns it" : "BIOS AUTO  <- nobody is overriding")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("WMI cross-check failed:");
            Console.WriteLine(ex.ToString());
        }
        return ok ? 0 : 2;
    }

    /// <summary>
    /// Fully reversible EC write test:
    ///   phase 1  baseline (read only)
    ///   phase 2  lock the fan at &lt;rpm&gt; and observe
    ///   phase 3  FanClear() inside a finally block -> hand control back to BIOS and observe
    /// The BIOS is restored no matter what happens in phase 2.
    /// </summary>
    private static int EcTest(string[] args)
    {
        int rpm = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 2500;
        int secs = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 15;
        if (rpm is < 1800 or > 5800) { Console.WriteLine("rpm must be within 1800..5800"); return 1; }
        byte target = (byte)(rpm / 100);

        using var ec = new EcAccess();
        using var wmi = new JiaolongWmi();

        void Sample(string tag)
        {
            byte c = ec.Read16(EcAccess.FanSpeedCpu);
            byte g = ec.Read16(EcAccess.FanSpeedGpu);
            byte ctl = ec.Read16(EcAccess.FanControl);
            var (wcpu, wgpu) = wmi.ReadFanRpm();
            int t = wmi.ReadCpuTemp();
            Console.WriteLine(
                $"  {tag,-8} EC[C]={c,3}({c * 100,4}) EC[G]={g,3}({g * 100,4}) ctl=0x{ctl:X2} manual={((ctl & 2) != 0).ToString().ToLowerInvariant(),-5} " +
                $"| WMI cpu={wcpu,4} gpu={wgpu,4} T={t,2}C");
        }

        void Hold(string tag, int seconds)
        {
            for (int i = 0; i < seconds; i++) { Sample(tag); Thread.Sleep(1000); }
        }

        Console.WriteLine($"target = {rpm} RPM   (EC byte = {target})");
        Console.WriteLine("--- phase 1: baseline (no write) ---");
        Sample("base"); Sample("base"); Hold("base", 3);

        try
        {
            Console.WriteLine($"--- phase 2: lock EC fan to {target * 100} RPM ---");
            ec.SetManualFanSpeed(target);
            Sample("write"); Sample("write"); Hold("locked", secs);
        }
        finally
        {
            Console.WriteLine("--- phase 3: FanClear -> hand back to BIOS ---");
            ec.ReleaseToBios();
            Sample("clear"); Sample("clear"); Hold("auto", secs);
        }

        Console.WriteLine("DONE - BIOS control restored.");
        return 0;
    }

    private static int Power()
    {
        Console.WriteLine($"active scheme : {PowerCfg.GetActiveScheme()}");
        Show("max frequency (MHz)", PowerCfg.MaxProcessorFrequency);
        Show("boost mode         ", PowerCfg.BoostMode);
        Show("core parking max % ", PowerCfg.CoreParkingMaxCores);
        return 0;
    }

    private static void Show(string label, string guid)
    {
        var v = PowerCfg.ReadIndexes(guid);
        Console.WriteLine($"{label}: {(v is null ? "(not set)" : $"AC={v.Value.Ac}  DC={v.Value.Dc}")}");
    }

    private static WmiCommand ParseCmd(string s)
    {
        s = s.Trim().TrimStart("0x".ToCharArray());
        if (byte.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var b))
            return (WmiCommand)b;
        return Enum.Parse<WmiCommand>(s, true);
    }
}
