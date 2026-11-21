using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Jiaolong.Core.Smu;

/// <summary>
/// AMD Zen4 SMU mailbox access (Curve Optimizer etc) for Dragon Range / Socket AM5 V1.
///
/// Registers are reached through PCI 0:0.0 config space: offset 0xB8 = index, 0xBC = data.
/// Addresses below correspond to DragonRange platform registers.
///
/// NOTE: WinRing0 must already be initialised (EcAccess does that on startup).
/// </summary>
public sealed class SmuClient
{
    private const string Ols = "WinRing0x64.dll";

    // PCI 0:0.0 - index/data pair the SMU mailbox hangs off
    private const ushort PciAddr = 0;        // bus0 dev0 fun0
    private const ushort OffIndex = 0xB8;
    private const ushort OffData = 0xBC;

    // DragonRange -> Socket_AM5_V1 (SMU message / response / argument block)
    private const uint Mp1Msg = 0x3B10530;
    private const uint Mp1Rsp = 0x3B1057C;
    private const uint Mp1Arg = 0x3B109C4;
    private const uint PsmuMsg = 0x3B10524;
    private const uint PsmuRsp = 0x3B10570;
    private const uint PsmuArg = 0x3B10A40;

    private const int Retry = 0x2000;

    // ---- command table (RyzenSmu.SMUCommands, Socket_AM5_V1) ------------------------
    /// <summary>Set Curve Optimiser for all cores. Argument = 0x100000 - magnitude.</summary>
    public const uint MsgSetCoAll = 7;
    public const uint MsgSetCoPerCore = 6;
    public const uint MsgEnableOc = 93;
    public const uint MsgDisableOc = 94;
    private const uint MsgGetBoostLimit = 0x6E;
    private const uint MsgSetBoostLimit = 0x70;

    public const int MinBoostLimitMhz = 3600;
    public const int MaxBoostLimitMhz = 5150;

    private readonly object _gate = new();
    private bool _driverReady;

    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern bool InitializeOls();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern void DeinitializeOls();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern uint GetDllStatus();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WritePciConfigDword(ushort pciAddress, ushort regAddress, uint value);
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool ReadPciConfigDwordEx(ushort pciAddress, ushort regAddress, out uint value);

    public static bool SupportsBoostLimit()
    {
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return cpu?.GetValue("VendorIdentifier") is "AuthenticAMD"
            && cpu.GetValue("Identifier") is string identifier
            && identifier.StartsWith("AMD64 Family 25 Model 97 Stepping ", StringComparison.Ordinal)
            && cpu.GetValue("ProcessorNameString") is string name
            && name.Contains("Ryzen 7 7745HX", StringComparison.OrdinalIgnoreCase)
            && bios?.GetValue("BIOSVersion") is "MRID6_23_P_V35";
    }

    public SmuClient()
    {
        // EcAccess usually brought the driver up already; only initialise if it is not there.
        _driverReady = GetDllStatus() == 0;
        if (!_driverReady) _driverReady = InitializeOls();
        if (!_driverReady)
            throw new InvalidOperationException(
                $"WinRing0 driver not ready (DllStatus=0x{GetDllStatus():X})");
    }

    private bool WriteReg(uint addr, uint data) =>
        WritePciConfigDword(PciAddr, OffIndex, addr) && WritePciConfigDword(PciAddr, OffData, data);

    private bool TryReadReg(uint addr, out uint value)
    {
        value = 0;
        return WritePciConfigDword(PciAddr, OffIndex, addr)
            && ReadPciConfigDwordEx(PciAddr, OffData, out value);
    }

    private bool WaitForResponse(uint address, out uint response)
    {
        for (int i = 0; i < Retry; i++)
        {
            if (!TryReadReg(address, out response)) return false;
            if (response != 0) return true;
        }
        response = 0;
        return false;
    }

    /// <summary>
    /// Wait for an idle mailbox, clear the previous response, send, then read the new response.
    /// Returns the final RSP register value (1 == SMU acknowledged the command).
    /// </summary>
    private uint SendMsg(uint msgAddr, uint rspAddr, uint argAddr, uint msg, uint[] args)
    {
        lock (_gate)
        {
            if (args.Length > 6) throw new ArgumentOutOfRangeException(nameof(args));
            if (!WaitForResponse(rspAddr, out _) || !WriteReg(rspAddr, 0)
                || !TryReadReg(rspAddr, out uint cleared) || cleared != 0)
                return 0;

            for (int i = 0; i < 6; i++)
            {
                uint address = argAddr + (uint)(i * 4);
                uint value = i < args.Length ? args[i] : 0;
                if (!WriteReg(address, value) || !TryReadReg(address, out uint written) || written != value)
                    return 0;
            }
            if (!WriteReg(msgAddr, msg) || !WaitForResponse(rspAddr, out uint response))
                return 0;
            if (response != 1) return response;

            for (int i = 0; i < args.Length; i++)
                if (!TryReadReg(argAddr + (uint)(i * 4), out args[i])) return 0;
            return response;
        }
    }

    /// <summary>Runs a command on the MP1 mailbox.</summary>
    public uint SendMp1(uint msg, params uint[] args) => SendMsg(Mp1Msg, Mp1Rsp, Mp1Arg, msg, args);

    /// <summary>
    /// Runs a command on the RSMU/PSMU mailbox (e.g. Curve Optimizer / OC commands).
    /// </summary>
    public uint SendRsmu(uint msg, params uint[] args) => SendMsg(PsmuMsg, PsmuRsp, PsmuArg, msg, args);

    internal int ReadBoostLimit()
    {
        uint[] args = [0];
        uint response = SendRsmu(MsgGetBoostLimit, args);
        if (response != 1 || args[0] is 0 or > 9000)
            throw new InvalidOperationException($"SMU 睿频上限读取失败（RSP=0x{response:X}，返回 {args[0]}）");
        return (int)args[0];
    }

    internal void WriteBoostLimitVerified(int mhz)
    {
        lock (_gate)
        {
            uint response = SendRsmu(MsgSetBoostLimit, (uint)mhz);
            if (response != 1)
                throw new InvalidOperationException($"SMU 拒绝写入 {mhz} MHz（RSP=0x{response:X}）");

            int first = ReadBoostLimit();
            int second = ReadBoostLimit();
            if (first != mhz || second != mhz)
                throw new InvalidOperationException($"SMU 目标 {mhz} MHz，实际回读 {first} / {second} MHz");
        }
    }

    /// <summary>
    /// Curve Optimizer for all cores.
    /// <paramref name="offset"/> is negative (undervolt) or 0 (off); positive values are rejected.
    ///
    /// The wire encoding is <c>0x100000 - |offset|</c>.
    /// The displayed value is a magnitude (0..50), so -40 becomes 0x100000 - 40.
    /// </summary>
    /// <returns>true when the SMU acknowledged (RSP == 1).</returns>
    public bool SetCurveOptimizer(int offset, out uint rsp)
    {
        if (offset > 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "CO offset must be -50..0 (negative = undervolt)");
        int magnitude = Math.Abs(offset);
        if (magnitude > 50)
            throw new ArgumentOutOfRangeException(nameof(offset), "CO offset must be within -50..0");

        uint encoded = 0x100000u - (uint)magnitude;
        rsp = SendRsmu(MsgSetCoAll, encoded);
        return rsp == 1;
    }

    /// <summary>Convenience wrapper that throws on failure.</summary>
    public void ApplyCurveOptimizer(int offset)
    {
        if (!SetCurveOptimizer(offset, out var rsp))
            throw new InvalidOperationException($"SMU rejected set-coall (RSP=0x{rsp:X})");
    }
}
