using System;
using NAudio.Wave;
using NewQ.Core.Signals;

namespace NewQ.App.Audio;

/// <summary>A running test signal on one audio route. Change <see cref="Settings"/> live; <see cref="Stop"/> fades out.</summary>
public sealed class TestSignal
{
    private readonly AudioEngine _engine;
    private readonly Provider _provider;
    private readonly ISampleProvider _input;

    internal TestSignal(AudioEngine engine, Guid routeId, int sampleRate, TestSignalSettings settings)
    {
        _engine = engine;
        RouteId = routeId;
        _provider = new Provider(new TestSignalGenerator(sampleRate, settings));
        _input = engine.AddInput(routeId, _provider);
    }

    public Guid RouteId { get; }
    public bool IsRunning => !_provider.Generator.IsFinished;

    public TestSignalSettings Settings
    {
        get => _provider.Generator.Settings;
        set => _provider.Generator.Settings = value;
    }

    /// <summary>Fades out (20 ms); the input then leaves the route mix by itself.</summary>
    public void Stop() => _provider.Generator.Stop();

    /// <summary>Removes the input immediately (used when the audio graph is rebuilt).</summary>
    internal void Detach() => _engine.RemoveInput(RouteId, _input);

    private sealed class Provider : ISampleProvider
    {
        public Provider(TestSignalGenerator generator)
        {
            Generator = generator;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(generator.SampleRate, 2);
        }

        public TestSignalGenerator Generator { get; }
        public WaveFormat WaveFormat { get; }

        // Returning fewer samples than requested makes the mixer drop this input (after the fade-out).
        public int Read(float[] buffer, int offset, int count) => Generator.Fill(buffer, offset, count / 2) * 2;
    }
}
