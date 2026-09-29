using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;

namespace App {
    internal static class RecognitionService {
        private const int SampleRate = 16000;
        private const int RecognitionWindowSeconds = 12;
        private const int MaximumSampleCount = SampleRate * RecognitionWindowSeconds;

        internal static MediaEncodingProfile PcmProfile() {
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(SampleRate, 1, 16);

            return profile;
        }

        internal static async Task<StorageFile> RecordAsync(string deviceId, MicrophoneCaptureMode captureMode, int seconds, CancellationToken token, Action<RuntimeComponent.AudioLevelState> meterReady = null, Action recordingStarted = null) {
            var recordingFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync("recording-" + Guid.NewGuid() + ".wav");

            try {
                using (var capture = new MediaCapture()) {
                    var settings = new MediaCaptureInitializationSettings {
                        StreamingCaptureMode = StreamingCaptureMode.Audio,
                        AudioDeviceId = deviceId ?? "",
                        MediaCategory = MediaCategory.Other,
                        AudioProcessing = captureMode == MicrophoneCaptureMode.Raw ? AudioProcessing.Raw : AudioProcessing.Default
                    };

                    await capture.InitializeAsync(settings);

                    try {
                        var meterState = new RuntimeComponent.AudioLevelState();

                        var effectProperties = new Windows.Foundation.Collections.PropertySet();
                        effectProperties.Add("Meter", meterState);

                        var meterEffect = new Windows.Media.Effects.AudioEffectDefinition("RuntimeComponent.AudioMeter", effectProperties);
                        await capture.AddAudioEffectAsync(meterEffect);

                        meterReady?.Invoke(meterState);
                    } catch (Exception exception) {
                        meterReady?.Invoke(null);
                        await DiagnosticLog.WriteAsync("meter-unavailable", exception);
                    }

                    token.ThrowIfCancellationRequested();

                    await capture.StartRecordToStorageFileAsync(PcmProfile(), recordingFile);

                    try {
                        token.ThrowIfCancellationRequested();
                        recordingStarted?.Invoke();

                        await Task.Delay(TimeSpan.FromSeconds(seconds), token);
                    } finally {
                        await capture.StopRecordAsync();
                    }
                }

                return recordingFile;
            } catch {
                await recordingFile.DeleteAsync();
                throw;
            }
        }

        internal static async Task<short[]> DecodeAsync(StorageFile sourceFile, CancellationToken token) {
            var decodedFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync("decoded-" + Guid.NewGuid() + ".wav");

            try {
                // Decode only the central 12 seconds, avoiding whole-file PCM allocations
                var musicProperties = await sourceFile.Properties.GetMusicPropertiesAsync();
                var transcoder = new MediaTranscoder();

                if (musicProperties.Duration > TimeSpan.FromSeconds(RecognitionWindowSeconds)) {
                    transcoder.TrimStartTime = TimeSpan.FromTicks(musicProperties.Duration.Ticks / 2) - TimeSpan.FromSeconds(RecognitionWindowSeconds / 2.0);
                }

                transcoder.TrimStopTime = transcoder.TrimStartTime + TimeSpan.FromSeconds(RecognitionWindowSeconds);

                var preparedTranscode = await transcoder.PrepareFileTranscodeAsync(sourceFile, decodedFile, PcmProfile());
                if (!preparedTranscode.CanTranscode) {
                    throw new InvalidOperationException(AppText.Get("AudioFileDecodeFailedPrefix") + preparedTranscode.FailureReason);
                }

                await preparedTranscode.TranscodeAsync().AsTask(token);

                var buffer = await FileIO.ReadBufferAsync(decodedFile);
                var bytes = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer)) {
                    reader.ReadBytes(bytes);
                }

