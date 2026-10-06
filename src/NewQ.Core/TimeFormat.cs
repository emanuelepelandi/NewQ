namespace NewQ.Core;

public static class TimeFormat
{
    /// <summary>Formats seconds as m:ss.cc (or h:mm:ss for long durations).</summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
    }
}

public static class Decibels
{
    /// <summary>Anything at or below this level is treated as silence.</summary>
    public const double Floor = -60.0;

    public static double ToGain(double db) => db <= Floor ? 0.0 : Math.Pow(10.0, db / 20.0);
}
