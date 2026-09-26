using System.Runtime.InteropServices;

namespace Jiaolong.Core.Ec;

/// <summary>
/// Embedded-controller access for MECHREVO Jiaolong via the WinRing0 / OpenLibSys driver.
///
/// Protocol recovered byte-by-byte from the third-party console IL (6.6 build,
/// <c>RyzenSmu.Smu::{ECStatus, Read_EC_EX, Write_EC_EX}</c>).  The 7.3 build shipped a
/// broken <c>Read_EC_EX</c> (never issued a read); 6.6 is correct and is what we mirror here.
///
/// Two independent port pairs are used:
///   * standard EC   : 0x66 (status/cmd) + 0x62 (data), cmds RD_EC=0x80 / WR_EC=0x81, 8-bit address
///   * extended EC   : 0x4E (index)     + 0x4F (data), regs 0x2E=command, 0x2F=operand,
///                     command 0x10 = address low, 0x11 = address high, 0x12 = data phase
///
/// WARNING: writes here bypass the BIOS fan policy entirely (fail-closed: the EC keeps the
/// last value until it is explicitly cleared or loses power).
/// </summary>
public sealed class EcAccess : IDisposable
{
    // ---- ports / bits -------------------------------------------------
    private const ushort EC_SC = 0x66;
    private const ushort EC_DATA = 0x62;
    private const byte EC_OBF = 0x01;
    private const byte EC_IBF = 0x02;
    private const byte RD_EC = 0x80;
    private const byte WR_EC = 0x81;

    private const ushort EC_ADDR_PORT = 0x4E;
    private const ushort EC_DATA_PORT = 0x4F;

    private const byte REG_IDX_CMD = 0x2E;
    private const byte REG_IDX_VAL = 0x2F;
    private const byte CMD_ADDR_LO = 0x10;
    private const byte CMD_ADDR_HI = 0x11;
    private const byte CMD_DATA = 0x12;

    // ---- well known registers -----------------------------------------
    public const ushort FanSpeedCpu = 0xC83C;
    public const ushort FanSpeedGpu = 0xC83D;
    public const ushort FanControl = 0x0B20;   // bit0x02 = manual fan mode
    public const ushort ChipId1 = 0x2000;      // expected 0x55 -- read oracle
    public const ushort ChipId2 = 0x2001;
    public const ushort ChipVer = 0x1060;

    // ---- OpenLibSys P/Invoke -----------------------------------------
    private const string Ols = "WinRing0x64.dll";

    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern bool InitializeOls();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern void DeinitializeOls();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern uint GetDllStatus();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern uint GetDriverVersion();
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern byte ReadIoPortByte(ushort port);
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern void WriteIoPortByte(ushort port, byte value);
    [DllImport(Ols, CallingConvention = CallingConvention.Cdecl)] private static extern bool IsMsr();

    private bool _init;
    private bool _disposed;

    public uint DllStatus { get; private set; }
    public bool DriverReady { get; private set; }

    public EcAccess()
    {
        _init = InitializeOls();
        DllStatus = GetDllStatus();
        // OLS_DLL_NO_ERROR = 0
        DriverReady = _init && DllStatus == 0;
        if (!DriverReady)
            throw new InvalidOperationException(
                $"InitializeOls failed (DllStatus=0x{DllStatus:X}). " +
                "Needs Administrator and the WinRing0_1_2_0 driver service.");
    }

    /// <summary>Waits until the EC accepts/rejects more input. Mirrors Smu::ECStatus exactly.</summary>
    private bool EcStatus(byte flag)
    {
        bool invert = flag == EC_OBF;      // OBF: ready when SET; IBF: ready when CLEAR
        for (int i = 0; i < 100; i++)
        {
            byte st = ReadIoPortByte(EC_SC);
            int v = invert ? (~st & 0xFF) : st;
            if ((v & flag) == 0) return true;
        }
        return false;
    }

