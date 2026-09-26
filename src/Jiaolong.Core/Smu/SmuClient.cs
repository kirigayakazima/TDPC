using System.Runtime.InteropServices;

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

    private readonly object _gate = new();
    private bool _driverReady;

    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern bool InitializeOls();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern void DeinitializeOls();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern uint GetDllStatus();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WritePciConfigDword(ushort pciAddress, ushort regAddress, uint value);
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint ReadPciConfigDword(ushort pciAddress, ushort regAddress);

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

    private uint ReadReg(uint addr)
    {
        WritePciConfigDword(PciAddr, OffIndex, addr);
        return ReadPciConfigDword(PciAddr, OffData);
    }

    private bool WaitDone(uint rsp)
    {
        for (int i = Retry; i > 0; i--)
            if (ReadReg(rsp) == 1) return true;
        return false;
    }

    /// <summary>
    /// Full SMU transaction: mutex -> clear MSG -> write args -> write msg -> wait -> read args.
    /// Returns the final RSP register value (1 == SMU acknowledged the command).
    /// </summary>
    private uint SendMsg(uint msgAddr, uint rspAddr, uint argAddr, uint msg, uint[] args)
    {
        lock (_gate)
        {
            // 1. clear / kick the message register, retrying until the SMU accepts it
            bool kicked = false;
            for (int i = Retry; i > 0 && !kicked; i--) kicked = WriteReg(msgAddr, 0);
            if (!kicked) { ReadReg(rspAddr); return 0; }

            // 2. arguments: allocate fixed 6-slot buffer and write all slots (zero-fill unused)
            //    so the SMU sees a clean register state
            var buffer = new uint[6];
            int count = Math.Min(args.Length, buffer.Length);
            for (int i = 0; i < count; i++) buffer[i] = args[i];
            for (int i = 0; i < buffer.Length; i++) WriteReg(argAddr + (uint)(i * 4), buffer[i]);

            // 3. fire the message
            WriteReg(msgAddr, msg);

            // 4. wait for completion
            if (!WaitDone(rspAddr)) { ReadReg(rspAddr); return 0; }

            // 5. read results back
            for (int i = 0; i < args.Length; i++) args[i] = ReadReg(argAddr + (uint)(i * 4));

            return ReadReg(rspAddr);
        }
    }

    /// <summary>Runs a command on the MP1 mailbox.</summary>
    public uint SendMp1(uint msg, params uint[] args) => SendMsg(Mp1Msg, Mp1Rsp, Mp1Arg, msg, args);

    /// <summary>
    /// Runs a command on the RSMU/PSMU mailbox (e.g. Curve Optimizer / OC commands).
    /// </summary>
    public uint SendRsmu(uint msg, params uint[] args) => SendMsg(PsmuMsg, PsmuRsp, PsmuArg, msg, args);

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
