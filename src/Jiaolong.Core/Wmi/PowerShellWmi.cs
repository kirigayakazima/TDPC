using System.Diagnostics;
using System.Text;

namespace Jiaolong.Core.Wmi;

/// <summary>
/// WMI transport that talks to one long-lived <c>powershell.exe</c> running
/// <c>WmiBridge.ps1</c> (System.Management based).
///
/// Why a subprocess at all: on this machine <c>CLSID_WbemLocator</c> is genuinely not
/// registered, so hand-written COM IWbemLocator cannot be created (verified: CoCreateInstance
/// and Activator both return 0x80040154, and none of the 5 wbemprox locator coclasses exposes
/// IID_IWbemLocator).  <c>System.Management</c> however works fine inside Windows PowerShell,
/// and a persistent process keeps the cost to ~10-30 ms per call.
/// </summary>
public sealed class PowerShellWmi : IDisposable
{
    private readonly Process _ps;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly object _gate = new();
    private bool _disposed;

    public PowerShellWmi(string? bridgeScriptPath = null, int timeoutMs = 8000)
    {
        string script = bridgeScriptPath ?? Path.Combine(AppContext.BaseDirectory, "WmiBridge.ps1");
        if (!File.Exists(script))
            throw new FileNotFoundException("WMI bridge script not found", script);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NoLogo -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.ASCII,
            StandardInputEncoding = Encoding.ASCII,
            StandardErrorEncoding = Encoding.ASCII,
        };

        _ps = Process.Start(psi) ?? throw new InvalidOperationException("cannot start powershell bridge");
        _stdin = _ps.StandardInput;
        _stdout = _ps.StandardOutput;

        string hello = ReadLine(timeoutMs);
        if (!hello.StartsWith("OK", StringComparison.Ordinal))
            throw new InvalidOperationException("bridge handshake failed: " + hello);
    }

    /// <summary>Send one 32-byte packet, return the 32-byte reply.</summary>
    public byte[] Exchange(byte[] packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.Length != WmiProtocol.PacketSize)
            throw new ArgumentException($"packet must be {WmiProtocol.PacketSize} bytes");

        string verb = packet[1] == WmiProtocol.Set ? "SET" : "GET";
        var parts = new List<string>(2 + 32) { verb, packet[3].ToString("X2") };
        if (verb == "SET")
        {
            for (int i = 4; i < packet.Length; i++) parts.Add(packet[i].ToString("X2"));
        }
        return RoundTrip(string.Join(' ', parts));
    }

    public byte[] Get(WmiCommand cmd) => Exchange(WmiProtocol.MakePacket(WmiProtocol.Get, (byte)cmd, ReadOnlySpan<byte>.Empty));

    public void Set(WmiCommand cmd, params byte[] payload) =>
        Exchange(WmiProtocol.MakePacket(WmiProtocol.Set, (byte)cmd, payload));

    public (int Cpu, int Gpu) ReadFanRpm()
    {
        var d = Get(WmiCommand.CPUGPUFanSpeed);
        return (WmiProtocol.ReadU16(d, 4), WmiProtocol.ReadU16(d, 6));
    }

    public int ReadCpuTemp() => Get(WmiCommand.CPUThermometer)[4];

    /// <summary>
    /// Run an arbitrary WQL query through the same bridge.
    /// Returns one string[] per row (field order == SELECT order). Empty when nothing matched.
    /// </summary>
    public string[][] Query(string wql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string request = "WQL " + Uri.EscapeDataString(wql);
        string payload = RoundTripRaw(request);
        if (payload.Length == 0) return Array.Empty<string[]>();

        return Uri.UnescapeDataString(payload)
                  .Split(';', StringSplitOptions.RemoveEmptyEntries)
                  .Select(row => row.Split('~', StringSplitOptions.None))
                  .ToArray();
    }

    private byte[] RoundTrip(string request)
    {
        string hex = RoundTripRaw(request);
        var outBytes = new byte[hex.Length / 2];
        for (int i = 0; i < outBytes.Length; i++)
            outBytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return outBytes;
    }

    /// <summary>Sends a raw protocol line and returns everything after "OK ".</summary>
    private string RoundTripRaw(string request)
    {
        lock (_gate)
        {
            _stdin.WriteLine(request);
            _stdin.Flush();
            string line = ReadLine(8000);
            if (line.StartsWith("OK ", StringComparison.Ordinal))
                return line[3..].Trim();
            if (line.StartsWith("OK", StringComparison.Ordinal))
                return string.Empty;
            throw new InvalidOperationException("WMI bridge: " + line);
        }
    }

    private string ReadLine(int timeoutMs)
    {
        var task = Task.Run(() => _stdout.ReadLine() ?? string.Empty);
        if (!task.Wait(timeoutMs))
            throw new TimeoutException("WMI bridge did not answer within " + timeoutMs + " ms");
        return task.Result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stdin.WriteLine("QUIT"); _stdin.Flush(); } catch { /* already gone */ }
        try
        {
            if (!_ps.WaitForExit(1500)) _ps.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
        _ps.Dispose();
        _stdin.Dispose();
        _stdout.Dispose();
    }
}
