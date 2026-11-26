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
    private readonly string _scriptPath;
    private readonly int _timeoutMs;
    private readonly object _gate = new();
    private BridgeProcess? _bridge;
    private bool _hasStartedOnce;
    private DateTime _nextRestartUtc = DateTime.MinValue;
    private volatile bool _disposed;

    public event Action<string>? Diagnostic;

    public PowerShellWmi(string? bridgeScriptPath = null, int timeoutMs = 8000)
    {
        _scriptPath = bridgeScriptPath ?? Path.Combine(AppContext.BaseDirectory, "WmiBridge.ps1");
        if (!File.Exists(_scriptPath))
            throw new FileNotFoundException("WMI bridge script not found", _scriptPath);
        _timeoutMs = Math.Max(500, timeoutMs);

        _bridge = new BridgeProcess(_scriptPath, _timeoutMs);
        _hasStartedOnce = true;
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        List<string>? diagnostics = null;
        Monitor.Enter(_gate);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_bridge is null || !_bridge.IsAlive)
                {
                    if (attempt == 0 && DateTime.UtcNow < _nextRestartUtc)
                        throw new InvalidOperationException("WMI 桥正在冷却重试中");

                    bool recovering = _hasStartedOnce || _bridge is not null;
                    TearDownBridge(graceful: false);
                    try
                    {
                        _bridge = new BridgeProcess(_scriptPath, _timeoutMs);
                        _hasStartedOnce = true;
                        _nextRestartUtc = DateTime.MinValue;
                        if (recovering)
                            (diagnostics ??= new List<string>(2)).Add("WMI 桥已自动重建并恢复");
                    }
                    catch
                    {
                        _nextRestartUtc = DateTime.UtcNow.AddSeconds(3);
                        throw;
                    }
                }

                string? line;
                try
                {
                    _bridge.WriteLine(request);
                    line = _bridge.ReadLine(_timeoutMs);
                    if (line is null)
                        throw new EndOfStreamException("WMI bridge exited unexpectedly");
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or EndOfStreamException or ObjectDisposedException)
                {
                    string? stderr = _bridge?.LastStderr;
                    TearDownBridge(graceful: false);

                    if (attempt == 0 && !_disposed)
                    {
                        string detail = string.IsNullOrEmpty(stderr) ? ex.Message : $"{ex.Message} | {stderr}";
                        (diagnostics ??= new List<string>(2)).Add($"WMI 桥异常（{detail}），正在自动重建…");
                        continue;
                    }

                    _nextRestartUtc = DateTime.UtcNow.AddSeconds(3);
                    throw;
                }

                if (line.StartsWith("OK ", StringComparison.Ordinal))
                    return line[3..].Trim();
                if (line.StartsWith("OK", StringComparison.Ordinal))
                    return string.Empty;
                throw new InvalidOperationException("WMI bridge: " + line);
            }

            throw new InvalidOperationException("WMI bridge failed after recovery attempt");
        }
        finally
        {
            Monitor.Exit(_gate);
            if (diagnostics is not null)
            {
                foreach (string message in diagnostics)
                {
                    try { Diagnostic?.Invoke(message); } catch { /* listener failure must not break transport */ }
                }
            }
        }
    }

    private void TearDownBridge(bool graceful)
    {
        var bridge = Interlocked.Exchange(ref _bridge, null);
        bridge?.Dispose(graceful);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        bool lockTaken = false;
        try
        {
            Monitor.TryEnter(_gate, 200, ref lockTaken);
            if (!lockTaken)
            {
                Volatile.Read(ref _bridge)?.ForceKill();
                Monitor.Enter(_gate, ref lockTaken);
            }
            TearDownBridge(graceful: true);
        }
        finally
        {
            if (lockTaken) Monitor.Exit(_gate);
        }
    }

    private sealed class BridgeProcess
    {
        private readonly Process _ps;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private Task<string?>? _pendingRead;
        private volatile string? _lastStderr;
        private int _disposedFlag;

        public BridgeProcess(string scriptPath, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NoLogo -ExecutionPolicy Bypass -File \"{scriptPath}\"",
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
            _ps.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _lastStderr = e.Data.Trim();
            };
            _ps.BeginErrorReadLine();

            try
            {
                string? hello = ReadLine(timeoutMs);
                if (hello is null || !hello.StartsWith("OK", StringComparison.Ordinal))
                {
                    string detail = string.IsNullOrEmpty(_lastStderr) ? (hello ?? "<eof>") : $"{hello ?? "<eof>"} | {_lastStderr}";
                    throw new InvalidOperationException("bridge handshake failed: " + detail);
                }
            }
            catch
            {
                Dispose(graceful: false);
                throw;
            }
        }

        public bool IsAlive
        {
            get
            {
                if (Volatile.Read(ref _disposedFlag) != 0) return false;
                try { return !_ps.HasExited; }
                catch { return false; }
            }
        }

        public string? LastStderr => _lastStderr;

        public void WriteLine(string line)
        {
            _stdin.WriteLine(line);
            _stdin.Flush();
        }

        public string? ReadLine(int timeoutMs)
        {
            if (_pendingRead is { IsCompleted: false })
                throw new IOException("WMI bridge stream has an active pending read");

            var task = Task.Run(() => _stdout.ReadLine());
            _pendingRead = task;
            if (!task.Wait(timeoutMs))
                throw new TimeoutException("WMI bridge did not answer within " + timeoutMs + " ms");

            _pendingRead = null;
            return task.GetAwaiter().GetResult();
        }

        public void ForceKill()
        {
            try
            {
                if (!_ps.HasExited) _ps.Kill(entireProcessTree: true);
            }
            catch { /* process already gone */ }
        }

        public void Dispose(bool graceful)
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

            if (graceful)
            {
                try
                {
                    if (!_ps.HasExited)
                    {
                        _stdin.WriteLine("QUIT");
                        _stdin.Flush();
                        _ps.WaitForExit(1500);
                    }
                }
                catch { /* fall through to kill */ }
            }

            ForceKill();
            try { _ps.WaitForExit(1500); } catch { /* best effort */ }

            // Waiting briefly after killing guarantees the child's stdout hits EOF
            // before we dispose the StreamReader underneath the background read task.
            try { _pendingRead?.Wait(500); } catch { /* ignore read errors on teardown */ }
            _pendingRead = null;

            try { _stdin.Dispose(); } catch { }
            try { _stdout.Dispose(); } catch { }
            try { _ps.Dispose(); } catch { }
        }
    }
}
