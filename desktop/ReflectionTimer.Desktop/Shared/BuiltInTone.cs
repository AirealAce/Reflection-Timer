using NAudio.Wave;
using ReflectionTimer.Core;

namespace ReflectionTimer.Desktop;

// Original, synthesized notification tones: no sampled/commercial soundtrack.
internal sealed class BuiltInTone(SoundEvent kind) : ISampleProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
    private int position;
    private readonly double[] frequencies = kind switch {
        SoundEvent.Success => [523.25, 659.25, 783.99], SoundEvent.Failure => [392, 293.66, 220],
        SoundEvent.LowTime or SoundEvent.TimeReached => [440, 440, 554.37], _ => [659.25, 523.25, 783.99, 659.25]
    };
    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<float> buffer)
    {
        const int segment = 11025; // 250 ms, including a short gap per note.
        var count = Math.Min(buffer.Length, frequencies.Length * segment - position);
        for (var i = 0; i < count; i++, position++) {
            var local = position % segment; var time = local / 44100d;
            var envelope = Math.Min(1, time / .01) * Math.Clamp((.20 - time) / .04, 0, 1);
            buffer[i] = (float)(.18 * envelope * Math.Sin(2 * Math.PI * frequencies[position / segment] * time));
        }
        return count;
    }
    public static string PathFor(SoundEvent kind) => "builtin:" + kind;
}
