using System.Collections;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FH6RB.ViewModels;

namespace FH6RB.Controls;

public sealed class WaveformView : Control
{
    public static readonly StyledProperty<float[]?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, float[]?>(nameof(Peaks));

    public static readonly StyledProperty<IEnumerable?> MarkersProperty =
        AvaloniaProperty.Register<WaveformView, IEnumerable?>(nameof(Markers));

    public static readonly StyledProperty<long> SampleLengthProperty =
        AvaloniaProperty.Register<WaveformView, long>(nameof(SampleLength));

    public static readonly StyledProperty<double> PlayFractionProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(PlayFraction), -1);

    public static readonly StyledProperty<long> RegionStartProperty =
        AvaloniaProperty.Register<WaveformView, long>(nameof(RegionStart), -1);

    public static readonly StyledProperty<long> RegionEndProperty =
        AvaloniaProperty.Register<WaveformView, long>(nameof(RegionEnd), -1);

    public float[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public IEnumerable? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public long SampleLength { get => GetValue(SampleLengthProperty); set => SetValue(SampleLengthProperty, value); }
    public double PlayFraction { get => GetValue(PlayFractionProperty); set => SetValue(PlayFractionProperty, value); }
    public long RegionStart { get => GetValue(RegionStartProperty); set => SetValue(RegionStartProperty, value); }
    public long RegionEnd { get => GetValue(RegionEndProperty); set => SetValue(RegionEndProperty, value); }

    public IDictionary<string, int>? LabelRows { get; set; }

    public event Action<double>? SeekRequested;
    public event Action? LabelRowsChanged;

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#15181d"));
    private static readonly IBrush MidBrush = new SolidColorBrush(Color.Parse("#2a2f37"));
    private static readonly IBrush LabelBg = new SolidColorBrush(Color.Parse("#cc15181d"));
    private static readonly IPen WavePen = new Pen(new SolidColorBrush(Color.Parse("#3f7fb8")), 1);
    private static readonly IPen MidPen = new Pen(new SolidColorBrush(Color.Parse("#2a2f37")), 1);
    private static readonly IPen MarkerPen = new Pen(new SolidColorBrush(Color.Parse("#e0b341")), 1.5);
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.Parse("#23a55a")), 1.5);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#e0b341"));
    private static readonly IBrush CutBrush = new SolidColorBrush(Color.Parse("#aa181b21"));
    private static readonly IPen RegionPen = new Pen(new SolidColorBrush(Color.Parse("#6f7785")), 1);
    private static readonly IBrush LabelLockBg = new SolidColorBrush(Color.Parse("#e0b341"));
    private static readonly IBrush LabelLockText = new SolidColorBrush(Color.Parse("#15181d"));
    private static readonly Typeface LabelFace = new("Inter");

    private readonly List<(MarkerField Marker, Rect Rect)> _labels = [];
    private readonly Dictionary<string, int> _renderedRows = [];
    private MarkerField? _drag;
    private double _dragOffset;
    private bool _seeking;
    private int _regionDrag;
    private MarkerField? _labelLock;
    private Point _lastPointer;
    private TopLevel? _topLevel;

    static WaveformView()
    {
        AffectsRender<WaveformView>(PeaksProperty, MarkersProperty, SampleLengthProperty, PlayFractionProperty,
            RegionStartProperty, RegionEndProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);

        if (_topLevel is null)
        {
            return;
        }
        
        _topLevel.AddHandler(KeyDownEvent, OnTopKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _topLevel.AddHandler(KeyUpEvent, OnTopKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(KeyDownEvent, OnTopKeyDown);
            _topLevel.RemoveHandler(KeyUpEvent, OnTopKeyUp);
            _topLevel = null;
        }

        _labelLock = null;
    }

    private void OnTopKeyDown(object? sender, KeyEventArgs e)
    {
        if (_labelLock is not null || e.Key is not (Key.LeftShift or Key.RightShift))
        {
            return;
        }

        _labelLock = HitLabel(_lastPointer);

        if (_labelLock is not null)
        {
            InvalidateVisual();
        }
    }

    private void OnTopKeyUp(object? sender, KeyEventArgs e)
    {
        if (_labelLock is not null && e.Key is (Key.LeftShift or Key.RightShift))
        {
            _labelLock = null;
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkersProperty)
        {
            Hook(change.OldValue as IEnumerable, false);
            Hook(change.NewValue as IEnumerable, true);
        }
    }

    private void Hook(IEnumerable? items, bool add)
    {
        if (items is null) return;
        foreach (var o in items)
        {
            if (o is INotifyPropertyChanged n)
            {
                if (add) n.PropertyChanged += OnMarkerChanged;
                else n.PropertyChanged -= OnMarkerChanged;
            }
        }
    }

    private void OnMarkerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MarkerField.Position) or nameof(MarkerField.SecondsText))
        {
            InvalidateVisual();
        }
    }

    private double XOf(long pos) => SampleLength <= 1 ? 0 : pos / (double) (SampleLength - 1) * Bounds.Width;

    private MarkerField? HitLabel(Point pt)
    {
        for (var i = _labels.Count - 1; i >= 0; i--)
        {
            if (_labels[i].Rect.Contains(pt)) return _labels[i].Marker;
        }

        return null;
    }

    private MarkerField? NearestLine(double x, double tol)
    {
        if (Markers is null) return null;

        MarkerField? best = null;
        var bestD = tol;
        foreach (var o in Markers)
        {
            if (o is not MarkerField m || m.Position < 0) continue;
            var d = Math.Abs(XOf(m.Position) - x);
            
            if (!(d <= bestD))
            {
                continue;
            }
            
            bestD = d;
            best = m;
        }

        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        
        if (Bounds.Width <= 0)
        {
            return;
        }

        var pt = e.GetPosition(this);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && SampleLength > 1)
        {
            var props = e.GetCurrentPoint(this).Properties;

            if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
            {
                var snap = HitLabel(pt);
                var frame = snap is not null
                    ? snap.Position
                    : (long) Math.Round(Math.Clamp(pt.X, 0, Bounds.Width) / Bounds.Width * (SampleLength - 1));

                if (props.IsLeftButtonPressed)
                {
                    _regionDrag = 1;
                    SetRegionStart(frame);
                }
                else
                {
                    _regionDrag = 2;
                    SetRegionEnd(frame);
                }

                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _labelLock is not null && SampleLength > 1
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _drag = _labelLock;
            _dragOffset = pt.X - XOf(_drag.Position);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (SampleLength > 0)
        {
            _drag = HitLabel(pt) ?? NearestLine(pt.X, 6);
            if (_drag is not null)
            {
                _dragOffset = pt.X - XOf(_drag.Position);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        _seeking = true;
        e.Pointer.Capture(this);
        RaiseSeek(pt.X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        _lastPointer = e.GetPosition(this);

        if (Bounds.Width <= 0)
        {
            return;
        }

        var cursorX = e.GetPosition(this).X;

        if (_regionDrag != 0 && SampleLength > 1)
        {
            var rf = (long) Math.Round(Math.Clamp(cursorX, 0, Bounds.Width) / Bounds.Width * (SampleLength - 1));

            if (_regionDrag == 1)
            {
                SetRegionStart(rf);
            }
            else
            {
                SetRegionEnd(rf);
            }

            return;
        }

        if (_drag is not null && SampleLength > 1)
        {
            var x = Math.Clamp(cursorX - _dragOffset, 0, Bounds.Width);
            var frame = (long) Math.Round(x / Bounds.Width * (SampleLength - 1));
            _drag.Position = Math.Clamp(frame, 0, SampleLength - 1);
        }
        else if (_seeking)
        {
            RaiseSeek(Math.Clamp(cursorX, 0, Bounds.Width));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_drag is null && !_seeking && _regionDrag == 0)
        {
            return;
        }
        
        _drag = null;
        _dragOffset = 0;
        _seeking = false;
        _regionDrag = 0;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || LabelRows is null)
        {
            return;
        }

        var m = _labelLock ??= HitLabel(e.GetPosition(this));

        if (m is null)
        {
            return;
        }

        var cur = LabelRows.TryGetValue(m.Name, out var r)
            ? r
            : _renderedRows.TryGetValue(m.Name, out var rr) ? rr : 0;

        var next = Math.Clamp(cur + (e.Delta.Y > 0 ? -1 : 1), 0, 28);

        LabelRows[m.Name] = next;
        InvalidateVisual();
        LabelRowsChanged?.Invoke();
        e.Handled = true;
    }

    private void SetRegionStart(long frame)
    {
        if (SampleLength <= 1) return;
        var end = RegionEnd >= 0 ? RegionEnd : SampleLength - 1;
        RegionStart = Math.Clamp(Math.Min(frame, end), 0, SampleLength - 1);
    }

    private void SetRegionEnd(long frame)
    {
        if (SampleLength <= 1) return;
        var start = RegionStart >= 0 ? RegionStart : 0;
        RegionEnd = Math.Clamp(Math.Max(frame, start), 0, SampleLength - 1);
    }

    private void RaiseSeek(double x)
    {
        var frac = Math.Clamp(x / Bounds.Width, 0, 1);
        SeekRequested?.Invoke(frac);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        
        if (w <= 0 || h <= 0)
        {
            return;
        }

        ctx.FillRectangle(BgBrush, new Rect(0, 0, w, h));

        var mid = h / 2;
        ctx.DrawLine(MidPen, new Point(0, mid), new Point(w, mid));

        var peaks = Peaks;
        
        if (peaks is { Length: > 0 })
        {
            var n = peaks.Length;
            var amax = mid - 2;
            
            for (var i = 0; i < n; i++)
            {
                var x = (i + 0.5) / n * w;
                var a = Math.Clamp(peaks[i], 0f, 1f) * amax;
                
                if (a < 0.5)
                {
                    a = 0.5;
                }
                
                ctx.DrawLine(WavePen, new Point(x, mid - a), new Point(x, mid + a));
            }
        }

        var pf = PlayFraction;
        
        if (pf is >= 0 and <= 1)
        {
            var cx = pf * w;
            ctx.DrawLine(CursorPen, new Point(cx, 0), new Point(cx, h));
        }

        if (SampleLength > 1)
        {
            var rs = RegionStart;
            var re = RegionEnd;

            if (rs > 0)
            {
                var x0 = XOf(rs);
                ctx.FillRectangle(CutBrush, new Rect(0, 0, x0, h));
                ctx.DrawLine(RegionPen, new Point(x0, 0), new Point(x0, h));
            }

            if (re >= 0 && re < SampleLength - 1)
            {
                var x1 = XOf(re);
                ctx.FillRectangle(CutBrush, new Rect(x1, 0, w - x1, h));
                ctx.DrawLine(RegionPen, new Point(x1, 0), new Point(x1, h));
            }
        }

        _labels.Clear();
        _renderedRows.Clear();
        
        if (Markers is not null && SampleLength > 0)
        {
            var idx = 0;
            
            foreach (var o in Markers)
            {
                if (o is not MarkerField m || m.Position < 0)
                {
                    continue;
                }

                var x = XOf(m.Position);
                
                ctx.DrawLine(MarkerPen, new Point(x, 0), new Point(x, h));

                var locked = ReferenceEquals(m, _labelLock);

                var label = new FormattedText(m.Name, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, LabelFace, 9, locked ? LabelLockText : LabelBrush);

                var lx = Math.Min(x + 3, w - label.Width - 2);
                
                if (lx < 1)
                {
                    lx = 1;
                }
                
                var row = LabelRows is not null && LabelRows.TryGetValue(m.Name, out var lr) ? lr : idx % 6 * 2;
                _renderedRows[m.Name] = row;
                var ly = 2 + row * 5.5;
                var rect = new Rect(lx - 1, ly - 1, label.Width + 2, label.Height + 1);

                ctx.FillRectangle(locked ? LabelLockBg : LabelBg, rect);
                ctx.DrawText(label, new Point(lx, ly));
                _labels.Add((m, rect));
                idx++;
            }
        }
    }
}
