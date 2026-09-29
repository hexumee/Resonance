using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace App {
    public sealed partial class DetailsPage : Page {
        private readonly SongLibrary library = LibraryTools.Library;
        private SongEntry selectedSong;
        private bool libraryActionInProgress;
        private CancellationTokenSource coverOperation;
        private CancellationTokenSource previewOperation;
        private MediaPlayer previewPlayer;
        private MediaSource previewSource;
        private ContentDialog confirmationDialog;
        private Task libraryTask = Task.CompletedTask;
        private DispatcherTimer statusTimer;
        private Storyboard statusAnimation;
        private int statusVersion;

        public DetailsPage() {
            InitializeComponent();

            SetPreviewIcon(false);
            PreviewStoreButton.Content = AppText.Get("PreviewOpenStore");
            YouTubeButton.Content = AppText.Get("ShellYouTube");
            PreviewAttribution.Text = AppText.Get("PreviewAttribution");
        }

        private void SetPreviewIcon(bool playing) {
            PreviewIcon.Symbol = playing ? Symbol.Pause : Symbol.Play;
        }

        private void ShowStatus(string message, int dismissAfterSeconds = 6) {
            ++statusVersion;

            statusTimer?.Stop();
            statusAnimation?.Stop();

            Status.Text = message;
            StatusNotice.Visibility = Visibility.Visible;
            StatusTranslation.Y = 80;
            StatusNotice.Opacity = 0;

            AnimateStatus(false);

            var timer = new DispatcherTimer {
                Interval = TimeSpan.FromSeconds(dismissAfterSeconds)
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

        private void HideStatus(string expectedMessage = null) {
            if (expectedMessage != null && Status.Text != expectedMessage) {
                return;
            }

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

        protected override async void OnNavigatedTo(NavigationEventArgs e) {
            base.OnNavigatedTo(e);

            selectedSong = e.Parameter as SongEntry;

            try {
                await LibraryTools.EnsureLoadedAsync();

                if (selectedSong != null) {
                    ShowSong(selectedSong);
                }
            } catch (Exception ex) {
                ShowStatus(AppText.Get("LibraryLoadFailed"));
                await DiagnosticLog.WriteAsync("library-load", ex);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) {
            coverOperation?.Cancel();
            previewOperation?.Cancel();
            StopPreview();
            confirmationDialog?.Hide();
            HideStatus();

            base.OnNavigatedFrom(e);
        }

        internal bool HandleBack() {
            return libraryActionInProgress;
        }

        internal async Task StopAsync() {
            coverOperation?.Cancel();
            previewOperation?.Cancel();
            StopPreview();
            confirmationDialog?.Hide();
            HideStatus();

            try {
                await libraryTask;
            } catch {

            }
        }

        internal void Resume() {
            if (selectedSong != null) {
                LoadCover(selectedSong);
                LoadPreview(selectedSong);
            }
        }

        private async void LoadCover(SongEntry song) {
            coverOperation?.Cancel();

            var current = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            coverOperation = current;

            try {
                var bitmap = await LibraryTools.ReadCoverAsync(song, current.Token);

                if (selectedSong == song && !current.IsCancellationRequested && bitmap != null) {
                    SongCover.Source = bitmap;
                    SongCover.Visibility = Visibility.Visible;
                }
            } catch (OperationCanceledException) {

            } catch (Exception ex) {
                if (coverOperation == current && selectedSong == song) {
                    ShowStatus(AppText.Get("CoverUnavailable"));
                }

                await DiagnosticLog.WriteAsync("cover", ex);
            } finally {
                if (coverOperation == current) {
                    coverOperation = null;
                }

                current.Dispose();
            }
        }

        private void SetInputEnabled(bool enabled) {
            CopyDataButton.IsEnabled = DeleteSongButton.IsEnabled = enabled && selectedSong != null;
        }

        private async void DeleteSong_Click(object sender, RoutedEventArgs e) {
            var song = selectedSong;
            if (libraryActionInProgress || song == null) {
                return;
            }

            libraryActionInProgress = true;
            SetInputEnabled(false);

            try {
                if (!library.History.Contains(song)) {
                    ShowStatus(AppText.Get("HistoryEntryNotFound"));
                    return;
                }

                string label = string.IsNullOrWhiteSpace(song.Artist) ? song.Title : song.Title + " — " + song.Artist;
                if (!await ConfirmDeleteAsync(label)) {
                    return;
                }

                libraryTask = LibraryTools.CommitAsync(() => library.History.Remove(song));
                await libraryTask;

                ShowStatus(AppText.Get("HistoryEntryDeleted"));
            } catch (Exception ex) {
                ShowStatus(AppText.Get("ActionFailed"));
                await DiagnosticLog.WriteAsync("library", ex);
            } finally {
                libraryActionInProgress = false;
                SetInputEnabled(true);
            }
        }

        private void ShowSong(SongEntry song) {
            previewOperation?.Cancel();
            StopPreview();

            selectedSong = song;
            SongTitle.Text = song.Title;
            SongArtist.Text = song.Artist;

            var fields = new[] {
                string.IsNullOrEmpty(song.Album) ? "" : AppText.Get("AlbumLabelPrefix") + song.Album,
                string.IsNullOrEmpty(song.ReleaseYear) ? "" : AppText.Get("ReleaseYearLabelPrefix") + song.ReleaseYear,
                string.IsNullOrEmpty(song.Genre) ? "" : AppText.Get("GenreLabelPrefix") + song.Genre
            };

            SongMetadata.Text = string.Join("\n", fields.Where(value => value.Length > 0));
            SongCover.Source = null;
            SongCover.Visibility = Visibility.Collapsed;

            LoadCover(song);
            LoadPreview(song);

            CopyDataButton.IsEnabled = DeleteSongButton.IsEnabled = !libraryActionInProgress;
            OpenTrackButton.IsEnabled = Uri.TryCreate(song.Url, UriKind.Absolute, out Uri trackUri) && trackUri.Scheme == "https";
            SearchSongButton.IsEnabled = YouTubeButton.IsEnabled = true;
        }
        
        private async Task<bool> ConfirmDeleteAsync(string label) {
            var dialog = new ConfirmationDialog {
                Title = AppText.Get("ShellDeleteSingleTitle"),
                Message = string.Format(AppText.Get("ShellDeleteSinglePrompt"), label),
                PrimaryButtonText = AppText.Get("ShellDelete"),
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

        private async void LoadPreview(SongEntry song) {
            previewOperation?.Cancel();

            PreviewButton.IsEnabled = false;
            PreviewStoreButton.IsEnabled = false;
            PreviewAttribution.Visibility = Visibility.Collapsed;

            ShowStatus(AppText.Get("PreviewSearching"), 12);
            SetPreviewIcon(false);

            if (TryPreviewUris(song, out Uri preview, out Uri store)) {
                SetPreviewAvailable(song, preview, store);
                return;
            }

            var current = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            previewOperation = current;

            try {
                var found = await RecognitionService.FindPreviewAsync(song, current.Token);

                if (selectedSong != song || current.IsCancellationRequested || found == null) {
                    if (selectedSong == song && !current.IsCancellationRequested) {
                        ShowStatus(AppText.Get("PreviewUnavailable"));
                    }

                    return;
                }

                song.PreviewUrl = found.Item1.AbsoluteUri;
                song.PreviewStoreUrl = found.Item2.AbsoluteUri;

                SetPreviewAvailable(song, found.Item1, found.Item2);
            } catch (OperationCanceledException) {

            } catch (Exception ex) {
                if (selectedSong == song && !current.IsCancellationRequested) {
                    ShowStatus(AppText.Get("PreviewFailed"));
                }

                await DiagnosticLog.WriteAsync("preview-lookup", ex);
            } finally {
                if (previewOperation == current) {
                    previewOperation = null;
                }

                current.Dispose();
            }
        }

        private static bool TryPreviewUris(SongEntry song, out Uri preview, out Uri store) {
            preview = null;
            store = null;

            return Uri.TryCreate(song.PreviewUrl, UriKind.Absolute, out preview) && (preview.Scheme == "https" || preview.Scheme == "http") && Uri.TryCreate(song.PreviewStoreUrl, UriKind.Absolute, out store) && store.Scheme == "https";
        }

        private void SetPreviewAvailable(SongEntry song, Uri preview, Uri store) {
            if (selectedSong != song) {
                return;
            }

            song.PreviewUrl = preview.AbsoluteUri;
            song.PreviewStoreUrl = store.AbsoluteUri;
            PreviewButton.IsEnabled = true;
            PreviewStoreButton.IsEnabled = true;
            PreviewAttribution.Visibility = Visibility.Visible;

            HideStatus(AppText.Get("PreviewSearching"));
        }

        private async void Preview_Click(object sender, RoutedEventArgs e) {
            if (selectedSong == null) {
                return;
            }

            if (previewPlayer != null) {
                if (previewPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) {
                    previewPlayer.Pause();
                    SetPreviewIcon(false);
                } else {
                    previewPlayer.Play();
                    SetPreviewIcon(true);
                }

                return;
            }

            if (!TryPreviewUris(selectedSong, out Uri preview, out Uri store)) {
                return;
            }

            try {
                // Stream the Apple preview directly; the app never downloads or stores its audio
                previewSource = MediaSource.CreateFromUri(preview);

                var player = new MediaPlayer {
                    AutoPlay = false,
                    Volume = 1,
                    IsMuted = false,
                    AudioCategory = MediaPlayerAudioCategory.Media
                };

                previewPlayer = player;
                player.CommandManager.IsEnabled = false;
                PreviewButton.IsEnabled = false;

                ShowStatus(AppText.Get("PreviewOpening"), 15);

                player.MediaOpened += async (mediaPlayer, args) => {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                        if (previewPlayer != mediaPlayer) {
                            return;
                        }

                        PreviewButton.IsEnabled = true;

                        SetPreviewIcon(true);
                        HideStatus(AppText.Get("PreviewOpening"));
                        mediaPlayer.Play();
                    });
                };

                player.MediaEnded += async (mediaPlayer, args) => {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                        if (previewPlayer != mediaPlayer) {
                            return;
                        }

                        StopPreview();
                        HideStatus(AppText.Get("PreviewOpening"));
                        ShowStatus(AppText.Get("PreviewPlaybackEnded"));
                    });
                };

                player.MediaFailed += async (mediaPlayer, args) => {
                    string error = args.Error + ": " + args.ErrorMessage;

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                        if (previewPlayer != mediaPlayer) {
                            return;
                        }

                        StopPreview();
                        HideStatus(AppText.Get("PreviewOpening"));
                        ShowStatus(AppText.Get("PreviewPlaybackFailed"));
                    });

                    await DiagnosticLog.WriteAsync("preview-playback", new InvalidOperationException(error));
                };

                player.Source = previewSource;
            } catch (Exception ex) {
                StopPreview();
                HideStatus(AppText.Get("PreviewOpening"));
                ShowStatus(AppText.Get("PreviewPlaybackFailed"));

                await DiagnosticLog.WriteAsync("preview-playback", ex);
            }
        }

        private void StopPreview() {
            var player = previewPlayer;
            previewPlayer = null;

            if (player != null) {
                player.Pause();
                player.Source = null;

                player.Dispose();
            }

            previewSource?.Dispose();
            previewSource = null;

            if (PreviewButton != null) {
                SetPreviewIcon(false);
                PreviewButton.IsEnabled = selectedSong != null && !string.IsNullOrEmpty(selectedSong.PreviewUrl);
            }
        }

        private async void PreviewStore_Click(object sender, RoutedEventArgs e) {
            if (selectedSong == null || !TryPreviewUris(selectedSong, out Uri preview, out Uri store)) {
                return;
            }

            try {
                if (!await Windows.System.Launcher.LaunchUriAsync(store)) {
                    ShowStatus(AppText.Get("BrowserLaunchFailed"));
                }
            } catch (Exception ex) {
                ShowStatus(AppText.Get("WebSearchLaunchFailedPrefix") + ex.Message);
            }
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

        private void CopySong_Click(object sender, RoutedEventArgs e) {
            CopyText(selectedSong == null ? "" : string.IsNullOrEmpty(selectedSong.Artist) ? selectedSong.Title : selectedSong.Artist + " — " + selectedSong.Title);
        }

        private async void SearchSong_Click(object sender, RoutedEventArgs e) {
            if (selectedSong == null) {
                return;
            }

            try {
                if (!await Windows.System.Launcher.LaunchUriAsync(SongSearch.GoogleSearchUri(selectedSong))) {
                    ShowStatus(AppText.Get("BrowserLaunchFailed"));
                }
            } catch (Exception ex) {
                ShowStatus(AppText.Get("WebSearchLaunchFailedPrefix") + ex.Message);
            }
        }

        private async void YouTube_Click(object sender, RoutedEventArgs e) {
            if (selectedSong == null) {
                return;
            }

            try {
                if (!await Windows.System.Launcher.LaunchUriAsync(SongSearch.YouTubeSearchUri(selectedSong))) {
                    ShowStatus(AppText.Get("BrowserLaunchFailed"));
                }
            } catch (Exception ex) {
                ShowStatus(AppText.Get("WebSearchLaunchFailedPrefix") + ex.Message);
            }
        }

        private async void OpenTrack_Click(object sender, RoutedEventArgs e) {
            if (selectedSong != null && Uri.TryCreate(selectedSong.Url, UriKind.Absolute, out Uri uri) && uri.Scheme == "https") {
                try {
                    if (!await Windows.System.Launcher.LaunchUriAsync(uri)) {
                        ShowStatus(AppText.Get("BrowserLaunchFailed"));
                    }
                } catch (Exception ex) {
                    ShowStatus(ex.Message);
                }
            }
        }
    }
}
