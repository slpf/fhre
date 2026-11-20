using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using FH6RB.Assets;
using FH6RB.Services;

namespace FH6RB.ViewModels;

public sealed partial class MainWindowViewModel
{
    private sealed record LoudnessRequest(TrackItemViewModel Track, string Path, int? SubIndex);
    private HashSet<TrackItemViewModel>? _loudnessTracks;
    private CancellationTokenSource? _loudnessCts;
    private Task? _loudnessTask;
    private bool _loudnessShutdown;

    private void RefreshLoudnessTracking()
    {
        _loudnessTracks ??= [];
        foreach (var removed in _loudnessTracks.Where(t => !Tracks.Contains(t)).ToArray())
        {
            removed.PropertyChanged -= OnLoudnessSourceChanged;
            _loudnessTracks.Remove(removed);
        }
        foreach (var track in Tracks)
        {
            if (_loudnessTracks.Add(track)) track.PropertyChanged += OnLoudnessSourceChanged;
        }
        ScheduleLoudnessMeasurements();
    }

    private void OnLoudnessSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(TrackItemViewModel.SourcePath) or nameof(TrackItemViewModel.ReplacementPath)
            or nameof(TrackItemViewModel.RestoreFsbPath))) return;
        if (sender is TrackItemViewModel track)
        {
            track.LoudnessError = null;
            track.LoudnessText = Settings.MeasureTrackLoudness ? Str.TrackLoudnessPending : "";
        }
        ScheduleLoudnessMeasurements();
    }

    private LoudnessRequest? LoudnessSource(TrackItemViewModel track)
    {
        if (track.UsesFileSource && track.FileSource is { } source)
            return new(track, source, track.IsFileSourceFsb ? 0 : null);
        if (track.RestoreFsbPath is { } restored) return new(track, restored, 0);
        return _loadedBankPath is { } bank && track.SubIndex >= 0 && track.SampleLength > 0
            ? new(track, bank, track.SubIndex) : null;
    }

    private void CancelLoudnessMeasurements()
    {
        var cancellation = Interlocked.Exchange(ref _loudnessCts, null);
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task StopLoudnessAnalysisAsync()
    {
        CancelLoudnessMeasurements();
        if (_loudnessTask is { } task) await task;
    }

    private void ScheduleLoudnessMeasurements()
    {
        CancelLoudnessMeasurements();
        if (_loudnessTracks is null || _loudnessShutdown) return;
        if (!Settings.MeasureTrackLoudness)
        {
            foreach (var track in Tracks)
            {
                track.LoudnessText = "";
                track.LoudnessError = null;
            }
            return;
        }
        if (IsBusy || IsLoading) return;
        var requests = new List<LoudnessRequest>();
        foreach (var track in Tracks.OrderByDescending(t => t.Enabled))
        {
            if (track.LoudnessText.Length == 0) track.LoudnessText = Str.TrackLoudnessPending;
            var source = LoudnessSource(track);
            if (source is null || !Tools.HasFfmpeg || source.SubIndex is not null && !Tools.HasVgmstream)
            {
                track.LoudnessText = Str.TrackLoudnessUnavailable;
                continue;
            }
            requests.Add(source);
        }
        var cancellation = new CancellationTokenSource();
        _loudnessCts = cancellation;
        var previous = _loudnessTask ?? Task.CompletedTask;
        _loudnessTask = Task.Run(async () =>
        {
            var token = cancellation.Token;
            try
            {
                await previous.ConfigureAwait(false);
                await Task.Delay(150, token).ConfigureAwait(false);
                await Parallel.ForEachAsync(requests, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                    async (request, ct) =>
                    {
                        var text = Str.TrackLoudnessUnavailable;
                        string? error = null;
                        try
                        {
                            var value = await TrackLoudnessService.MeasureAsync(request.Path, request.SubIndex, ct).ConfigureAwait(false);
                            if (value is { } lufs) text = lufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS";
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                            Log.Line($"ui.loudness.failed source=\"{request.Path}\" sample={request.SubIndex}: {ex}");
                        }
                        ct.ThrowIfCancellationRequested();
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (ct.IsCancellationRequested || !Settings.MeasureTrackLoudness
                                || !Tracks.Contains(request.Track) || LoudnessSource(request.Track) != request) return;
                            request.Track.LoudnessError = error;
                            request.Track.LoudnessText = text;
                        });
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Log.Line($"ui.loudness.queue.failed: {ex}"); }
            finally { cancellation.Dispose(); }
        });
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBuild));
        ScheduleLoudnessMeasurements();
    }
}
