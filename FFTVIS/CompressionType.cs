using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFTVIS
{
    /// <summary>
    /// Represents the different types of compression used for the frame data.
    /// </summary>
    public enum CompressionType
    {
        Uncompressed = 0,    // 0000 No compression, raw double values
        Zstd = 1 << 0,       // 0001 Compress with Zstd
        Quantize = 1 << 1,   // 0010 Quantize the doubles
        DeltaEncode = 1 << 2 // 0100 DeltaEncode frame-to-frame
    }

    /// <summary>
    /// Represents the quantization levels for the frame data.
    /// </summary>
    public enum QuantizeLevel
    {
        Q16 = 0, // 16 bit
        Q8 = 1,  //  8 bit
    }
}
