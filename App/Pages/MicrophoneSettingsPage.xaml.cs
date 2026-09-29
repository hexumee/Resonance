using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace App {
    public sealed class MicrophoneEntry {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public sealed partial class MicrophoneSettingsPage : Page {
        private readonly ObservableCollection<MicrophoneEntry> microphones = new ObservableCollection<MicrophoneEntry>();

        private bool loadingInputs;
        private bool refreshingInputs;
        private bool preferencesReady;

        private InMemoryRandomAccessStream testSegment;
        private MediaPlayer testPlayer;
        private MediaSource playbackSource;
        private IRandomAccessStream playbackStream;
        private DispatcherTimer playbackTimer;
        private DateTime playbackStarted;
        private bool playbackOpened;
        private bool testPlaying;

        private CancellationTokenSource operation;
        private Task activeTask = Task.CompletedTask;

        private MicrophoneLevel levelMeter;

        private bool initializingPage;

        public MicrophoneSettingsPage() {
            InitializeComponent();

            NavigationCacheMode = NavigationCacheMode.Enabled;
            levelMeter = new MicrophoneLevel(LevelPanel, LevelBar, LevelText);

            SetInputEnabled(false);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e) {
            base.OnNavigatedTo(e);

            if (initializingPage) {
                return;
            }

            initializingPage = true;

            try {
                await InitializeInputsAsync();
            } finally {
                initializingPage = false;

                SetInputEnabled(true);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) {
            operation?.Cancel();
            StopLevelMeter();
            StopTestPlayback();

            base.OnNavigatedFrom(e);
        }

        internal async Task StopAsync() {
            operation?.Cancel();
            StopTestPlayback();

            await activeTask;

            StopLevelMeter();
        }

        internal bool HandleBack() {
            if (operation != null) {
                Cancel_Click(this, new RoutedEventArgs());

                return true;
            }

            return refreshingInputs || initializingPage;
        }

        private void SetInputEnabled(bool enabled) {
            enabled = enabled && preferencesReady && !refreshingInputs && operation == null;
            TestButton.IsEnabled = RefreshButton.IsEnabled = ToneButton.IsEnabled = Microphones.IsEnabled = CaptureMode.IsEnabled = enabled;
            PlayTestButton.IsEnabled = enabled && testSegment != null;
            CancelButton.Visibility = operation == null ? Visibility.Collapsed : Visibility.Visible;
            CancelButton.IsEnabled = operation != null && !operation.IsCancellationRequested;
        }

        private void StartLevelMeter(RuntimeComponent.AudioLevelState state) {
            levelMeter.Start(state);
        }

        private void StopLevelMeter() {
            levelMeter.Stop();
        }

        private async void Test_Click(object sender, RoutedEventArgs e) {
            if (!preferencesReady || operation != null || refreshingInputs) {
                return;
            }

            activeTask = TestAsync();
            await activeTask;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) {
            operation?.Cancel();
            StopLevelMeter();

            CancelButton.IsEnabled = false;
        }

        private async Task TestAsync() {
            var current = new CancellationTokenSource();
            operation = current;

            ClearTestSegment();
            SetInputEnabled(false);

            StorageFile recording = null;
            try {
                Status.Text = AppText.Get("MicrophoneCheckStarted");
                Signal.Text = "";

                var selected = Microphones.SelectedItem as MicrophoneEntry;
                MicrophoneCaptureMode captureMode = CaptureMode.SelectedIndex == 1 ? MicrophoneCaptureMode.Raw : MicrophoneCaptureMode.Standard;

                recording = await RecognitionService.RecordAsync(selected?.Id, captureMode, 3, current.Token, state => {
                    if (!current.IsCancellationRequested) {
                        StartLevelMeter(state);
                    }
                });

                StopLevelMeter();

                current.Token.ThrowIfCancellationRequested();

                var pcm = await RecognitionService.ReadRecordingAsync(recording);
                current.Token.ThrowIfCancellationRequested();

                var segment = new InMemoryRandomAccessStream();
                try {
                    using (var writer = new DataWriter(segment)) {
                        try {
                            writer.WriteBytes(AudioDiagnostics.EncodeWave(pcm));

                            await writer.StoreAsync().AsTask(current.Token);
                            await writer.FlushAsync().AsTask(current.Token);
                        } finally {
                            writer.DetachStream();
                        }
                    }

                    current.Token.ThrowIfCancellationRequested();
                    segment.Seek(0);

                    testSegment = segment;
                } catch {
                    segment.Dispose();
                    throw;
                }

                var measured = SignalMeasurements.Measure(pcm);
                Signal.Text = string.Format(AppText.Get("MicrophoneSignalReport"), measured.Peak * 100.0 / 32768, AudioDiagnostics.Db(measured.Rms), AudioDiagnostics.Db(measured.AcRms), measured.Mean * 100 / 32768);
                Status.Text = AppText.Get(measured.Peak == 0 ? "MicrophoneInputSilent" : measured.AcRms < 32 && Math.Abs(measured.Mean) >= 32 ? "MicrophoneInputDcOnly" : measured.AcRms < 32 ? "MicrophoneSignalWeak" : "MicrophoneSignalReady");
            } catch (OperationCanceledException) {
                Status.Text = AppText.Get("RecognitionCancelled");
            } catch (UnauthorizedAccessException) {
                Status.Text = AppText.Get("MicrophonePermissionRequired");
            } catch (Exception ex) {
                Status.Text = AppText.Get("RecognitionFailed");
                await DiagnosticLog.WriteAsync("microphone", ex);
            } finally {
                StopLevelMeter();

                if (recording != null) {
                    try {
                        await recording.DeleteAsync();
                    } catch {

                    }
                }

                operation = null;
                current.Dispose();

                SetInputEnabled(true);
            }
        }

        private void StopTestPlayback() {
            testPlaying = false;

            playbackTimer?.Stop();
            playbackTimer = null;

            var player = testPlayer;
            testPlayer = null;

            if (player != null) {
                player.Pause();
                player.Source = null;
                player.Dispose();
            }

            playbackSource?.Dispose();
            playbackSource = null;

            playbackStream?.Dispose();
            playbackStream = null;

            PlayTestButton.Content = AppText.Get("PlayLastMicrophoneTest");
            ToneButton.Content = AppText.Get("PlayDiagnosticTone");
            PlaybackStatus.Text = "";
        }

        private void ClearTestSegment() {
            StopTestPlayback();

            testSegment?.Dispose();
            testSegment = null;

            PlayTestButton.IsEnabled = false;
        }

        private void PlayTest_Click(object sender, RoutedEventArgs e) {
            if (operation != null || refreshingInputs || testSegment == null) {
                return;
            }

            if (testPlaying) {
                StopTestPlayback();
                return;
            }

            try {
                playbackStream = testSegment.CloneStream();
                playbackStream.Seek(0);

                StartPlayback(MediaSource.CreateFromStream(playbackStream, "audio/wav"), false);
            } catch (Exception ex) {
                StopTestPlayback();

                PlaybackStatus.Text = AppText.Get("AudioPlaybackFailedPrefix") + ex.Message;
            }
        }

        private void Tone_Click(object sender, RoutedEventArgs e) {
            if (operation != null || refreshingInputs) {
                return;
            }

            if (testPlaying) {
                StopTestPlayback();
                return;
            }

            try {
                StartPlayback(MediaSource.CreateFromUri(new Uri("ms-appx:///Assets/diagnostic-tone.wav")), true);
            } catch (Exception ex) {
                StopTestPlayback();

                PlaybackStatus.Text = AppText.Get("AudioPlaybackFailedPrefix") + ex.Message;
            }
        }

        private void StartPlayback(MediaSource source, bool tone) {
            playbackSource = source;

            var player = new MediaPlayer {
                AutoPlay = false,
                Volume = 1,
                IsMuted = false,
                AudioCategory = MediaPlayerAudioCategory.Media
            };

            testPlayer = player;
            testPlaying = true;

            playbackOpened = false;
            playbackStarted = DateTime.UtcNow;

            player.CommandManager.IsEnabled = false;

            if (tone) {
                ToneButton.Content = AppText.Get("StopDiagnosticTone");
            } else {
                PlayTestButton.Content = AppText.Get("StopMicrophoneTestPlayback");
            }

            PlaybackStatus.Text = AppText.Get("AudioPlaybackStarting");

            // WinRT media callbacks run outside the UI thread, ignore callbacks from disposed players
            player.MediaOpened += async (sender, args) => {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                    if (testPlayer != sender) {
                        return;
                    }

                    playbackOpened = true;
                    sender.Play();
                });
            };

            player.MediaEnded += async (sender, args) => {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                    if (testPlayer != sender) {
                        return;
                    }

                    StopTestPlayback();

                    PlaybackStatus.Text = AppText.Get("AudioPlaybackCompleted");
                });
            };

            player.MediaFailed += async (sender, args) => {
                string error = args.Error + ": " + args.ErrorMessage + " (0x" + (args.ExtendedErrorCode?.HResult ?? 0).ToString("X8") + ")";

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => {
                    if (testPlayer != sender) {
                        return;
                    }

                    StopTestPlayback();

                    PlaybackStatus.Text = AppText.Get("AudioPlaybackFailedPrefix") + error;
                });
            };

            playbackTimer = new DispatcherTimer {
                Interval = TimeSpan.FromMilliseconds(200)
            };

            playbackTimer.Tick += (sender, args) => {
                if (testPlayer != player) {
                    return;
                }

                if (!playbackOpened && DateTime.UtcNow - playbackStarted > TimeSpan.FromSeconds(10)) {
                    StopTestPlayback();
                    PlaybackStatus.Text = AppText.Get("AudioPlaybackTimeout");

                    return;
                }

                var session = player.PlaybackSession;
                PlaybackStatus.Text = string.Format(AppText.Get("AudioPlaybackProgress"), tone ? AppText.Get("DiagnosticToneName") : AppText.Get("MicrophoneTestRecordingName"), session.Position.TotalSeconds, session.NaturalDuration.TotalSeconds, session.PlaybackState);
            };

            playbackTimer.Start();
            player.Source = source;
        }

        private async Task InitializeInputsAsync() {
            if (preferencesReady) {
                return;
            }

            loadingInputs = true;
            CaptureMode.SelectedIndex = AppPreferences.CaptureMode == MicrophoneCaptureMode.Raw ? 1 : 0;

            loadingInputs = false;

            await RefreshInputsAsync();
            preferencesReady = true;

            SetInputEnabled(true);
        }

        private async Task RefreshInputsAsync() {
            if (operation != null || refreshingInputs) {
                return;
            }

            refreshingInputs = true;
            loadingInputs = true;

            SetInputEnabled(false);

            try {
                var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
                var current = Microphones.SelectedItem as MicrophoneEntry;
                string wanted = current?.Id ?? ApplicationData.Current.LocalSettings.Values["MicrophoneId"] as string ?? "";

                microphones.Clear();

                microphones.Add(new MicrophoneEntry {
                    Id = "",
                    Name = AppText.Get("DefaultMicrophoneName")
                });

                foreach (var device in devices) {
                    if (device.IsEnabled) {
                        microphones.Add(new MicrophoneEntry {
                            Id = device.Id,
                            Name = device.Name
                        });
                    }
                }

                Microphones.ItemsSource = microphones;

                MicrophoneEntry selection = microphones[0];
                foreach (var device in microphones) {
                    if (device.Id == wanted) {
                        selection = device;
                    }
                }

                Microphones.SelectedItem = selection;

                if (!string.IsNullOrEmpty(wanted) && selection.Id != wanted) {
                    Signal.Text = AppText.Get("SavedMicrophoneUnavailable");
                } else if (microphones.Count <= 2) {
                    Signal.Text = AppText.Get("UnifiedPhoneMicrophoneHint");
                }
            } catch (Exception ex) {
                Status.Text = AppText.Get("MicrophoneListFailedPrefix") + ex.Message;
            } finally {
                loadingInputs = false;
                refreshingInputs = false;

                SetInputEnabled(true);
            }
        }

        private void Microphone_Changed(object sender, SelectionChangedEventArgs e) {
            if (loadingInputs || !preferencesReady) {
                return;
            }

            if (Microphones.SelectedItem is MicrophoneEntry selected) {
                ApplicationData.Current.LocalSettings.Values["MicrophoneId"] = selected.Id;
                Signal.Text = "";
            }
        }

        private void CaptureMode_Changed(object sender, SelectionChangedEventArgs e) {
            if (loadingInputs || !preferencesReady) {
                return;
            }

            AppPreferences.CaptureMode = CaptureMode.SelectedIndex == 1 ? MicrophoneCaptureMode.Raw : MicrophoneCaptureMode.Standard;
            Signal.Text = "";
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) {
            await RefreshInputsAsync();
        }
    }
}
