namespace FFTVIS
{
    /// <summary>
    /// The general purpose compressor applied to a v3 payload.
    /// Brotli is the default: it compresses this data about 10% better than zstd and is built into .NET, Apple's
    /// Compression framework (iOS 15+/macOS 12+) and browsers' fetch (as a Content-Encoding), so clients rarely need a
    /// third-party library. Zstd needs ZstdNet's native library, which is only bundled for Windows.
    /// </summary>
    public enum FvzCodec : byte
    {
        None = 0,
        Zstd = 1,
        Brotli = 2,
    }

    /// <summary>
    /// Version 2 compression bitmask. Only used to read v2 files; v3 files describe compression with
    /// <see cref="Metadata.Codec"/>, <see cref="Metadata.QuantizeBits"/> and <see cref="Metadata.DeltaEncoded"/>.
    /// </summary>
    [Flags]
    public enum CompressionType
    {
        Uncompressed = 0,    // 0000 No compression, raw double values
        Zstd = 1 << 0,       // 0001 Compress with Zstd
        Quantize = 1 << 1,   // 0010 Quantize the doubles
        DeltaEncode = 1 << 2 // 0100 DeltaEncode frame-to-frame
    }

    /// <summary>Version 2 quantization levels. Only used to read v2 files.</summary>
    public enum QuantizeLevel
    {
        Q16 = 0, // 16 bit
        Q8 = 1,  //  8 bit
    }
}
