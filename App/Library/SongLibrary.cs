using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace App {
    internal sealed class SongLibrary {
        internal ObservableCollection<SongEntry> History { get; } = new ObservableCollection<SongEntry>();
        private readonly SemaphoreSlim saveLock = new SemaphoreSlim(1, 1);
        private readonly Func<string, string> getText;
        private bool loaded;

        internal SongLibrary(Func<string, string> getText = null) {
            this.getText = getText ?? (key => key);
        }

        private static string Text(JsonObject item, string name) {
            return item.TryGetValue(name, out IJsonValue value) && value.ValueType == JsonValueType.String ? value.GetString() : "";
        }

        private static JsonObject Object(JsonObject item, string name) {
            return item.TryGetValue(name, out IJsonValue value) && value.ValueType == JsonValueType.Object ? value.GetObject() : null;
        }

        internal static SongEntry FromTrack(JsonObject track) {
            var song = new SongEntry {
                Title = Text(track, "title"),
                Artist = Text(track, "subtitle"),
                TrackKey = Text(track, "key"),
                Url = Text(track, "url"),
                Date = DateTimeOffset.Now.ToString("o")
            };

            var images = Object(track, "images");
            song.CoverUrl = images == null ? "" : Text(images, "coverart");

            var genres = Object(track, "genres");
            song.Genre = genres == null ? "" : Text(genres, "primary");

            if (track.TryGetValue("sections", out IJsonValue sections) && sections.ValueType == JsonValueType.Array) {
                foreach (var sectionValue in sections.GetArray()) {
                    if (sectionValue.ValueType != JsonValueType.Object) {
                        continue;
                    }

                    var section = sectionValue.GetObject();

                    if (Text(section, "type") != "SONG") {
                        continue;
                    }

                    if (!section.TryGetValue("metadata", out IJsonValue metadata) || metadata.ValueType != JsonValueType.Array) {
                        continue;
                    }

                    foreach (var field in metadata.GetArray()) {
                        if (field.ValueType != JsonValueType.Object) {
                            continue;
                        }

                        var item = field.GetObject();
                        string title = Text(item, "title");

                        if (title == "Album") {
                            song.Album = Text(item, "text");
                        } else if (title == "Released") {
                            song.ReleaseYear = Text(item, "text");
                        }
                    }
                }
            }

            return song;
        }

        private SongEntry ReadSong(JsonObject item) {
            string title = Text(item, "title");

            if (string.IsNullOrWhiteSpace(title)) {
                throw new FormatException(getText("LibraryTrackMissingTitle"));
            }

            return new SongEntry {
                Title = title,
                Artist = Text(item, "artist"),
                Date = Text(item, "date"),
                Url = Text(item, "url"),
                TrackKey = Text(item, "key"),
                Album = Text(item, "album"),
                ReleaseYear = Text(item, "year"),
                Genre = Text(item, "genre"),
                CoverUrl = Text(item, "cover"),
                OfflineQueueId = Text(item, "offlineQueueId")
            };
        }

        private static JsonObject WriteSong(SongEntry song) {
            return new JsonObject {
                ["title"] = JsonValue.CreateStringValue(song.Title ?? ""),
                ["artist"] = JsonValue.CreateStringValue(song.Artist ?? ""),
                ["date"] = JsonValue.CreateStringValue(song.Date ?? ""),
                ["url"] = JsonValue.CreateStringValue(song.Url ?? ""),
                ["key"] = JsonValue.CreateStringValue(song.TrackKey ?? ""),
                ["album"] = JsonValue.CreateStringValue(song.Album ?? ""),
                ["year"] = JsonValue.CreateStringValue(song.ReleaseYear ?? ""),
                ["genre"] = JsonValue.CreateStringValue(song.Genre ?? ""),
                ["cover"] = JsonValue.CreateStringValue(song.CoverUrl ?? ""),
                ["offlineQueueId"] = JsonValue.CreateStringValue(song.OfflineQueueId ?? "")
            };
        }

        internal async Task LoadAsync() {
            if (loaded) {
                return;
            }

            var folder = ApplicationData.Current.LocalFolder;

            if (await folder.TryGetItemAsync("library.json") is StorageFile file) {
                Restore(await FileIO.ReadTextAsync(file));
            } else {
                Restore(Snapshot());
            }
        }

        internal void Restore(string data) {
            var root = JsonObject.Parse(data);

            if (root.GetNamedNumber("version", 0) != 1) {
                throw new FormatException(getText("LibraryUnsupportedVersion"));
            }

            SongEntry[] history = root.GetNamedArray("history").Select(v => ReadSong(v.GetObject())).ToArray();

            // Parse everything before publishing; a broken file must never be replaced by an empty library
            History.Clear();

            foreach (var song in history.Take(500)) {
                History.Add(song);
            }

            loaded = true;
        }

        internal string Snapshot() {
            var history = new JsonArray();

            foreach (var song in History) {
                history.Add(WriteSong(song));
            }

            return new JsonObject {
                ["version"] = JsonValue.CreateNumberValue(1),
                ["history"] = history
            }.Stringify();
        }

        internal void AddRecognition(SongEntry song) {
            History.Insert(0, song);

            while (History.Count > 500) {
                History.RemoveAt(History.Count - 1);
            }
        }

        internal bool ContainsOfflineRecognition(string queueId) {
            return !string.IsNullOrWhiteSpace(queueId) && History.Any(song => string.Equals(song.OfflineQueueId, queueId, StringComparison.Ordinal));
        }

        internal async Task SaveAsync() {
            if (!loaded) {
                throw new InvalidOperationException(getText("LibraryNotLoaded"));
            }

            await saveLock.WaitAsync();

            StorageFile pending = null;
            try {
                string data = Snapshot();
                var folder = ApplicationData.Current.LocalFolder;

                pending = await folder.CreateFileAsync("library-" + Guid.NewGuid() + ".tmp");
                await FileIO.WriteTextAsync(pending, data);

                if (!(await folder.TryGetItemAsync("library.json") is StorageFile target)) {
                    await pending.RenameAsync("library.json");
                } else {
                    await pending.MoveAndReplaceAsync(target);
                }

                pending = null;
            } finally {
                if (pending != null) {
                    try {
                        await pending.DeleteAsync();
                    } catch {
                        // should not happen
                    }
                }

                saveLock.Release();
            }
        }
    }
}
