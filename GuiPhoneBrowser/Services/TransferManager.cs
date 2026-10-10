using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GuiPhoneBrowser.Models;

namespace GuiPhoneBrowser.Services
{
    public class TransferManager
    {
        public enum OverwriteAction
        {
            Prompt,
            Overwrite,
            OverwriteAll,
            Skip,
            SkipAll,
            Cancel
        }

        private readonly AdbService _adbService;
        private readonly LocalFileService _localService;

        public event Action<TransferProgress>? ProgressChanged;
        public event Action<string, bool>? LogMessage; // message, isError

        public Func<string, Task<OverwriteAction>>? ConfirmOverwritePrompt { get; set; }

        public TransferManager(AdbService adbService, LocalFileService localService)
        {
            _adbService = adbService;
            _localService = localService;
        }

        private void Log(string message, bool isError = false)
        {
            LogMessage?.Invoke(message, isError);
        }

        private void Report(int currentIndex, int totalItems, string itemName, long bytesTransferred, long totalBytes, string status)
        {
            ProgressChanged?.Invoke(new TransferProgress
            {
                CurrentItemIndex = currentIndex,
                TotalItems = totalItems,
                CurrentItemName = itemName,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                StatusMessage = status
            });
        }

        public async Task TransferPhoneToPcAsync(
            string serial,
            IReadOnlyList<FileItem> phoneItems,
            string localDestDir,
            bool isMove,
            CancellationToken cancellationToken)
        {
            if (phoneItems == null || phoneItems.Count == 0) return;

            if (isMove && phoneItems.Any(item => !item.IsParentDirectory && item.IsDirectory))
            {
                Log("Folder moves are disabled until every nested file can be verified. Copy the folder, verify it, then remove the source manually.", isError: true);
                return;
            }

            long totalBytes = 0;
            foreach (var item in phoneItems) totalBytes += Math.Max(0, item.Size);

            long bytesDone = 0;
            OverwriteAction rememberAction = OverwriteAction.Prompt;

            Log($"Started {(isMove ? "MOVE" : "COPY")} from Phone to PC ({phoneItems.Count} items, total {Helpers.SizeFormatter.FormatBytes(totalBytes)})");

            for (int i = 0; i < phoneItems.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = phoneItems[i];
                if (item.IsParentDirectory) continue;

                string destPath = Path.Combine(localDestDir, item.Name);
                Report(i + 1, phoneItems.Count, item.Name, bytesDone, totalBytes, $"{(isMove ? "Moving" : "Copying")} {item.Name} to PC...");

                if (File.Exists(destPath) || Directory.Exists(destPath))
                {
                    if (rememberAction == OverwriteAction.SkipAll)
                    {
                        Log($"[SKIP] Destination exists: {destPath}");
                        bytesDone += item.Size;
                        continue;
                    }
                    if (rememberAction != OverwriteAction.OverwriteAll)
                    {
                        var choice = ConfirmOverwritePrompt != null
                            ? await ConfirmOverwritePrompt(destPath)
                            : OverwriteAction.Cancel;

                        if (choice == OverwriteAction.Cancel)
                        {
                            Log("Transfer canceled by user.");
                            return;
                        }
                        if (choice == OverwriteAction.Skip)
                        {
                            Log($"[SKIP] Destination exists: {destPath}");
                            bytesDone += item.Size;
                            continue;
                        }
                        if (choice == OverwriteAction.SkipAll)
                        {
                            rememberAction = OverwriteAction.SkipAll;
                            bytesDone += item.Size;
                            continue;
                        }
                        if (choice == OverwriteAction.OverwriteAll)
                        {
                            rememberAction = OverwriteAction.OverwriteAll;
                        }
                    }
                }

                // Execute pull with -a
                var pullResult = await _adbService.PullFileAsync(serial, item.FullPath, localDestDir, cancellationToken);
                if (!pullResult.Success)
                {
                    Log($"[ERROR] Failed to download '{item.Name}': {pullResult.StdErr}", isError: true);
                    continue;
                }

                // Verify existence and size
                bool verified = false;
                if (!item.IsDirectory)
                {
                    if (File.Exists(destPath))
                    {
                        long localSize = _localService.GetFileSize(destPath);
                        if (item.IsSizeKnown && localSize == item.Size)
                        {
                            verified = true;
                        }
                        else
                        {
                            Log($"[WARNING] Size mismatch for '{item.Name}' (Phone: {item.Size} B, PC: {localSize} B)", isError: true);
                        }

                        // adb pull -a already applies the source timestamp. Avoid rebuilding it
                        // from an ls display string, which has no timezone or sub-minute precision.
                    }
                    else
                    {
                        Log($"[ERROR] Destination file missing after pull: {destPath}", isError: true);
                    }
                }
                else
                {
                    verified = Directory.Exists(destPath);
                }

                bytesDone += item.Size;
                Report(i + 1, phoneItems.Count, item.Name, bytesDone, totalBytes, $"Completed {item.Name}");

                if (isMove)
                {
                    if (verified)
                    {
                        // Verified successfully; safe to delete source
                        var delRes = item.IsDirectory
                            ? await _adbService.DeleteDirectoryAsync(serial, item.FullPath, cancellationToken)
                            : await _adbService.DeleteFileAsync(serial, item.FullPath, cancellationToken);

                        if (delRes.Success)
                        {
                            Log($"[MOVE OK] {item.Name} verified and removed from phone.");
                        }
                        else
                        {
                            Log($"[WARNING] Copied {item.Name} successfully, but failed to delete phone source: {delRes.StdErr}", isError: true);
                        }
                    }
                    else
                    {
                        Log($"[SAFETY ABORT] Verification failed for '{item.Name}'. Phone source was NOT deleted to prevent data loss.", isError: true);
                    }
                }
                else
                {
                    Log($"[COPY OK] {item.Name} ({Helpers.SizeFormatter.FormatBytes(item.Size)}) downloaded to PC with timestamps.");
                }
            }

            Log($"Finished {(isMove ? "move" : "copy")} operation from Phone to PC.");
        }

