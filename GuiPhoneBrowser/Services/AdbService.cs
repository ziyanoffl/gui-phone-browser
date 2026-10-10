using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GuiPhoneBrowser.Models;

namespace GuiPhoneBrowser.Services
{
    public class AdbService
    {
        private const string BundledPlatformToolsRevision = "37.0.1";
        private string? _customAdbPath;

        public class AdbResult
        {
            public int ExitCode { get; set; }
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;
            public bool Success => ExitCode == 0;
        }

        public string? CustomAdbPath
        {
            get => _customAdbPath;
            set => _customAdbPath = value;
        }

        public string? GetAdbPath()
        {
            if (!string.IsNullOrWhiteSpace(_customAdbPath) && File.Exists(_customAdbPath))
            {
                return _customAdbPath;
            }

            string? bundledAdb = ExtractBundledAdb();
            if (!string.IsNullOrEmpty(bundledAdb)) return bundledAdb;

            // 1. Look in base application directory / platform-tools
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string inSubfolder = Path.Combine(baseDir, "platform-tools", "adb.exe");
            if (File.Exists(inSubfolder)) return inSubfolder;

            string inBase = Path.Combine(baseDir, "adb.exe");
            if (File.Exists(inBase)) return inBase;

            // 2. Relative to working directory
            string relPt = Path.Combine(Environment.CurrentDirectory, "platform-tools", "adb.exe");
            if (File.Exists(relPt)) return relPt;

            // 3. Look in system PATH
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(segment.Trim(), "adb.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }

            return null;
        }

        private static string? ExtractBundledAdb()
        {
            string destinationDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GuiPhoneBrowser", "platform-tools-" + BundledPlatformToolsRevision);
            string[] files = { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll" };

            try
            {
                Directory.CreateDirectory(destinationDirectory);
                Assembly assembly = typeof(AdbService).Assembly;

                foreach (string file in files)
                {
                    string destination = Path.Combine(destinationDirectory, file);
                    if (File.Exists(destination)) continue;

                    using Stream? resource = assembly.GetManifestResourceStream($"GuiPhoneBrowser.platform-tools.{file}");
                    if (resource == null) return null;

                    string temporary = destination + ".tmp";
                    using (var output = File.Create(temporary))
                    {
                        resource.CopyTo(output);
                    }
                    File.Move(temporary, destination, overwrite: true);
                }

                string adbPath = Path.Combine(destinationDirectory, "adb.exe");
                return File.Exists(adbPath) ? adbPath : null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<AdbResult> ExecuteAsync(string arguments, CancellationToken cancellationToken = default, int timeoutMs = 60000)
        {
            string? adb = GetAdbPath();
            if (string.IsNullOrEmpty(adb))
            {
                return new AdbResult
                {
                    ExitCode = -1,
                    StdErr = "adb.exe was not found. Please verify platform-tools is available."
                };
            }

            var psi = new ProcessStartInfo
            {
                FileName = adb,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };

            try
            {
                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var waitForExitTask = process.WaitForExitAsync(linkedCts.Token);

                await Task.WhenAll(stdoutTask, stderrTask, waitForExitTask);

                return new AdbResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = await stdoutTask,
                    StdErr = await stderrTask
                };
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }

                return new AdbResult
                {
                    ExitCode = -99,
                    StdErr = "Operation was cancelled or timed out."
                };
            }
            catch (Exception ex)
            {
                return new AdbResult
                {
                    ExitCode = -1,
                    StdErr = $"ADB execution error: {ex.Message}"
                };
            }
        }

        public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            var devices = new List<DeviceInfo>();
            var result = await ExecuteAsync("devices -l", cancellationToken, 10000);

            if (!result.Success && string.IsNullOrEmpty(result.StdOut))
            {
                return devices;
            }

            string[] lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("* daemon", StringComparison.OrdinalIgnoreCase)) continue;

                // Example: RF8M30XYZ device product:a52sxq model:SM_A528B device:a52sxq transport_id:1
                // Or: 123456789 unauthorized transport_id:2
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var dev = new DeviceInfo
                {
                    Serial = parts[0],
                    State = parts[1]
                };

                for (int i = 2; i < parts.Length; i++)
                {
                    string p = parts[i];
                    int colon = p.IndexOf(':');
                    if (colon > 0 && colon < p.Length - 1)
                    {
                        string key = p.Substring(0, colon);
                        string val = p.Substring(colon + 1);
                        if (key.Equals("model", StringComparison.OrdinalIgnoreCase)) dev.Model = val;
                        else if (key.Equals("product", StringComparison.OrdinalIgnoreCase)) dev.Product = val;
                        else if (key.Equals("device", StringComparison.OrdinalIgnoreCase)) dev.Device = val;
                    }
                }

                devices.Add(dev);
            }

