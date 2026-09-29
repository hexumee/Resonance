using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace App {
    public sealed partial class LibraryPage : Page {
        private readonly SongLibrary library = LibraryTools.Library;
        private bool libraryReady;
        private bool libraryActionInProgress;
        private bool selectingHistory;
        private Task libraryTask = Task.CompletedTask;
        private SongEntry selectedSong;
        private ContentDialog confirmationDialog;
        private MenuFlyout historyItemMenu;
        private DispatcherTimer statusTimer;
        private Storyboard statusAnimation;
        private int statusVersion;

        public LibraryPage() {
            InitializeComponent();

            NavigationCacheMode = NavigationCacheMode.Enabled;
            EmptyHistoryText.Text = AppText.Get("ShellEmptyHistory");

            SetInputEnabled(false);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e) {
            base.OnNavigatedTo(e);

            library.History.CollectionChanged += Library_Changed;

            try {
                await LibraryTools.EnsureLoadedAsync();

                libraryReady = true;

                RefreshLibraryView();
            } catch (Exception ex) {
                ShowStatus(AppText.Get("LibraryLoadFailed"));
                await DiagnosticLog.WriteAsync("library-load", ex);
            } finally {
                SetInputEnabled(true);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) {
            library.History.CollectionChanged -= Library_Changed;

            historyItemMenu?.Hide();
            HideStatus();

            base.OnNavigatedFrom(e);
        }

        private void ShowStatus(string message) {
            ++statusVersion;

            statusTimer?.Stop();
            statusAnimation?.Stop();

            Status.Text = message;
            StatusNotice.Visibility = Visibility.Visible;
            StatusTranslation.Y = 80;
            StatusNotice.Opacity = 0;

            AnimateStatus(false);

            var timer = new DispatcherTimer {
                Interval = TimeSpan.FromSeconds(6)
            };
            statusTimer = timer;

            timer.Tick += (sender, args) => {
                if (sender != statusTimer) {
                    return;
                }

                timer.Stop();
                statusTimer = null;

                AnimateStatus(true);
            };

            timer.Start();
        }

        private void AnimateStatus(bool dismiss) {
            statusAnimation?.Stop();

            int version = statusVersion;

            var slide = new DoubleAnimation {
                To = dismiss ? Math.Max(100, StatusNotice.ActualHeight + 24) : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = new CubicEase {
                    EasingMode = dismiss ? EasingMode.EaseIn : EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(slide, StatusTranslation);
            Storyboard.SetTargetProperty(slide, "Y");

            var fade = new DoubleAnimation {
                To = dismiss ? 0 : 1,
                Duration = slide.Duration
            };
            Storyboard.SetTarget(fade, StatusNotice);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var animation = new Storyboard();
            animation.Children.Add(slide);
            animation.Children.Add(fade);

            if (dismiss) {
                animation.Completed += (sender, args) => {
                    if (version == statusVersion) {
                        HideStatus();
                    }
                };
            }

            statusAnimation = animation;
            animation.Begin();
        }

        private void HideStatus() {
            ++statusVersion;

            statusTimer?.Stop();
            statusTimer = null;

            statusAnimation?.Stop();
            statusAnimation = null;

            StatusNotice.Visibility = Visibility.Collapsed;
            StatusNotice.Opacity = 1;
            StatusTranslation.Y = 0;
        }

        private void DismissStatus_Click(object sender, RoutedEventArgs e) {
            statusTimer?.Stop();
            statusTimer = null;

            AnimateStatus(true);
        }

        private void Library_Changed(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
            RefreshLibraryView();
        }

        internal bool HandleBack() {
            if (libraryActionInProgress) {
                return true;
            }

            if (selectingHistory) {
                SetHistorySelection(false);

                return true;
            }

            return false;
        }

        internal async Task StopAsync() {
            confirmationDialog?.Hide();
            historyItemMenu?.Hide();

            try {
                await libraryTask;
            } catch {

            }
        }

        private void SetInputEnabled(bool enabled) {
            enabled = enabled && libraryReady && !libraryActionInProgress;
            LibrarySearch.IsEnabled = History.IsEnabled = enabled;
            deleteSomeButton.IsEnabled = enabled && History.SelectedItems.Count > 0;
        }

        private void ShowSong(SongEntry song) {
            selectedSong = song;
        }

        private void OpenDetails(SongEntry song) {
            if (!LibraryBusy && song != null) {
                Frame.Navigate(typeof(DetailsPage), song);
            }
        }

        private void File_Click(object sender, RoutedEventArgs e) {
            if (!LibraryBusy) {
                Frame.Navigate(typeof(MainPage), "file");
            }
        }

        private void RefreshLibraryView() {
            if (History == null || LibrarySearch == null) {
                return;
            }

            var source = library.History;
            var visible = source.Where(song => song.Matches(LibrarySearch.Text)).ToArray();
            var selection = selectingHistory ? History.SelectedItems.Cast<SongEntry>().ToArray() : new SongEntry[0];

            History.ItemsSource = visible;

            if (selectingHistory) {
                foreach (var song in selection) {
                    if (visible.Contains(song) && !History.SelectedItems.Contains(song)) {
                        History.SelectedItems.Add(song);
                    }
                }
            }

            if (EmptyHistoryText != null) {
                EmptyHistoryText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                EmptyHistoryText.Text = AppText.Get(string.IsNullOrWhiteSpace(LibrarySearch.Text) ? "ShellEmptyHistory" : "ShellEmptySearch");
            }

            LibraryHeading.Text = (AppText.Get("HistoryTrackCountPrefix")) + source.Count;
        }

        private void LibrarySearch_Changed(object sender, TextChangedEventArgs e) {
            RefreshLibraryView();
        }

        private void CopyText(string text) {
            if (selectedSong == null) {
                return;
            }

            try {
                var package = new DataPackage();
                package.SetText(text ?? "");
                Clipboard.SetContent(package);

                ShowStatus(AppText.Get("ClipboardCopySucceeded"));
            } catch (Exception ex) {
                ShowStatus(AppText.Get("ClipboardCopyFailedPrefix") + ex.Message);
            }
        }

        private void CopyData_Click(object sender, RoutedEventArgs e) {
            if (selectedSong == null) {
                return;
            }

            string titleAndArtist = string.IsNullOrWhiteSpace(selectedSong.Artist) ? selectedSong.Title : selectedSong.Artist + " — " + selectedSong.Title;
            CopyText(titleAndArtist);
        }

        private bool LibraryBusy {
            get {
                return !libraryReady || libraryActionInProgress;
            }
        }

        private async Task LibraryActionAsync(Func<Task> action) {
            if (LibraryBusy) {
                return;
            }

            libraryActionInProgress = true;

            SetInputEnabled(false);

            try {
                await action();
            } catch (Exception ex) {
                ShowStatus(AppText.Get("ActionFailed"));
                await DiagnosticLog.WriteAsync("library", ex);
            } finally {
                libraryTask = Task.CompletedTask;
                libraryActionInProgress = false;

                SetInputEnabled(true);
                RefreshLibraryView();
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

        private async Task CommitLibraryAsync(Action change) {
            libraryTask = LibraryTools.CommitAsync(change);
            await libraryTask;
        }

        private async void DeleteSong_Click(object sender, RoutedEventArgs e) {
            var song = selectedSong;

            await LibraryActionAsync(async () => {
                // Delete only this recognition, other occurrences remain in history
                if (song == null || !library.History.Contains(song)) {
                    ShowStatus(AppText.Get("HistoryEntryNotFound"));
                    return;
                }

                string label = string.IsNullOrWhiteSpace(song.Artist) ? song.Title : song.Title + " — " + song.Artist;
                if (!await ConfirmAsync(AppText.Get("ShellDeleteSingleTitle"), string.Format(AppText.Get("ShellDeleteSinglePrompt"), label), AppText.Get("ShellDelete"))) {
                    return;
                }

                await CommitLibraryAsync(() => library.History.Remove(song));
                ShowStatus(AppText.Get("HistoryEntryDeleted"));
            });
        }

        private async void ClearHistory_Click(object sender, RoutedEventArgs e) {
            await LibraryActionAsync(async () => {
                if (library.History.Count == 0) {
                    ShowStatus(AppText.Get("HistoryEmpty"));
                    return;
                }

                if (!await ConfirmAsync(AppText.Get("ClearHistoryTitle"), AppText.Get("ClearHistoryPrompt"), AppText.Get("ClearHistory"))) {
                    return;
                }

                await CommitLibraryAsync(() => library.History.Clear());
                ShowStatus(AppText.Get("HistoryCleared"));
            });
        }

        private void SelectHistory_Click(object sender, RoutedEventArgs e) {
            if (!LibraryBusy) {
                SetHistorySelection(!selectingHistory);

                deleteSomeButton.Visibility = selectingHistory ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SetHistorySelection(bool enabled) {
            selectingHistory = enabled;
            History.SelectionMode = enabled ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;

            if (enabled) {
                History.SelectedItems.Clear();
            }

            History.IsItemClickEnabled = !enabled;
            deleteSomeButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            deleteSomeButton.IsEnabled = false;
        }

        private void HistorySelection_Changed(object sender, SelectionChangedEventArgs e) {
            deleteSomeButton.IsEnabled = !LibraryBusy && History.SelectedItems.Count > 0;
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e) {
            var songs = History.SelectedItems.Cast<SongEntry>().ToArray();

            await LibraryActionAsync(async () => {
                if (songs.Length == 0) {
                    return;
                }

                if (!await ConfirmAsync(AppText.Get("ShellDeleteSelected"), string.Format(AppText.Get("ShellDeleteHistoryPrompt"), songs.Length), AppText.Get("ShellDelete"))) {
                    return;
                }

                await CommitLibraryAsync(() => {
                    foreach (var song in songs) {
                        library.History.Remove(song);
                    }
                });

                SetHistorySelection(false);
                ShowStatus(string.Format(AppText.Get("ShellDeleted"), songs.Length));
            });
        }

        private void HistoryActions_Click(object sender, RoutedEventArgs e) {
            if (LibraryBusy || selectingHistory) {
                return;
            }

            var button = (Button)sender;
            if (!(button.Tag is SongEntry song)) {
                return;
            }

            ShowSong(song);
            historyItemMenu?.Hide();

            historyItemMenu = CreateHistoryMenu(song);
            historyItemMenu.ShowAt(button);
        }

        private MenuFlyout CreateHistoryMenu(SongEntry song) {
            var menu = new MenuFlyout();

            Action<string, RoutedEventHandler> add = (label, handler) => {
                var item = new MenuFlyoutItem {
                    Text = AppText.Get(label)
                };

                item.Click += (s, args) => {
                    selectedSong = song;
                    handler(s, args);
                };

                menu.Items.Add(item);
            };

            add("ShellCopyData", CopyData_Click);
            add("ShellYouTube", YouTube_Click);
            menu.Items.Add(new MenuFlyoutSeparator());
            add("ShellDelete", DeleteSong_Click);

            return menu;
        }

        private async void YouTube_Click(object sender, RoutedEventArgs e) {
            if (selectedSong == null) {
                return;
            }

            try {
                await Windows.System.Launcher.LaunchUriAsync(SongSearch.YouTubeSearchUri(selectedSong));
            } catch (Exception ex) {
                ShowStatus(AppText.Get("WebSearchLaunchFailedPrefix") + ex.Message);
            }
        }

        private void History_Click(object sender, ItemClickEventArgs e) {
            if (selectingHistory || LibraryBusy) {
                return;
            }

            ShowSong((SongEntry)e.ClickedItem);
            OpenDetails(selectedSong);
        }

    }
}
