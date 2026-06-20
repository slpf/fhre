using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FH6RB.Services;
using FH6RB.ViewModels;

namespace FH6RB.Views;

public partial class WaveformWindow : Window
{
    private readonly PlaybackService _player = new();
    private readonly DispatcherTimer _timer;
    private readonly List<(MarkerField Marker, long Position)> _snapshot = [];
    private double _cursorSec;
    private MarkerField? _hoverField;
    private bool _shiftHeld;

    private const double SeekStep = 0.25;

    private EditWindowViewModel Vm => (EditWindowViewModel) DataContext!;

    public bool Saved { get; private set; }

    private double RegionStartSec =>
        Wave.RegionStart > 0 && Vm.SampleRate > 0 ? (double) Wave.RegionStart / Vm.SampleRate : 0;

    private double RegionEndSec =>
        Wave.RegionEnd >= 0 && Vm.SampleRate > 0 ? (double) Wave.RegionEnd / Vm.SampleRate : double.MaxValue;

    public WaveformWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;

        Wave.SeekRequested += OnSeek;
        Wave.LabelRowsChanged += OnLabelRowsChanged;
        Wave.RegionChanged += OnRegionChanged;
        _player.Ended += OnPlaybackEnded;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);

        Wave.Focusable = true;

        Opened += (_, _) =>
        {
            Wave.LabelRows = Vm.Settings?.WaveformLabelRows;

            _snapshot.Clear();
            foreach (var m in Vm.AllMarkers)
            {
                _snapshot.Add((m, m.Position));
            }

            _cursorSec = RegionStartSec;
            UpdateUi();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _player.Dispose();

            if (!Saved)
            {
                foreach (var (m, pos) in _snapshot)
                {
                    m.Position = pos;
                }
            }
        };
    }

    private void OnSetFromPlayhead(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MarkerField field } || field.SampleRate <= 0)
        {
            return;
        }

        var seconds = _player.HasMedia ? _player.Position.TotalSeconds : _cursorSec;
        var frame = (long) Math.Round(seconds * field.SampleRate);

        field.Position = Vm.SampleLength > 0
            ? Math.Clamp(frame, 0, Vm.SampleLength - 1)
            : Math.Max(0, frame);
    }
    
    private void OnPlayPause(object? sender, RoutedEventArgs e)
    {
        var wav = Vm.WavPath;
        if (string.IsNullOrEmpty(wav) || !File.Exists(wav))
        {
            return;
        }

        if (_player.IsPlaying)
        {
            _player.TogglePause();
        }
        else if (_player.IsPaused)
        {
            var pos = _player.Position.TotalSeconds;

            if (pos < RegionStartSec - 0.001 || pos >= RegionEndSec)
            {
                _player.Position = TimeSpan.FromSeconds(RegionStartSec);
            }

            _player.TogglePause();
        }
        else
        {
            var startSec = _cursorSec < RegionStartSec - 0.001 || _cursorSec >= RegionEndSec
                ? RegionStartSec
                : _cursorSec;

            _player.Play(wav, 0);
            _player.Position = TimeSpan.FromSeconds(startSec);
            _timer.Start();
        }

        UpdateUi();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                OnPlayPause(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.Left:
                Nudge(-SeekStep);
                e.Handled = true;
                break;

            case Key.Right:
                Nudge(SeekStep);
                e.Handled = true;
                break;

            case Key.Escape:
                if (FocusManager?.GetFocusedElement() is TextBox)
                {
                    Wave.Focus();
                    e.Handled = true;
                }

                break;

            case Key.LeftShift:
            case Key.RightShift:
                if (!_shiftHeld)
                {
                    _shiftHeld = true;

                    if (_hoverField is not null)
                    {
                        Wave.FocusMarker(_hoverField);
                    }
                }

                break;
        }
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _shiftHeld = false;
        }
    }

    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text == " ")
        {
            e.Handled = true;
        }
    }

    private void Nudge(double deltaSec)
    {
        var total = Vm.TotalSeconds;

        if (total <= 0)
        {
            return;
        }

        var endSec = RegionEndSec >= total ? total : RegionEndSec;
        var baseSec = _player.HasMedia ? _player.Position.TotalSeconds : _cursorSec;
        _cursorSec = Math.Clamp(baseSec + deltaSec, RegionStartSec, endSec);

        if (_player.HasMedia)
        {
            _player.Position = TimeSpan.FromSeconds(_cursorSec);
        }

        UpdateUi();
    }

    private void OnSeek(double fraction)
    {
        if (_player.HasMedia && _player.Duration > TimeSpan.Zero)
        {
            var dur = _player.Duration.TotalSeconds;
            var endSec = RegionEndSec >= dur ? dur : RegionEndSec;
            _cursorSec = Math.Clamp(fraction * dur, RegionStartSec, endSec);
            _player.Position = TimeSpan.FromSeconds(_cursorSec);
            UpdateUi();
        }
        else
        {
            var total = Vm.TotalSeconds;

            if (total <= 0)
            {
                return;
            }

            var endSec = RegionEndSec >= total ? total : RegionEndSec;
            _cursorSec = Math.Clamp(fraction * total, RegionStartSec, endSec);
            UpdateUi();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_player.HasMedia && _player.IsPlaying && _player.Position.TotalSeconds >= RegionEndSec)
        {
            ResetToStart();
        }

        UpdateUi();
    }

    private void UpdateUi()
    {
        var total = Vm.TotalSeconds;
        double cur;

        if (_player.HasMedia)
        {
            _cursorSec = _player.Position.TotalSeconds;
            var dur = _player.Duration.TotalSeconds;

            if (dur > 0)
            {
                total = dur;
            }

            cur = _cursorSec;
            Wave.PlayFraction = dur > 0 ? cur / dur : -1;
            SetPlaying(_player.IsPlaying);
        }
        else
        {
            cur = _cursorSec;
            Wave.PlayFraction = total > 0 ? Math.Clamp(cur / total, 0, 1) : -1;
            SetPlaying(false);
        }

        var start = RegionStartSec;
        var end = RegionEndSec >= total ? total : RegionEndSec;

        TimeText.Text =
            $"{Fmt(TimeSpan.FromSeconds(start))} / {Fmt(TimeSpan.FromSeconds(cur))} / {Fmt(TimeSpan.FromSeconds(end))}";
    }

    private void SetPlaying(bool playing)
    {
        PlayIcon.IsVisible = !playing;
        PauseIcon.IsVisible = playing;
    }
    
    private static string Fmt(TimeSpan t) =>
        FormattableString.Invariant($"{(int) t.TotalMinutes}:{t.Seconds:00} ({t.TotalSeconds:0.000})");

    private void OnResetDefaults(object? sender, RoutedEventArgs e) => Vm.ResetMarkersToDefaults();

    private TextBox? _ctxField;

    private void OnFieldContextRequested(object? sender, ContextRequestedEventArgs e) => _ctxField = sender as TextBox;

    private void OnFieldCut(object? sender, RoutedEventArgs e) => _ctxField?.Cut();

    private void OnFieldCopy(object? sender, RoutedEventArgs e) => _ctxField?.Copy();

    private void OnFieldPaste(object? sender, RoutedEventArgs e) => _ctxField?.Paste();

    private void OnFieldRevert(object? sender, RoutedEventArgs e) => (_ctxField?.DataContext as MarkerField)?.Revert();

    private void OnFieldReset(object? sender, RoutedEventArgs e) => (_ctxField?.DataContext as MarkerField)?.Reset();

    private void OnLabelRowsChanged()
    {
        if (Vm.Settings is { } s)
        {
            SettingsService.Save(s);
        }
    }

    private void OnRegionChanged()
    {
        if (_player.HasMedia)
        {
            _player.Stop();
        }

        UpdateUi();
    }

    private void ResetToStart()
    {
        _cursorSec = RegionStartSec;
        _player.Stop();
    }

    private void OnPlaybackEnded() =>
        Dispatcher.UIThread.Post(() =>
        {
            ResetToStart();
            UpdateUi();
        });

    private void OnFieldNamePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: MarkerField field }
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsLeftButtonPressed)
        {
            Wave.SetRegionStart(field.Position);
        }
        else if (props.IsRightButtonPressed)
        {
            Wave.SetRegionEnd(field.Position);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void OnFieldNameEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { Tag: MarkerField field })
        {
            return;
        }

        _hoverField = field;
    }

    private void OnFieldNameExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { Tag: MarkerField field } && ReferenceEquals(_hoverField, field))
        {
            _hoverField = null;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Saved = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