            return devices;
        }

        public List<LocalFileService.PathShortcut> GetPhoneShortcuts()
        {
            return new List<LocalFileService.PathShortcut>
            {
                new() { Name = "Internal Storage (/sdcard)", Path = "/sdcard" },
                new() { Name = "Camera & DCIM", Path = "/sdcard/DCIM" },
                new() { Name = "Downloads", Path = "/sdcard/Download" },
                new() { Name = "Pictures", Path = "/sdcard/Pictures" },
                new() { Name = "Documents", Path = "/sdcard/Documents" },
                new() { Name = "Music", Path = "/sdcard/Music" },
                new() { Name = "Movies", Path = "/sdcard/Movies" },
                new() { Name = "Root Storage (/storage)", Path = "/storage" }
            };
        }

        public async Task<List<FileItem>> GetDirectoryItemsAsync(string serial, string remotePath, CancellationToken cancellationToken = default)
        {
            var list = new List<FileItem>();
            string cleanPath = remotePath.TrimEnd('/');
            if (string.IsNullOrEmpty(cleanPath)) cleanPath = "/";

            // Add parent directory navigation if not root
            if (cleanPath != "/" && !string.IsNullOrEmpty(cleanPath))
            {
                string parentPath = cleanPath.Substring(0, cleanPath.LastIndexOf('/'));
                if (string.IsNullOrEmpty(parentPath)) parentPath = "/";

                list.Add(new FileItem
                {
                    Name = "..",
                    FullPath = parentPath,
                    IsDirectory = true,
                    IsParentDirectory = true
                });
            }

            // Execute ls -la in shell
            // Escaping single quotes in path
            string escaped = cleanPath.Replace("'", "'\\''");
            string cmd = $"shell ls -la '{escaped}/'";
            var result = await ExecuteAsync($"-s {serial} {cmd}", cancellationToken, 20000);

            if (!result.Success && string.IsNullOrEmpty(result.StdOut))
            {
                return list;
            }

            string[] lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Regex for Toybox/Toolbox ls -la:
            // e.g.: drwxrwx--x 12 root sdcard_rw 4096 2026-03-15 12:30 DCIM
            // Or: -rw-rw---- 1 root sdcard_rw 12456 2026-03-15 12:30 photo.jpg
            // Or: lrwxrwxrwx 1 root root 21 2026-03-15 12:30 sdcard -> /storage/emulated/0
            // Or: -rw-rw---- 1 u0_a123 u0_a123 1024 Jan 15 2025 myfile.txt
            var regexIso = new Regex(@"^([drwxsl\-]{10})\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})\s+(.+)$");
            var regexStandard = new Regex(@"^([drwxsl\-]{10})\s+\d+\s+\S+\s+\S+\s+(\d+)\s+([A-Za-z]{3}\s+\d{1,2}\s+(?:\d{4}|\d{2}:\d{2}))\s+(.+)$");

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;

                string permissions = string.Empty;
                long size = 0;
                bool sizeKnown = false;
                DateTime? modified = null;
                string name = string.Empty;
                bool isDir = false;

                var match = regexIso.Match(line);
                if (match.Success)
                {
                    permissions = match.Groups[1].Value;
                    sizeKnown = long.TryParse(match.Groups[2].Value, out size);
                    if (DateTime.TryParseExact(match.Groups[3].Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    {
                        modified = dt;
                    }
                    name = match.Groups[4].Value;
                }
                else
                {
                    match = regexStandard.Match(line);
                    if (match.Success)
                    {
                        permissions = match.Groups[1].Value;
                        sizeKnown = long.TryParse(match.Groups[2].Value, out size);
                        string dateStr = match.Groups[3].Value;
                        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        {
                            modified = dt;
                        }
                        name = match.Groups[4].Value;
                    }
                    else
                    {
                        // Fallback manual parse if line starts with permissions
                        if (line.Length > 10 && (line[0] == '-' || line[0] == 'd' || line[0] == 'l' || line[0] == 's'))
                        {
                            permissions = line.Substring(0, 10);
                            string remaining = line.Substring(10).Trim();
                            var tokens = remaining.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (tokens.Length >= 5)
                            {
                                sizeKnown = long.TryParse(tokens[3], out size);
                                name = string.Join(" ", tokens.Skip(5));
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(name)) continue;

                // Handle symlink arrow "name -> /target"
                int arrow = name.IndexOf("->", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    name = name.Substring(0, arrow).Trim();
                }

                if (name == "." || name == "..") continue;

                isDir = permissions.StartsWith("d") || permissions.StartsWith("l");

                string fullItemPath = cleanPath == "/" ? $"/{name}" : $"{cleanPath}/{name}";

                list.Add(new FileItem
                {
                    Name = name,
                    FullPath = fullItemPath,
                    IsDirectory = isDir,
                    Size = isDir ? 0 : size,
                    IsSizeKnown = isDir || sizeKnown,
                    ModifiedTime = modified,
                    Permissions = permissions
                });
            }

            // Return directories first, then files sorted by name
            return list.OrderBy(x => !x.IsParentDirectory)
                       .ThenBy(x => !x.IsDirectory)
                       .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        public async Task<AdbResult> PullFileAsync(string serial, string remotePath, string localDestPath, CancellationToken cancellationToken = default)
        {
            // Note: adb pull -a preserves timestamps and permissions
            string escapedRemote = remotePath.Replace("\"", "\\\"");
            string escapedLocal = localDestPath.Replace("\"", "\\\"");
            string cmd = $"-s {serial} pull -a \"{escapedRemote}\" \"{escapedLocal}\"";
            return await ExecuteAsync(cmd, cancellationToken, 600000);
        }

        public async Task<AdbResult> PushFileAsync(string serial, string localPath, string remoteDestDir, CancellationToken cancellationToken = default)
        {
            string escapedLocal = localPath.Replace("\"", "\\\"");
            string escapedRemote = remoteDestDir.Replace("\"", "\\\"");
            string cmd = $"-s {serial} push \"{escapedLocal}\" \"{escapedRemote}/\"";
            return await ExecuteAsync(cmd, cancellationToken, 600000);
        }

        public async Task<bool> TouchFileAsync(string serial, string remoteFilePath, DateTime time, CancellationToken cancellationToken = default)
        {
            // Include an explicit UTC offset so the phone's local timezone cannot shift the date.
            string dateStr = time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            string escaped = remoteFilePath.Replace("'", "'\\''");
            string cmd = $"-s {serial} shell touch -d '{dateStr}' '{escaped}'";
            var result = await ExecuteAsync(cmd, cancellationToken, 10000);
            return result.Success;
        }

        public async Task<AdbResult> DeleteFileAsync(string serial, string remotePath, CancellationToken cancellationToken = default)
        {
            string escaped = remotePath.Replace("'", "'\\''");
            string cmd = $"-s {serial} shell rm -f '{escaped}'";
            return await ExecuteAsync(cmd, cancellationToken, 15000);
        }

        public async Task<AdbResult> DeleteDirectoryAsync(string serial, string remotePath, CancellationToken cancellationToken = default)
        {
            string escaped = remotePath.Replace("'", "'\\''");
            string cmd = $"-s {serial} shell rm -rf '{escaped}'";
            return await ExecuteAsync(cmd, cancellationToken, 30000);
        }

        public async Task<AdbResult> CreateDirectoryAsync(string serial, string remotePath, CancellationToken cancellationToken = default)
        {
            string escaped = remotePath.Replace("'", "'\\''");
            string cmd = $"-s {serial} shell mkdir -p '{escaped}'";
            return await ExecuteAsync(cmd, cancellationToken, 15000);
        }

        public async Task<long?> GetRemoteFileSizeAsync(string serial, string remotePath, CancellationToken cancellationToken = default)
        {
            string escaped = remotePath.Replace("'", "'\\''");
            string cmd = $"-s {serial} shell stat -c '%s' '{escaped}'";
            var result = await ExecuteAsync(cmd, cancellationToken, 10000);
            if (result.Success && long.TryParse(result.StdOut.Trim(), out long size))
            {
                return size;
            }
            return null;
        }

        public async Task<DateTime?> GetRemoteFileModifiedTimeUtcAsync(string serial, string remotePath, CancellationToken cancellationToken = default)
        {
            string escaped = remotePath.Replace("'", "'\\''");
            string cmd = $"-s {serial} shell stat -c '%Y' '{escaped}'";
            var result = await ExecuteAsync(cmd, cancellationToken, 10000);
            if (result.Success && long.TryParse(result.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSeconds))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            return null;
        }
    }
}
