using System.Collections;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FH6RB.Core;
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

    public static readonly StyledProperty<double> StartFractionProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(StartFraction), -1);

    public static readonly StyledProperty<long> RegionStartProperty =
        AvaloniaProperty.Register<WaveformView, long>(nameof(RegionStart), -1);

    public static readonly StyledProperty<long> RegionEndProperty =
        AvaloniaProperty.Register<WaveformView, long>(nameof(RegionEnd), -1);

    public float[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public IEnumerable? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public long SampleLength { get => GetValue(SampleLengthProperty); set => SetValue(SampleLengthProperty, value); }
    public double PlayFraction { get => GetValue(PlayFractionProperty); set => SetValue(PlayFractionProperty, value); }
    public double StartFraction { get => GetValue(StartFractionProperty); set => SetValue(StartFractionProperty, value); }
    public long RegionStart { get => GetValue(RegionStartProperty); set => SetValue(RegionStartProperty, value); }
    public long RegionEnd { get => GetValue(RegionEndProperty); set => SetValue(RegionEndProperty, value); }

    public IDictionary<string, int>? LabelRows { get; set; }

    public event Action<double>? SeekRequested;
    public event Action? LabelRowsChanged;
    public event Action? RegionChanged;

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#15181d"));
    private static readonly IBrush MidBrush = new SolidColorBrush(Color.Parse("#2a2f37"));
    private static readonly IBrush LabelBg = new SolidColorBrush(Color.Parse("#cc15181d"));
    private static readonly IPen WavePen = new Pen(new SolidColorBrush(Color.Parse("#3f7fb8")), 1);
    private static readonly IPen MidPen = new Pen(new SolidColorBrush(Color.Parse("#2a2f37")), 1);
    private static readonly IPen MarkerPen = new Pen(new SolidColorBrush(Color.Parse("#e0b341")), 1.5);
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.Parse("#23a55a")), 1.5);
    private static readonly IPen StartPen = new Pen(new SolidColorBrush(Color.Parse("#c8d2e0")), 1.5) { DashStyle = DashStyle.Dash };
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
    private double _viewStart;
    private double _viewLen;
    private bool _panning;
    private double _panLastX;
    private MarkerField? _labelLock;
    private IBrush _hlBrush = new SolidColorBrush(Color.Parse("#f5356c"));
    private IPen _hlPen = new Pen(new SolidColorBrush(Color.Parse("#f5356c")), 2.5);
    private Point _lastPointer;
    private bool _pointerInside;
    private TopLevel? _topLevel;

    static WaveformView()
    {
        AffectsRender<WaveformView>(PeaksProperty, MarkersProperty, SampleLengthProperty, PlayFractionProperty,
            StartFractionProperty, RegionStartProperty, RegionEndProperty);
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

        if (this.TryFindResource("Accent", out var accent) && accent is IBrush ab)
        {
            _hlBrush = ab;
            _hlPen = new Pen(ab, 2.5);
        }
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

        SetFocus(null);
    }

    private void OnTopKeyDown(object? sender, KeyEventArgs e)
    {
        if (_labelLock is not null || e.Key is not (Key.LeftShift or Key.RightShift) || !_pointerInside)
        {
            return;
        }

        SetFocus(HitLabel(_lastPointer));
    }

    private void OnTopKeyUp(object? sender, KeyEventArgs e)
    {
        if (_labelLock is not null && e.Key is (Key.LeftShift or Key.RightShift))
        {
            SetFocus(null);
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _pointerInside = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInside = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkersProperty)
        {
            Hook(change.OldValue as IEnumerable, false);
            Hook(change.NewValue as IEnumerable, true);
        }
        else if (change.Property == SampleLengthProperty)
        {
            _viewStart = 0;
            _viewLen = 0;
        }
        else if (change.Property == RegionStartProperty || change.Property == RegionEndProperty)
        {
            RegionChanged?.Invoke();
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

    private const double ZoomStep = 1.25;
    private const double MinViewLen = 16;

    private double Domain => Math.Max(1, SampleLength - 1);

    private (double Start, double Len) View()
    {
        var dom = Domain;
        var len = _viewLen <= 0 || _viewLen >= dom ? dom : Math.Max(_viewLen, Math.Min(MinViewLen, dom));
        var start = Math.Clamp(_viewStart, 0, dom - len);
        return (start, len);
    }

    private double XOf(long pos)
    {
        if (SampleLength <= 1 || Bounds.Width <= 0)
        {
            return 0;
        }

        var (vs, vl) = View();
        return (pos - vs) / vl * Bounds.Width;
    }

    private double SampleAt(double x)
    {
        if (Bounds.Width <= 0)
        {
            return 0;
        }

        var (vs, vl) = View();
        return Math.Clamp(vs + x / Bounds.Width * vl, 0, Domain);
    }

    private void ZoomAt(double mouseX, double deltaY)
    {
        var w = Bounds.Width;
        if (w <= 0)
        {
            return;
        }

        var (vs, vl) = View();
        var mx = Math.Clamp(mouseX, 0, w);
        var sAt = vs + mx / w * vl;
        var factor = deltaY > 0 ? 1.0 / ZoomStep : ZoomStep;
        _viewLen = Math.Clamp(vl * factor, MinViewLen, Domain);
        _viewStart = sAt - mx / w * _viewLen;
        (_viewStart, _viewLen) = View();
        InvalidateVisual();
    }

    private MarkerField? HitLabel(Point pt)
    {
        for (var i = _labels.Count - 1; i >= 0; i--)
        {
            if (_labels[i].Rect.Contains(pt)) return _labels[i].Marker;
        }

        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        
        if (Bounds.Width <= 0)
        {
            return;
        }

        var pt = e.GetPosition(this);

        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _panLastX = pt.X;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && SampleLength > 1)
        {
            var props = e.GetCurrentPoint(this).Properties;

            if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
            {
                var snap = HitLabel(pt);
                var frame = snap is not null
                    ? snap.Position
                    : (long) Math.Round(SampleAt(pt.X));

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
            _dragOffset = pt.X - Math.Clamp(XOf(_drag.Position), 0, Bounds.Width);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
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

        if (_panning)
        {
            var (_, vl) = View();
            _viewStart -= (cursorX - _panLastX) / Bounds.Width * vl;
            _panLastX = cursorX;
            (_viewStart, _viewLen) = View();
            InvalidateVisual();
            return;
        }

        if (_regionDrag != 0 && SampleLength > 1)
        {
            var rf = (long) Math.Round(SampleAt(cursorX));

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
            var frame = (long) Math.Round(SampleAt(cursorX - _dragOffset));
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

        if (_drag is null && !_seeking && _regionDrag == 0 && !_panning)
        {
            return;
        }
        
        _drag = null;
        _dragOffset = 0;
        _seeking = false;
        _regionDrag = 0;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && SampleLength > 1 && Bounds.Width > 0)
        {
            ZoomAt(e.GetPosition(this).X, e.Delta.Y);
            e.Handled = true;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || LabelRows is null)
        {
            return;
        }

        var m = _labelLock ?? HitLabel(e.GetPosition(this));

        if (m is null)
        {
            return;
        }

        SetFocus(m);

        var cur = LabelRows.TryGetValue(m.Name, out var r)
            ? r
            : _renderedRows.TryGetValue(m.Name, out var rr) ? rr : 0;

        var next = Math.Clamp(cur + (e.Delta.Y > 0 ? -1 : 1), 0, 28);

        LabelRows[m.Name] = next;
        InvalidateVisual();
        LabelRowsChanged?.Invoke();
        e.Handled = true;
    }

    public void SetRegionStart(long frame)
    {
        if (SampleLength <= 1) return;
        var end = RegionEnd >= 0 ? RegionEnd : SampleLength - 1;
        RegionStart = Math.Clamp(Math.Min(frame, end), 0, SampleLength - 1);
    }

    public void SetRegionEnd(long frame)
    {
        if (SampleLength <= 1) return;
        var start = RegionStart >= 0 ? RegionStart : 0;
        RegionEnd = Math.Clamp(Math.Max(frame, start), 0, SampleLength - 1);
    }

    public void FocusMarker(MarkerField? marker) => SetFocus(marker);

    private void SetFocus(MarkerField? marker)
    {
        if (ReferenceEquals(_labelLock, marker))
        {
            return;
        }

        if (_labelLock is not null)
        {
            _labelLock.Highlighted = false;
        }

        _labelLock = marker;

        if (_labelLock is not null)
        {
            _labelLock.Highlighted = true;
        }

        InvalidateVisual();
    }

    private void RaiseSeek(double x)
    {
        var sample = SampleAt(x);
        var frac = SampleLength > 1 ? sample / (SampleLength - 1) : 0;
        SeekRequested?.Invoke(Math.Clamp(frac, 0, 1));
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
            var (vs, vl) = View();

            for (var px = 0; px < (int) w; px++)
            {
                var a = (int) (vs + px / w * vl);
                var b = (int) (vs + (px + 1) / w * vl);
                a = a < 0 ? 0 : a >= n ? n - 1 : a;
                b = b < 0 ? 0 : b >= n ? n - 1 : b;
                if (b < a) b = a;

                var step = (b - a) / 2048 + 1;
                var min = peaks[a];
                var max = peaks[a];

                for (var i = a; i <= b; i += step)
                {
                    var s = peaks[i];
                    if (s < min) min = s;
                    if (s > max) max = s;
                }

                var y0 = mid - Math.Clamp(max, -1f, 1f) * amax;
                var y1 = mid - Math.Clamp(min, -1f, 1f) * amax;

                if (y1 - y0 < 1)
                {
                    y0 -= 0.5;
                    y1 += 0.5;
                }

                ctx.DrawLine(WavePen, new Point(px + 0.5, y0), new Point(px + 0.5, y1));
            }
        }

        var sf = StartFraction;

        if (sf is >= 0 and <= 1)
        {
            var sx = XOf((long) (sf * Domain));
            if (sx >= 0 && sx <= w)
            {
                ctx.DrawLine(StartPen, new Point(sx, 0), new Point(sx, h));
            }
        }

        var pf = PlayFraction;

        if (pf is >= 0 and <= 1)
        {
            var cx = XOf((long) (pf * Domain));
            if (cx >= 0 && cx <= w)
            {
                ctx.DrawLine(CursorPen, new Point(cx, 0), new Point(cx, h));
            }
        }

        if (SampleLength > 1)
        {
            var rs = RegionStart;
            var re = RegionEnd;

            if (rs > 0)
            {
                var x0 = Math.Clamp(XOf(rs), 0, w);
                ctx.FillRectangle(CutBrush, new Rect(0, 0, x0, h));
                ctx.DrawLine(RegionPen, new Point(x0, 0), new Point(x0, h));
            }

            if (re >= 0 && re < SampleLength - 1)
            {
                var x1 = Math.Clamp(XOf(re), 0, w);
                ctx.FillRectangle(CutBrush, new Rect(x1, 0, w - x1, h));
                ctx.DrawLine(RegionPen, new Point(x1, 0), new Point(x1, h));
            }
        }

        _labels.Clear();
        _renderedRows.Clear();
        
        if (Markers is not null && SampleLength > 0)
        {
            var idx = 0;
            double lockX = 0, lockLx = 0, lockLy = 0;
            FormattedText? lockLabel = null;
            Rect lockRect = default;
            
            foreach (var o in Markers)
            {
                if (o is not MarkerField m || m.Position < 0)
                {
                    continue;
                }

                var x = XOf(m.Position);

                var locked = ReferenceEquals(m, _labelLock);

                if (!locked && x >= 0 && x <= w)
                {
                    ctx.DrawLine(MarkerPen, new Point(x, 0), new Point(x, h));
                }

                var label = new FormattedText(m.Name, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, LabelFace, 9, locked ? LabelLockText : LabelBrush);

                var lx = Math.Min(x + 3, w - label.Width - 2);
                
                if (lx < 1)
                {
                    lx = 1;
                }
                
                var row = LabelRows is not null && LabelRows.TryGetValue(m.Name, out var lr)
                    ? lr
                    : Math.Clamp(MarkerDefaults.DefaultLabelRows.GetValueOrDefault(m.Name, idx % 6 * 2), 0, 28);
                _renderedRows[m.Name] = row;
                var ly = 2 + row * 5.5;
                var rect = new Rect(lx - 1, ly - 1, label.Width + 2, label.Height + 1);

                if (locked)
                {
                    lockX = x;
                    lockLx = lx;
                    lockLy = ly;
                    lockLabel = label;
                    lockRect = rect;
                }
                else
                {
                    ctx.FillRectangle(LabelBg, rect);
                    ctx.DrawText(label, new Point(lx, ly));
                }

                _labels.Add((m, rect));
                idx++;
            }

            if (lockLabel is not null)
            {
                ctx.DrawLine(_hlPen, new Point(lockX, 0), new Point(lockX, h));
                ctx.FillRectangle(_hlBrush, lockRect);
                ctx.DrawText(lockLabel, new Point(lockLx, lockLy));
            }
        }
    }
}
