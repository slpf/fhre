using System.Buffers.Binary;
using System.Security.Cryptography;
using NAudio.Wave;

namespace FH6RB.Services;

public sealed record AudioBuildInfo(
    int SampleRate,
    int Channels,
    long Frames,
    int BitsPerSample,
    double[] RmsDb,
    double[] PeakDb,
    double? StereoCorrelation,
    string PcmSha256);

public sealed record AudioBuildComparison(
    double[] ChannelCorrelation,
    double[] GainDb,
    double[] ErrorRmsDb);

public static class AudioBuildValidation
{
    private const int VorbisShortBlock = 256;
    private const int VorbisLongBlock = 2048;

    public static void ValidateVorbisPackets(string path, long offset, long length, long expectedFrames,
        ReadOnlySpan<byte> sampleHeader, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(path);
        if (offset < 0 || length <= 0 || offset > stream.Length - length)
        {
            throw new InvalidDataException("Vorbis sample data lies outside its file.");
        }

        stream.Position = offset;
        var remaining = length;
        var seek = VorbisSeekTable(sampleHeader);
        var seekIndex = 0;
        var packetCount = 0L;
        var mixedFrames = 0L;
        var previousBlock = 0;
        var shortBlocks = true;
        var longBlocks = true;
        var mixedBlocks = true;
        Span<byte> header = stackalloc byte[2];
        while (remaining >= header.Length)
        {
            ct.ThrowIfCancellationRequested();
            var packetOffset = length - remaining;
            stream.ReadExactly(header);
            remaining -= header.Length;
            var size = BinaryPrimitives.ReadUInt16LittleEndian(header);
            if (size is 0 or ushort.MaxValue)
            {
                break;
            }
            if (size > remaining)
            {
                throw new InvalidDataException("Vorbis packet extends beyond its sample data.");
            }

            var first = stream.ReadByte();
            if (first < 0 || (first & 1) != 0)
            {
                throw new InvalidDataException("Vorbis sample contains an invalid audio packet.");
            }
            var block = (first & 2) != 0 ? VorbisLongBlock : VorbisShortBlock;
            if (packetCount > 0) mixedFrames += (previousBlock + block) / 4;
            previousBlock = block;

            while (seekIndex < seek.Length && BinaryPrimitives.ReadUInt32LittleEndian(seek[(seekIndex + 4)..]) <= packetOffset)
            {
                var position = BinaryPrimitives.ReadUInt32LittleEndian(seek[(seekIndex + 4)..]);
                var frames = BinaryPrimitives.ReadUInt32LittleEndian(seek[seekIndex..]);
                if (position != packetOffset)
                    throw new InvalidDataException("Vorbis seek table points outside a packet boundary.");
                shortBlocks &= frames == packetCount * (VorbisShortBlock / 2);
                longBlocks &= frames == packetCount * (VorbisLongBlock / 2);
                mixedBlocks &= frames == mixedFrames;
                seekIndex += 8;
            }
            stream.Seek(size - 1, SeekOrigin.Current);
            remaining -= size;
            packetCount++;
        }

        if (packetCount == 0)
        {
            throw new InvalidDataException("Vorbis sample contains no audio packets.");
        }
        bool Covers(long frames) => frames >= expectedFrames && frames - expectedFrames < VorbisLongBlock / 2;
        if (seekIndex != seek.Length || !(shortBlocks && Covers((packetCount - 1) * (VorbisShortBlock / 2))
            || longBlocks && Covers((packetCount - 1) * (VorbisLongBlock / 2))
            || mixedBlocks && Covers(mixedFrames)))
            throw new InvalidDataException("Vorbis packets do not cover the duration declared by the sample header.");

        ValidateVorbisPadding(stream, remaining, ct);
    }

