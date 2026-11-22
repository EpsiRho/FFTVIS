namespace FFTVIS
{
    /// <summary>
    /// Everything that decides what an .fvz file contains. Generation settings (bands, FFT, mapping, dB, frequency
    /// range, smoothness, frame rate, channels) change the frames; storage settings (quantize bits, delta, codec,
    /// level, decay) only change how they are stored, and all but decay are cheap to change.
    /// </summary>
    public sealed record EncoderOptions
    {
        /// <summary>Bars per frame. Clients wanting fewer bars can average neighbours.</summary>
        public int BarCount { get; init; } = 64;

        /// <summary>The floor to ignore sound below, a negative number. -70 to -90 suits music.</summary>
        public double DbFloor { get; init; } = -80;

        /// <summary>The range of dB shown above the floor. Lower exaggerates, higher smooths. 70-120 is typical.</summary>
        public double DbRange { get; init; } = 90;

        /// <summary>Lowest frequency in Hz, typically 20.</summary>
        public double FrequencyMin { get; init; } = 20;

        /// <summary>Highest frequency in Hz, typically 20000. -1 means half the sample rate.</summary>
        public double FrequencyMax { get; init; } = 20000;

        /// <summary>Average each bar with this many neighbours on each side.</summary>
        public int Smoothness { get; init; } = 0;

        /// <summary>How frequencies map to bars.</summary>
        public SpectrogramMapping BinMap { get; init; } = SpectrogramMapping.Normalized;

        /// <summary>FFT window in samples, a power of two (1024-32768). 4096 suits 44.1/48 kHz.</summary>
        public int FFTResolution { get; init; } = 4096;

        /// <summary>
        /// Frames per second stored. Clients should interpolate between frames (see
        /// <see cref="FrequencyVisualizer.GetFrameFromTime(double, int, bool)"/>), so 30-60 looks smooth on any display;
        /// lower rates shrink the file almost proportionally.
        /// </summary>
        public int FPS { get; init; } = 60;

        /// <summary>Generate left and right separately instead of a mono mix. Doubles the data.</summary>
        public bool Stereo { get; init; } = false;

        /// <summary>Resample before analysis (0 keeps the file's rate).</summary>
        public int TargetSampleRate { get; init; } = 0;

        /// <summary>Bits per value, 4-16. 8 is visually lossless for bars; 6 is ~35% smaller and still hard to tell apart.</summary>
        public int QuantizeBits { get; init; } = 8;

        /// <summary>Store differences between frames, which compress far better. Lossless; leave on.</summary>
        public bool DeltaEncode { get; init; } = true;

        /// <summary>The compressor for the payload.</summary>
        public FvzCodec Codec { get; init; } = FvzCodec.Brotli;

        /// <summary>Compressor level, -1 for the default (brotli 5, zstd 19).</summary>
        public int CompressionLevel { get; init; } = -1;

        /// <summary>
        /// Peak-meter fall rate in full-scale units per second (0 = off): bars rise instantly and fall at most this fast.
        /// Around 1.5-3 looks natural and makes files ~35% smaller.
        /// </summary>
        public double DecayPerSecond { get; init; } = 0;

        /// <summary>Threads used to generate frames (0 = all cores).</summary>
        public int MaxThreads { get; init; } = 0;

        /// <summary>Throws if a setting is outside what the format and encoder support.</summary>
        public void Validate()
        {
            static void Check(bool ok, string message)
            {
                if (!ok) throw new ArgumentOutOfRangeException(message);
            }
            Check(BarCount is >= 1 and <= ushort.MaxValue, "BarCount must be 1-65535.");
            Check(FPS is >= 1 and <= ushort.MaxValue, "FPS must be 1-65535.");
            Check(FFTResolution >= 256 && (FFTResolution & (FFTResolution - 1)) == 0, "FFTResolution must be a power of two of at least 256.");
            Check(DbRange > 0, "DbRange must be positive.");
            Check(FrequencyMin > 0 && (FrequencyMax == -1 || FrequencyMax > FrequencyMin), "Frequency range is invalid.");
            Check(Smoothness >= 0, "Smoothness can't be negative.");
            Check(QuantizeBits is >= FvzFormat.MinQuantizeBits and <= FvzFormat.MaxQuantizeBits, "QuantizeBits must be 4-16.");
            Check(DecayPerSecond >= 0, "DecayPerSecond can't be negative.");
            Check(TargetSampleRate >= 0, "TargetSampleRate can't be negative.");
        }
    }
}
