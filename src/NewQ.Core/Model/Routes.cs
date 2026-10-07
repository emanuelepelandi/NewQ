using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace NewQ.Core.Model;

/// <summary>A point in normalized coordinates (0–1 across the output), used for keystone and warp offsets.</summary>
public readonly record struct NormalizedPoint(double X, double Y)
{
    public static readonly NormalizedPoint Zero = new(0, 0);
}

/// <summary>
/// A named audio destination ("PA", "Monitor palco"...): a sound card (WASAPI endpoint, or the ASIO driver
/// chosen in the settings) and the first of the two channels it plays on, with its own output gain.
/// </summary>
public sealed class AudioRoute : ObservableObject
{
    private string _name = "Audio";
    private string _deviceId = "";
    private int _firstChannel;
    private double _gainDb;
    private bool _muted;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetField(ref _name, string.IsNullOrWhiteSpace(value) ? "Audio" : value.Trim()); }

    /// <summary>WASAPI endpoint id; empty = Windows default device. Ignored with ASIO (the driver is global).</summary>
    public string DeviceId { get => _deviceId; set => SetField(ref _deviceId, value ?? ""); }

    /// <summary>0-based first output channel: the route plays on channels FirstChannel and FirstChannel+1.</summary>
    public int FirstChannel { get => _firstChannel; set => SetField(ref _firstChannel, Math.Clamp(value, 0, 62)); }

    /// <summary>Output gain of the route in dB.</summary>
    public double GainDb { get => _gainDb; set => SetField(ref _gainDb, Math.Clamp(value, Decibels.Floor, 12)); }

    public bool Muted { get => _muted; set => SetField(ref _muted, value); }

    public override string ToString() => Name;
}

/// <summary>
/// A named video surface ("Fondale", "Schermo LED"...). Cues draw on the route's canvas; each output shows a part
/// of that canvas on a monitor/projector, with geometry correction and edge blending. Two projectors in blend =
/// one route with two outputs.
/// </summary>
public sealed class VideoRoute : ObservableObject
{
    private string _name = "Video";
    private int _canvasWidth = 1920;
    private int _canvasHeight = 1080;

    private ObservableCollection<VideoOutput> _outputs = new();

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => SetField(ref _name, string.IsNullOrWhiteSpace(value) ? "Video" : value.Trim()); }

    /// <summary>Resolution of the canvas cues are composed on (aspect ratio of the whole surface).</summary>
    public int CanvasWidth { get => _canvasWidth; set => SetField(ref _canvasWidth, Math.Clamp(value, 16, 16384)); }
    public int CanvasHeight { get => _canvasHeight; set => SetField(ref _canvasHeight, Math.Clamp(value, 16, 16384)); }

    public ObservableCollection<VideoOutput> Outputs
    {
        get => _outputs;
        set => SetField(ref _outputs, value ?? new());
    }

    public override string ToString() => Name;
}

/// <summary>One physical output of a video route, with its geometry.</summary>
public sealed class VideoOutput : ObservableObject
{
    public const int PreviewWindow = -1;
    public const int MaxWarpDivisions = 8;

    private string _name = "Uscita";
    private int _screenIndex = PreviewWindow;
    private double _sourceX, _sourceY, _sourceWidth = 1, _sourceHeight = 1;
    private double _destX, _destY, _destWidth = 1, _destHeight = 1;
    private NormalizedPoint _topLeft, _topRight, _bottomRight, _bottomLeft;
    private int _warpColumns, _warpRows;
    private List<NormalizedPoint> _warpPoints = new();
    private double _blendLeft, _blendRight, _blendTop, _blendBottom;
    private double _blendGamma = 2.2;
    private double _blendCurve = 2.0;

    public string Name { get => _name; set => SetField(ref _name, string.IsNullOrWhiteSpace(value) ? "Uscita" : value.Trim()); }

    /// <summary>Monitor index, or <see cref="PreviewWindow"/> for a windowed preview.</summary>
    public int ScreenIndex { get => _screenIndex; set => SetField(ref _screenIndex, value); }

    // ---- which part of the route canvas this output shows (normalized 0–1) ----
    public double SourceX { get => _sourceX; set => SetField(ref _sourceX, Clamp01(value)); }
    public double SourceY { get => _sourceY; set => SetField(ref _sourceY, Clamp01(value)); }
    public double SourceWidth { get => _sourceWidth; set => SetField(ref _sourceWidth, Math.Clamp(value, 0.01, 1)); }
    public double SourceHeight { get => _sourceHeight; set => SetField(ref _sourceHeight, Math.Clamp(value, 0.01, 1)); }

