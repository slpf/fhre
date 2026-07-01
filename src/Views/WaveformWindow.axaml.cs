using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using FH6RB;
using FH6RB.Assets;
using FH6RB.Core;
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
    private bool _looping;
    private double _loopStart;
    private double _loopEnd;
    private double _dropStartSec = -1;
    private readonly HashSet<Key> _down = [];
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
        Wave.HeadSeekRequested += OnHeadSeek;
        Wave.LabelRowsChanged += OnLabelRowsChanged;
        Wave.RegionChanged += OnRegionChanged;
        _player.Ended += OnPlaybackEnded;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);

        Wave.Focusable = true;

        Opened += (_, _) =>
        {
            WindowMemory.Restore(this, Vm.Settings, "Waveform");
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
            _player.Ended -= OnPlaybackEnded;
            Wave.SeekRequested -= OnSeek;
            Wave.HeadSeekRequested -= OnHeadSeek;
            Wave.LabelRowsChanged -= OnLabelRowsChanged;
            Wave.RegionChanged -= OnRegionChanged;

            WindowMemory.Save(this, Vm.Settings, "Waveform");
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
            LoopFinder.ClearCache();
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

    private void StartPlayback(double fromSec, bool loop)
    {
        var wav = Vm.WavPath;
        if (string.IsNullOrEmpty(wav) || !File.Exists(wav))
        {
            return;
        }

        _looping = loop;
        _player.Play(wav, 0);
        _player.Position = TimeSpan.FromSeconds(Math.Max(0, fromSec));
        if (loop)
        {
            _player.SetLoop(_loopStart, _loopEnd);
        }
        else
        {
            _player.ClearLoop();
        }

        _headSec = fromSec;
        _timer.Start();
        UpdateUi();
    }

    private void StopPlayback()
    {
        _looping = false;
        _dropStartSec = -1;
        _player.Stop();
        _headSec = _startSec;
        UpdateUi();
    }

    private void Resume()
    {
        _player.TogglePause();

        if (_player.IsPlaying)
        {
            _timer.Start();
        }

        UpdateUi();
    }

    private void PauseToggle()
    {
        if (!_player.HasMedia)
        {
            return;
        }

        _player.TogglePause();

        if (_player.IsPlaying)
        {
            _timer.Start();
        }

        UpdateUi();
    }

    private void PlayStop()
    {
        if (_player.IsPaused)
        {
            Resume();
        }
        else if (_player.IsPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback(_startSec, loop: false);
        }
    }

    private void StartLoop(double startSec, double endSec)
    {
        if (endSec <= startSec)
        {
            return;
        }

        _loopStart = startSec;
        _loopEnd = endSec;
        StartPlayback(startSec, loop: true);
    }

    private void StartLoopWithReturnTo(double dropSec, double endSec, double loopStartSec)
    {
        if (endSec <= dropSec)
        {
            return;
        }

        _loopStart = loopStartSec;
        _loopEnd = endSec;
        _dropStartSec = dropSec;
        StartPlayback(dropSec, loop: false);
    }

    private void LoopToggle()
    {
        if (_looping && (_player.IsPlaying || _player.IsPaused))
        {
            StopPlayback();
        }
        else
        {
            StartLoop(RegionStartSec, EffectiveEndSec(Total()));
        }
    }

    private CancellationTokenSource? _suggestCts;
    private bool _forceClose;
    private bool _confirmingClose;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            _suggestCts?.Cancel();
            base.OnClosing(e);
            return;
        }

        if (Vm.IsSuggesting)
        {
            e.Cancel = true;
            if (!_confirmingClose)
            {
                _ = ConfirmCloseWhileSuggestingAsync();
            }
            return;
        }

        _suggestCts?.Cancel();
        base.OnClosing(e);
    }

    private async Task ConfirmCloseWhileSuggestingAsync()
    {
        _confirmingClose = true;
        var ok = await MessageDialog.ShowAsync(this,
            Str.DlgInterruptSearchTitle,
            Str.DlgInterruptSearchBody,
            Str.DlgInterruptSearchOk,
            Str.BtnCancel);
        _confirmingClose = false;
        if (!ok) return;

        _suggestCts?.Cancel();
        _forceClose = true;
        Close();
    }

    private void OnSuggestLoops(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(() => SuggestLoopsAsync(sender), "suggest loops", this);

    private async Task SuggestLoopsAsync(object? sender)
    {
        if (sender is not Control ctrl || ctrl.Tag is not MarkerField start || start.LoopEndName is null || Vm.SampleRate <= 0)
        {
            return;
        }

        MarkerField? end = null;

        foreach (var m in Vm.AllMarkers)
        {
            if (m.Name == start.LoopEndName)
            {
                end = m;
                break;
            }
        }

        if (end is null)
        {
            return;
        }

        // MarkerField? postDrop = null;
        // if (start.Name == "PostRaceLoopStart")
        // {
        //     foreach (var m in Vm.AllMarkers)
        //     {
        //         if (m.Name == "PostDrop")
        //         {
        //             postDrop = m;
        //             break;
        //         }
        //     }
        // }

        var rate = Vm.SampleRate;
        var auto = Vm.Settings?.LoopAutoTune ?? true;
        // var role = start.Name == "PostRaceLoopStart" ? LoopRole.Post
        //     : start.Name == "TrackLoopStart" ? LoopRole.Track
        //     : LoopRole.Generic;
        var role = LoopRole.Generic;
        var minMatch = auto ? 0.80 : (Vm.Settings?.LoopMinMatch ?? 0.80);

        List<LoopPair> pairs;

        try
        {
            if (Vm.Peaks is null)
            {
                Vm.IsSuggesting = true;
                await Vm.EnsurePeaksAsync();
            }

            var samples = Vm.Peaks;
            if (samples is null)
            {
                return;
            }

            var cacheKey = Vm.WavPath;

            var stages = auto ? LoopSearchDefaults.AutoStages : (Vm.Settings?.LoopStages ?? LoopSearchDefaults.AutoStages);
            var loopOptions = auto
                ? new LoopSearchOptions { Role = role, AutoTune = true, MinLoopSeconds = LoopSearchDefaults.MinLoopSeconds, Stages = stages }
                : new LoopSearchOptions
                {
                    AutoTune = false,
                    Role = role,
                    MinLoopSeconds = LoopSearchDefaults.MinLoopSeconds,
                    Stages = stages,
                    NoteDeviation = Vm.Settings?.LoopNoteDeviation ?? 0.0875,
                    BorderSimilarityThreshold = Vm.Settings?.LoopBorderSimilarity ?? 0.3,
                    TransitionSmoothnessThreshold = Vm.Settings?.LoopTransitionSmoothness ?? 0.3,
                    LoudnessDifference = Vm.Settings?.LoopLoudnessDifference ?? 0.4,
                    UseHarmonicChroma = Vm.Settings?.LoopUseHarmonicChroma ?? false,
                    RequireOnsetAlignment = Vm.Settings?.LoopRequireOnsetAlignment ?? true,
                    PreEmphasis = Vm.Settings?.LoopPreEmphasis ?? false,
                    MultiResolution = Vm.Settings?.LoopMultiResolution ?? false,
                    DisablePruning = Vm.Settings?.LoopDisablePruning ?? false,
                };

            Action<string>? logger = null;
#if DEBUG
            logger = s => FH6RB.Services.Log.Line(s);
#endif

            _suggestCts?.Cancel();
            _suggestCts = new CancellationTokenSource();
            var ct = _suggestCts.Token;

            var task = Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return new List<LoopPair>();
                return LoopFinder.Find(samples, rate, loopOptions, cacheKey, logger, null, ct);
            }, ct);
            if (!Vm.IsSuggesting && !task.IsCompleted)
            {
                var first = await Task.WhenAny(task, Task.Delay(120));
                if (first != task)
                {
                    Vm.IsSuggesting = true;
                }
            }

            pairs = await task;
            if (ct.IsCancellationRequested) return;
        }
        finally
        {
            Vm.IsSuggesting = false;
        }

        var headerBrush = Application.Current?.FindResource("Header") as IBrush;
        var muteBrush = Application.Current?.FindResource("TxtMute") as IBrush;
        var monoFont = Application.Current?.FindResource("MonoFont") as FontFamily;

        var panel = new StackPanel { Spacing = 0 };
        var flyout = new SuggestFlyout
        {
            Content = panel,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };

        if (pairs.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Str.MenuNoLoops,
                Padding = new Thickness(14, 10),
                Foreground = muteBrush,
            });
        }
        else
        {
            const int MinShown = 10;
            const double StepDown = 0.025;
            const double MinThreshold = 0.50;

            var effectiveMinMatch = minMatch;
            while (effectiveMinMatch > MinThreshold)
            {
                var count = 0;
                foreach (var p in pairs)
                {
                    if (p.Score >= effectiveMinMatch) count++;
                    if (count >= MinShown) break;
                }
                if (count >= MinShown) break;
                effectiveMinMatch -= StepDown;
            }

            var shown = 0;

            foreach (var p in pairs)
            {
                if (p.Score < effectiveMinMatch)
                {
                    continue;
                }

                var ss = p.LoopStart;
                var es = p.LoopEnd;
                var startSec = ss / (double) rate;
                var endSec = es / (double) rate;
                var lenSec = (es - ss) / (double) rate;
                var matchPct = p.Score * 100;

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(14, 8),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = headerBrush,
                    Content = new TextBlock
                    {
                        Text = $"{startSec,7:0.00}s ({ss,8})  \u2192  {endSec,7:0.00}s ({es,8})   \u00b7   Length: {lenSec,6:0.0}s   \u00b7   Match: {matchPct,5:0.0}%   \u00b7   {p.Source}",
                        FontFamily = monoFont!,
                        FontSize = 12,
                        TextTrimming = TextTrimming.None,
                    },
                };

                var captureStart = start;
                var captureEnd = end;
                // var capturePostDrop = postDrop;
                btn.Click += (_, _) =>
                {
                    captureStart.Position = ss;
                    captureEnd.Position = es;
                    // capturePostDrop?.Position = ss;
                    StartLoop(ss / (double) rate, es / (double) rate);
                    Wave.Focus();
                    flyout.Hide();
                };

                panel.Children.Add(btn);
                shown++;

                if (shown >= 10)
                {
                    break;
                }
            }

            if (shown == 0)
            {
                panel.Children.Clear();
                panel.Children.Add(new TextBlock
                {
                    Text = Str.MenuNoLoops,
                    Padding = new Thickness(14, 10),
                    Foreground = muteBrush,
                });
            }
        }

        flyout.ShowAt(ctrl);
    }

    private void OnPlayMarkerLoop(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: MarkerField start } || Vm.SampleRate <= 0)
        {
            return;
        }

        var end = start.LoopEndName is { } endName ? FindMarker(endName) : null;
        var loopReturnTo = start.LoopReturnToName is { } rName ? FindMarker(rName) : null;

        if (end is null || start.Position < 0 || end.Position < 0)
        {
            return;
        }

        var startSec = (double) start.Position / Vm.SampleRate;
        var endSec = (double) end.Position / Vm.SampleRate;

        if (loopReturnTo is { Position: < 0 })
        {
            loopReturnTo = null;
        }

        if (loopReturnTo is not null)
        {
            if (start.Position > end.Position)
            {
                SafeAsync.Run(() => ShowDropAfterLoopAsync(start.Name, end.Name), "drop after loop", this);
                return;
            }

            StartLoopWithReturnTo(startSec, endSec, (double) loopReturnTo.Position / Vm.SampleRate);
        }
        else
        {
            StartLoop(startSec, endSec);
        }

        Wave.Focus();
    }

    private MarkerField? FindMarker(string name)
    {
        foreach (var m in Vm.AllMarkers)
        {
            if (m.Name == name)
            {
                return m;
            }
        }

        return null;
    }

    private async Task ShowDropAfterLoopAsync(string startName, string endName)
    {
        var (title, body) = (startName, endName) switch
        {
            ("TrackDrop", "TrackLoopEnd") => (Str.DlgTrackDropAfterLoopTitle, Str.DlgTrackDropAfterLoopBody),
            ("PostDrop", "PostRaceLoopEnd") => (Str.DlgPostDropAfterLoopTitle, Str.DlgPostDropAfterLoopBody),
            _ => (Str.TitleNotice, $"{startName} must not be later than {endName}."),
        };

        await MessageDialog.ShowAsync(this, title, body);
    }

    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (_player.IsPaused)
        {
            Resume();
        }
        else
        {
            StartPlayback(_startSec, loop: false);
        }
    }

    private void OnPause(object? sender, RoutedEventArgs e) => PauseToggle();

    private void OnStop(object? sender, RoutedEventArgs e) => StopPlayback();

    private void OnPlayLoop(object? sender, RoutedEventArgs e)
    {
        StartLoop(RegionStartSec, EffectiveEndSec(Total()));
        Wave.Focus();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            if (e.Key == Key.Escape)
            {
                Wave.Focus();
                e.Handled = true;
            }

            return;
        }

        if (e.Key is Key.Space or Key.P or Key.L)
        {
            if (!_down.Add(e.Key))
            {
                e.Handled = true;
                return;
            }
        }

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

            case Key.L:
                LoopToggle();
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
        _down.Remove(e.Key);

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

        var lo = RegionStartSec;
        var hi = EffectiveEndSec(total);
        var baseSec = _player.HasMedia ? _player.Position.TotalSeconds : _headSec;

        double target;

        if (baseSec < lo)
        {
            if (deltaSec < 0)
            {
                return;
            }

            target = Math.Min(baseSec + deltaSec, hi);
        }
        else if (baseSec > hi)
        {
            if (deltaSec > 0)
            {
                return;
            }

            target = Math.Max(baseSec + deltaSec, lo);
        }
        else
        {
            target = Math.Clamp(baseSec + deltaSec, lo, hi);
        }

        _headSec = target;

        if (_dropStartSec >= 0 && (target < _dropStartSec || target > _loopEnd))
        {
            _dropStartSec = -1;
        }

        if (_looping && (target < _loopStart || target >= _loopEnd))
        {
            _looping = false;
            _player.ClearLoop();
        }

        if (_player.HasMedia)
        {
            _player.Position = TimeSpan.FromSeconds(target);
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

        if (!_player.IsPlaying && !_player.IsPaused)
        {
            _headSec = _startSec;
        }

        UpdateUi();
    }

    private void OnHeadSeek(double fraction)
    {
        if (!_player.IsPlaying && !_player.IsPaused)
        {
            return;
        }

        var total = Total();

        if (total <= 0)
        {
            return;
        }

        var target = Math.Clamp(fraction * total, RegionStartSec, EffectiveEndSec(total));
        _headSec = target;

        if (_dropStartSec >= 0 && (target < _dropStartSec || target > _loopEnd))
        {
            _dropStartSec = -1;
        }

        if (_looping && (target < _loopStart || target >= _loopEnd))
        {
            _looping = false;
            _player.ClearLoop();
        }

        if (_player.HasMedia)
        {
            _player.Position = TimeSpan.FromSeconds(target);
        }

        UpdateUi();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_player.HasMedia && _player.IsPlaying)
        {
            var currentPos = _player.Position.TotalSeconds;

            if (_dropStartSec >= 0 && currentPos >= _loopEnd)
            {
                _dropStartSec = -1;
                _looping = true;
                _player.SetLoop(_loopStart, _loopEnd);
            }
            else if (!_looping && currentPos >= RegionEndSec)
            {
                StopPlayback();
            }
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
        }

        Wave.PlayFraction = total > 0 ? Math.Clamp(_headSec / total, 0, 1) : -1;
        Wave.StartFraction = total > 0 ? Math.Clamp(_startSec / total, 0, 1) : -1;

        var end = RegionEndSec >= total ? total : RegionEndSec;

        TimeText.Text =
            $"{Fmt(TimeSpan.FromSeconds(_startSec))} / {Fmt(TimeSpan.FromSeconds(_headSec))} / {Fmt(TimeSpan.FromSeconds(end))}";
    }

    private static string Fmt(TimeSpan t) =>
        FormattableString.Invariant($"{(int) t.TotalMinutes}:{t.Seconds:00} ({t.TotalSeconds:0.000})");

    private void OnResetDefaults(object? sender, RoutedEventArgs e) => Vm.ResetMarkersToDefaults();

    private void OnLoopSearchSettings(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(LoopSearchSettingsAsync, "loop search settings", this);

    private async Task LoopSearchSettingsAsync()
    {
        var settings = Vm.Settings ?? new AppSettings();
        var dlg = new LoopSearchSettingsWindow
        {
            DataContext = new LoopSearchSettingsViewModel(settings),
        };
        dlg.RunTestsRequested += OnRunTestsRequested;
        dlg.Closed += (_, _) => dlg.RunTestsRequested -= OnRunTestsRequested;
        await dlg.ShowDialog(this);
    }

    private async void OnRunTestsRequested(object? sender, EventArgs e)
    {
#if DEBUG
        if (Vm.IsSuggesting)
        {
            return;
        }

        Vm.IsSuggesting = true;
        try
        {
            var testsDir = LoopFinderTester.ResolveTestsDir();
            var resultsDir = LoopFinderTester.ResolveResultsDir();
            var settings = Vm.Settings ?? new AppSettings();
            _suggestCts?.Cancel();
            _suggestCts = new CancellationTokenSource();
            await Task.Run(() => LoopFinderTester.RunAsync(
                testsDir, resultsDir, settings, progress: null, _suggestCts.Token));
        }
        catch (Exception ex)
        {
            Log.Line($"LoopFinder tests failed: {ex.Message}");
        }
        finally
        {
            Vm.IsSuggesting = false;
        }
#endif
    }

    private void OnSavePreset(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(SavePresetAsync, "save preset", this);

    private async Task SavePresetAsync()
    {
        if (!Vm.CanEditMarkers)
        {
            FH6RB.Services.Log.Line("Preset save: no markers to save (CanEditMarkers=false)");
            return;
        }
        var artist = (Vm.Artist ?? "").Trim();
        var title = (Vm.Title ?? "").Trim();
        var defaultName = artist.Length > 0 && title.Length > 0
            ? $"{artist} - {title}"
            : (title.Length > 0 ? title : Str.PresetNameWatermark);

        var name = await InputDialog.ShowAsync(this, Str.PresetNameTitle, Str.PresetNameWatermark,
            defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var positions = Vm.CollectMarkerPositions();
        if (positions.Count == 0)
        {
            FH6RB.Services.Log.Line("Preset save: no marker positions set");
            return;
        }

        if (MarkerPresetService.Save(name!, Vm.SampleRate, positions))
        {
            FH6RB.Services.Log.Line($"Preset saved: {name} ({positions.Count} markers)");
        }
    }

    private void OnLoadPreset(object? sender, RoutedEventArgs e)
        => SafeAsync.Run(LoadPresetAsync, "load preset", this);

    private async Task LoadPresetAsync()
    {
        if (!Vm.CanEditMarkers)
        {
            FH6RB.Services.Log.Line("Preset load: no markers to apply (CanEditMarkers=false)");
            return;
        }
        var name = await MarkerPresetLoadDialog.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(name)) return;

        var preset = MarkerPresetService.Load(name!);
        if (preset is null) return;

        var hits = Vm.ApplyMarkerPositions(preset.Markers);
        FH6RB.Services.Log.Line($"Preset loaded: {name} ({hits} markers updated)");
    }

    private TextBox? _ctxField;

    private void OnFieldContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _ctxField = sender as TextBox;

        if (_ctxField?.ContextFlyout is MenuFlyout mf && _ctxField.DataContext is MarkerField f)
        {
            foreach (var item in mf.Items.OfType<MenuItem>())
            {
                if (item.Tag is "toSeconds")
                {
                    item.IsVisible = f.DisplayUnit != MarkerValueUnit.Seconds;
                }
                else if (item.Tag is "toSamples")
                {
                    item.IsVisible = f.DisplayUnit != MarkerValueUnit.Samples;
                }
            }
        }
    }

    private void OnFieldCut(object? sender, RoutedEventArgs e) => _ctxField?.Cut();

    private void OnFieldCopy(object? sender, RoutedEventArgs e) => _ctxField?.Copy();

    private void OnFieldPaste(object? sender, RoutedEventArgs e) => _ctxField?.Paste();

    private void OnFieldRevert(object? sender, RoutedEventArgs e) => (_ctxField?.DataContext as MarkerField)?.Revert();

    private void OnFieldReset(object? sender, RoutedEventArgs e) => (_ctxField?.DataContext as MarkerField)?.Reset();

    private void OnFieldToSeconds(object? sender, RoutedEventArgs e) => SetFieldUnit(MarkerValueUnit.Seconds);

    private void OnFieldToSamples(object? sender, RoutedEventArgs e) => SetFieldUnit(MarkerValueUnit.Samples);

    private void SetFieldUnit(MarkerValueUnit unit)
    {
        if (_ctxField?.DataContext is MarkerField f)
        {
            f.DisplayUnit = unit;
        }
    }

    private void OnLabelRowsChanged()
    {
        if (Vm.Settings is { } s)
        {
            SettingsService.Save(s);
        }
    }

    private void OnRegionChanged()
    {
        var total = Total();
        var end = EffectiveEndSec(total);
        _startSec = Math.Clamp(_startSec, RegionStartSec, end > RegionStartSec ? end : RegionStartSec);
        UpdateUi();
    }

    private void OnPlaybackEnded() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_looping)
            {
                StartPlayback(_loopStart, loop: true);
            }
            else
            {
                StopPlayback();
            }
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

    private sealed class SuggestFlyout : Flyout
    {
        protected override Control CreatePresenter()
        {
            var presenter = base.CreatePresenter()!;
            presenter.MaxWidth = double.PositiveInfinity;
            return presenter;
        }
    }
}
