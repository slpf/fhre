using Avalonia.Controls;
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

        if (!_player.HasMedia)
        {
            var startSec = _cursorSec < RegionStartSec - 0.001 || _cursorSec >= RegionEndSec
                ? RegionStartSec
                : _cursorSec;

            _player.Play(wav, 0);
            _player.Position = TimeSpan.FromSeconds(startSec);
            _timer.Start();
        }
        else if (_player.IsPlaying)
        {
            _player.TogglePause();
        }
        else
        {
            var pos = _player.Position.TotalSeconds;

            if (pos < RegionStartSec - 0.001 || pos >= RegionEndSec)
            {
                _player.Position = TimeSpan.FromSeconds(RegionStartSec);
            }

            _player.TogglePause();
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
            _player.TogglePause();
            _player.Position = TimeSpan.FromSeconds(RegionEndSec);
        }

        UpdateUi();
    }

    private void UpdateUi()
    {
        if (_player.HasMedia)
        {
            _cursorSec = _player.Position.TotalSeconds;
            var dur = _player.Duration.TotalSeconds;
            Wave.PlayFraction = dur > 0 ? _cursorSec / dur : -1;
            TimeText.Text = $"{Fmt(_player.Position)} / {Fmt(_player.Duration)}";
            SetPlaying(_player.IsPlaying);
        }
        else
        {
            var total = Vm.TotalSeconds;
            Wave.PlayFraction = total > 0 ? Math.Clamp(_cursorSec / total, 0, 1) : -1;
            TimeText.Text = $"{Fmt(TimeSpan.FromSeconds(_cursorSec))} / {Fmt(TimeSpan.FromSeconds(total))}";
            SetPlaying(false);
        }
    }

    private void SetPlaying(bool playing)
    {
        PlayIcon.IsVisible = !playing;
        PauseIcon.IsVisible = playing;
    }
    
    private static string Fmt(TimeSpan t) =>
        FormattableString.Invariant($"{(int) t.TotalMinutes}:{t.Seconds:00} ({t.TotalSeconds:0.000})");

    private async void OnEditDefaults(object? sender, RoutedEventArgs e)
    {
        if (Vm.Settings is null)
        {
            return;
        }

        var vm = new MarkerDefaultsViewModel(Vm.Settings);
        var w = new MarkerDefaultsWindow { DataContext = vm };
        await w.ShowDialog(this);
    }

    private void OnResetDefaults(object? sender, RoutedEventArgs e) => Vm.ResetMarkersToDefaults();

    private void OnLabelRowsChanged()
    {
        if (Vm.Settings is { } s)
        {
            SettingsService.Save(s);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Saved = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
