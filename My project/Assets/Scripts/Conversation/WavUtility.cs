using System;
using System.IO;
using UnityEngine;

/// <summary>Minimal 16-bit PCM WAV encoding for sending microphone audio to STT APIs.</summary>
public static class WavUtility
{
    /// <summary>Encode mono/stereo float samples as a 16-bit PCM WAV file.</summary>
    public static byte[] FromSamples(float[] samples, int channels, int sampleRate)
    {
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using var w = new BinaryWriter(stream);

        int dataBytes = samples.Length * 2;
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataBytes);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                       // PCM chunk size
        w.Write((short)1);                 // PCM format
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2); // byte rate
        w.Write((short)(channels * 2));     // block align
        w.Write((short)16);                 // bits per sample
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataBytes);
        foreach (float s in samples)
            w.Write((short)(Mathf.Clamp(s, -1f, 1f) * short.MaxValue));

        w.Flush();
        return stream.ToArray();
    }

    /// <summary>Build an AudioClip from raw little-endian 16-bit mono PCM (what ElevenLabs' pcm_* formats return).</summary>
    public static AudioClip ClipFromPcm16(byte[] pcm, int sampleRate, string name = "speech")
    {
        int count = pcm.Length / 2;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;

        var clip = AudioClip.Create(name, count, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    /// <summary>Peak absolute sample value, to tell silence from speech.</summary>
    public static float Peak(float[] samples)
    {
        float peak = 0f;
        foreach (float s in samples)
            peak = Mathf.Max(peak, Mathf.Abs(s));
        return peak;
    }
}
