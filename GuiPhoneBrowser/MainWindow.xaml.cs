using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GuiPhoneBrowser.Models;
using GuiPhoneBrowser.Services;

namespace GuiPhoneBrowser
{
    public partial class MainWindow : Window
    {
        private readonly AdbService _adbService;
        private readonly LocalFileService _localService;
        private readonly TransferManager _transferManager;

        private string _currentPcPath = string.Empty;
        private string _currentPhonePath = "/sdcard";
        private DeviceInfo? _currentDevice;

        private CancellationTokenSource? _transferCts;
        private bool _isTransferring = false;

        public MainWindow()
        {
            InitializeComponent();

            _adbService = new AdbService();
            _localService = new LocalFileService();
            _transferManager = new TransferManager(_adbService, _localService);

            _transferManager.ProgressChanged += OnTransferProgressChanged;
            _transferManager.LogMessage += OnTransferLogMessage;
            _transferManager.ConfirmOverwritePrompt = PromptOverwriteAsync;

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CheckAdbStatus();
            LoadPcShortcuts();
            LoadPhoneShortcuts();

            // Initial PC directory: Downloads or User Profile
            string initialPc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(initialPc))
            {
                initialPc = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            await NavigatePcAsync(initialPc);

            // Initial Phone device scan
            await RefreshDevicesAsync();
        }

        private void CheckAdbStatus()
        {
            string? adbPath = _adbService.GetAdbPath();
            if (!string.IsNullOrEmpty(adbPath))
            {
                bool isBundled = adbPath.Contains("platform-tools", StringComparison.OrdinalIgnoreCase);
                TxtAdbStatus.Text = isBundled ? "ADB: Bundled (Ready)" : "ADB: System Detected";
                TxtAdbStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15803D"));
            }
            else
            {
                TxtAdbStatus.Text = "ADB: Missing";
                TxtAdbStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"));
                MessageBox.Show(
                    "adb.exe was not found in the application directory or system PATH.\n" +
                    "Please ensure platform-tools are present.",
                    "ADB Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #region Device Management

        private async Task RefreshDevicesAsync()
        {
            BtnRefreshDevices.IsEnabled = false;
            TxtDeviceState.Text = "Scanning...";
            TxtDeviceState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569"));

            try
            {
                var devices = await _adbService.GetDevicesAsync();
                CboDevices.ItemsSource = devices;

                if (devices.Count > 0)
                {
                    // Select previously selected or first device
                    var matched = devices.FirstOrDefault(d => d.Serial == _currentDevice?.Serial) ?? devices[0];
                    CboDevices.SelectedItem = matched;
                }
                else
                {
                    _currentDevice = null;
                    CboDevices.SelectedItem = null;
                    UpdateDeviceStateBadge(null);
                    LvPhoneFiles.ItemsSource = null;
                    TxtPhoneStatus.Text = "No Android device detected. Connect via USB and enable USB Debugging.";
                }
            }
            catch (Exception ex)
            {
                Log($"Error scanning devices: {ex.Message}", isError: true);
            }
            finally
            {
                BtnRefreshDevices.IsEnabled = true;
            }
        }

        private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDevicesAsync();
        }

        private async void CboDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _currentDevice = CboDevices.SelectedItem as DeviceInfo;
            UpdateDeviceStateBadge(_currentDevice);

            if (_currentDevice != null && _currentDevice.IsAuthorized)
            {
                await NavigatePhoneAsync(_currentPhonePath);
            }
            else
            {
                LvPhoneFiles.ItemsSource = null;
                if (_currentDevice != null && _currentDevice.IsUnauthorized)
                {
                    TxtPhoneStatus.Text = "Device unauthorized. Please unlock phone and tap 'Allow USB debugging'.";
                }
            }
        }

        private void UpdateDeviceStateBadge(DeviceInfo? device)
        {
            if (device == null)
            {
                TxtDeviceState.Text = "No Device";
                TxtDeviceState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
                BadgeDeviceState.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9"));
                return;
            }

            if (device.IsAuthorized)
            {
                TxtDeviceState.Text = "Connected";
                TxtDeviceState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15803D"));
                BadgeDeviceState.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7"));
            }
            else if (device.IsUnauthorized)
            {
                TxtDeviceState.Text = "Unauthorized (Check Phone Screen)";
                TxtDeviceState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B45309"));
                BadgeDeviceState.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7"));
            }
            else
            {
                TxtDeviceState.Text = device.State;
                TxtDeviceState.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"));
                BadgeDeviceState.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));
            }
        }

