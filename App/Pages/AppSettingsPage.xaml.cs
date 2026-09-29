using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace App {
    public sealed partial class AppSettingsPage : Page {
        private bool busy;
        private Task activeTask = Task.CompletedTask;
        private ContentDialog confirmationDialog;

        public AppSettingsPage() {
            InitializeComponent();

            CacheCovers.Content = AppText.Get("CacheCoversSetting");
            Logging.Content = AppText.Get("DiagnosticLoggingSetting");
            BackupLibraryButton.Content = AppText.Get("SettingsBackupLibrary");
            RestoreLibraryButton.Content = AppText.Get("SettingsRestoreLibrary");
            ClearCoversButton.Content = AppText.Get("ClearCoverCache");
            ExportLogButton.Content = AppText.Get("ExportDiagnosticLog");
            DiagnosticHelp.Text = AppText.Get("DiagnosticSettingsHelp");
            SaveButton.Content = AppText.Get("SaveSettings");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e) {
            base.OnNavigatedTo(e);

            CacheCovers.IsChecked = AppPreferences.CacheCovers;
            Logging.IsChecked = AppPreferences.Logging;
        }

        internal bool HandleBack() {
            return busy;
        }

        internal Task StopAsync() {
            confirmationDialog?.Hide();

            return activeTask;
        }

        private void Save_Click(object sender, RoutedEventArgs e) {
            if (busy) {
                return;
            }

            AppPreferences.CacheCovers = CacheCovers.IsChecked == true;
            AppPreferences.Logging = Logging.IsChecked == true;
            Status.Text = AppText.Get("SettingsSaved");
        }

        private void SetActionButtonsEnabled(bool enabled) {
            BackupLibraryButton.IsEnabled = RestoreLibraryButton.IsEnabled = ClearCoversButton.IsEnabled = ExportLogButton.IsEnabled = SaveButton.IsEnabled = enabled;
        }

        private async Task RunAsync(Func<Task> action) {
            busy = true;

            SetActionButtonsEnabled(false);

            try {
                await action();
            } catch (Exception ex) {
                Status.Text = AppText.Get("ActionFailed");
                await DiagnosticLog.WriteAsync("settings", ex);
            } finally {
                busy = false;
                SetActionButtonsEnabled(true);
            }
        }

        private async Task<bool> ConfirmAsync(string title, string message, string accept) {
            var dialog = new ConfirmationDialog {
                Title = title,
                Message = message,
                PrimaryButtonText = accept,
                SecondaryButtonText = AppText.Get("Cancel")
            };

            confirmationDialog = dialog;

            try {
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            } finally {
                if (confirmationDialog == dialog) {
                    confirmationDialog = null;
                }
            }
        }

        private async void BackupLibrary_Click(object sender, RoutedEventArgs e) {
            if (busy) {
                return;
            }

            activeTask = RunAsync(async () => {
                try {
                    await LibraryTools.EnsureLoadedAsync();
                    await LibraryTools.PendingSave;

                    string snapshot = LibraryTools.Library.Snapshot();

                    var picker = new FileSavePicker {
                        SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                        SuggestedFileName = "Resonance-backup-" + DateTime.Now.ToString("yyyy-MM-dd")
                    };

                    picker.FileTypeChoices.Add(AppText.Get("LibraryBackupFileType"), new[] { ".json" });

                    var file = await picker.PickSaveFileAsync();
                    if (file == null) {
                        return;
                    }

                    await LibraryTools.SavePickedFileAsync(file, snapshot);
                    Status.Text = AppText.Get("LibraryBackupSaved");
                } catch (Exception ex) {
                    Status.Text = AppText.Get("ActionFailed");
                    await DiagnosticLog.WriteAsync("library-backup", ex);
                }
            });

            await activeTask;
        }

        private async void RestoreLibrary_Click(object sender, RoutedEventArgs e) {
            if (busy) {
                return;
            }

            activeTask = RunAsync(async () => {
                try {
                    var picker = new FileOpenPicker {
                        SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                    };

                    picker.FileTypeFilter.Add(".json");

                    var file = await picker.PickSingleFileAsync();
                    if (file == null) {
                        return;
                    }

                    if ((await file.GetBasicPropertiesAsync()).Size > 8 * 1024 * 1024) {
                        throw new InvalidOperationException(AppText.Get("LibraryBackupTooLarge"));
                    }

                    string data = await FileIO.ReadTextAsync(file);

                    var candidate = new SongLibrary(AppText.Get);
                    candidate.Restore(data.TrimStart('\uFEFF'));

                    if (!await ConfirmAsync(AppText.Get("LibraryRestoreTitle"), AppText.Get("LibraryRestorePromptPrefix") + candidate.History.Count + ".", AppText.Get("RestoreLibrary"))) {
                        return;
                    }

                    await LibraryTools.CommitAsync(() => LibraryTools.Library.Restore(candidate.Snapshot()));
                    Status.Text = AppText.Get("LibraryRestored");
                } catch (Exception ex) {
                    Status.Text = ex is FormatException ? AppText.Get("LibraryBackupInvalid") : AppText.Get("ActionFailed");
                    await DiagnosticLog.WriteAsync("library-restore", ex);
                }
            });

            await activeTask;
        }

        private async void ClearCovers_Click(object sender, RoutedEventArgs e) {
            if (busy) {
                return;
            }

            activeTask = RunAsync(async () => {
                try {
                    await CoverCache.ClearAsync();
                    Status.Text = AppText.Get("CoverCacheCleared");
                } catch {
                    Status.Text = AppText.Get("CoverCacheClearFailed");
                }
            });

            await activeTask;
        }

        private async void ExportLog_Click(object sender, RoutedEventArgs e) {
            if (busy) {
                return;
            }

            activeTask = RunAsync(async () => {
                try {
                    var picker = new FileSavePicker {
                        SuggestedFileName = "Resonance-diagnostics"
                    };

                    picker.FileTypeChoices.Add(AppText.Get("TextFileType"), new[] { ".txt" });

                    var file = await picker.PickSaveFileAsync();
                    if (file != null) {
                        await LibraryTools.SavePickedFileAsync(file, await DiagnosticLog.ReadAsync());
                        Status.Text = AppText.Get("DiagnosticLogExported");
                    }
                } catch {
                    Status.Text = AppText.Get("DiagnosticLogExportFailed");
                }
            });

            await activeTask;
        }
    }
}
