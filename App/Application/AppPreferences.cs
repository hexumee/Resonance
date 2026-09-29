using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace App {
    internal enum MicrophoneCaptureMode {
        Standard = 0,
        Raw = 1
    }

    internal static class AppPreferences {
        private static object Get(string key) {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object value) ? value : null;
        }

        internal static MicrophoneCaptureMode CaptureMode {
            get {
                var value = Get("CaptureMode");

                if (value is int storedMode && storedMode == (int)MicrophoneCaptureMode.Raw) {
                    return MicrophoneCaptureMode.Raw;
                }

                return MicrophoneCaptureMode.Standard;
            }
            set {
                ApplicationData.Current.LocalSettings.Values["CaptureMode"] = value == MicrophoneCaptureMode.Raw ? (int)MicrophoneCaptureMode.Raw : (int)MicrophoneCaptureMode.Standard;
            }
        }

        internal static bool CacheCovers {
            get {
                return !(Get("CacheCovers") is bool) || (bool)Get("CacheCovers");
            }
            set {
                ApplicationData.Current.LocalSettings.Values["CacheCovers"] = value;
            }
        }

        internal static bool Logging {
            get {
                return Get("Logging") is bool && (bool)Get("Logging");
            } set {
                ApplicationData.Current.LocalSettings.Values["Logging"] = value;
            }
        }
    }

    internal static class DiagnosticLog {
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        internal static async Task WriteAsync(string operation, Exception error) {
            await AppendAsync(operation + " " + error.GetType().Name + " 0x" + error.HResult.ToString("X8"));
        }

        internal static Task MetricAsync(string operation, long milliseconds, ulong memory) {
            return AppendAsync(operation + " elapsed_ms=" + milliseconds + " app_memory_bytes=" + memory);
        }

        private static async Task AppendAsync(string message) {
            if (!AppPreferences.Logging) {
                return;
            }

            await gate.WaitAsync();

            try {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync("diagnostics.txt", CreationCollisionOption.OpenIfExists);

                if ((await file.GetBasicPropertiesAsync()).Size > 128 * 1024) {
                    if (!(await folder.TryGetItemAsync("diagnostics-previous.txt") is StorageFile previous)) {
                        await file.RenameAsync("diagnostics-previous.txt");
                    } else {
                        await file.MoveAndReplaceAsync(previous);
                    }

                    file = await folder.CreateFileAsync("diagnostics.txt");
                }

                // Do not retain exception messages/stack traces: they may contain paths or request data
                string line = DateTimeOffset.Now.ToString("o") + " " + message + "\r\n";
                await FileIO.AppendTextAsync(file, line);
            } catch {
                // Logging must never interrupt recognition or library operations
            } finally {
                gate.Release();
            }
        }

        internal static async Task<string> ReadAsync() {
            await gate.WaitAsync();

            try {
                string result = "Resonance diagnostics\r\n";

                foreach (var name in new[] { "diagnostics-previous.txt", "diagnostics.txt" }) {
                    if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(name) is StorageFile file) {
                        result += await FileIO.ReadTextAsync(file);
                    }
                }

                return result;
            } finally {
                gate.Release();
            }
        }
    }
}