    // ---- where it goes on the screen (normalized 0–1): scaling and positioning ----
    public double DestX { get => _destX; set => SetField(ref _destX, Math.Clamp(value, -1, 1)); }
    public double DestY { get => _destY; set => SetField(ref _destY, Math.Clamp(value, -1, 1)); }
    public double DestWidth { get => _destWidth; set => SetField(ref _destWidth, Math.Clamp(value, 0.01, 3)); }
    public double DestHeight { get => _destHeight; set => SetField(ref _destHeight, Math.Clamp(value, 0.01, 3)); }

    // ---- keystone / corner pin: offsets of the 4 corners, in screen-normalized units ----
    public NormalizedPoint TopLeft { get => _topLeft; set => SetField(ref _topLeft, value); }
    public NormalizedPoint TopRight { get => _topRight; set => SetField(ref _topRight, value); }
    public NormalizedPoint BottomRight { get => _bottomRight; set => SetField(ref _bottomRight, value); }
    public NormalizedPoint BottomLeft { get => _bottomLeft; set => SetField(ref _bottomLeft, value); }

    // ---- warping: a (columns+1)×(rows+1) grid of offsets on top of the keystone; 0×0 = off ----
    public int WarpColumns { get => _warpColumns; set => SetWarpSize(value, _warpRows); }
    public int WarpRows { get => _warpRows; set => SetWarpSize(_warpColumns, value); }

    /// <summary>Offsets of the warp control points, row by row (top to bottom, left to right).</summary>
    public List<NormalizedPoint> WarpPoints
    {
        get => _warpPoints;
        set { _warpPoints = value ?? new(); NormalizeWarp(); OnPropertyChanged(); }
    }

    // ---- edge blending: width of the soft edge as a fraction of this output's image ----
    public double BlendLeft { get => _blendLeft; set => SetField(ref _blendLeft, Math.Clamp(value, 0, 0.5)); }
    public double BlendRight { get => _blendRight; set => SetField(ref _blendRight, Math.Clamp(value, 0, 0.5)); }
    public double BlendTop { get => _blendTop; set => SetField(ref _blendTop, Math.Clamp(value, 0, 0.5)); }
    public double BlendBottom { get => _blendBottom; set => SetField(ref _blendBottom, Math.Clamp(value, 0, 0.5)); }

    /// <summary>Display gamma used to linearize the blend ramp (2.2 for most projectors).</summary>
    public double BlendGamma { get => _blendGamma; set => SetField(ref _blendGamma, Math.Clamp(value, 1, 3.5)); }

    /// <summary>Shape of the ramp (1 = linear, 2 = smooth S-curve).</summary>
    public double BlendCurve { get => _blendCurve; set => SetField(ref _blendCurve, Math.Clamp(value, 1, 4)); }

    [JsonIgnore] public bool HasWarp => _warpColumns > 0 && _warpRows > 0;

    public NormalizedPoint GetWarpPoint(int column, int row)
        => HasWarp ? _warpPoints[row * (_warpColumns + 1) + column] : NormalizedPoint.Zero;

    public void SetWarpPoint(int column, int row, NormalizedPoint offset)
    {
        if (!HasWarp) return;
        _warpPoints[row * (_warpColumns + 1) + column] = offset;
        OnPropertyChanged(nameof(WarpPoints));
    }

    public void ResetGeometry()
    {
        TopLeft = TopRight = BottomRight = BottomLeft = NormalizedPoint.Zero;
        for (var i = 0; i < _warpPoints.Count; i++) _warpPoints[i] = NormalizedPoint.Zero;
        OnPropertyChanged(nameof(WarpPoints));
    }

    private void SetWarpSize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 0, MaxWarpDivisions);
        rows = Math.Clamp(rows, 0, MaxWarpDivisions);
        if (columns == _warpColumns && rows == _warpRows) return;
        _warpColumns = columns;
        _warpRows = rows;
        _warpPoints = new List<NormalizedPoint>(new NormalizedPoint[HasWarp ? (columns + 1) * (rows + 1) : 0]);
        OnPropertyChanged(nameof(WarpColumns));
        OnPropertyChanged(nameof(WarpRows));
        OnPropertyChanged(nameof(WarpPoints));
    }

    /// <summary>Keeps the point list consistent with the grid size (e.g. after loading a hand-edited file).</summary>
    private void NormalizeWarp()
    {
        var expected = HasWarp ? (_warpColumns + 1) * (_warpRows + 1) : 0;
        if (_warpPoints.Count == expected) return;
        var fixedList = new List<NormalizedPoint>(new NormalizedPoint[expected]);
        for (var i = 0; i < Math.Min(expected, _warpPoints.Count); i++) fixedList[i] = _warpPoints[i];
        _warpPoints = fixedList;
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    public override string ToString() => Name;
}
