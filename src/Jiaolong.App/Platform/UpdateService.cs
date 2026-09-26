namespace Jiaolong.App.Platform;

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

public sealed record UpdateInfo(
    bool HasUpdate,
    string CurrentVersionStr,
    string LatestVersionStr,
    string ReleaseTitle,
    string ReleaseNotes,
    string DownloadUrl,
    long AssetSizeBytes
);

public static class UpdateService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/kirigayakazima/TDPC/releases/latest";
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string CurrentVersionString => $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// <summary>
    /// Checks GitHub for the latest release. Returns null on network failure or if up-to-date.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
            req.Headers.UserAgent.ParseAdd($"TDPC-Client/{CurrentVersionString}");
            req.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            string releaseTitle = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : tagName;
            string releaseNotes = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";

            // Parse version number (e.g. "v1.0.1" -> 1.0.1)
            string cleanTag = tagName.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(cleanTag, out var latestVer))
            {
                // Fallback for short versions like "1.0"
                if (Version.TryParse(cleanTag + ".0", out var shortVer))
                    latestVer = shortVer;
                else
                    return null;
            }

            // Find zip asset download url
            string downloadUrl = "";
            long assetSize = 0;
            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                        assetSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                        break;
                    }
                }
            }

            bool hasUpdate = latestVer > CurrentVersion;

            return new UpdateInfo(
                HasUpdate: hasUpdate,
                CurrentVersionStr: CurrentVersionString,
                LatestVersionStr: $"v{latestVer.Major}.{latestVer.Minor}.{latestVer.Build}",
                ReleaseTitle: releaseTitle,
                ReleaseNotes: releaseNotes,
                DownloadUrl: downloadUrl,
                AssetSizeBytes: assetSize
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the update zip package and seamlessly replaces the running application.
    /// </summary>
    public static async Task DownloadAndApplyUpdateAsync(
        UpdateInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
            throw new InvalidOperationException("未找到可用的更新下载地址");

        string tempRoot = Path.Combine(Path.GetTempPath(), "TDPC_Update");
        if (Directory.Exists(tempRoot))
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
        Directory.CreateDirectory(tempRoot);

        string zipFile = Path.Combine(tempRoot, "update.zip");
        string extractDir = Path.Combine(tempRoot, "files");
        Directory.CreateDirectory(extractDir);

        // 1. Download file with progress reporting
        using (var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? info.AssetSizeBytes;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(zipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[16384];
            long readBytes = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                readBytes += bytesRead;
                if (totalBytes > 0)
                {
                    progress?.Report(Math.Clamp((double)readBytes / totalBytes * 100.0, 0, 100));
                }
            }
        }

        // 2. Extract zip package
        ZipFile.ExtractToDirectory(zipFile, extractDir, overwriteFiles: true);

        // In case zip contains a nested folder, resolve inner directory
        string deploySourceDir = extractDir;
        if (!File.Exists(Path.Combine(deploySourceDir, "TDPC.exe")))
        {
            var subDirs = Directory.GetDirectories(extractDir);
            if (subDirs.Length == 1 && File.Exists(Path.Combine(subDirs[0], "TDPC.exe")))
            {
                deploySourceDir = subDirs[0];
            }
        }

        if (!File.Exists(Path.Combine(deploySourceDir, "TDPC.exe")))
        {
            throw new FileNotFoundException("更新包内未找到 TDPC.exe，更新已中止");
        }

        // 3. Prepare replacement script
        string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        int currentPid = Environment.ProcessId;
        string scriptPath = Path.Combine(tempRoot, "apply.ps1");

        // The script waits 0.6s for TDPC to terminate, copies new files, launches new TDPC, and cleans temp
        string scriptContent = $@"
Start-Sleep -Milliseconds 600
Stop-Process -Id {currentPid} -Force -ErrorAction SilentlyContinue
Copy-Item -Path '{deploySourceDir}\*' -Destination '{appDir}' -Recurse -Force
Start-Process -FilePath '{appDir}\TDPC.exe'
Start-Sleep -Seconds 3
Remove-Item -Path '{tempRoot}' -Recurse -Force -ErrorAction SilentlyContinue
";
        await File.WriteAllTextAsync(scriptPath, scriptContent, ct);

        // 4. Launch updater script in hidden window
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true
        });

        // 5. Exit current application gracefully
        Application.Current.Dispatcher.Invoke(() =>
        {
            Application.Current.Shutdown();
        });
    }
}