                return ReadWave(bytes);
            } finally {
                await decodedFile.DeleteAsync();
            }
        }

        internal static async Task<short[]> ReadRecordingAsync(StorageFile recordingFile) {
            var buffer = await FileIO.ReadBufferAsync(recordingFile);
            var bytes = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) {
                reader.ReadBytes(bytes);
            }

            return ReadWave(bytes, false);
        }

        internal static short[] ReadWave(byte[] bytes, bool padToRecognitionWindow = true) {
            using (var reader = new BinaryReader(new MemoryStream(bytes))) {
                if (new string(reader.ReadChars(4)) != "RIFF") {
                    throw new InvalidDataException(AppText.Get("WaveRiffHeaderInvalid"));
                }

                reader.ReadUInt32();

                if (new string(reader.ReadChars(4)) != "WAVE") {
                    throw new InvalidDataException(AppText.Get("WaveFormatInvalid"));
                }

                bool hasPcmFormat = false;
                long dataOffset = 0;
                uint dataLength = 0;
                while (reader.BaseStream.Position + 8 <= bytes.Length) {
                    string chunkId = new string(reader.ReadChars(4));
                    uint chunkLength = reader.ReadUInt32();
                    long chunkStart = reader.BaseStream.Position;
                    long chunkEnd = chunkStart + chunkLength;

                    if (chunkEnd > bytes.Length) {
                        throw new InvalidDataException(AppText.Get("WaveFileCorrupt"));
                    }

                    if (chunkId == "fmt " && chunkLength >= 16) {
                        ushort format = reader.ReadUInt16();
                        ushort channelCount = reader.ReadUInt16();
                        uint sampleRate = reader.ReadUInt32();

                        reader.ReadUInt32();

                        ushort blockAlignment = reader.ReadUInt16();
                        ushort bitsPerSample = reader.ReadUInt16();

                        hasPcmFormat = format == 1 && channelCount == 1 && sampleRate == SampleRate && bitsPerSample == 16 && blockAlignment == 2;
                    } else if (chunkId == "data") {
                        dataOffset = chunkStart;
                        dataLength = chunkLength;
                    }

                    reader.BaseStream.Position = chunkEnd + (chunkLength & 1);
                }

                if (!hasPcmFormat || dataLength == 0 || dataLength % 2 != 0) {
                    throw new InvalidDataException(AppText.Get("PcmFormatRequired"));
                }

                reader.BaseStream.Position = dataOffset;

                int outputSampleCount = padToRecognitionWindow ? MaximumSampleCount : (int)Math.Min((uint)MaximumSampleCount, dataLength / 2);
                var samples = new short[outputSampleCount];
                int samplesToRead = (int)Math.Min((uint)samples.Length, dataLength / 2);
                for (int sampleIndex = 0; sampleIndex < samplesToRead; ++sampleIndex) {
                    samples[sampleIndex] = reader.ReadInt16();
                }

                return samples;
            }
        }

        internal static bool HasInternetAccess() {
            try {
                var connectionProfile = NetworkInformation.GetInternetConnectionProfile();
                if (connectionProfile == null) {
                    return false;
                }

                var connectivityLevel = connectionProfile.GetNetworkConnectivityLevel();
                return connectivityLevel == NetworkConnectivityLevel.InternetAccess || connectivityLevel == NetworkConnectivityLevel.ConstrainedInternetAccess;
            } catch {
                return false;
            }
        }

        internal static async Task<string> CreateSignatureAsync(short[] pcm, CancellationToken token) {
            var fingerprintWatch = Stopwatch.StartNew();
            string signature = await RuntimeComponent.FingerprintGenerator.GenerateAsync(pcm).AsTask(token);

            await DiagnosticLog.MetricAsync("fingerprint", fingerprintWatch.ElapsedMilliseconds, Windows.System.MemoryManager.AppMemoryUsage);
            token.ThrowIfCancellationRequested();

            return signature;
        }

        internal static async Task<JsonObject> RecognizeSignatureAsync(string signature, int sampleMilliseconds, CancellationToken token) {
            if (string.IsNullOrWhiteSpace(signature)  || sampleMilliseconds < 1 || sampleMilliseconds > RecognitionWindowSeconds * 1000) {
                throw new ArgumentException(AppText.Get("RecognitionFingerprintRequired"));
            }

            token.ThrowIfCancellationRequested();

            uint timestamp = unchecked((uint)(DateTimeOffset.UtcNow - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMilliseconds);
            var requestBody = new JsonObject {
                ["geolocation"] = new JsonObject {
                    ["altitude"] = JsonValue.CreateNumberValue(300),
                    ["latitude"] = JsonValue.CreateNumberValue(45),
                    ["longitude"] = JsonValue.CreateNumberValue(2)
                },
                ["signature"] = new JsonObject {
                    ["samplems"] = JsonValue.CreateNumberValue(sampleMilliseconds),
                    ["timestamp"] = JsonValue.CreateNumberValue(timestamp),
                    ["uri"] = JsonValue.CreateStringValue(signature)
                },
                ["timestamp"] = JsonValue.CreateNumberValue(timestamp),
                ["timezone"] = JsonValue.CreateStringValue("Europe/Paris")
            };

            string requestUrl = "https://amp.shazam.com/discovery/v5/en/US/android/-/tag/" + Guid.NewGuid().ToString().ToUpperInvariant() + "/" + Guid.NewGuid() + "?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&video=v3";

            using (var client = new HttpClient()) {
                using (var content = new HttpStringContent(requestBody.Stringify(), Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/json")) {
                    using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                        requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));

                        client.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 6.0.1; SM-G920F Build/MMB29K)");
                        client.DefaultRequestHeaders.TryAppendWithoutValidation("Content-Language", "en_US");

                        using (var response = await client.PostAsync(new Uri(requestUrl), content).AsTask(requestTimeout.Token)) {
                            if ((int)response.StatusCode == 429) {
                                throw new InvalidOperationException(AppText.Get("ShazamRateLimit"));
                            }

                            response.EnsureSuccessStatusCode();

                            string responseBody = await response.Content.ReadAsStringAsync().AsTask(requestTimeout.Token);
                            return JsonObject.Parse(responseBody);
                        }
                    }
                }
            }
        }

        internal static async Task<Tuple<Uri, Uri>> FindPreviewAsync(SongEntry song, CancellationToken token) {
            if (song == null || string.IsNullOrWhiteSpace(song.Title)) {
                return null;
            }

            string searchTerm = string.IsNullOrWhiteSpace(song.Artist) ? song.Title : song.Artist + " " + song.Title;
            string countryCode;

            try {
                countryCode = Windows.System.UserProfile.GlobalizationPreferences.HomeGeographicRegion;
            } catch {
                countryCode = "US";
            }

            if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2) {
                countryCode = "US";
            }

            var requestUri = new Uri("https://itunes.apple.com/search?term=" + Uri.EscapeDataString(searchTerm) + "&entity=song&limit=25&country=" + Uri.EscapeDataString(countryCode));
            using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                using (var client = new HttpClient()) {
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));

                    using (var response = await client.GetAsync(requestUri).AsTask(requestTimeout.Token)) {
                        response.EnsureSuccessStatusCode();

                        string responseBody = await response.Content.ReadAsStringAsync().AsTask(requestTimeout.Token);
                        var searchResults = JsonObject.Parse(responseBody);

                        foreach (var resultValue in searchResults.GetNamedArray("results", new JsonArray())) {
                            if (resultValue.ValueType != JsonValueType.Object) {
                                continue;
                            }

                            var resultTrack = resultValue.GetObject();
                            if (NormalizePreviewName(GetString(resultTrack, "trackName")) != NormalizePreviewName(song.Title)) {
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(song.Artist) && NormalizePreviewName(GetString(resultTrack, "artistName")) != NormalizePreviewName(song.Artist)) {
                                continue;
                            }

                            if (TryPreviewUri(GetString(resultTrack, "previewUrl"), out Uri previewUri) && TryHttpsUri(GetString(resultTrack, "trackViewUrl"), out Uri storeUri)) {
                                return Tuple.Create(previewUri, storeUri);
                            }
                        }
                    }
                }
            }

            return null;
        }

        private static string GetString(JsonObject jsonObject, string propertyName) {
            return jsonObject.TryGetValue(propertyName, out IJsonValue value) && value.ValueType == JsonValueType.String ? value.GetString() : "";
        }

        private static bool TryHttpsUri(string value, out Uri uri) {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https";
        }

        private static bool TryPreviewUri(string value, out Uri uri) {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http");
        }

        private static string NormalizePreviewName(string value) {
            var normalized = new StringBuilder();

            foreach (char character in (value ?? "").Normalize(NormalizationForm.FormKC)) {
                if (char.IsLetterOrDigit(character)) {
                    normalized.Append(char.ToLowerInvariant(character));
                }
            }

            return normalized.ToString();
        }
    }
}
