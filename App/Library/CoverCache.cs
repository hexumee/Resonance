using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;

namespace App {
    internal static class CoverCache {
        private const uint MaximumCoverBytes = 2 * 1024 * 1024;
        private const ulong MaximumCacheBytes = 20 * 1024 * 1024;

        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        private static Task<StorageFolder> FolderAsync() {
            return ApplicationData.Current.LocalCacheFolder.CreateFolderAsync("covers", CreationCollisionOption.OpenIfExists).AsTask();
        }

        internal static async Task<StorageFile> GetAsync(string url, CancellationToken token) {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || uri.Scheme != "https") {
                return null;
            }

            await gate.WaitAsync(token);

            StorageFile pending = null;
            try {
                var folder = await FolderAsync();
                var hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256).HashData(CryptographicBuffer.ConvertStringToBinary(url, BinaryStringEncoding.Utf8));
                string name = CryptographicBuffer.EncodeToHexString(hash) + ".img";

                if (AppPreferences.CacheCovers) {
                    var cached = await folder.TryGetItemAsync(name) as StorageFile;
                    if (cached != null) {
                        return cached;
                    }
                }

                pending = await folder.CreateFileAsync(Guid.NewGuid() + ".tmp");
                using (var client = new HttpClient()) {
                    using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).AsTask(token)) {
                        response.EnsureSuccessStatusCode();

                        // An unexpectedly redirected endpoint must not downgrade to cleartext HTTP
                        if (response.RequestMessage.RequestUri.Scheme != "https") {
                            throw new InvalidOperationException(AppText.Get("CoverRedirectInsecure"));
                        }

                        if (response.Content.Headers.ContentLength > MaximumCoverBytes) {
                            throw new InvalidOperationException(AppText.Get("CoverTooLarge"));
                        }

                        using (var input = await response.Content.ReadAsInputStreamAsync().AsTask(token)) {
                            using (var reader = new DataReader(input) {
                                InputStreamOptions = InputStreamOptions.Partial
                            }) {
                                using (var output = await pending.OpenAsync(FileAccessMode.ReadWrite)) {
                                    using (var writer = new DataWriter(output)) {
                                        uint total = 0;

                                        while (true) {
                                            uint count = await reader.LoadAsync(8192).AsTask(token);
                                            if (count == 0) {
                                                break;
                                            }

                                            total += count;

                                            if (total > MaximumCoverBytes) {
                                                throw new InvalidOperationException(AppText.Get("CoverTooLarge"));
                                            }

                                            var bytes = new byte[count];
                                            reader.ReadBytes(bytes);
                                            writer.WriteBytes(bytes);

                                            await writer.StoreAsync().AsTask(token);
                                        }

                                        if (total == 0) {
                                            throw new InvalidOperationException(AppText.Get("CoverEmpty"));
                                        }

                                        await writer.FlushAsync().AsTask(token);
                                    }
                                }
                            }
                        }
                    }
                }

                token.ThrowIfCancellationRequested();

                if (AppPreferences.CacheCovers) {
                    await pending.RenameAsync(name, NameCollisionOption.ReplaceExisting);
                    await TrimAsync(folder, name);
                }

                var result = pending;
                pending = null;

                return result;
            } finally {
                if (pending != null) {
                    try {
                        await pending.DeleteAsync();
                    } catch {
                        // should not happen
                    }
                }

                gate.Release();
            }
        }

        internal static async Task ReleaseAsync(StorageFile file) {
            if (file != null && file.Name.EndsWith(".tmp", StringComparison.Ordinal)) {
                try {
                    await file.DeleteAsync();
                } catch {
                    // should not happen
                }
            }
        }

        internal static async Task ForgetAsync(StorageFile file) {
            await gate.WaitAsync();

            try {
                if (file != null) {
                    try {
                        await file.DeleteAsync();
                    } catch {
                        // should not happen
                    }
                }
            } finally {
                gate.Release();
            }
        }

        private static async Task TrimAsync(StorageFolder folder, string keep) {
            var entries = new List<Tuple<StorageFile, ulong, DateTimeOffset>>();

            foreach (var file in await folder.GetFilesAsync()) {
                var properties = await file.GetBasicPropertiesAsync();
                entries.Add(Tuple.Create(file, properties.Size, properties.DateModified));
            }

            ulong total = 0;
            foreach (var entry in entries) {
                total += entry.Item2;
            }

            foreach (var entry in entries.OrderBy(item => item.Item3)) {
                if (total <= MaximumCacheBytes && entries.Count <= 100) {
                    break;
                }

                if (entry.Item1.Name == keep) {
                    continue;
                }

                await entry.Item1.DeleteAsync();

                total -= entry.Item2;
                entries.Remove(entry);
            }
        }

        internal static async Task ClearAsync() {
            await gate.WaitAsync();

            try {
                foreach (var file in await (await FolderAsync()).GetFilesAsync()) {
                    await file.DeleteAsync();
                }
            } finally {
                gate.Release();
            }
        }
    }
}