    private void Select(byte index) => WriteIoPortByte(EC_ADDR_PORT, index);
    private void PutVal(byte value) => WriteIoPortByte(EC_DATA_PORT, value);
    private byte GetVal() => ReadIoPortByte(EC_DATA_PORT);

    /// <summary>16-bit EC register read (6.6 implementation, verified against ChipId1 = 0x55).</summary>
    public byte Read16(ushort address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EcStatus(EC_IBF)) return 0;

        Select(REG_IDX_CMD); PutVal(CMD_ADDR_HI);
        Select(REG_IDX_VAL); PutVal((byte)(address >> 8));
        Select(REG_IDX_CMD); PutVal(CMD_ADDR_LO);
        Select(REG_IDX_VAL); PutVal((byte)(address & 0xFF));
        Select(REG_IDX_CMD); PutVal(CMD_DATA);
        Select(REG_IDX_VAL);
        return GetVal();
    }

    /// <summary>16-bit EC register write (write path; use sparingly).</summary>
    public bool Write16(ushort address, byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EcStatus(EC_IBF)) return false;

        Select(REG_IDX_CMD); PutVal(CMD_ADDR_HI);
        Select(REG_IDX_VAL); PutVal((byte)(address >> 8));
        Select(REG_IDX_CMD); PutVal(CMD_ADDR_LO);
        Select(REG_IDX_VAL); PutVal((byte)(address & 0xFF));
        Select(REG_IDX_CMD); PutVal(CMD_DATA);
        Select(REG_IDX_VAL); PutVal(value);
        return true;
    }

    /// <summary>Standard 8-bit EC read (0x66/0x62 + RD_EC). Kept for completeness.</summary>
    public byte Read8(byte address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EcStatus(EC_IBF)) return 0;
        WriteIoPortByte(EC_SC, RD_EC);
        if (!EcStatus(EC_IBF)) return 0;
        WriteIoPortByte(EC_DATA, address);
        if (!EcStatus(EC_OBF)) return 0;
        return ReadIoPortByte(EC_DATA);
    }

    // ---- typed fan helpers -------------------------------------------
    public int ReadFanRpmCpu() => Read16(FanSpeedCpu) * 100;
    public int ReadFanRpmGpu() => Read16(FanSpeedGpu) * 100;

    /// <summary>
    /// True when the EC currently holds a fan target, i.e. manual override is active.
    ///
    /// NOTE: this deliberately does NOT look at bit 0x02 of <see cref="FanControl"/>.  The
    /// third-party tool assumes that bit is the "manual mode" flag, but an empirical test on
    /// MRID6-23 disproved it: writing 0x02 there read back as 0x19 while the fan was clearly
    /// locked.  The register behaves as a *status* mirror of the current target, not a control
    /// bit.  The two fan target registers are the reliable, directly observable indicator:
    ///   0/0   -> BIOS owns the fan
    ///   N/N   -> EC owns the fan, target = N*100 RPM
    /// </summary>
    public bool IsManualFanMode() => Read16(FanSpeedCpu) != 0 || Read16(FanSpeedGpu) != 0;

    /// <summary>
    /// Take over both fans at a fixed speed. <paramref name="value"/> is RPM/100 (18..58 = 1800..5800).
    /// 0x0B20 is touched as well (read-modify-write, mirroring the original tool) but it is not
    /// the control bit - see <see cref="IsManualFanMode"/>. Writing the two target registers is
    /// what actually makes the fan move.
    /// </summary>
    public void SetManualFanSpeed(byte value)
    {
        Write16(FanSpeedCpu, value);
        Write16(FanSpeedGpu, value);
        byte ctl = Read16(FanControl);
        Write16(FanControl, (byte)(ctl | 0x02));
    }

    /// <summary>Hand fan control back to the BIOS. Safe, idempotent, this is our fail-safe path.</summary>
    public void ReleaseToBios()
    {
        Write16(FanSpeedCpu, 0);
        Write16(FanSpeedGpu, 0);
        Write16(FanControl, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_init) DeinitializeOls();
    }
}
