using System;

namespace NewQ.App.Video;

/// <summary>
/// The colour effects of a layer on the CPU, with the same formula as the GPU shader (Shaders.cs, Effects): the
/// inspector draws its preview and its slider tracks with it, so they show exactly what the output will do.
/// </summary>
internal readonly struct ColorEffects
{
    private readonly float _contrast, _brightness, _saturation, _cos, _sin;
    private readonly bool _rotate, _invert;

    /// <param name="hueDegrees">-180…180</param>
    /// <param name="saturation">% (100 = unchanged)</param>
    /// <param name="contrast">% (100 = unchanged)</param>
    /// <param name="brightness">% added to every channel</param>
    public ColorEffects(double hueDegrees, double saturation, double contrast, double brightness, bool invert)
    {
        _contrast = (float)(contrast / 100);
        _brightness = (float)(brightness / 100);
        _saturation = (float)(saturation / 100);
        var radians = hueDegrees * Math.PI / 180;
        _rotate = hueDegrees != 0;
        _cos = (float)Math.Cos(radians);
        _sin = (float)Math.Sin(radians);
        _invert = invert;
    }

    public static ColorEffects Neutral => new(0, 100, 100, 0, false);

    /// <summary>Applies the effects to a colour (0–1 channels).</summary>
    public (float R, float G, float B) Apply(float r, float g, float b)
    {
        r = (r - 0.5f) * _contrast + 0.5f + _brightness;
        g = (g - 0.5f) * _contrast + 0.5f + _brightness;
        b = (b - 0.5f) * _contrast + 0.5f + _brightness;
        var l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        r = l + (r - l) * _saturation;
        g = l + (g - l) * _saturation;
        b = l + (b - l) * _saturation;
        if (_rotate)
        {
            // Rotation around the grey axis g = (1,1,1)/√3: c·cos + (g×c)·sin + g(g·c)(1−cos)
            const float k = 0.57735027f;
            var dot = k * (r + g + b) * (1 - _cos);
            var cx = k * (b - g);
            var cy = k * (r - b);
            var cz = k * (g - r);
            (r, g, b) = (r * _cos + cx * _sin + k * dot, g * _cos + cy * _sin + k * dot, b * _cos + cz * _sin + k * dot);
        }
        r = Math.Clamp(r, 0, 1);
        g = Math.Clamp(g, 0, 1);
        b = Math.Clamp(b, 0, 1);
        return _invert ? (1 - r, 1 - g, 1 - b) : (r, g, b);
    }
}
