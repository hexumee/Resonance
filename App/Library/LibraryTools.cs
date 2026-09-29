using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.UI.Xaml.Media.Imaging;

namespace App {
    internal static class LibraryTools {
        internal static readonly SongLibrary Library = new SongLibrary(AppText.Get);
        private static readonly SemaphoreSlim saveGate = new SemaphoreSlim(1, 1);

        private static Task loadTask;

        internal static Task PendingSave {
            get;
            private set;
        } = Task.CompletedTask;

        internal static Task EnsureLoadedAsync() {
            if (loadTask == null || loadTask.IsFaulted || loadTask.IsCanceled) {
                loadTask = Library.LoadAsync();
            }

            return loadTask;
        }

        internal static Task CommitAsync(Action change) {
            return PendingSave = CommitCoreAsync(change);
        }

        private static async Task CommitCoreAsync(Action change) {
            await saveGate.WaitAsync();

            try {
                await EnsureLoadedAsync();

                string before = Library.Snapshot();

                try {
                    change();
                    await Library.SaveAsync();
                } catch {
                    Library.Restore(before);
                    throw;
                }
            } finally {
                saveGate.Release();
            }
        }

        internal static async Task<BitmapImage> ReadCoverAsync(SongEntry song, CancellationToken token) {
            StorageFile file = null;

            try {
                file = await CoverCache.GetAsync(song.CoverUrl, token);

                token.ThrowIfCancellationRequested();

                if (file == null) {
                    return null;
                }

                var bitmap = new BitmapImage {
                    DecodePixelWidth = 300
                };

                using (var stream = await file.OpenReadAsync()) {
                    await bitmap.SetSourceAsync(stream).AsTask(token);
                }

                token.ThrowIfCancellationRequested();

                return bitmap;
            } catch (OperationCanceledException) {
                throw;
            } catch {
                await CoverCache.ForgetAsync(file);
                throw;
            } finally {
                await CoverCache.ReleaseAsync(file);
            }
        }

        internal static async Task SavePickedFileAsync(StorageFile file, string data) {
            CachedFileManager.DeferUpdates(file);

            FileUpdateStatus updated;

            try {
                await FileIO.WriteTextAsync(file, data);
            } finally {
                updated = await CachedFileManager.CompleteUpdatesAsync(file);
            }

            if (updated != FileUpdateStatus.Complete && updated != FileUpdateStatus.CompleteAndRenamed) {
                throw new InvalidOperationException(AppText.Get("FileSaveUnconfirmedPrefix") + updated);
            }
        }
    }
}
