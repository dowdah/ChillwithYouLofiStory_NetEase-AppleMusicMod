using System;

namespace MusicBridge;

internal sealed class PcmFormat
{
    public int SampleRate, Channels, BitsPerSample;
    public long Frames;
    public double Duration => (double)Frames / SampleRate;
    public void Apply(NeteasePlaybackSource source)
    { source.SampleRate = SampleRate; source.Channels = Channels; source.BitsPerSample = BitsPerSample; source.PcmFrames = Frames; }
}

// Owned by one worker only. Units are interleaved PCM frames, never scalar samples.
internal interface INeteaseAudioDecoder : IDisposable
{
    PcmFormat Format { get; }
    int Read(float[] samples, int frames);
    void Seek(long frame);
}
