using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FFTVIS
{
    /// <summary>
    /// Reads and writes the version 3 container. Every field is little-endian and written explicitly (no struct
    /// marshalling), so other languages can follow the table in the README byte for byte.
    /// <code>
    ///  0  char[8]  magic "FFTVIS\0\0"
    ///  8  int32    version (3)
    /// 12  uint16   header size: offset of the payload length field
    /// 14  uint8    channels (1 or 2)
    /// 15  uint8    quantize bits (4-16)
    /// 16  uint32   fft resolution
    /// 20  uint16   bands
    /// 22  uint16   frame rate
    /// 24  uint32   frames per channel
    /// 28  uint32   sample rate
    /// 32  float32  max amplitude
    /// 36  float32  decay per second
    /// 40  float32  dB floor
    /// 44  float32  dB range
    /// 48  float32  frequency min
    /// 52  float32  frequency max
    /// 56  uint8    codec (0 none, 1 zstd, 2 brotli)
    /// 57  uint8    flags (bit 0: delta encoded)
    /// 58  uint8    mapping (0 normalized, 1 mel, 2 log10)
    /// 59  uint8    reserved
    /// 60  float32[bands + 1] band edges in Hz
    /// hs  uint32   payload length, then the payload
    /// </code>
    /// The decompressed payload is frame-major: for each frame, for each channel, for each band, one value: a byte when
    /// quantize bits are 8 or fewer, otherwise a little-endian uint16. With delta encoding each value is the difference
    /// from the same channel and band in the previous frame, wrapped to the value's width (add and wrap to decode).
    /// </summary>
    public static class FvzFormat
    {
        public const int CurrentVersion = 3;
        public const int MinQuantizeBits = 4;
        public const int MaxQuantizeBits = 16;
        internal const int FixedHeaderSize = 60;
        internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("FFTVIS\0\0");

        /// <summary>Writes a v3 file: header, then the compressed payload built from <paramref name="channels"/>.</summary>
        public static void Write(Stream output, Metadata md, IReadOnlyList<double[][]> channels, int compressionLevel = -1)
        {
            if (md.QuantizeBits < MinQuantizeBits || md.QuantizeBits > MaxQuantizeBits)
                throw new ArgumentOutOfRangeException(nameof(md), $"QuantizeBits must be {MinQuantizeBits}-{MaxQuantizeBits}.");
            md.Version = CurrentVersion;
            md.Channels = (byte)channels.Count;
            md.TotalFrames = (uint)channels[0].Length;

            int headerSize = FixedHeaderSize + 4 * (md.NumBands + 1);
            var header = new byte[headerSize];
            var h = header.AsSpan();
            Magic.CopyTo(h);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], md.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(h[12..], (ushort)headerSize);
            h[14] = md.Channels;
            h[15] = md.QuantizeBits;
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], md.FftResolution);
            BinaryPrimitives.WriteUInt16LittleEndian(h[20..], md.NumBands);
            BinaryPrimitives.WriteUInt16LittleEndian(h[22..], md.FrameRate);
            BinaryPrimitives.WriteUInt32LittleEndian(h[24..], md.TotalFrames);
            BinaryPrimitives.WriteUInt32LittleEndian(h[28..], md.SampleRate);
            BinaryPrimitives.WriteSingleLittleEndian(h[32..], md.MaxAmplitude);
            BinaryPrimitives.WriteSingleLittleEndian(h[36..], md.DecayPerSecond);
            BinaryPrimitives.WriteSingleLittleEndian(h[40..], md.DbFloor);
            BinaryPrimitives.WriteSingleLittleEndian(h[44..], md.DbRange);
            BinaryPrimitives.WriteSingleLittleEndian(h[48..], md.FrequencyMin);
            BinaryPrimitives.WriteSingleLittleEndian(h[52..], md.FrequencyMax);
            h[56] = (byte)md.Codec;
            h[57] = (byte)(md.DeltaEncoded ? 1 : 0);
            h[58] = (byte)md.Mapping;
            for (int i = 0; i <= md.NumBands; i++)
                BinaryPrimitives.WriteSingleLittleEndian(h[(FixedHeaderSize + 4 * i)..],
                    i < md.BandEdges.Length ? md.BandEdges[i] : 0f);

            var payload = Compress(Pack(md, channels), md.Codec, compressionLevel);
            output.Write(header);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)payload.Length);
            output.Write(len);
            output.Write(payload);
        }

        /// <summary>Quantizes (and delta encodes) the frames into the uncompressed payload layout.</summary>
        internal static byte[] Pack(Metadata md, IReadOnlyList<double[][]> channels)
        {
            int bands = md.NumBands, chs = channels.Count, frames = channels[0].Length;
            int levels = (1 << md.QuantizeBits) - 1;
            bool wide = md.QuantizeBits > 8;
            var output = new byte[(long)frames * chs * bands * (wide ? 2 : 1)];
            var prev = new int[chs * bands];
            int p = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < chs; c++)
                {
                    var frame = channels[c][f];
                    for (int b = 0; b < bands; b++)
                    {
                        int q = (int)Math.Round(Math.Clamp(frame[b], 0, 1) * levels);
                        int v = q;
                        if (md.DeltaEncoded)
                        {
                            v = q - prev[c * bands + b];
                            prev[c * bands + b] = q;
                        }
                        if (wide)
                        {
                            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(p), unchecked((ushort)v));
                            p += 2;
                        }
                        else
                        {
                            output[p++] = unchecked((byte)v);
                        }
                    }
                }
            }
            return output;
        }

        /// <summary>Reads a v3 file whose first 12 bytes (magic and version) have already been validated.</summary>
        internal static FrequencyVisualizer Read(Stream input)
        {
            var md = ReadHeader(input);
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(ReadExactly(input, 4));
            var raw = Decompress(ReadExactly(input, checked((int)payloadLength)), md.Codec);
            return new FrequencyVisualizer(md, Unpack(md, raw));
        }

        /// <summary>Reads a v3 header (through the band edges), leaving the stream at the payload length field.</summary>
        internal static Metadata ReadHeader(Stream input)
        {
            var fixedPart = ReadExactly(input, FixedHeaderSize);
            var h = fixedPart.AsSpan();
            var md = new Metadata
            {
                Version = BinaryPrimitives.ReadInt32LittleEndian(h[8..]),
                Channels = h[14],
                QuantizeBits = h[15],
                FftResolution = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]),
                NumBands = BinaryPrimitives.ReadUInt16LittleEndian(h[20..]),
                FrameRate = BinaryPrimitives.ReadUInt16LittleEndian(h[22..]),
                TotalFrames = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]),
                SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(h[28..]),
                MaxAmplitude = BinaryPrimitives.ReadSingleLittleEndian(h[32..]),
                DecayPerSecond = BinaryPrimitives.ReadSingleLittleEndian(h[36..]),
                DbFloor = BinaryPrimitives.ReadSingleLittleEndian(h[40..]),
                DbRange = BinaryPrimitives.ReadSingleLittleEndian(h[44..]),
                FrequencyMin = BinaryPrimitives.ReadSingleLittleEndian(h[48..]),
                FrequencyMax = BinaryPrimitives.ReadSingleLittleEndian(h[52..]),
                Codec = (FvzCodec)h[56],
                DeltaEncoded = (h[57] & 1) != 0,
                Mapping = (SpectrogramMapping)h[58],
            };
            int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
            if (md.Channels is < 1 or > 2) throw new InvalidDataException($"Unsupported channel count {md.Channels}.");
            if (md.QuantizeBits is < MinQuantizeBits or > MaxQuantizeBits) throw new InvalidDataException($"Unsupported quantize bits {md.QuantizeBits}.");

            // The header may grow in later minor revisions: read what we know, skip the rest.
            var rest = ReadExactly(input, headerSize - FixedHeaderSize);
            int edges = Math.Min(md.NumBands + 1, rest.Length / 4);
            md.BandEdges = new float[edges];
            for (int i = 0; i < edges; i++)
                md.BandEdges[i] = BinaryPrimitives.ReadSingleLittleEndian(rest.AsSpan(4 * i));
            return md;
        }

        internal static float[][][] Unpack(Metadata md, byte[] raw)
        {
            int bands = md.NumBands, chs = md.Channels, frames = (int)md.TotalFrames;
            bool wide = md.QuantizeBits > 8;
            long expected = (long)frames * chs * bands * (wide ? 2 : 1);
            if (raw.Length < expected) throw new InvalidDataException($"Payload is {raw.Length} bytes, expected {expected}.");

            float scale = 1f / ((1 << md.QuantizeBits) - 1);
            var output = new float[chs][][];
            for (int c = 0; c < chs; c++) output[c] = new float[frames][];
            var current = new int[chs * bands];
            int mask = wide ? 0xFFFF : 0xFF;
            int p = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < chs; c++)
                {
                    var frame = output[c][f] = new float[bands];
                    for (int b = 0; b < bands; b++)
                    {
                        int v;
                        if (wide) { v = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(p)); p += 2; }
                        else v = raw[p++];
                        if (md.DeltaEncoded)
                        {
                            v = (current[c * bands + b] + v) & mask;
                            current[c * bands + b] = v;
                        }
                        frame[b] = v * scale;
                    }
                }
            }
            return output;
        }

        /// <summary>Compresses with a codec. Level -1 picks the default (brotli 5, zstd 19): higher brotli levels are 3-7x slower for under 1% smaller files, except 11 (~7% smaller, ~15x slower).</summary>
        public static byte[] Compress(byte[] data, FvzCodec codec, int level = -1)
        {
            switch (codec)
            {
                case FvzCodec.None:
                    return data;
                case FvzCodec.Brotli:
                {
                    int quality = level < 0 ? 5 : Math.Clamp(level, 0, 11);
                    var dest = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
                    if (!BrotliEncoder.TryCompress(data, dest, out int written, quality, 24))
                        throw new InvalidOperationException("Brotli compression failed.");
                    return dest[..written];
                }
                case FvzCodec.Zstd:
                {
                    using var c = new ZstdNet.Compressor(new ZstdNet.CompressionOptions(level < 0 ? 19 : Math.Clamp(level, 1, 22)));
                    return c.Wrap(data);
                }
                default:
                    throw new NotSupportedException($"Unknown codec {codec}.");
            }
        }

        public static byte[] Decompress(byte[] data, FvzCodec codec)
        {
            switch (codec)
            {
                case FvzCodec.None:
                    return data;
                case FvzCodec.Brotli:
                {
                    using var input = new MemoryStream(data);
                    using var brotli = new BrotliStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream(data.Length * 4);
                    brotli.CopyTo(output);
                    return output.ToArray();
                }
                case FvzCodec.Zstd:
                {
                    using var d = new ZstdNet.Decompressor();
                    return d.Unwrap(data);
                }
                default:
                    throw new NotSupportedException($"Unknown codec {codec}.");
            }
        }

        internal static byte[] ReadExactly(Stream s, int count)
        {
            var buffer = new byte[count];
            s.ReadExactly(buffer);
            return buffer;
        }
    }
}
