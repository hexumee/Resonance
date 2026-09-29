using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace App {
    internal sealed class PendingRecognition {
        internal string Id { get; private set; }
        internal string Signature { get; private set; }
        internal int SampleMilliseconds { get; private set; }
        internal string Created { get; private set; }

        internal string Display {
            get {
                string date = DateTimeOffset.TryParse(Created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset created) ? created.ToLocalTime().ToString("g") : Created;
                return string.Format(AppText.Get("OfflineQueueItemSubtitle"), date, Math.Max(1, SampleMilliseconds / 1000));
            }
        }

        internal PendingRecognition(string id, string signature, int sampleMilliseconds, string created) {
            Id = id;
            Signature = signature;
            SampleMilliseconds = sampleMilliseconds;
            Created = created;
        }
    }

    internal static class PendingRecognitionQueue {
        private const int CurrentVersion = 1;
        private const int MaxItems = 500;
        private const int MaxFileSize = 8 * 1024 * 1024;
        private const string SignaturePrefix = "data:audio/vnd.shazam.sig;base64,";

        private static readonly ObservableCollection<PendingRecognition> items = new ObservableCollection<PendingRecognition>();
        private static readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);

        private static Task loadTask;
        private static bool loaded;

        internal static ObservableCollection<PendingRecognition> Items {
            get {
                return items;
            }
        }

        internal static Task EnsureLoadedAsync() {
            if (loadTask == null || loadTask.IsFaulted || loadTask.IsCanceled) {
                loadTask = LoadCoreAsync();
            }

            return loadTask;
        }

        internal static async Task AddAsync(string signature, int sampleMilliseconds) {
            await EnsureLoadedAsync();

            ValidateSignature(signature);

            if (sampleMilliseconds < 1 || sampleMilliseconds > 12000) {
                throw new ArgumentOutOfRangeException("sampleMilliseconds");
            }

            await CommitAsync(() => {
                if (items.Count >= MaxItems) {
                    throw new InvalidOperationException(AppText.Get("OfflineQueueFull"));
                }

                items.Insert(0, new PendingRecognition(Guid.NewGuid().ToString("N"), signature, sampleMilliseconds, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
            });
        }

        internal static async Task RemoveAsync(IEnumerable<string> ids) {
            await EnsureLoadedAsync();

            var wanted = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (wanted.Count == 0) {
                return;
            }

            await CommitAsync(() => {
                for (int index = items.Count - 1; index >= 0; --index) {
                    if (wanted.Contains(items[index].Id)) {
                        items.RemoveAt(index);
                    }
                }
            });
        }

        private static async Task LoadCoreAsync() {
            var folder = ApplicationData.Current.LocalFolder;

            if (await folder.TryGetItemAsync("pending-recognitions.json") is StorageFile file) {
                if ((await file.GetBasicPropertiesAsync()).Size > MaxFileSize) {
                    throw new InvalidOperationException(AppText.Get("OfflineQueueInvalid"));
                }

                Restore(await FileIO.ReadTextAsync(file));
            }

            loaded = true;
        }

        private static void Restore(string data) {
            var root = JsonObject.Parse(data);
            if (root.GetNamedNumber("version", 0) != CurrentVersion) {
                throw new FormatException(AppText.Get("OfflineQueueInvalid"));
            }

            var pending = root.GetNamedArray("pending", new JsonArray());
            if (pending.Count > MaxItems) {
                throw new FormatException(AppText.Get("OfflineQueueInvalid"));
            }

            var restored = new List<PendingRecognition>();
            foreach (var value in pending) {
                if (value.ValueType != JsonValueType.Object) {
                    throw new FormatException(AppText.Get("OfflineQueueInvalid"));
                }

                var item = value.GetObject();
                string id = GetText(item, "id");
                string signature = GetText(item, "signature");
                string created = GetText(item, "created");
                int milliseconds = checked((int)item.GetNamedNumber("samplems", 0));

                ValidateSignature(signature);

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(created) || milliseconds < 1 || milliseconds > 12000) {
                    throw new FormatException(AppText.Get("OfflineQueueInvalid"));
                }

                restored.Add(new PendingRecognition(id, signature, milliseconds, created));
            }

            items.Clear();

            foreach (var item in restored) {
                items.Add(item);
            }
        }

        private static string GetText(JsonObject item, string key) {
            return item.TryGetValue(key, out IJsonValue value) && value.ValueType == JsonValueType.String ? value.GetString() : "";
        }

        private static void ValidateSignature(string signature) {
            if (string.IsNullOrWhiteSpace(signature) || !signature.StartsWith(SignaturePrefix, StringComparison.Ordinal)) {
                throw new FormatException(AppText.Get("OfflineQueueInvalid"));
            }

            try {
                Convert.FromBase64String(signature.Substring(SignaturePrefix.Length));
            } catch (FormatException) {
                throw new FormatException(AppText.Get("OfflineQueueInvalid"));
            }
        }

        private static async Task CommitAsync(Action change) {
            if (!loaded) {
                await EnsureLoadedAsync();
            }

            await saveGate.WaitAsync();

            try {
                string before = Snapshot();

                try {
                    change();
                    await SaveCoreAsync();
                } catch {
                    Restore(before);
                    throw;
                }
            } finally {
                saveGate.Release();
            }
        }

        private static string Snapshot() {
            var pending = new JsonArray();

            foreach (var item in items) {
                pending.Add(new JsonObject {
                    ["id"] = JsonValue.CreateStringValue(item.Id),
                    ["signature"] = JsonValue.CreateStringValue(item.Signature),
                    ["samplems"] = JsonValue.CreateNumberValue(item.SampleMilliseconds),
                    ["created"] = JsonValue.CreateStringValue(item.Created)
                });
            }

            return new JsonObject {
                ["version"] = JsonValue.CreateNumberValue(CurrentVersion),
                ["pending"] = pending
            }.Stringify();
        }

        private static async Task SaveCoreAsync() {
            string data = Snapshot();

            if (EncodingByteCount(data) > MaxFileSize) {
                throw new InvalidOperationException(AppText.Get("OfflineQueueFull"));
            }

            var folder = ApplicationData.Current.LocalFolder;
            StorageFile pendingFile = null;

            try {
                pendingFile = await folder.CreateFileAsync("pending-recognitions-" + Guid.NewGuid().ToString("N") + ".tmp");
                await FileIO.WriteTextAsync(pendingFile, data);

                if (!(await folder.TryGetItemAsync("pending-recognitions.json") is StorageFile target)) {
                    await pendingFile.RenameAsync("pending-recognitions.json");
                } else {
                    await pendingFile.MoveAndReplaceAsync(target);
                }

                pendingFile = null;
            } finally {
                if (pendingFile != null) {
                    try {
                        await pendingFile.DeleteAsync();
                    } catch {

                    }
                }
            }
        }

        private static int EncodingByteCount(string value) {
            return System.Text.Encoding.UTF8.GetByteCount(value);
        }
    }
}
