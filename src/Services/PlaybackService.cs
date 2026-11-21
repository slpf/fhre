using Avalonia.Threading;
using NAudio.Wave;

namespace FH6RB.Services;

public sealed class PlaybackService : IDisposable
{
    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private LoopSampleProvider? _loop;
    public event Action<Exception?>? Ended;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _out?.PlaybackState == PlaybackState.Paused;
    public bool HasMedia => _reader is not null;

    public TimeSpan Position
    {
        get => _loop?.Position ?? TimeSpan.Zero;
        set
        {
            if (_loop is null || _reader is null) return;
            _loop.RequestSeek((long)(value.TotalSeconds * _reader.WaveFormat.SampleRate));
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public void Play(string wavPath, double volumeDb, double fromSec = 0,
        (double Start, double End)? loop = null)
    {
        Stop();

        try
        {
            _reader = new AudioFileReader(wavPath) { Volume = Lin(volumeDb) };
            _loop = new LoopSampleProvider(_reader);
            _reader.Position = ToBytes(_reader, fromSec);
            if (loop is { } bounds)
            {
                _loop.SetLoop(ToBytes(_reader, bounds.Start), ToBytes(_reader, bounds.End));
            }

            _out = new WaveOutEvent { DesiredLatency = 300, NumberOfBuffers = 3 };
            _out.PlaybackStopped += OnStopped;
            _out.Init(_loop);
            _out.Play();
        }
        catch
        {
            Stop();
            throw;
        }
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
        if (_reader is { } r)
        {
            r.Volume = Lin(db);
        }
    }

    private static long ToBytes(AudioFileReader reader, double seconds)
    {
        var format = reader.WaveFormat;
        var frame = (long)Math.Clamp(Math.Round(seconds * format.SampleRate), 0, reader.Length / format.BlockAlign);
        return frame * format.BlockAlign;
    }

    public void ClearLoop() => _loop?.Clear();

    public void Stop()
    {
        var output = _out;
        var reader = _reader;
        _out = null;
        _reader = null;
        _loop = null;

        try
        {
            if (output is not null)
            {
                output.PlaybackStopped -= OnStopped;
                output.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Line("audio output cleanup failed: " + ex);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (sender is not null && ReferenceEquals(sender, _out)) Ended?.Invoke(e.Exception);
        });
    }

    private static float Lin(double db) => (float)Math.Pow(10, db / 20.0);

    private sealed class LoopSampleProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly object _gate = new();
        private bool _enabled;
        private long _start;
        private long _end;
        private long? _seekPos;

        public WaveFormat WaveFormat => _reader.WaveFormat;

        public LoopSampleProvider(AudioFileReader reader) => _reader = reader;

        public TimeSpan Position => _reader.CurrentTime;

        public void SetLoop(long startBytes, long endBytes)
        {
            lock (_gate)
            {
                _start = startBytes;
                _end = endBytes;
                _enabled = endBytes > startBytes;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _enabled = false;
            }
        }

        public void RequestSeek(long sampleFrame)
        {
            if (_reader.WaveFormat.SampleRate <= 0) return;
            var block = _reader.WaveFormat.Channels * (_reader.WaveFormat.BitsPerSample / 8);
            lock (_gate)
            {
                _seekPos = sampleFrame * block;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            bool enabled;
            long start, end;
            long? seekPos;
            lock (_gate)
            {
                seekPos = _seekPos;
                _seekPos = null;
                enabled = _enabled;
                start = _start;
                end = _end;
            }

            if (seekPos is { } position)
            {
                _reader.Position = Math.Clamp(position, 0, _reader.Length);
            }

            if (!enabled)
            {
                return _reader.Read(buffer, offset, count);
            }

            var bytesPerSample = _reader.WaveFormat.BitsPerSample / 8;
            var read = 0;
            while (read < count)
            {
                if (_reader.Position >= end)
                {
                    _reader.Position = start;
                }

                var samplesToEnd = (end - _reader.Position) / bytesPerSample;
                var toRead = (int)Math.Min(count - read, samplesToEnd);
                var n = _reader.Read(buffer, offset + read, toRead);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return read;
        }
    }

    public void Dispose() => Stop();
}