        public async Task TransferPcToPhoneAsync(
            string serial,
            IReadOnlyList<FileItem> localItems,
            string phoneDestDir,
            bool isMove,
            CancellationToken cancellationToken)
        {
            if (localItems == null || localItems.Count == 0) return;

            if (isMove && localItems.Any(item => !item.IsParentDirectory && item.IsDirectory))
            {
                Log("Folder moves are disabled until every nested file can be verified. Copy the folder, verify it, then remove the source manually.", isError: true);
                return;
            }

            long totalBytes = 0;
            foreach (var item in localItems) totalBytes += Math.Max(0, item.Size);

            long bytesDone = 0;
            OverwriteAction rememberAction = OverwriteAction.Prompt;

            Log($"Started {(isMove ? "MOVE" : "COPY")} from PC to Phone ({localItems.Count} items, total {Helpers.SizeFormatter.FormatBytes(totalBytes)})");

            for (int i = 0; i < localItems.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = localItems[i];
                if (item.IsParentDirectory) continue;

                string cleanDestDir = phoneDestDir.TrimEnd('/');
                string remoteTarget = cleanDestDir == "/" ? $"/{item.Name}" : $"{cleanDestDir}/{item.Name}";

                Report(i + 1, localItems.Count, item.Name, bytesDone, totalBytes, $"{(isMove ? "Moving" : "Copying")} {item.Name} to Phone...");

                // Check remote existence
                long? existingSize = await _adbService.GetRemoteFileSizeAsync(serial, remoteTarget, cancellationToken);
                if (existingSize.HasValue)
                {
                    if (rememberAction == OverwriteAction.SkipAll)
                    {
                        Log($"[SKIP] Remote destination exists: {remoteTarget}");
                        bytesDone += item.Size;
                        continue;
                    }
                    if (rememberAction != OverwriteAction.OverwriteAll)
                    {
                        var choice = ConfirmOverwritePrompt != null
                            ? await ConfirmOverwritePrompt(remoteTarget)
                            : OverwriteAction.Cancel;

                        if (choice == OverwriteAction.Cancel)
                        {
                            Log("Transfer canceled by user.");
                            return;
                        }
                        if (choice == OverwriteAction.Skip)
                        {
                            Log($"[SKIP] Remote destination exists: {remoteTarget}");
                            bytesDone += item.Size;
                            continue;
                        }
                        if (choice == OverwriteAction.SkipAll)
                        {
                            rememberAction = OverwriteAction.SkipAll;
                            bytesDone += item.Size;
                            continue;
                        }
                        if (choice == OverwriteAction.OverwriteAll)
                        {
                            rememberAction = OverwriteAction.OverwriteAll;
                        }
                    }
                }

                // Execute push
                var pushResult = await _adbService.PushFileAsync(serial, item.FullPath, cleanDestDir, cancellationToken);
                if (!pushResult.Success)
                {
                    Log($"[ERROR] Failed to upload '{item.Name}': {pushResult.StdErr}", isError: true);
                    continue;
                }

                // Verify
                bool verified = false;
                if (!item.IsDirectory)
                {
                    long? remoteSize = await _adbService.GetRemoteFileSizeAsync(serial, remoteTarget, cancellationToken);
                    if (item.IsSizeKnown && remoteSize.HasValue && remoteSize.Value == item.Size)
                    {
                        verified = true;
                    }
                    else if (remoteSize.HasValue)
                    {
                        Log($"[WARNING] Remote size mismatch for '{item.Name}' (PC: {item.Size} B, Phone: {remoteSize.Value} B)", isError: true);
                    }
                    else if (!remoteSize.HasValue)
                    {
                        Log($"[WARNING] Could not verify the phone copy of '{item.Name}'.", isError: true);
                    }

                    // Attempt timestamp touch on Android
                    if (item.ModifiedTime.HasValue)
                    {
                        bool touched = await _adbService.TouchFileAsync(serial, remoteTarget, item.ModifiedTime.Value, cancellationToken);
                        DateTime? remoteModified = touched
                            ? await _adbService.GetRemoteFileModifiedTimeUtcAsync(serial, remoteTarget, cancellationToken)
                            : null;
                        DateTime expectedModifiedUtc = item.ModifiedTime.Value.ToUniversalTime();
                        bool timestampVerified = remoteModified.HasValue &&
                            Math.Abs((remoteModified.Value - expectedModifiedUtc).TotalSeconds) <= 2;
                        if (!timestampVerified)
                        {
                            Log($"[WARNING] Could not verify the modified time for '{item.Name}'.", isError: true);
                            verified = false;
                        }
                    }
                    else
                    {
                        Log($"[WARNING] No source modified time is available for '{item.Name}'.", isError: true);
                        verified = false;
                    }
                }
                else
                {
                    verified = true;
                }

                bytesDone += item.Size;
                Report(i + 1, localItems.Count, item.Name, bytesDone, totalBytes, $"Completed {item.Name}");

                if (isMove)
                {
                    if (verified)
                    {
                        bool deleted = _localService.DeleteItem(item);
                        if (deleted)
                        {
                            Log($"[MOVE OK] {item.Name} verified on phone and removed from PC.");
                        }
                        else
                        {
                            Log($"[WARNING] Copied {item.Name} successfully, but failed to delete PC source.", isError: true);
                        }
                    }
                    else
                    {
                        Log($"[SAFETY ABORT] Verification failed for '{item.Name}'. PC source was NOT deleted to prevent data loss.", isError: true);
                    }
                }
                else
                {
                    Log($"[COPY OK] {item.Name} ({Helpers.SizeFormatter.FormatBytes(item.Size)}) uploaded to Phone.");
                }
            }

            Log($"Finished {(isMove ? "move" : "copy")} operation from PC to Phone.");
        }
    }
}
