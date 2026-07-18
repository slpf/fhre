using NAudio.Wave;

namespace FH6RB.Services;

public sealed class PlaybackService : IDisposable
{
    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private LoopSampleProvider? _loop;
    private bool _stopping;

    public event Action? Ended;

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

    public void Play(string wavPath, double volumeDb, double fromSec = 0)
    {
        Stop();

        _reader = new AudioFileReader(wavPath)
        {
            Volume = Lin(volumeDb)
        };

        _loop = new LoopSampleProvider(_reader);
        _out = new WaveOutEvent { DesiredLatency = 300, NumberOfBuffers = 3 };
        _out.PlaybackStopped += OnStopped;
        _out.Init(_loop);

        if (fromSec > 0)
        {
            var wf = _reader.WaveFormat;
            var block = wf.Channels * (wf.BitsPerSample / 8);
            _reader.Position = Math.Clamp((long)(fromSec * wf.SampleRate) * block, 0, _reader.Length);
        }

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
        if (_reader is { } r)
        {
            r.Volume = Lin(db);
        }
    }

    public void SetLoop(double startSec, double endSec)
    {
        if (_loop is null || _reader is null)
        {
            return;
        }

        var wf = _reader.WaveFormat;
        var block = wf.Channels * (wf.BitsPerSample / 8);

        long ToBytes(double sec)
        {
            var frame = (long)Math.Round(sec * wf.SampleRate);
            return Math.Clamp(frame * block, 0, _reader.Length);
        }

        _loop.SetLoop(ToBytes(startSec), ToBytes(endSec));
    }

    public void ClearLoop() => _loop?.Clear();

    public void Stop()
    {
        if (_out is null)
        {
            return;
        }

        _stopping = true;
        _out.PlaybackStopped -= OnStopped;

        try
        {
            _out.Stop();
        }
        catch
        {
        }

        _out.Dispose();
        _reader?.Dispose();
        _out = null;
        _reader = null;
        _loop = null;
        _stopping = false;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!_stopping && ReferenceEquals(sender, _out))
        {
            Ended?.Invoke();
        }
    }

    private static float Lin(double db) => (float)Math.Pow(10, db / 20.0);

    private sealed class LoopSampleProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly object _gate = new();
        private bool _enabled;
        private long _start;
        private long _end;
        private volatile bool _seekPending;
        private long _seekPos;

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
            _seekPos = sampleFrame * block;
            _seekPending = true;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_seekPending)
            {
                _reader.Position = Math.Clamp(_seekPos, 0, _reader.Length);
                _seekPending = false;
            }

            bool enabled;
            long start, end;
            lock (_gate)
            {
                enabled = _enabled;
                start = _start;
                end = _end;
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

                var samplesToEnd = (int)((end - _reader.Position) / bytesPerSample);
                if (samplesToEnd <= 0)
                {
                    _reader.Position = start;
                    continue;
                }

                var toRead = Math.Min(count - read, samplesToEnd);
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
