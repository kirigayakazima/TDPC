namespace Jiaolong.Core.Wmi;

/// <summary>Mechrevo firmware command ids (recovered from the official console IL).</summary>
public enum WmiCommand : byte
{
    SystemPerMode = 0x08,        // 0 balance / 1 performance / 2 quiet
    GPUMode = 0x09,              // 0 hybrid / 1 discrete(dGPU direct)
    RGBKeyboardStatus = 0x0A,
    FnLock = 0x0B,
    TPLock = 0x0C,
    CPUGPUFanSpeed = 0x0D,       // READ: u16@4 cpu rpm, u16@6 gpu rpm
    Ambientlight = 0x0F,
    RGBKeyboardMode = 0x10,
    RGBKeyboardColor = 0x11,
    RGBKeyboardBrightness = 0x12,
    SystemAcType = 0x13,         // 1 type-c / 2 barrel
    MaxFanSpeedSwitch = 0x14,    // 1 = limit enabled
    MaxFanSpeed = 0x15,          // per-mode RPM cap: actual = min(biosCurve, value*100)
    CPUThermometer = 0x16,       // cpu temperature in C
    CPUPower = 0x17,             // sub: 0x02 SPL / 0x03 SPPT / 0x04 temp wall / byte4 = custom-mode state
}

public static class WmiProtocol
{
    public const byte Get = 0xFA;
    public const byte Set = 0xFB;
    public const int PacketSize = 32;

    public static byte[] MakePacket(byte type, byte command, ReadOnlySpan<byte> payload)
    {
        var pkt = new byte[PacketSize];
        pkt[1] = type;
        pkt[3] = command;
        int n = Math.Min(payload.Length, PacketSize - 4);
        payload[..n].CopyTo(pkt.AsSpan(4));
        return pkt;
    }

    public static int ReadU16(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8);
}

/// <summary>
/// Independent ACPI-WMI client for the Mechrevo <c>MICommonInterface</c> device
/// (<c>root\WMI</c>, <c>MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'</c>, method
/// <c>MiInterface</c>, in <c>InData</c> / out <c>OutData</c>, both 32-byte packets).
///
/// Needs Administrator.  Verified on Jiaolong 16PRO 2023 (MRID6-23, BIOS V35): 14/14 commands read.
/// Transport is a persistent System.Management bridge - see <see cref="PowerShellWmi"/> for why
/// the raw COM route was abandoned.
/// </summary>
public sealed class JiaolongWmi : IDisposable
{
    private readonly PowerShellWmi _inner;

    public JiaolongWmi(string? bridgeScriptPath = null) =>
        _inner = new PowerShellWmi(bridgeScriptPath);

    public byte[] Exchange(byte[] packet) => _inner.Exchange(packet);

    public byte[] Get(WmiCommand cmd) => _inner.Get(cmd);

    public void Set(WmiCommand cmd, params byte[] payload) => _inner.Set(cmd, payload);

    /// <summary>Current RPM of both fans.</summary>
    public (int Cpu, int Gpu) ReadFanRpm() => _inner.ReadFanRpm();

    public int ReadCpuTemp() => _inner.ReadCpuTemp();

    /// <summary>Arbitrary WQL through the same bridge (e.g. Win32_Processor for clocks/load).</summary>
    public string[][] Query(string wql) => _inner.Query(wql);

    /// <summary>1 when the third extra mode (custom) is active - byte4 of CPUPower.</summary>
    public int ReadCustomModeState() => Get(WmiCommand.CPUPower)[4];

    public void Dispose() => _inner.Dispose();
}