        #endregion

        #region Windows PC Navigation

        private void LoadPcShortcuts()
        {
            var shortcuts = _localService.GetShortcuts();
            CboPcShortcuts.ItemsSource = shortcuts;
        }

        private async void CboPcShortcuts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboPcShortcuts.SelectedItem is LocalFileService.PathShortcut sc)
            {
                await NavigatePcAsync(sc.Path);
            }
        }

        private async Task NavigatePcAsync(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
            {
                MessageBox.Show($"Directory not found: {targetPath}", "PC Navigation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _currentPcPath = Path.GetFullPath(targetPath);
            TxtPcPath.Text = _currentPcPath;
            TxtPcStatus.Text = "Loading...";

            try
            {
                var items = await _localService.GetDirectoryItemsAsync(_currentPcPath);
                LvPcFiles.ItemsSource = items;

                int folderCount = items.Count(i => i.IsDirectory && !i.IsParentDirectory);
                int fileCount = items.Count(i => !i.IsDirectory);
                long totalBytes = items.Where(i => !i.IsDirectory).Sum(i => i.Size);

                TxtPcStatus.Text = $"{items.Count(i => !i.IsParentDirectory)} items ({folderCount} folders, {fileCount} files, {Helpers.SizeFormatter.FormatBytes(totalBytes)})";
            }
            catch (Exception ex)
            {
                TxtPcStatus.Text = $"Error: {ex.Message}";
            }
        }

        private async void BtnPcGo_Click(object sender, RoutedEventArgs e)
        {
            await NavigatePcAsync(TxtPcPath.Text);
        }

        private async void TxtPcPath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await NavigatePcAsync(TxtPcPath.Text);
            }
        }

        private async void BtnPcUp_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPcPath)) return;
            var parent = Directory.GetParent(_currentPcPath);
            if (parent != null)
            {
                await NavigatePcAsync(parent.FullName);
            }
        }

        private async void BtnPcRefresh_Click(object sender, RoutedEventArgs e)
        {
            await NavigatePcAsync(_currentPcPath);
        }

        private async void LvPcFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LvPcFiles.SelectedItem is FileItem item)
            {
                if (item.IsDirectory)
                {
                    await NavigatePcAsync(item.FullPath);
                }
                else
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = item.FullPath,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not open file: {ex.Message}", "Open File", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private async void LvPcFiles_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && LvPcFiles.SelectedItem is FileItem item && item.IsDirectory)
            {
                await NavigatePcAsync(item.FullPath);
            }
            else if (e.Key == Key.Back)
            {
                BtnPcUp_Click(this, new RoutedEventArgs());
            }
            else if (e.Key == Key.F5)
            {
                await NavigatePcAsync(_currentPcPath);
            }
            else if (e.Key == Key.Delete)
            {
                BtnPcDelete_Click(this, new RoutedEventArgs());
            }
        }

        private void LvPcFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Update button states if needed
        }

        private async void BtnPcNewFolder_Click(object sender, RoutedEventArgs e)
        {
            string folderName = PromptInput("Create New Folder on PC", "Enter folder name:", "New Folder");
            if (string.IsNullOrWhiteSpace(folderName)) return;

            string target = Path.Combine(_currentPcPath, folderName);
            if (_localService.CreateDirectory(target))
            {
                await NavigatePcAsync(_currentPcPath);
            }
            else
            {
                MessageBox.Show("Failed to create folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnPcDelete_Click(object sender, RoutedEventArgs e)
        {
            var selected = LvPcFiles.SelectedItems.Cast<FileItem>().Where(i => !i.IsParentDirectory).ToList();
            if (selected.Count == 0) return;

            var result = MessageBox.Show(
                $"Are you sure you want to permanently delete {selected.Count} item(s) from PC?\nThis action cannot be undone.",
                "Confirm Permanent Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            int deletedCount = 0;
            foreach (var item in selected)
            {
                if (_localService.DeleteItem(item)) deletedCount++;
            }

            Log($"Deleted {deletedCount} item(s) from PC.");
            await NavigatePcAsync(_currentPcPath);
        }

        #endregion

        #region Android Phone Navigation

        private void LoadPhoneShortcuts()
        {
            var shortcuts = _adbService.GetPhoneShortcuts();
            CboPhoneShortcuts.ItemsSource = shortcuts;
        }

        private async void CboPhoneShortcuts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboPhoneShortcuts.SelectedItem is LocalFileService.PathShortcut sc)
            {
                await NavigatePhoneAsync(sc.Path);
            }
        }

        private async Task NavigatePhoneAsync(string remotePath)
        {
            if (_currentDevice == null || !_currentDevice.IsAuthorized)
            {
                TxtPhoneStatus.Text = "Please connect and authorize an Android device.";
                return;
            }

            _currentPhonePath = remotePath;
            TxtPhonePath.Text = _currentPhonePath;
            TxtPhoneStatus.Text = "Loading...";

            try
            {
                using var cts = new CancellationTokenSource(15000);
                var items = await _adbService.GetDirectoryItemsAsync(_currentDevice.Serial, _currentPhonePath, cts.Token);
                LvPhoneFiles.ItemsSource = items;

                int folderCount = items.Count(i => i.IsDirectory && !i.IsParentDirectory);
                int fileCount = items.Count(i => !i.IsDirectory);
                long totalBytes = items.Where(i => !i.IsDirectory).Sum(i => i.Size);

                TxtPhoneStatus.Text = $"{items.Count(i => !i.IsParentDirectory)} items ({folderCount} folders, {fileCount} files, {Helpers.SizeFormatter.FormatBytes(totalBytes)})";
            }
            catch (Exception ex)
            {
                TxtPhoneStatus.Text = $"Error: {ex.Message}";
            }
        }

        private async void BtnPhoneGo_Click(object sender, RoutedEventArgs e)
        {
            await NavigatePhoneAsync(TxtPhonePath.Text);
        }

        private async void TxtPhonePath_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await NavigatePhoneAsync(TxtPhonePath.Text);
            }
        }

        private async void BtnPhoneUp_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPhonePath) || _currentPhonePath == "/") return;
            string clean = _currentPhonePath.TrimEnd('/');
            int lastSlash = clean.LastIndexOf('/');
            string parent = lastSlash <= 0 ? "/" : clean.Substring(0, lastSlash);
            await NavigatePhoneAsync(parent);
        }

        private async void BtnPhoneRefresh_Click(object sender, RoutedEventArgs e)
        {
            await NavigatePhoneAsync(_currentPhonePath);
        }

        private async void LvPhoneFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LvPhoneFiles.SelectedItem is FileItem item && item.IsDirectory)
            {
                await NavigatePhoneAsync(item.FullPath);
            }
        }

        private async void LvPhoneFiles_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && LvPhoneFiles.SelectedItem is FileItem item && item.IsDirectory)
            {
                await NavigatePhoneAsync(item.FullPath);
            }
            else if (e.Key == Key.Back)
            {
                BtnPhoneUp_Click(this, new RoutedEventArgs());
            }
            else if (e.Key == Key.F5)
            {
                await NavigatePhoneAsync(_currentPhonePath);
            }
            else if (e.Key == Key.Delete)
            {
                BtnPhoneDelete_Click(this, new RoutedEventArgs());
            }
        }

        private void LvPhoneFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private async void BtnPhoneNewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null || !_currentDevice.IsAuthorized) return;

            string folderName = PromptInput("Create New Folder on Phone", "Enter folder name:", "New Folder");
            if (string.IsNullOrWhiteSpace(folderName)) return;

            string target = _currentPhonePath.TrimEnd('/') + "/" + folderName;
            var res = await _adbService.CreateDirectoryAsync(_currentDevice.Serial, target);
            if (res.Success)
            {
                await NavigatePhoneAsync(_currentPhonePath);
            }
            else
            {
                MessageBox.Show($"Failed to create folder: {res.StdErr}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnPhoneDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null || !_currentDevice.IsAuthorized) return;

            var selected = LvPhoneFiles.SelectedItems.Cast<FileItem>().Where(i => !i.IsParentDirectory).ToList();
            if (selected.Count == 0) return;

            var result = MessageBox.Show(
                $"Are you sure you want to permanently delete {selected.Count} item(s) from Android Phone?\nThis action cannot be undone.",
                "Confirm Permanent Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            int deletedCount = 0;
            foreach (var item in selected)
            {
                var delRes = item.IsDirectory
                    ? await _adbService.DeleteDirectoryAsync(_currentDevice.Serial, item.FullPath)
                    : await _adbService.DeleteFileAsync(_currentDevice.Serial, item.FullPath);

                if (delRes.Success) deletedCount++;
                else Log($"Failed to delete {item.Name}: {delRes.StdErr}", isError: true);
            }

            Log($"Deleted {deletedCount} item(s) from Phone.");
            await NavigatePhoneAsync(_currentPhonePath);
        }

        #endregion

        #region Transfer Operations

        private void SetTransferring(bool isTransferring)
        {
            _isTransferring = isTransferring;
            BtnCancelTransfer.IsEnabled = isTransferring;
            BtnCopyPcToPhone.IsEnabled = !isTransferring;
            BtnMovePcToPhone.IsEnabled = !isTransferring;
            BtnCopyPhoneToPc.IsEnabled = !isTransferring;
            BtnMovePhoneToPc.IsEnabled = !isTransferring;

            if (!isTransferring)
            {
                PbTransfer.Value = 0;
                TxtTransferStatus.Text = "Ready";
            }
        }

        private async void BtnCopyPcToPhone_Click(object sender, RoutedEventArgs e)
        {
            await RunPcToPhoneTransferAsync(isMove: false);
        }

        private async void BtnMovePcToPhone_Click(object sender, RoutedEventArgs e)
        {
            var selected = LvPcFiles.SelectedItems.Cast<FileItem>().Where(i => !i.IsParentDirectory).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select items to move from PC.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (selected.Any(item => item.IsDirectory))
            {
                MessageBox.Show(
                    "Folder moves are temporarily disabled because the app cannot verify every nested file before deleting the source. You can copy the folder and remove it manually after checking the result.",
                    "Folder Move Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Are you sure you want to MOVE {selected.Count} item(s) to the Phone?\n\n" +
                "Each source file is removed only after its destination size and modified time are verified.",
                "Confirm Move to Phone", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            await RunPcToPhoneTransferAsync(isMove: true);
        }

        private async Task RunPcToPhoneTransferAsync(bool isMove)
        {
            if (_currentDevice == null || !_currentDevice.IsAuthorized)
            {
                MessageBox.Show("Please connect and authorize an Android phone first.", "Phone Not Ready", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selected = LvPcFiles.SelectedItems.Cast<FileItem>().Where(i => !i.IsParentDirectory).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select items to transfer from PC.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _transferCts = new CancellationTokenSource();
            SetTransferring(true);

            try
            {
                await _transferManager.TransferPcToPhoneAsync(
                    _currentDevice.Serial,
                    selected,
                    _currentPhonePath,
                    isMove,
                    _transferCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("Transfer operation was cancelled.");
            }
            catch (Exception ex)
            {
                Log($"Transfer failed: {ex.Message}", isError: true);
                MessageBox.Show($"Transfer error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetTransferring(false);
                await NavigatePhoneAsync(_currentPhonePath);
                if (isMove) await NavigatePcAsync(_currentPcPath);
            }
        }

        private async void BtnCopyPhoneToPc_Click(object sender, RoutedEventArgs e)
        {
            await RunPhoneToPcTransferAsync(isMove: false);
        }

        private async void BtnMovePhoneToPc_Click(object sender, RoutedEventArgs e)
        {
            var selected = LvPhoneFiles.SelectedItems.Cast<FileItem>().Where(i => !i.IsParentDirectory).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select items to move from Phone.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (selected.Any(item => item.IsDirectory))
            {
                MessageBox.Show(
                    "Folder moves are temporarily disabled because the app cannot verify every nested file before deleting the source. You can copy the folder and remove it manually after checking the result.",
                    "Folder Move Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Are you sure you want to MOVE {selected.Count} item(s) to PC?\n\n" +
                "Each source file is removed only after its destination size is verified. ADB preserves modified time during download.",
                "Confirm Move to PC", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            await RunPhoneToPcTransferAsync(isMove: true);
        }

        private async Task RunPhoneToPcTransferAsync(bool isMove)
        {
            if (_currentDevice == null || !_currentDevice.IsAuthorized)
            {
                MessageBox.Show("Please connect and authorize an Android phone first.", "Phone Not Ready", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selected = LvPhoneFiles.SelectedItems.Cast<FileItem>().Where(i => !i.IsParentDirectory).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Please select items to transfer from Phone.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _transferCts = new CancellationTokenSource();
            SetTransferring(true);

            try
            {
                await _transferManager.TransferPhoneToPcAsync(
                    _currentDevice.Serial,
                    selected,
                    _currentPcPath,
                    isMove,
                    _transferCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("Transfer operation was cancelled.");
            }
            catch (Exception ex)
            {
                Log($"Transfer failed: {ex.Message}", isError: true);
                MessageBox.Show($"Transfer error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetTransferring(false);
                await NavigatePcAsync(_currentPcPath);
                if (isMove) await NavigatePhoneAsync(_currentPhonePath);
            }
        }

        private void BtnCancelTransfer_Click(object sender, RoutedEventArgs e)
        {
            if (_isTransferring && _transferCts != null)
            {
                BtnCancelTransfer.IsEnabled = false;
                TxtTransferStatus.Text = "Cancelling transfer...";
                _transferCts.Cancel();
            }
        }

        #endregion

        #region Progress, Log & Overwrite Callbacks

        private void OnTransferProgressChanged(TransferProgress p)
        {
            Dispatcher.Invoke(() =>
            {
                PbTransfer.Value = p.Percentage;
                TxtTransferStatus.Text = $"[{p.CurrentItemIndex}/{p.TotalItems}] {p.StatusMessage} ({p.Percentage:0}%)";
            });
        }

        private void OnTransferLogMessage(string msg, bool isError)
        {
            Dispatcher.Invoke(() =>
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                TxtLogOutput.AppendText(line + Environment.NewLine);
                SvLog.ScrollToEnd();
            });
        }

        private void Log(string msg, bool isError = false)
        {
            OnTransferLogMessage(msg, isError);
        }

        private Task<TransferManager.OverwriteAction> PromptOverwriteAsync(string destinationPath)
        {
            return Dispatcher.InvokeAsync(() =>
            {
                string filename = Path.GetFileName(destinationPath);
                var result = MessageBox.Show(
                    $"The file '{filename}' already exists at the destination:\n{destinationPath}\n\nDo you want to overwrite it?\n\n- Click 'Yes' to overwrite this file.\n- Click 'No' to skip this file.\n- Click 'Cancel' to abort remaining transfers.",
                    "File Already Exists",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes) return TransferManager.OverwriteAction.Overwrite;
                if (result == MessageBoxResult.No) return TransferManager.OverwriteAction.Skip;
                return TransferManager.OverwriteAction.Cancel;
            }).Task;
        }

        private void BtnToggleLog_Click(object sender, RoutedEventArgs e)
        {
            if (BorderLogDrawer.Visibility == Visibility.Visible)
            {
                BorderLogDrawer.Visibility = Visibility.Collapsed;
                BtnToggleLog.Content = "📜 Show Log";
            }
            else
            {
                BorderLogDrawer.Visibility = Visibility.Visible;
                BtnToggleLog.Content = "📜 Hide Log";
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLogOutput.Clear();
        }

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "GUI Phone Browser\n" +
                "====================================\n\n" +
                "Transfer & Timestamp Preservation:\n" +
                "• Phone to PC: Uses 'adb pull -a' to preserve modified time and mode.\n" +
                "• PC to Phone: Sets modified time in UTC after upload when Android storage permits.\n" +
                "• Move: File moves require a known matching size; PC-to-phone moves also require timestamp sync. Folder moves are disabled until nested content can be verified.\n" +
                "• Only modified time is preserved. Android does not expose a portable creation time through this transfer flow.\n\n" +
                "Connecting Your Android Phone:\n" +
                "1. Connect phone via USB.\n" +
                "2. Go to Settings > About Phone and tap 'Build number' 7 times.\n" +
                "3. In Settings > Developer options, turn ON 'USB debugging'.\n" +
                "4. Check phone screen and select 'Always allow from this computer'.\n" +
                "5. Click '🔄 Refresh' in the top bar.",
                "About GUI Phone Browser", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private string PromptInput(string title, string prompt, string defaultValue = "")
        {
            // Simple WPF modal input box
            var win = new Window
            {
                Title = title,
                Width = 380,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8FAFC"))
            };

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.Medium };
            Grid.SetRow(lbl, 0);

            var tb = new TextBox { Text = defaultValue, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4) };
            tb.SelectAll();
            Grid.SetRow(tb, 1);

            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var btnOk = new Button { Content = "OK", Width = 70, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnCancel = new Button { Content = "Cancel", Width = 70, Height = 26, IsCancel = true };

            string resultText = string.Empty;
            btnOk.Click += (_, _) => { resultText = tb.Text; win.DialogResult = true; win.Close(); };
            btnCancel.Click += (_, _) => { win.DialogResult = false; win.Close(); };

            sp.Children.Add(btnOk);
            sp.Children.Add(btnCancel);
            Grid.SetRow(sp, 2);

            grid.Children.Add(lbl);
            grid.Children.Add(tb);
            grid.Children.Add(sp);

            win.Content = grid;
            tb.Focus();

            return win.ShowDialog() == true ? resultText : string.Empty;
        }

        #endregion
    }
}
