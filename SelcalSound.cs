using System;
using System.IO;
using System.Media;
using System.Text;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VpilotBliss
{
    internal sealed class SelcalSound : IDisposable
    {
        private readonly object gate = new object();
        private SoundPlayer player;
        private MemoryStream stream;
        private bool disposed;

        public void Play()
        {
            PlayWave(SelcalRecording.Value);
        }

        private static readonly Lazy<byte[]> SelcalRecording = new Lazy<byte[]>(DecodeSelcalRecording);
        public void PlayWake() => PlayWave(GenerateWake());

        internal static byte[] DecodeSelcalRecording()
        {
            using (var recording = typeof(SelcalSound).Assembly.GetManifestResourceStream("VpilotBliss.Selcal.mp3"))
            {
                if (recording == null) throw new InvalidOperationException("SELCAL recording is missing.");
                using (var reader = new StreamMediaFoundationReader(recording, null))
                using (var chimeStream = new MemoryStream(GenerateSelcalChime(reader.TotalTime.TotalSeconds), false))
                using (var chimeReader = new WaveFileReader(chimeStream))
                using (var output = new MemoryStream())
                {
                    var mixer = new MixingSampleProvider(new[] { Stereo(reader), Stereo(chimeReader) });
                    var samples = new List<float>();
                    var buffer = new float[4096];
                    float peak = 0;
                    int count;
                    while ((count = mixer.Read(buffer, 0, buffer.Length)) > 0)
                        for (int i = 0; i < count; i++) { samples.Add(buffer[i]); peak = Math.Max(peak, Math.Abs(buffer[i])); }
                    float gain = peak > 0.95f ? 0.95f / peak : 1;
                    using (var pcm = new MemoryStream())
                    using (var writer = new BinaryWriter(pcm, Encoding.ASCII, true))
                    {
                        foreach (float sample in samples) writer.Write((short)(sample * gain * short.MaxValue));
                        writer.Flush();
                        pcm.Position = 0;
                        using (var source = new RawSourceWaveStream(pcm, new WaveFormat(44100, 16, 2)))
                            WaveFileWriter.WriteWavFileToStream(output, source);
                    }
                    return output.ToArray();
                }
            }
        }

        internal static byte[] GenerateSelcalChime(double seconds)
        {
            // Original descending two-note chime, synthesized without sampled audio.
            const int rate = 44100;
            int samples = (int)(Math.Max(0.01, Math.Min(seconds, 60)) * rate);
            int gap = Math.Min(1764, samples / 10);
            int firstPulse = (samples - gap) / 2;
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
                for (int i = 0; i < samples; i++)
                {
                    if (i >= firstPulse && i < firstPulse + gap) { writer.Write((short)0); continue; }
                    bool first = i < firstPulse;
                    int position = first ? i : i - firstPulse - gap;
                    int duration = first ? firstPulse : samples - firstPulse - gap;
                    double frequency = first ? 1150 : 575;
                    double phase = 2 * Math.PI * frequency * position / rate;
                    double ramp = Math.Min(1, Math.Min(position, duration - 1 - position) / (rate * 0.003));
                    double decay = Math.Pow(1.0 - position / (double)duration, 1.2);
                    double tone = Math.Sin(phase);
                    writer.Write((short)(tone * ramp * decay * 0.65 * short.MaxValue));
                }
                writer.Flush();
                return output.ToArray();
            }
        }

        private static ISampleProvider Stereo(WaveStream wave)
        {
            ISampleProvider provider = wave.ToSampleProvider();
            if (provider.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
            if (provider.WaveFormat.Channels != 2) throw new InvalidOperationException("Alert recording must be mono or stereo.");
            return provider.WaveFormat.SampleRate == 44100 ? provider : new WdlResamplingSampleProvider(provider, 44100);
        }
        internal static byte[] GenerateWake()
        {
            // Two soft, lower notes: 120ms + 40ms gap + 120ms.
            // Rounded fades and a gentle overtone keep it distinct without a piercing edge.
            const int rate = 44100, pulse = 5292, gap = 1764;
            const int samples = pulse * 2 + gap;
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
                for (int i = 0; i < samples; i++)
                {
                    if (i >= pulse && i < pulse + gap) { writer.Write((short)0); continue; }
                    int position = i < pulse ? i : i - pulse - gap;
                    double frequency = i < pulse ? 523.25 : 659.25;
                    double phase = 2 * Math.PI * frequency * position / rate;
                    double fade = Math.Min(1, Math.Min(position, pulse - 1 - position) / (rate * 0.012));
                    double value = 0.92 * Math.Sin(phase) + 0.08 * Math.Sin(phase * 2);
                    writer.Write((short)(value * fade * 0.60 * short.MaxValue));
                }
                writer.Flush();
                return output.ToArray();
            }
        }
        private void PlayWave(byte[] wave)
        {
            lock (gate)
            {
                if (disposed) return;
                Release();
                stream = new MemoryStream(wave, false);
                player = new SoundPlayer(stream);
                player.Load();
                player.Play();
            }
        }
        private void Release()
        {
            player?.Stop();
            player?.Dispose();
            stream?.Dispose();
            player = null;
            stream = null;
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; Release(); }
        }
    }
}





