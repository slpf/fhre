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
    private double _startSec;
    private double _headSec;
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

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
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

            _startSec = RegionStartSec;
            _headSec = RegionStartSec;
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

            Wave.Peaks = null;
            Vm.Peaks = null;

            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        };
    }

    private double Total()
    {
        if (_player.HasMedia && _player.Duration > TimeSpan.Zero)
        {
            return _player.Duration.TotalSeconds;
        }

        return Vm.TotalSeconds;
    }

    private double EffectiveEndSec(double total) => RegionEndSec >= total ? total : RegionEndSec;

    private void OnSetFromPlayhead(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MarkerField field } || field.SampleRate <= 0)
        {
            return;
        }

        var frame = (long) Math.Round(_startSec * field.SampleRate);

        field.Position = Vm.SampleLength > 0
            ? Math.Clamp(frame, 0, Vm.SampleLength - 1)
            : Math.Max(0, frame);
    }

    private void StartPlaybackFromHead()
    {
        var wav = Vm.WavPath;
        if (string.IsNullOrEmpty(wav) || !File.Exists(wav))
        {
            return;
        }

        if (_headSec < RegionStartSec - 0.001 || _headSec >= RegionEndSec)
        {
            _headSec = RegionStartSec;
        }

        _player.Play(wav, 0);
        _player.Position = TimeSpan.FromSeconds(_headSec);
        _timer.Start();
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
            StartPlaybackFromHead();
        }

        if (_player.IsPlaying)
        {
            _timer.Start();
        }

        UpdateUi();
    }

    private void PlayStop()
    {
        var wav = Vm.WavPath;
        if (string.IsNullOrEmpty(wav) || !File.Exists(wav))
        {
            return;
        }

        if (_player.IsPlaying)
        {
            _player.Stop();
            _headSec = _startSec;
            UpdateUi();
            return;
        }

        StartPlaybackFromHead();
        UpdateUi();
    }

    private void PauseToggle()
    {
        if (!_player.HasMedia)
        {
            return;
        }

        if (_player.IsPaused)
        {
            var pos = _player.Position.TotalSeconds;
            if (pos < RegionStartSec - 0.001 || pos >= RegionEndSec)
            {
                _player.Position = TimeSpan.FromSeconds(RegionStartSec);
            }
        }

        _player.TogglePause();

        if (_player.IsPlaying)
        {
            _timer.Start();
        }

        UpdateUi();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                PlayStop();
                e.Handled = true;
                break;

            case Key.P:
                PauseToggle();
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
        var total = Total();

        if (total <= 0)
        {
            return;
        }

        var end = EffectiveEndSec(total);
        var baseSec = _player.HasMedia ? _player.Position.TotalSeconds : _headSec;
        _headSec = Math.Clamp(baseSec + deltaSec, _startSec, end);

        if (_player.HasMedia)
        {
            _player.Position = TimeSpan.FromSeconds(_headSec);
        }

        UpdateUi();
    }

    private void OnSeek(double fraction)
    {
        var total = Total();

        if (total <= 0)
        {
            return;
        }

        var end = EffectiveEndSec(total);
        _startSec = Math.Clamp(fraction * total, RegionStartSec, end);
        _headSec = _startSec;

        if (_player.HasMedia)
        {
            _player.Position = TimeSpan.FromSeconds(_headSec);
        }

        UpdateUi();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_player.HasMedia && _player.IsPlaying && _player.Position.TotalSeconds >= RegionEndSec)
        {
            ResetToStart();
        }

        UpdateUi();

        if (!_player.IsPlaying)
        {
            _timer.Stop();
        }
    }

    private void UpdateUi()
    {
        var total = Vm.TotalSeconds;

        if (_player.HasMedia)
        {
            _headSec = _player.Position.TotalSeconds;
            var dur = _player.Duration.TotalSeconds;

            if (dur > 0)
            {
                total = dur;
            }

            SetPlaying(_player.IsPlaying);
        }
        else
        {
            SetPlaying(false);
        }

        Wave.PlayFraction = total > 0 ? Math.Clamp(_headSec / total, 0, 1) : -1;
        Wave.StartFraction = total > 0 ? Math.Clamp(_startSec / total, 0, 1) : -1;

        var end = RegionEndSec >= total ? total : RegionEndSec;

        TimeText.Text =
            $"{Fmt(TimeSpan.FromSeconds(_startSec))} / {Fmt(TimeSpan.FromSeconds(_headSec))} / {Fmt(TimeSpan.FromSeconds(end))}";
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

        var total = Total();
        var end = EffectiveEndSec(total);
        _startSec = Math.Clamp(_startSec, RegionStartSec, end > RegionStartSec ? end : RegionStartSec);
        _headSec = Math.Clamp(_headSec, _startSec, end > _startSec ? end : _startSec);
        UpdateUi();
    }

    private void ResetToStart()
    {
        _headSec = _startSec;
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
