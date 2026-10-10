using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using GuiPhoneBrowser.Models;
using GuiPhoneBrowser.Services;

namespace GuiPhoneBrowser
{
    public partial class App : Application
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        protected override async void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Contains("--test", StringComparer.OrdinalIgnoreCase))
            {
                if (AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    var stdOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    Console.SetOut(stdOut);
                    var stdErr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                    Console.SetError(stdErr);
                }

                int exitCode = await RunSelfTestsAsync().ConfigureAwait(false);
                Environment.Exit(exitCode);
                return;
            }

            base.OnStartup(e);
        }

        private static async Task<int> RunSelfTestsAsync()
        {
            Console.WriteLine("=== GUI Phone Browser Basic Smoke Checks ===");
            bool allPassed = true;

            void Assert(bool condition, string testName)
            {
                if (condition)
                {
                    Console.WriteLine($"[PASS] {testName}");
                }
                else
                {
                    Console.WriteLine($"[FAIL] {testName}");
                    allPassed = false;
                }
            }

            try
            {
                // Test 1: LocalFileService operations and shortcuts
                var localService = new LocalFileService();
                var shortcuts = localService.GetShortcuts();
                Assert(shortcuts.Count > 0, "LocalFileService: Retrieves system drives and shortcuts");

                // Test 2: LocalFileService directory listing
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                var items = await localService.GetDirectoryItemsAsync(currentDir);
                Assert(items.Count > 0, "LocalFileService: Lists directory items");

                // Test 3: Timestamp preservation
                string tempTestFile = Path.Combine(Path.GetTempPath(), $"gpb_test_{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(tempTestFile, "Timestamp preservation test payload.");
                var expectedTime = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Utc);
                bool timeSet = localService.SetFileTimestamp(tempTestFile, expectedTime);
                Assert(timeSet, "LocalFileService: SetFileTimestamp succeeds");

                var actualTime = File.GetLastWriteTimeUtc(tempTestFile);
                bool timeMatches = Math.Abs((actualTime - expectedTime).TotalSeconds) < 2.0;
                Assert(timeMatches, $"LocalFileService: Preserved timestamp matches expected ({actualTime} vs {expectedTime})");

                long reportedSize = localService.GetFileSize(tempTestFile);
                Assert(reportedSize > 0, $"LocalFileService: Correct file size reported ({reportedSize} bytes)");

                bool deleted = localService.DeleteItem(new FileItem { FullPath = tempTestFile, IsDirectory = false });
                Assert(deleted && !File.Exists(tempTestFile), "LocalFileService: Test file deleted safely");

                // Test 4: AdbService detection and execution
                var adbService = new AdbService();
                string? adbPath = adbService.GetAdbPath();
                Assert(!string.IsNullOrEmpty(adbPath) && File.Exists(adbPath), $"AdbService: Detected ADB at '{adbPath}'");

                var verResult = await adbService.ExecuteAsync("version");
                Assert(verResult.Success && verResult.StdOut.Contains("Android Debug Bridge"), "AdbService: Executes adb version successfully");

                var devices = await adbService.GetDevicesAsync();
                Assert(devices != null, "AdbService: GetDevicesAsync executes without exception");

                // Test 5: AdbService phone shortcuts
                var phoneShortcuts = adbService.GetPhoneShortcuts();
                Assert(phoneShortcuts.Any(s => s.Path == "/sdcard"), "AdbService: Phone shortcuts include /sdcard");

                // Test 6: Transfer manager construction only; no transfer or move is simulated.
                var transferManager = new TransferManager(adbService, localService);
                Assert(transferManager != null, "TransferManager: Initializes (transfer behavior not tested)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRITICAL EXCEPTION] {ex}");
                allPassed = false;
            }

            Console.WriteLine(allPassed ? "=== ALL SMOKE CHECKS PASSED ===" : "=== SOME SMOKE CHECKS FAILED ===");
            return allPassed ? 0 : 1;
        }
    }
}
