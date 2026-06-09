using NAudio.Wave;

namespace FH6RB.Services;

public sealed class PlaybackService : IDisposable
{
    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private bool _stopping;

    public event Action? Ended;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _out?.PlaybackState == PlaybackState.Paused;
    public bool HasMedia => _reader is not null;

    public TimeSpan Position
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set { if (_reader is not null) _reader.CurrentTime = value; }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public void Play(string wavPath, double volumeDb)
    {
        Stop();
        _reader = new AudioFileReader(wavPath) { Volume = Lin(volumeDb) };
        _out = new WaveOutEvent();
        _out.PlaybackStopped += OnStopped;
        _out.Init(_reader);
        _out.Play();
    }

    public void TogglePause()
    {
        if (_out is null)
        {
            return;
        }

        if (_out.PlaybackState == PlaybackState.Playing)
        {
            _out.Pause();
        }
        else if (_out.PlaybackState == PlaybackState.Paused)
        {
            _out.Play();
        }
    }

    public void SetVolumeDb(double db)
    {
        if (_reader is not null)
        {
            _reader.Volume = Lin(db);
        }
    }

    public void Stop()
    {
        if (_out is null)
        {
            return;
        }

        _stopping = true;
        _out.PlaybackStopped -= OnStopped;
        try { _out.Stop(); } catch { /* ignore */ }
        _out.Dispose();
        _reader?.Dispose();
        _out = null;
        _reader = null;
        _stopping = false;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!_stopping)
        {
            Ended?.Invoke();
        }
    }

    private static float Lin(double db) => (float)Math.Pow(10, db / 20.0);

    public void Dispose() => Stop();
}
