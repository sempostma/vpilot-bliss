using System;
using System.Linq;
using VpilotBliss;

internal static class SelcalSoundCheck
{
    public static void Run()
    {
        byte[] wake = SelcalSound.GenerateWake();
        if (wake.Length != 44 + 12348 * 2 || System.Text.Encoding.ASCII.GetString(wake, 0, 4) != "RIFF")
            throw new Exception("Wake chime must be a 280ms PCM WAV.");
        for (int i = 5292; i < 7056; i++)
            if (BitConverter.ToInt16(wake, 44 + i * 2) != 0) throw new Exception("Wake pulses must have a silent 40ms gap.");
        int peak = 0;
        for (int i = 44; i < wake.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wake, i)));
        if (peak < 16000 || peak >= short.MaxValue) throw new Exception("Wake cue level is too quiet or clipped.");

        System.IO.File.WriteAllBytes(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wake-preview.wav"), wake);
        if (Energy(wake, 200, 523.25) <= Energy(wake, 200, 1400) * 10 ||
            Energy(wake, 7256, 659.25) <= Energy(wake, 7256, 2100) * 10)
            throw new Exception("Wake chime must use its softer lower pitches.");
        Console.WriteLine("PASS soft lower-pitched wake chime is 280ms long, has a silent gap and does not clip");
        byte[] recording = SelcalSound.DecodeSelcalRecording();
        using (var stream = new System.IO.MemoryStream(recording))
        using (var reader = new NAudio.Wave.WaveFileReader(stream))
        {
            if (reader.TotalTime.TotalSeconds <= 0) throw new Exception("SELCAL recording is empty.");
        }
        if (recording.SequenceEqual(wake)) throw new Exception("SELCAL and wake sounds must be separate.");
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selcal-mix-preview.wav"), recording);
        byte[] synthesized = SelcalSound.GenerateSelcalChime(1.25);
        if (synthesized.Length != 44 + (int)(1.25 * 44100) * 2)
            throw new Exception("Original chime must match the recording duration.");
        int sampleCount = (synthesized.Length - 44) / 2;
        int firstPulse = (sampleCount - 1764) / 2;
        for (int i = firstPulse; i < firstPulse + 1764; i++)
            if (BitConverter.ToInt16(synthesized, 44 + i * 2) != 0) throw new Exception("Two-note chime must have one silent gap.");
        if (Energy(synthesized, 1000, 1150) <= Energy(synthesized, 1000, 575) * 10 ||
            Energy(synthesized, firstPulse + 2764, 575) <= Energy(synthesized, firstPulse + 2764, 1150) * 10)
            throw new Exception("SELCAL chime must descend from high to low without restarting.");
        if (typeof(SelcalSound).Assembly.GetManifestResourceNames().Any(name => name.Contains("OriginalSelcal")))
            throw new Exception("Copied vPilot sound must not be embedded.");
        Console.WriteLine("PASS original synthesized SELCAL chime matches requested duration and replaces the copied recording");
    }
    private static double Energy(byte[] wav, int start, double frequency)
    {
        double real = 0, imaginary = 0;
        for (int i = 0; i < 4410; i++)
        {
            double value = BitConverter.ToInt16(wav, 44 + (start + i) * 2);
            double angle = 2 * Math.PI * frequency * i / 44100;
            real += value * Math.Cos(angle);
            imaginary += value * Math.Sin(angle);
        }
        return real * real + imaginary * imaginary;
    }
}

