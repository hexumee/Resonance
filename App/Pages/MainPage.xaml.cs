using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace App {
    public sealed partial class MainPage : Page {
        private bool libraryReady;
        private bool initializingPage;
        private CancellationTokenSource recognitionCancellation;
        private CancellationTokenSource queueSendCancellation;
        private Task activeTask = Task.CompletedTask;
        private bool operationCompleted;
        private OfflineQueueDialog offlineDialog;
        private DispatcherTimer windowTimer;

        private readonly Stopwatch windowWatch = new Stopwatch();
        private DispatcherTimer noticeTimer;
        private Storyboard noticeAnimation;
        private SongEntry noticeSong;
        private int noticeVersion;

        public MainPage() {
            InitializeComponent();

            NavigationCacheMode = NavigationCacheMode.Enabled;

            ResetRecognitionUi();
            SetInputEnabled(false);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e) {
            base.OnNavigatedTo(e);

            if (initializingPage) {
                return;
            }

            initializingPage = true;

            try {
                await LibraryTools.EnsureLoadedAsync();

                libraryReady = true;

                SetInputEnabled(recognitionCancellation == null);

                if (e.NavigationMode == NavigationMode.New && e.Parameter as string == "file" && Frame.Content == this) {
                    activeTask = RunAsync(false);
                    await activeTask;
                }
            } catch (Exception ex) {
                Status.Text = AppText.Get("LibraryLoadFailed");
                Status.Visibility = Visibility.Visible;

                await DiagnosticLog.WriteAsync("library-load", ex);
            } finally {
                initializingPage = false;

                SetInputEnabled(recognitionCancellation == null);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) {
            recognitionCancellation?.Cancel();
            queueSendCancellation?.Cancel();
            offlineDialog?.Hide();
            ResetRecognitionUi();
            HideNotice();

            base.OnNavigatedFrom(e);
        }

        internal async Task StopAsync() {
            recognitionCancellation?.Cancel();
            queueSendCancellation?.Cancel();
            offlineDialog?.Hide();
            HideNotice();

            await activeTask;

            try {
                await LibraryTools.PendingSave;
            } catch {

            }
        }

        internal bool HandleBack() {
            if (recognitionCancellation == null) {
                if (queueSendCancellation == null) {
                    return false;
                }

                queueSendCancellation.Cancel();
                offlineDialog?.Hide();

                return true;
            }

            CancelRecognition();
            return true;
        }

        private void SetInputEnabled(bool enabled) {
            enabled = enabled && libraryReady;
            ListenButton.IsEnabled = enabled || (recognitionCancellation != null && !recognitionCancellation.IsCancellationRequested);
            HistoryButton.IsEnabled = SettingsButton.IsEnabled = MicrophoneSettingsButton.IsEnabled = OfflineRecognitionsButton.IsEnabled = enabled;
        }

        private async Task RunAsync(bool useMicrophone) {
            if (!libraryReady || recognitionCancellation != null || queueSendCancellation != null) {
                return;
            }

            var recognitionOperation = new CancellationTokenSource();
            recognitionCancellation = recognitionOperation;
            operationCompleted = false;

            BeginRecognitionUi(useMicrophone, false);
            SetInputEnabled(false);

            string selectedMicrophoneId = ApplicationData.Current.LocalSettings.Values["MicrophoneId"] as string ?? "";
            MicrophoneCaptureMode captureMode = AppPreferences.CaptureMode;

            try {
                await RecognizeOnceAsync(useMicrophone, selectedMicrophoneId, captureMode, recognitionOperation.Token);
            } catch (OperationCanceledException) {
                Status.Text = AppText.Get("RecognitionCancelled");
            } catch (UnauthorizedAccessException) {
                Status.Text = AppText.Get("MicrophonePermissionRequired");
            } catch (Exception ex) {
                Status.Text = AppText.Get("RecognitionFailed");
                await DiagnosticLog.WriteAsync("recognition", ex);
            } finally {
                ResetRecognitionUi();
                recognitionCancellation = null;

                SetInputEnabled(true);

                if (!operationCompleted && !recognitionOperation.IsCancellationRequested) {
                    ShowFailureNotice(Status.Text);
                }

                recognitionOperation.Dispose();
            }
        }

        private async Task RecognizeOnceAsync(bool useMicrophone, string selectedMicrophoneId, MicrophoneCaptureMode captureMode, CancellationToken token) {
            StorageFile recording = null;

            try {
                StorageFile inputFile;

                if (useMicrophone) {
                    Status.Text = AppText.Get("RecognitionListening");

                    try {
                        inputFile = recording = await RecognitionService.RecordAsync(selectedMicrophoneId, captureMode, 12, token, null, () => {
                            if (!token.IsCancellationRequested) {
                                StartRecognitionWindow();
                            }
                        });
                    } finally {
                        EndRecognitionWindow(false);
                    }
                } else {
                    var picker = new FileOpenPicker {
                        SuggestedStartLocation = PickerLocationId.MusicLibrary
                    };

                    foreach (string extension in new[] { ".wav", ".mp3", ".m4a", ".wma", ".flac", ".mp4" }) {
                        picker.FileTypeFilter.Add(extension);
                    }

                    inputFile = await picker.PickSingleFileAsync();
                    if (inputFile == null) {
                        recognitionCancellation?.Cancel();

                        Status.Text = AppText.Get("AudioFileSelectionCancelled");

                        return;
                    }
                }

                token.ThrowIfCancellationRequested();

                Status.Text = AppText.Get("AudioPreparing");

                var pcm = useMicrophone ? await RecognitionService.ReadRecordingAsync(inputFile) : await RecognitionService.DecodeAsync(inputFile, token);
                token.ThrowIfCancellationRequested();

                if (useMicrophone) {
                    var measured = SignalMeasurements.Measure(pcm);

                    if (measured.Peak == 0) {
                        Status.Text = AppText.Get("MicrophoneInputSilent");
                        return;
                    }

                    if (measured.AcRms < 32 && Math.Abs(measured.Mean) >= 32) {
                        Status.Text = AppText.Get("MicrophoneInputDcOnly");
                        return;
                    }

                    Array.Resize(ref pcm, 192000);
                }

                Status.Text = AppText.Get("RecognitionInProgress");

                string signature = await RecognitionService.CreateSignatureAsync(pcm, token);
                token.ThrowIfCancellationRequested();

                if (!RecognitionService.HasInternetAccess()) {
                    await SaveForLaterAsync(signature, pcm.Length / 16);
                    return;
                }

                JsonObject result;
                try {
                    result = await RecognitionService.RecognizeSignatureAsync(signature, pcm.Length / 16, token);
                } catch (Exception) {
                    token.ThrowIfCancellationRequested();

                    if (!RecognitionService.HasInternetAccess()) {
                        await SaveForLaterAsync(signature, pcm.Length / 16);
                        return;
                    }

                    throw;
                }

                token.ThrowIfCancellationRequested();

                var track = result.GetNamedObject("track", null);
                if (track == null) {
                    Status.Text = AppText.Get("RecognitionNoMatch");
                    return;
                }

                var songEntry = SongLibrary.FromTrack(track);
                if (string.IsNullOrWhiteSpace(songEntry.Title)) {
                    Status.Text = AppText.Get("RecognitionMissingTrackTitle");
                    return;
                }

                await LibraryTools.CommitAsync(() => LibraryTools.Library.AddRecognition(songEntry));

                operationCompleted = true;
                Status.Text = "";

                ShowRecognitionNotice(songEntry);
            } finally {
                if (recording != null) {
                    try {
                        await recording.DeleteAsync();
                    } catch {

                    }
                }
            }
        }

        private void Layout_SizeChanged(object sender, SizeChangedEventArgs e) {
            if (RecognitionButtonBox == null) {
                return;
            }

            bool compact = e.NewSize.Height < 400;
            RecognitionButtonBox.Width = RecognitionButtonBox.Height = compact ? 112 : 176;
            RecognitionPrompt.FontSize = compact ? 20 : 26;
            RecognitionPrompt.Margin = new Thickness(0, 0, 0, compact ? 12 : 28);
            RecognitionHint.Margin = new Thickness(0, compact ? 12 : 24, 0, 0);
        }

        private void StartRecognitionWindow() {
            windowWatch.Restart();

            WindowProgressTrack.Visibility = WindowProgressArc.Visibility = Visibility.Visible;
            RecognitionPrompt.Text = AppText.Get("ShellListening");

            SetWindowProgress(0);

            windowTimer = new DispatcherTimer {
                Interval = TimeSpan.FromMilliseconds(50)
            };

            windowTimer.Tick += (sender, args) => {
                if (sender != windowTimer || recognitionCancellation == null || recognitionCancellation.IsCancellationRequested) {
                    return;
                }

                SetWindowProgress(Math.Min(1, windowWatch.Elapsed.TotalSeconds / 12));
            };

            windowTimer.Start();
        }

        private void SetWindowProgress(double progress) {
            if (progress <= 0) {
                WindowProgressArc.Data = null;
                return;
            }

            // Avoid coincident endpoints for a complete 360 degree arc
            double angle = Math.Min(progress, .999999) * 2 * Math.PI;
            var figure = new PathFigure {
                StartPoint = new Point(88, 6),
                IsClosed = false
            };

            figure.Segments.Add(new ArcSegment {
                Point = new Point(88 + 82 * Math.Sin(angle), 88 - 82 * Math.Cos(angle)),
                Size = new Size(82, 82),
                IsLargeArc = progress > .5,
                SweepDirection = SweepDirection.Clockwise
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            WindowProgressArc.Data = geometry;
        }

        private void EndRecognitionWindow(bool testOnly) {
            windowTimer?.Stop();
            windowTimer = null;

            windowWatch.Stop();

            if (testOnly || recognitionCancellation == null || recognitionCancellation.IsCancellationRequested) {
                return;
            }

            SetWindowProgress(1);

            RecognitionPrompt.Text = AppText.Get("ShellProcessing");
        }

        private void ResetRecognitionUi() {
            windowTimer?.Stop();
            windowTimer = null;

            windowWatch.Reset();

            WindowProgressTrack.Visibility = WindowProgressArc.Visibility = Visibility.Collapsed;
            WindowProgressArc.Data = null;
            RecognitionPrompt.Text = AppText.Get("ShellReady");
            RecognitionHint.Text = AppText.Get("ShellWindowHint");
        }

        private void ShowNotice() {
            noticeTimer?.Stop();
            noticeAnimation?.Stop();

            noticeVersion++;

            ResultNotice.Visibility = Visibility.Visible;
            NoticeTranslation.Y = 80;
            ResultNotice.Opacity = 0;

            AnimateNotice(false);

            noticeTimer = new DispatcherTimer {
                Interval = TimeSpan.FromSeconds(6)
            };

            noticeTimer.Tick += (sender, args) => {
                if (sender != noticeTimer) {
                    return;
                }

                noticeTimer.Stop();
                AnimateNotice(true);
            };

            noticeTimer.Start();
        }

        private void AnimateNotice(bool dismiss) {
            noticeAnimation?.Stop();

            int version = noticeVersion;

            var slide = new DoubleAnimation {
                To = dismiss ? Math.Max(100, ResultNotice.ActualHeight + 24) : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = new CubicEase {
                    EasingMode = dismiss ? EasingMode.EaseIn : EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(slide, NoticeTranslation);
            Storyboard.SetTargetProperty(slide, "Y");

            var fade = new DoubleAnimation {
                To = dismiss ? 0 : 1,
                Duration = slide.Duration
            };
            Storyboard.SetTarget(fade, ResultNotice);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var animation = new Storyboard();
            animation.Children.Add(slide);
            animation.Children.Add(fade);

            if (dismiss) {
                animation.Completed += (sender, args) => {
                    if (version == noticeVersion) {
                        HideNotice();
                    }
                };
            }

            noticeAnimation = animation;
            animation.Begin();
        }

        private void DismissNotice_Click(object sender, RoutedEventArgs e) {
            noticeTimer?.Stop();
            AnimateNotice(true);
        }

        private void BeginRecognitionUi(bool microphone, bool testOnly) {
            HideNotice();

            if (testOnly) {
                return;
            }

            // File recognition can be started from history, but its result belongs on home
            Status.Text = "";
            RecognitionPrompt.Text = AppText.Get(microphone ? "ShellPreparing" : "ShellProcessing");
            RecognitionHint.Text = AppText.Get("ShellCancelHint");
        }

        private void CancelRecognition() {
            if (recognitionCancellation == null) {
                return;
            }

            recognitionCancellation.Cancel();
            ResetRecognitionUi();
            HideNotice();

            // Wait for MediaCapture cleanup before allowing a new recording
            ListenButton.IsEnabled = false;
        }

        private void HideNotice() {
            noticeVersion++;

            noticeTimer?.Stop();
            noticeTimer = null;

            noticeAnimation?.Stop();
            noticeAnimation = null;

            ResultNotice.Visibility = Visibility.Collapsed;
            noticeSong = null;
        }

        private void ShowRecognitionNotice(SongEntry song) {
            noticeSong = song;
            NoticeTitle.Text = song.Title;
            NoticeArtist.Text = song.Artist;

            ShowNotice();
        }

        private async Task SaveForLaterAsync(string signature, int sampleMilliseconds) {
            await PendingRecognitionQueue.AddAsync(signature, sampleMilliseconds);
            operationCompleted = true;

            Status.Text = AppText.Get("OfflineQueueSaved");
            noticeSong = null;
            NoticeTitle.Text = AppText.Get("OfflineQueueSavedTitle");
            NoticeArtist.Text = AppText.Get("OfflineQueueSaved");

            ShowNotice();
        }

        private void ShowFailureNotice(string message) {
            noticeSong = null;
            NoticeTitle.Text = AppText.Get("ShellNotRecognized");
            NoticeArtist.Text = message;

            ShowNotice();
        }

        private void Notice_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            var element = e.OriginalSource as DependencyObject;

            while (element != null && element != ResultNotice) {
                if (element is Button) {
                    return;
                }

                element = VisualTreeHelper.GetParent(element);
            }

            if (recognitionCancellation == null && noticeSong != null) {
                Frame.Navigate(typeof(DetailsPage), noticeSong);
            }
        }

        private void OpenHistory_Click(object sender, RoutedEventArgs e) {
            if (recognitionCancellation == null) {
                Frame.Navigate(typeof(LibraryPage));
            }
        }

        private void MicrophoneSettings_Click(object sender, RoutedEventArgs e) {
            if (recognitionCancellation == null) {
                Frame.Navigate(typeof(MicrophoneSettingsPage));
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e) {
            if (recognitionCancellation == null) {
                Frame.Navigate(typeof(AppSettingsPage));
            }
        }

        private async void About_Click(object sender, RoutedEventArgs e) {
            if (recognitionCancellation != null || !libraryReady) {
                return;
            }

            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            var dialog = new AboutDialog {
                Title = AppText.Get("ShellAbout"),
                SecondaryButtonText = AppText.Get("ShellClose"),
                Body = "Resonance " + version.Major + "." + version.Minor + "." + version.Build + "\n\n" + AppText.Get("ShellAboutBody")
            };

            await dialog.ShowAsync();
        }

        private async void Listen_Click(object sender, RoutedEventArgs e) {
            if (queueSendCancellation != null) {
                return;
            }

            if (recognitionCancellation != null) {
                CancelRecognition();
                return;
            }

            activeTask = RunAsync(true);
            await activeTask;
        }

        private async void OfflineRecognitions_Click(object sender, RoutedEventArgs e) {
            if (recognitionCancellation != null || queueSendCancellation != null || !libraryReady) {
                return;
            }

            try {
                await PendingRecognitionQueue.EnsureLoadedAsync();

                var dialog = new OfflineQueueDialog {
                    Title = AppText.Get("OfflineQueueTitle"),
                    SecondaryButtonText = AppText.Get("ShellClose")
                };

                dialog.Send.Content = AppText.Get("OfflineQueueSend");
                dialog.Remove.Content = AppText.Get("OfflineQueueRemove");

                dialog.ItemsList.SelectionChanged += (s, args) => dialog.Remove.IsEnabled = dialog.ItemsList.SelectedItems.Count > 0;

                dialog.Send.Click += OfflineQueueSend_Click;
                dialog.Remove.Click += OfflineQueueRemove_Click;

                dialog.Status.Text = "";
                dialog.Remove.IsEnabled = false;

                offlineDialog = dialog;

                RefreshOfflineQueue(dialog.ItemsList, dialog.Summary, dialog.Send, dialog.Status);

                await dialog.ShowAsync();
            } catch (Exception ex) {
                Status.Text = AppText.Get("ActionFailedPrefix") + ex.Message;
                ShowFailureNotice(Status.Text);
            } finally {
                offlineDialog = null;
            }
        }

        private async void OfflineQueueSend_Click(object sender, RoutedEventArgs e) {
            var dialog = offlineDialog;
            if (dialog == null) {
                return;
            }

            activeTask = SendPendingRecognitionsAsync(dialog.ItemsList, dialog.Summary, dialog.Send, dialog.Remove, dialog.Status);
            await activeTask;
        }

        private async void OfflineQueueRemove_Click(object sender, RoutedEventArgs e) {
            var dialog = offlineDialog;
            if (dialog == null) {
                return;
            }

            var selected = dialog.ItemsList.SelectedItems.Cast<ListViewItem>().Select(item => item.Tag as string).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
            try {
                await PendingRecognitionQueue.RemoveAsync(selected);

                dialog.Status.Text = AppText.Get("OfflineQueueRemoved");
                RefreshOfflineQueue(dialog.ItemsList, dialog.Summary, dialog.Send, dialog.Status);
            } catch (Exception ex) {
                dialog.Status.Text = ex.Message;
            }
        }

        private void RefreshOfflineQueue(ListView list, TextBlock summary, Button sendButton, TextBlock queueStatus) {
            list.Items.Clear();

            foreach (var pending in PendingRecognitionQueue.Items) {
                list.Items.Add(new ListViewItem {
                    Content = pending.Display,
                    Tag = pending.Id
                });
            }

            summary.Text = string.Format(AppText.Get("OfflineQueueSummary"), PendingRecognitionQueue.Items.Count);
            sendButton.IsEnabled = PendingRecognitionQueue.Items.Count > 0 && queueSendCancellation == null;

            if (PendingRecognitionQueue.Items.Count == 0 && string.IsNullOrWhiteSpace(queueStatus.Text)) {
                queueStatus.Text = AppText.Get("OfflineQueueEmpty");
            }
        }

        private async Task SendPendingRecognitionsAsync(ListView list, TextBlock summary, Button sendButton, Button removeButton, TextBlock queueStatus) {
            if (PendingRecognitionQueue.Items.Count == 0) {
                queueStatus.Text = AppText.Get("OfflineQueueEmpty");
                return;
            }

            if (!RecognitionService.HasInternetAccess()) {
                queueStatus.Text = AppText.Get("OfflineQueueNeedInternet");
                Status.Text = queueStatus.Text;

                return;
            }

            var queueSendOperation = new CancellationTokenSource();
            queueSendCancellation = queueSendOperation;

            SetInputEnabled(false);

            queueStatus.Text = AppText.Get("OfflineQueueSending");

            int recognized = 0;
            int notRecognized = 0;
            bool failed = false;

            try {
                foreach (var pending in PendingRecognitionQueue.Items.ToArray()) {
                    queueSendOperation.Token.ThrowIfCancellationRequested();

                    try {
                        var result = await RecognitionService.RecognizeSignatureAsync(pending.Signature, pending.SampleMilliseconds, queueSendOperation.Token);
                        queueSendOperation.Token.ThrowIfCancellationRequested();

                        var track = result.GetNamedObject("track", null);
                        var song = track == null ? null : SongLibrary.FromTrack(track);

                        if (song == null || string.IsNullOrWhiteSpace(song.Title)) {
                            ++notRecognized;
                        } else {
                            song.OfflineQueueId = pending.Id;

                            await LibraryTools.CommitAsync(() => {
                                if (!LibraryTools.Library.ContainsOfflineRecognition(pending.Id)) {
                                    LibraryTools.Library.AddRecognition(song);
                                }
                            });

                            ++recognized;
                        }

                        await PendingRecognitionQueue.RemoveAsync(new[] { pending.Id });

                        RefreshOfflineQueue(list, summary, sendButton, queueStatus);
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        queueStatus.Text = RecognitionService.HasInternetAccess() ? AppText.Get("ActionFailedPrefix") + ex.Message : AppText.Get("OfflineQueueNeedInternet");
                        Status.Text = queueStatus.Text;

                        try {
                            await DiagnosticLog.WriteAsync("offline-recognition-send", ex);
                        } catch {

                        }

                        failed = true;
                        break;
                    }
                }

                if (!queueSendOperation.IsCancellationRequested && !failed) {
                    queueStatus.Text = string.Format(AppText.Get("OfflineQueueSent"), recognized);

                    if (notRecognized > 0) {
                        queueStatus.Text += "\n" + string.Format(AppText.Get("OfflineQueueNoMatch"), notRecognized);
                    }

                    Status.Text = queueStatus.Text;
                }
            } catch (OperationCanceledException) {
                queueStatus.Text = AppText.Get("RecognitionCancelled");
                Status.Text = queueStatus.Text;
            } finally {
                queueSendCancellation = null;
                queueSendOperation.Dispose();

                SetInputEnabled(true);

                RefreshOfflineQueue(list, summary, sendButton, queueStatus);
            }
        }
    }
}
