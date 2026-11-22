using System.Buffers.Binary;
using System.Text;
using ZstdNet;

namespace FFTVIS
{
    /// <summary>
    /// Decodes .fvz files, version 3 and the older version 2.
    /// Call <see cref="ReadFile(string)"/>, <see cref="Decode(Stream)"/> or <see cref="DecodeMemoryStream(MemoryStream)"/>.
    /// </summary>
    public static class AudioDecoder
    {
        /// <summary>Returns the format version from the first 12 bytes, or -1 if they aren't an .fvz header.</summary>
        public static int ValidateFormat(ReadOnlySpan<byte> start)
        {
            if (start.Length < 12) return -1;
            if (Encoding.ASCII.GetString(start[..6]) != "FFTVIS") return -1;
            return BinaryPrimitives.ReadInt32LittleEndian(start[8..]);
        }

        /// <summary>Reads in a .fvz file from disk.</summary>
        /// <exception cref="NotSupportedException">The file or format version isn't supported</exception>
        public static FrequencyVisualizer ReadFile(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Decode(fs);
        }

        /// <summary>Reads in a .fvz file from a MemoryStream.</summary>
        public static FrequencyVisualizer DecodeMemoryStream(MemoryStream ms) => Decode(ms);

        /// <summary>Reads only the header of a v3 file (no payload decoding).</summary>
        public static Metadata ReadHeader(Stream stream)
        {
            var fv = Decode(stream, headerOnly: true);
            return fv.metadata;
        }

        /// <summary>Reads a .fvz file from any stream positioned at its start.</summary>
        public static FrequencyVisualizer Decode(Stream stream) => Decode(stream, headerOnly: false);

        private static FrequencyVisualizer Decode(Stream stream, bool headerOnly)
        {
            var start = new byte[12];
            stream.ReadExactly(start);
            int version = ValidateFormat(start);
            switch (version)
            {
                case 3:
                {
                    // FvzFormat.Read expects to read the fixed header from offset 0; give it the bytes we consumed.
                    using var joined = new PrefixedStream(start, stream);
                    if (headerOnly) return HeaderOnly(joined);
                    return FvzFormat.Read(joined);
                }
                case 2:
                    return ReadV2(start, stream);
                default:
                    throw new NotSupportedException($"Unsupported file version: {version}. Versions 2 and 3 are supported.");
            }
        }

        private static FrequencyVisualizer HeaderOnly(Stream s) =>
            new(FvzFormat.ReadHeader(s), new[] { Array.Empty<float[]>() });

        // ---- Version 2 ----
        // magic(8) version(4) fftResolution(4) numBands(2) frameRate(2) totalFrames(4) maxAmplitude(4)
        // compressionType(2) padding(2) quantizeLevel(1) padding(3) = 36 bytes, then the payload.
        private const int V2HeaderSize = 36;

        private static FrequencyVisualizer ReadV2(byte[] start, Stream stream)
        {
            var header = new byte[V2HeaderSize];
            start.CopyTo(header, 0);
            stream.ReadExactly(header, 12, V2HeaderSize - 12);
            var h = header.AsSpan();
            var md = new Metadata
            {
                Version = 2,
                FftResolution = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]),
                NumBands = BinaryPrimitives.ReadUInt16LittleEndian(h[16..]),
                FrameRate = BinaryPrimitives.ReadUInt16LittleEndian(h[18..]),
                TotalFrames = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]),
                MaxAmplitude = BinaryPrimitives.ReadSingleLittleEndian(h[24..]),
                LegacyCompressionType = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]),
                LegacyQuantize8 = h[32] != 0,
                Channels = 1,
            };
            var ct = (CompressionType)md.LegacyCompressionType;
            md.Codec = ct.HasFlag(CompressionType.Zstd) ? FvzCodec.Zstd : FvzCodec.None;
            md.DeltaEncoded = ct.HasFlag(CompressionType.DeltaEncode);
            md.QuantizeBits = (byte)(ct.HasFlag(CompressionType.Quantize) ? (md.LegacyQuantize8 ? 8 : 16) : 16);

            var frames = DecodeV2(stream, md.TotalFrames, md.NumBands,
                ct.HasFlag(CompressionType.Zstd), ct.HasFlag(CompressionType.Quantize),
                ct.HasFlag(CompressionType.DeltaEncode), md.LegacyQuantize8);
            return new FrequencyVisualizer(md, new[] { frames });
        }

        private static float[][] DecodeV2(Stream fs, uint totalFrames, ushort numBands, bool zstd, bool quant, bool delta, bool q8)
        {
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            int sampleCount = (int)(totalFrames * numBands);
            var output = new float[totalFrames][];
            byte[] raw;

            if (zstd)
            {
                int compressedLength = br.ReadInt32();
                using var decompressor = new Decompressor();
                raw = decompressor.Unwrap(br.ReadBytes(compressedLength));
            }
            else
            {
                int bytes = quant ? (q8 ? sampleCount : sampleCount * 2) : sampleCount * sizeof(double);
                raw = br.ReadBytes(bytes);
            }

            for (int f = 0; f < totalFrames; f++) output[f] = new float[numBands];

            if (delta && quant && !q8)
            {
                var current = new short[numBands];
                for (int i = 0; i < sampleCount; i++)
                {
                    int j = i % numBands;
                    current[j] += BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i * 2));
                    output[i / numBands][j] = (float)((current[j] / 32767.0 + 1.0) / 2.0);
                }
            }
            else if (delta && quant && q8)
            {
                var current = new sbyte[numBands];
                for (int i = 0; i < sampleCount; i++)
                {
                    int j = i % numBands;
                    current[j] += unchecked((sbyte)raw[i]);
                    output[i / numBands][j] = (float)((current[j] / 127.0 + 1.0) / 2.0);
                }
            }
            else if (delta)
            {
                var current = new double[numBands];
                for (int i = 0; i < sampleCount; i++)
                {
                    int j = i % numBands;
                    current[j] += BitConverter.ToDouble(raw, i * 8);
                    output[i / numBands][j] = (float)current[j];
                }
            }
            else if (quant && !q8)
            {
                for (int i = 0; i < sampleCount; i++)
                    output[i / numBands][i % numBands] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2)) / 65535f;
            }
            else if (quant)
            {
                for (int i = 0; i < sampleCount; i++)
                    output[i / numBands][i % numBands] = raw[i] / 255f;
            }
            else
            {
                for (int i = 0; i < sampleCount; i++)
                    output[i / numBands][i % numBands] = (float)BitConverter.ToDouble(raw, i * 8);
            }
            return output;
        }

        /// <summary>Replays bytes already read from a stream before continuing with the stream itself.</summary>
        private sealed class PrefixedStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly Stream _inner;
            private int _pos;

            public PrefixedStream(byte[] prefix, Stream inner)
            {
                _prefix = prefix;
                _inner = inner;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos < _prefix.Length)
                {
                    int n = Math.Min(count, _prefix.Length - _pos);
                    Array.Copy(_prefix, _pos, buffer, offset, n);
                    _pos += n;
                    return n;
                }
                return _inner.Read(buffer, offset, count);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { } // never dispose the caller's stream
        }
    }
}
