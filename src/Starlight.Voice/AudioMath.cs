namespace Starlight.Voice;

/// <summary>Small audio helpers: format conversion, resampling, level. Plain code, tested in the container.</summary>
public static class AudioMath
{
    /// <summary>Interleaved 32-bit float or 16-bit PCM bytes to mono floats.</summary>
    public static float[] ToMono(ReadOnlySpan<byte> bytes, int channels, bool isFloat, int bitsPerSample)
    {
        var bytesPerSample = bitsPerSample / 8;
        var frames = bytes.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var at = (f * channels + c) * bytesPerSample;
                sum += isFloat
                    ? BitConverter.ToSingle(bytes.Slice(at, 4))
                    : bitsPerSample == 16 ? BitConverter.ToInt16(bytes.Slice(at, 2)) / 32768f
                    : bitsPerSample == 24 ? ((bytes[at] | (bytes[at + 1] << 8) | ((sbyte)bytes[at + 2] << 16)) / 8388608f)
                    : BitConverter.ToInt32(bytes.Slice(at, 4)) / 2147483648f;
            }
            mono[f] = sum / channels;
        }
        return mono;
    }

    /// <summary>Resamples mono audio. Going down, a moving average first removes what the lower rate cannot hold.</summary>
    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0)
        {
            return input;
        }
        var source = input;
        if (toRate < fromRate)
        {
            var width = (int)Math.Ceiling((double)fromRate / toRate);
            source = new float[input.Length];
            double acc = 0;
            for (var i = 0; i < input.Length; i++)
            {
                acc += input[i];
                if (i >= width)
                {
                    acc -= input[i - width];
                }
                source[i] = (float)(acc / Math.Min(i + 1, width));
            }
        }
        var outLength = (int)((long)source.Length * toRate / fromRate);
        var output = new float[outLength];
        var step = (double)fromRate / toRate;
        for (var i = 0; i < outLength; i++)
        {
            var pos = i * step;
            var i0 = (int)pos;
            var i1 = Math.Min(i0 + 1, source.Length - 1);
            var t = (float)(pos - i0);
            output[i] = source[i0] * (1 - t) + source[i1] * t;
        }
        return output;
    }

    /// <summary>Loudness 0..1 for the orb, from RMS on a rough dB scale.</summary>
    public static double Level(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }
        var rms = Math.Sqrt(sum / samples.Length);
        var db = 20 * Math.Log10(Math.Max(rms, 1e-6));
        return Math.Clamp((db + 60) / 60, 0, 1); // -60 dB → 0, 0 dB → 1
    }
}
