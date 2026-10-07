using NewQ.Core.Signals;
using Xunit;

namespace NewQ.Core.Tests;

public class TestSignalTests
{
    private const int Rate = 48000;

    private static float[] Render(TestSignalGenerator gen, int frames)
    {
        var buffer = new float[frames * 2];
        var done = 0;
        while (done < frames)
        {
            var n = gen.Fill(buffer, done * 2, Math.Min(1024, frames - done));
            if (n == 0) break;
            done += n;
        }
        return buffer;
    }

    private static float[] Channel(float[] stereo, int ch) => Enumerable.Range(0, stereo.Length / 2).Select(i => stereo[2 * i + ch]).ToArray();

    private static int ZeroCrossings(float[] x, int from) =>
        Enumerable.Range(from + 1, x.Length - from - 1).Count(i => x[i - 1] < 0 && x[i] >= 0);

    [Fact]
    public void Sine_has_the_requested_frequency_and_level()
    {
        var gen = new TestSignalGenerator(Rate, new TestSignalSettings(TestSignalKind.Sine, 1000, -6));
        var left = Channel(Render(gen, Rate), 0);       // 1 s
        Assert.InRange(ZeroCrossings(left, 0), 998, 1001);
        var peak = left.Skip(Rate / 10).Max(Math.Abs);  // after the fade-in
        Assert.InRange(20 * Math.Log10(peak), -6.05, -5.95);
    }

    [Fact]
    public void Starts_without_a_click()
    {
        var gen = new TestSignalGenerator(Rate, new TestSignalSettings(TestSignalKind.WhiteNoise, LevelDb: 0));
        var left = Channel(Render(gen, 200), 0);
        Assert.True(Math.Abs(left[0]) < 0.01);           // ramp starts at ~0
    }

    [Fact]
    public void Stop_fades_out_then_ends()
    {
        var gen = new TestSignalGenerator(Rate, new TestSignalSettings(LevelDb: 0));
        Render(gen, Rate / 10);
        gen.Stop();
        var tail = Render(gen, Rate / 10);
        Assert.True(gen.IsFinished);
        Assert.Equal(0, gen.Fill(new float[64], 0, 32));
        Assert.True(Math.Abs(tail[(int)(0.015 * Rate) * 2]) < 0.3); // already low before the end of the 20 ms ramp
    }

    [Fact]
    public void Channel_modes_route_the_signal()
    {
        var left = Render(new TestSignalGenerator(Rate, new TestSignalSettings(Channels: TestSignalChannels.Left, LevelDb: 0)), 4800);
        Assert.True(Channel(left, 0).Max(Math.Abs) > 0.5);
        Assert.Equal(0, Channel(left, 1).Max(Math.Abs));

        var alt = Render(new TestSignalGenerator(Rate, new TestSignalSettings(Channels: TestSignalChannels.Alternate, LevelDb: 0)), 2 * Rate);
        var firstSecondRight = Channel(alt, 1).Take(Rate).Max(Math.Abs);
        var secondSecondLeft = Channel(alt, 0).Skip(Rate).Max(Math.Abs);
        var secondSecondRight = Channel(alt, 1).Skip(Rate).Max(Math.Abs);
        Assert.Equal(0, firstSecondRight);
        Assert.Equal(0, secondSecondLeft);
        Assert.True(secondSecondRight > 0.5);
    }

    [Fact]
    public void Sweep_goes_from_low_to_high()
    {
        var gen = new TestSignalGenerator(Rate, new TestSignalSettings(TestSignalKind.Sweep, SweepFromHz: 100, SweepToHz: 10000, SweepSeconds: 2, LevelDb: 0));
        var left = Channel(Render(gen, 2 * Rate), 0);
        var startCrossings = ZeroCrossings(left.Take(Rate / 10).ToArray(), 0);           // ~100–126 Hz → ~11 in 0.1 s
        var endCrossings = ZeroCrossings(left.Skip(2 * Rate - Rate / 10).ToArray(), 0); // ~8–10 kHz → ~900 in 0.1 s
        Assert.InRange(startCrossings, 8, 16);
        Assert.InRange(endCrossings, 700, 1000);
    }

    [Fact]
    public void Pink_noise_has_more_low_end_than_white()
    {
        static double HighFrequencyShare(float[] x)
        {
            // Energy of the first difference (a high-pass) relative to the total energy.
            double total = 0, diff = 0;
            for (var i = 1; i < x.Length; i++) { total += x[i] * x[i]; var d = x[i] - x[i - 1]; diff += d * d; }
            return diff / total;
        }
        var white = Channel(Render(new TestSignalGenerator(Rate, new TestSignalSettings(TestSignalKind.WhiteNoise, LevelDb: 0)), Rate), 0);
        var pink = Channel(Render(new TestSignalGenerator(Rate, new TestSignalSettings(TestSignalKind.PinkNoise, LevelDb: 0)), Rate), 0);
        Assert.True(HighFrequencyShare(pink) < HighFrequencyShare(white) / 3);
        Assert.True(pink.Max(Math.Abs) <= 1.0f);
    }

    [Fact]
    public void Settings_change_while_running()
    {
        var gen = new TestSignalGenerator(Rate, new TestSignalSettings(TestSignalKind.Sine, 1000, LevelDb: 0));
        Render(gen, Rate / 10);
        gen.Settings = gen.Settings with { LevelDb = -40 };
        var quiet = Channel(Render(gen, Rate / 10), 0);
        Assert.InRange(20 * Math.Log10(quiet.Max(Math.Abs)), -40.1, -39.9);
    }
}
