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
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set
        {
            if (_reader is null)
            {
                return;
            }

            if (_loop is null)
            {
                _reader.CurrentTime = value;
                return;
            }

            _loop.ExecuteUnderLock(() => _reader.CurrentTime = value);
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public void Play(string wavPath, double volumeDb)
    {
        Stop();
        
        _reader = new AudioFileReader(wavPath)
        {
            Volume = Lin(volumeDb)
        };
        
        _loop = new LoopSampleProvider(_reader);
        _out = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 4 };
        _out.PlaybackStopped += OnStopped;
        _out.Init(_loop);
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
        _reader?.Volume = Lin(db);
    }

    public void SetLoop(double startSec, double endSec)
    {
        if (_reader is null || _loop is null)
        {
            return;
        }

        var wf = _reader.WaveFormat;
        var block = wf.Channels * (wf.BitsPerSample / 8);

        long ToBytes(double sec)
        {
            var frame = (long) Math.Round(sec * wf.SampleRate);
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
             // ignored
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
        if (!_stopping)
        {
            Ended?.Invoke();
        }
    }

    private sealed class LoopSampleProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        private readonly object _gate = new();
        private bool _enabled;
        private long _startBytes;
        private long _endBytes;

        public LoopSampleProvider(AudioFileReader reader) => _reader = reader;

        public WaveFormat WaveFormat => _reader.WaveFormat;

        public void ExecuteUnderLock(Action action)
        {
            lock (_gate)
            {
                action();
            }
        }

        public void SetLoop(long startBytes, long endBytes)
        {
            lock (_gate)
            {
                _startBytes = startBytes;
                _endBytes = endBytes;
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

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                if (!_enabled)
                {
                    return _reader.Read(buffer, offset, count);
                }

                var bytesPerSample = _reader.WaveFormat.BitsPerSample / 8;
                var read = 0;
                var resets = 0;
                while (read < count)
                {
                    if (_reader.Position >= _endBytes)
                    {
                        _reader.Position = _startBytes;
                        if (++resets > 2)
                        {
                            break;
                        }
                    }

                    var samplesToEnd = (int) ((_endBytes - _reader.Position) / bytesPerSample);
                    if (samplesToEnd <= 0)
                    {
                        _reader.Position = _startBytes;
                        if (++resets > 2)
                        {
                            break;
                        }

                        continue;
                    }

                    var toRead = Math.Min(count - read, samplesToEnd);
                    var n = _reader.Read(buffer, offset + read, toRead);
                    if (n == 0)
                    {
                        _reader.Position = _startBytes;
                        if (++resets > 2)
                        {
                            break;
                        }

                        continue;
                    }

                    read += n;
                }

                return read;
            }
        }
    }

    private static float Lin(double db) => (float)Math.Pow(10, db / 20.0);

    public void Dispose() => Stop();
}
