using System;
using System.IO;
using System.Text;

namespace App {
    internal struct SignalMeasurements {
        internal int Peak;
        internal double Rms, AcRms, Mean;

        internal static SignalMeasurements Measure(short[] pcm) {
            if (pcm == null || pcm.Length == 0) {
                throw new ArgumentException("Empty PCM");
            }

            double sum = 0, energy = 0; int peak = 0;
            foreach (short sample in pcm) {
                sum += sample;
                energy += (double)sample * sample;
                peak = Math.Max(peak, Math.Abs((int)sample));
            }

            double mean = sum/pcm.Length;

            return new SignalMeasurements {
                Peak = peak,
                Mean = mean,
                Rms = Math.Sqrt(energy/pcm.Length),
                AcRms = Math.Sqrt(Math.Max(0, energy/pcm.Length-mean*mean))
            };
        }
    }

    internal static class AudioDiagnostics {
        internal static string Db(double value) {
            return value == 0 ? "−∞ dBFS" : (20 * Math.Log10(value / 32768)).ToString("F1") + " dBFS";
        }

        internal static byte[] EncodeWave(short[] pcm) {
            if (pcm == null || pcm.Length == 0) {
                throw new ArgumentException("Empty PCM");
            }

            using (var memory = new MemoryStream()) {
                using (var writer = new BinaryWriter(memory, Encoding.UTF8, true)) {
                    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                    writer.Write(36 + pcm.Length * 2);

                    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    writer.Write(16);

                    writer.Write((ushort)1);
                    writer.Write((ushort)1);
                    writer.Write(16000);
                    writer.Write(32000);

                    writer.Write((ushort)2);
                    writer.Write((ushort)16);

                    writer.Write(Encoding.ASCII.GetBytes("data"));
                    writer.Write(pcm.Length * 2);

                    foreach (short sample in pcm) {
                        writer.Write(sample);
                    }
                }

                return memory.ToArray();
            }
        }
    }
}
