using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace App {
    internal sealed class MicrophoneLevel {
        private readonly StackPanel levelPanel;
        private readonly ProgressBar levelBar;
        private readonly TextBlock levelText;

        internal MicrophoneLevel(StackPanel panel, ProgressBar bar, TextBlock text) {
            levelPanel = panel;
            levelBar = bar;
            levelText = text;
        }

        private DispatcherTimer levelTimer;
        private RuntimeComponent.AudioLevelState levelState;
        private ulong lastLevelFrame;
        private DateTime lastLevelUpdate;

        internal void Start(RuntimeComponent.AudioLevelState state) {
            Stop();

            levelPanel.Visibility = Visibility.Visible;
            levelState = state;
            lastLevelFrame = 0;
            lastLevelUpdate = DateTime.UtcNow;
            levelText.Text = AppText.Get(state == null ? "MeterUnavailable" : "MeterWaiting");

            if (state == null) {
                return;
            }

            levelTimer = new DispatcherTimer {
                Interval = TimeSpan.FromMilliseconds(100)
            };

            levelTimer.Tick += (sender, args) => {
                if (sender != levelTimer || levelState == null) {
                    return;
                }

                var reading = levelState.Read();
                if (reading.Frames == 0 || reading.Frames == lastLevelFrame) {
                    if ((DateTime.UtcNow - lastLevelUpdate).TotalSeconds > 1) {
                        levelBar.Value = 0;
                        levelText.Text = AppText.Get("MeterWaiting");
                    }

                    return;
                }

                lastLevelFrame = reading.Frames;
                lastLevelUpdate = DateTime.UtcNow;

                double decibels = reading.AcRms > 0 ? 20 * Math.Log10(reading.AcRms) : -120;
                levelBar.Value = Math.Max(0, Math.Min(100, (decibels + 60) * 100 / 60));
                levelText.Text = string.Format(AppText.Get("MeterReading"), reading.Peak * 100, decibels <= -120 ? "−∞" : decibels.ToString("F1"));

                if (reading.Peak >= .98) {
                    levelText.Text += " · " + AppText.Get("MeterClipping");
                }
            };

            levelTimer.Start();
        }

        internal void Stop() {
            levelTimer?.Stop();

            levelTimer = null;
            levelState = null;

            if (levelPanel != null) {
                levelPanel.Visibility = Visibility.Collapsed;
                levelBar.Value = 0;
                levelText.Text = "";
            }
        }
    }
}
