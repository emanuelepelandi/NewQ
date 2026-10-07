namespace NewQ.Core.Signals;

public enum TestSignalKind { Sine, PinkNoise, WhiteNoise, Sweep }

/// <summary>Which channels play: both, one, or alternating left/right every second (to check wiring).</summary>
public enum TestSignalChannels { Both, Left, Right, Alternate }

/// <param name="LevelDb">Peak level for tones, approximate peak for noise (dBFS).</param>
/// <param name="SweepSeconds">Duration of one logarithmic sweep; it then restarts.</param>
public sealed record TestSignalSettings(
    TestSignalKind Kind = TestSignalKind.Sine,
    double FrequencyHz = 1000,
    double LevelDb = -20,
    TestSignalChannels Channels = TestSignalChannels.Both,
    double SweepFromHz = 20,
    double SweepToHz = 20000,
    double SweepSeconds = 10);

/// <summary>
/// Audio test signals (stereo, interleaved float). Settings can change while running (thread-safe swap);
/// starts and stops with a short ramp so there are no clicks.
/// </summary>
public sealed class TestSignalGenerator
{
    private const double RampSeconds = 0.02;
    private const double AlternateSeconds = 1.0;

    private readonly int _sampleRate;
    private readonly int _rampFrames;
    private volatile TestSignalSettings _settings;
    private double _phase;
    private double _sweepTime;
    private long _frame;
    private double _ramp;          // 0..1 envelope
    private volatile bool _stopping;
    private uint _random = 0x9E3779B9;
    private double _b0, _b1, _b2, _b3, _b4, _b5, _b6; // pink noise filter state

    public TestSignalGenerator(int sampleRate, TestSignalSettings settings)
    {
        _sampleRate = sampleRate;
        _rampFrames = Math.Max(1, (int)(RampSeconds * sampleRate));
        _settings = settings;
    }

    public int SampleRate => _sampleRate;
    public TestSignalSettings Settings { get => _settings; set => _settings = value; }

    /// <summary>True once a stop was requested and the fade-out has completed.</summary>
    public bool IsFinished => _stopping && _ramp <= 0;

    /// <summary>Fades out over a few milliseconds; then <see cref="Fill"/> returns 0.</summary>
    public void Stop() => _stopping = true;

    /// <summary>Writes up to <paramref name="frames"/> stereo frames; returns the frames written (0 when finished).</summary>
    public int Fill(float[] buffer, int offset, int frames)
    {
        if (IsFinished) return 0;
        var s = _settings;
        var gain = Decibels.ToGain(s.LevelDb);
        var rampStep = 1.0 / _rampFrames;

        for (var f = 0; f < frames; f++)
        {
            _ramp = _stopping ? Math.Max(0, _ramp - rampStep) : Math.Min(1, _ramp + rampStep);
            var sample = Next(s) * gain * _ramp;

            var (left, right) = ChannelGains(s.Channels);
            buffer[offset + 2 * f] = (float)(sample * left);
            buffer[offset + 2 * f + 1] = (float)(sample * right);
            _frame++;

            if (_stopping && _ramp <= 0) return f + 1;
        }
        return frames;
    }

    private (double Left, double Right) ChannelGains(TestSignalChannels channels) => channels switch
    {
        TestSignalChannels.Left => (1, 0),
        TestSignalChannels.Right => (0, 1),
        TestSignalChannels.Alternate => (long)(_frame / (AlternateSeconds * _sampleRate)) % 2 == 0 ? (1, 0) : (0, 1),
        _ => (1, 1),
    };

    private double Next(TestSignalSettings s)
    {
        switch (s.Kind)
        {
            case TestSignalKind.Sine:
                return Oscillate(Math.Clamp(s.FrequencyHz, 1, _sampleRate / 2.0 - 1));

            case TestSignalKind.Sweep:
            {
                var from = Math.Clamp(Math.Min(s.SweepFromHz, s.SweepToHz), 1, _sampleRate / 2.0 - 1);
                var to = Math.Clamp(Math.Max(s.SweepFromHz, s.SweepToHz), 1, _sampleRate / 2.0 - 1);
                var duration = Math.Max(0.1, s.SweepSeconds);
                var frequency = from * Math.Pow(to / from, _sweepTime / duration); // logarithmic: equal time per octave
                _sweepTime += 1.0 / _sampleRate;
                if (_sweepTime >= duration) _sweepTime -= duration;
                return Oscillate(frequency);
            }

            case TestSignalKind.WhiteNoise:
                return White();

            default: // pink noise, Paul Kellet's refined filter (−3 dB/octave within ±0.05 dB above 9 Hz)
            {
                var w = White();
                _b0 = 0.99886 * _b0 + w * 0.0555179;
                _b1 = 0.99332 * _b1 + w * 0.0750759;
                _b2 = 0.96900 * _b2 + w * 0.1538520;
                _b3 = 0.86650 * _b3 + w * 0.3104856;
                _b4 = 0.55000 * _b4 + w * 0.5329522;
                _b5 = -0.7616 * _b5 - w * 0.0168980;
                var pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + w * 0.5362;
                _b6 = w * 0.115926;
                return Math.Clamp(pink * 0.11, -1, 1);
            }
        }
    }

    private double Oscillate(double frequency)
    {
        var value = Math.Sin(_phase);
        _phase += 2 * Math.PI * frequency / _sampleRate;
        if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
        return value;
    }

    /// <summary>Uniform white noise in [−1, 1] (xorshift32: fast, allocation-free, fine for audio).</summary>
    private double White()
    {
        var x = _random;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _random = x;
        return x / (double)uint.MaxValue * 2 - 1;
    }
}