    private static ReadOnlySpan<byte> VorbisSeekTable(ReadOnlySpan<byte> header)
    {
        for (var offset = 8; offset < header.Length;)
        {
            if (header.Length - offset < 4) throw new InvalidDataException("Vorbis metadata header is truncated.");
            var chunk = BinaryPrimitives.ReadUInt32LittleEndian(header[offset..]);
            var size = (int)((chunk >> 1) & 0xFFFFFF);
            offset += 4;
            if (size > header.Length - offset) throw new InvalidDataException("Vorbis metadata chunk is truncated.");
            if (chunk >> 25 == 11 && size >= 8)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header[(offset + 4)..]);
                if (length % 8 != 0 || length > size - 8)
                    throw new InvalidDataException("Vorbis seek table has an invalid length.");
                return header.Slice(offset + 8, (int)length);
            }
            offset += size;
        }
        return [];
    }

    private static void ValidateVorbisPadding(Stream stream, long length, CancellationToken ct)
    {
        Span<byte> padding = stackalloc byte[1024];
        while (length > 0)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = padding[..(int) Math.Min(length, padding.Length)];
            stream.ReadExactly(bytes);
            foreach (var value in bytes)
            {
                if (value is not (0 or byte.MaxValue))
                {
                    throw new InvalidDataException("Vorbis sample contains data after its final packet.");
                }
            }
            length -= bytes.Length;
        }
    }

    public static AudioBuildInfo Inspect(string wavPath, CancellationToken ct = default)
    {
        using var input = new WaveInput(wavPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sums = new double[input.Channels];
        var peaks = new double[input.Channels];
        double stereoProduct = 0;

        int count;
        while ((count = input.Read()) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(input.Bytes, 0, input.ByteCount);
            for (var i = 0; i < count; i += input.Channels)
            {
                for (var channel = 0; channel < input.Channels; channel++)
                {
                    var sample = input.Samples[i + channel];
                    sums[channel] += sample * sample;
                    peaks[channel] = Math.Max(peaks[channel], Math.Abs(sample));
                }

                if (input.Channels == 2)
                {
                    stereoProduct += input.Samples[i] * input.Samples[i + 1];
                }
            }
        }

        var rmsDb = new double[input.Channels];
        var peakDb = new double[input.Channels];
        for (var channel = 0; channel < input.Channels; channel++)
        {
            rmsDb[channel] = PowerDb(sums[channel] / input.Frames);
            peakDb[channel] = PowerDb(peaks[channel] * peaks[channel]);
        }

        double? stereoCorrelation = input.Channels == 2 && sums[0] > 0 && sums[1] > 0
            ? Correlation(stereoProduct, sums[0], sums[1])
            : null;

        return new AudioBuildInfo(input.SampleRate, input.Channels, input.Frames, input.BitsPerSample,
            rmsDb, peakDb, stereoCorrelation, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public static AudioBuildComparison Compare(string expectedWav, string decodedWav, int maximumTrailingFrames = 0,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTrailingFrames);
        using var expected = new WaveInput(expectedWav);
        using var decoded = new WaveInput(decodedWav);
        if (expected.SampleRate != decoded.SampleRate || expected.Channels != decoded.Channels ||
            decoded.Frames < expected.Frames || decoded.Frames - expected.Frames > maximumTrailingFrames)
        {
            throw new InvalidDataException(
                $"Audio timing or channels changed: {expected.Frames} frames, {expected.SampleRate} Hz, " +
                $"{expected.Channels} channels -> {decoded.Frames} frames, {decoded.SampleRate} Hz, " +
                $"{decoded.Channels} channels.");
        }

        var expectedSums = new double[expected.Channels];
        var decodedSums = new double[expected.Channels];
        var products = new double[expected.Channels];
        var errors = new double[expected.Channels];

        int count;
        while ((count = expected.Read()) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (decoded.Read(count) != count)
            {
                throw new InvalidDataException("Decoded WAV data ended before its expected length.");
            }

            for (var i = 0; i < count; i += expected.Channels)
            {
                for (var channel = 0; channel < expected.Channels; channel++)
                {
                    var a = expected.Samples[i + channel];
                    var b = decoded.Samples[i + channel];
                    var difference = b - a;
                    expectedSums[channel] += a * a;
                    decodedSums[channel] += b * b;
                    products[channel] += a * b;
                    errors[channel] += difference * difference;
                }
            }
        }

        if (maximumTrailingFrames == 0 && decoded.Read() != 0)
        {
            throw new InvalidDataException("Decoded WAV data exceeds its expected length.");
        }

        var correlation = new double[expected.Channels];
        var gainDb = new double[expected.Channels];
        var errorRmsDb = new double[expected.Channels];
        for (var channel = 0; channel < expected.Channels; channel++)
        {
            var a = expectedSums[channel];
            var b = decodedSums[channel];
            correlation[channel] = a == 0 || b == 0
                ? a == b ? 1 : 0
                : Correlation(products[channel], a, b);
            gainDb[channel] = a == b ? 0 : PowerDb(b) - PowerDb(a);
            errorRmsDb[channel] = PowerDb(errors[channel] / expected.Frames);
        }

        return new AudioBuildComparison(correlation, gainDb, errorRmsDb);
    }

    private static double PowerDb(double power)
    {
        if (!double.IsFinite(power))
        {
            throw new InvalidDataException("WAV sample levels exceed the supported range.");
        }
        return power > 0 ? 10 * Math.Log10(power) : double.NegativeInfinity;
    }

    private static double Correlation(double product, double a, double b)
    {
        if (!double.IsFinite(product) || !double.IsFinite(a) || !double.IsFinite(b))
        {
            throw new InvalidDataException("WAV sample levels exceed the supported range.");
        }
        return Math.Clamp(product / Math.Sqrt(a) / Math.Sqrt(b), -1, 1);
    }

    private sealed class WaveInput : IDisposable
    {
        private const int BufferSize = 65536;
        private readonly WaveFileReader _reader;
        private readonly int _bytesPerSample;
        private readonly bool _floatingPoint;
        private readonly int _blockAlign;
        private long _remaining;

        public int SampleRate { get; }
        public int Channels { get; }
        public long Frames { get; }
        public int BitsPerSample { get; }
        public byte[] Bytes { get; }
        public double[] Samples { get; }
        public int ByteCount { get; private set; }

        public WaveInput(string path)
        {
            _reader = new WaveFileReader(path);
            try
            {
                var format = _reader.WaveFormat.AsStandardWaveFormat();
                SampleRate = format.SampleRate;
                Channels = format.Channels;
                BitsPerSample = format.BitsPerSample;
                _floatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat;
                var supported = _floatingPoint
                    ? BitsPerSample is 32 or 64
                    : format.Encoding == WaveFormatEncoding.Pcm && BitsPerSample is 8 or 16 or 24 or 32;
                if (!supported)
                {
                    throw new InvalidDataException($"Unsupported WAV format: {format.Encoding}, {BitsPerSample} bits.");
                }

                _bytesPerSample = BitsPerSample / 8;
                _blockAlign = format.BlockAlign;
                if (SampleRate <= 0 || Channels <= 0 || _blockAlign != Channels * _bytesPerSample ||
                    _reader.Length <= 0 || _reader.Length % _blockAlign != 0)
                {
                    throw new InvalidDataException("WAV contains invalid metadata, no samples, or an incomplete sample frame.");
                }

                Frames = _reader.Length / _blockAlign;
                _remaining = _reader.Length;
                var samples = Math.Max(1, BufferSize / (Channels * sizeof(double))) * Channels;
                Bytes = new byte[samples * _bytesPerSample];
                Samples = new double[samples];
            }
            catch
            {
                _reader.Dispose();
                throw;
            }
        }

        public int Read(int maximumSamples = int.MaxValue)
        {
            ByteCount = 0;
            var requested = (int) Math.Min(Math.Min(_remaining, Bytes.Length), (long) maximumSamples * _bytesPerSample);
            while (ByteCount < requested)
            {
                var count = _reader.Read(Bytes, ByteCount, requested - ByteCount);
                if (count == 0 || count % _blockAlign != 0)
                {
                    throw new InvalidDataException("WAV sample data is truncated.");
                }
                ByteCount += count;
            }
            _remaining -= ByteCount;

            var sampleCount = ByteCount / _bytesPerSample;
            for (var i = 0; i < sampleCount; i++)
            {
                var bytes = Bytes.AsSpan(i * _bytesPerSample, _bytesPerSample);
                var sample = _floatingPoint
                    ? BitsPerSample == 32
                        ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
                        : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes))
                    : BitsPerSample switch
                    {
                        8 => (bytes[0] - 128) / 128.0,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
                        24 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608.0,
                        32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
                        _ => throw new InvalidDataException("Unsupported WAV sample size.")
                    };
                if (!double.IsFinite(sample) || !double.IsFinite(sample * sample))
                {
                    throw new InvalidDataException("WAV contains a non-finite or out-of-range sample.");
                }
                Samples[i] = sample;
            }

            return sampleCount;
        }

        public void Dispose() => _reader.Dispose();
    }
}
