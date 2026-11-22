namespace FFTVIS
{
    /// <summary>
    /// The header of an .fvz file. Version 3 files fill every field; version 2 files leave the v3-only fields at their
    /// defaults (one channel, no band edges, no decay) and keep their bitmask in <see cref="LegacyCompressionType"/>.
    /// </summary>
    public sealed class Metadata
    {
        /// <summary>Format version: 2 or 3.</summary>
        public int Version { get; set; } = FvzFormat.CurrentVersion;

        /// <summary>FFT window size in samples used to generate the frames.</summary>
        public uint FftResolution { get; set; }

        /// <summary>Bars per frame (per channel).</summary>
        public ushort NumBands { get; set; }

        /// <summary>Frames per second. Frame i describes the audio centred at i / FrameRate seconds (v3).</summary>
        public ushort FrameRate { get; set; }

        /// <summary>Frames per channel.</summary>
        public uint TotalFrames { get; set; }

        /// <summary>1 for mono, 2 for stereo (left then right inside each frame).</summary>
        public byte Channels { get; set; } = 1;

        /// <summary>Bits per stored value, 4 to 16. Values are stored as round(v · (2^bits − 1)).</summary>
        public byte QuantizeBits { get; set; } = 8;

        /// <summary>The compressor applied to the payload.</summary>
        public FvzCodec Codec { get; set; } = FvzCodec.Brotli;

        /// <summary>Each value is stored as the difference from the same band and channel in the previous frame.</summary>
        public bool DeltaEncoded { get; set; } = true;

        /// <summary>The largest bar value produced while generating (0-1).</summary>
        public float MaxAmplitude { get; set; }

        /// <summary>
        /// Fall rate applied at encode time, in full-scale units per second (0 = none): bars rise instantly and fall no
        /// faster than this, like a classic peak meter. Clients don't need to smooth the falls themselves when set.
        /// </summary>
        public float DecayPerSecond { get; set; }

        /// <summary>Sample rate of the audio the frames were generated from.</summary>
        public uint SampleRate { get; set; }

        /// <summary>How frequencies were mapped to bars.</summary>
        public SpectrogramMapping Mapping { get; set; } = SpectrogramMapping.Normalized;

        public float DbFloor { get; set; }
        public float DbRange { get; set; }
        public float FrequencyMin { get; set; }
        public float FrequencyMax { get; set; }

        /// <summary>
        /// Frequency edges in Hz, NumBands + 1 values: bar r covers [BandEdges[r], BandEdges[r + 1]). Lets clients find
        /// bass, mids and highs without knowing the mapping's maths. Empty for v2 files.
        /// </summary>
        public float[] BandEdges { get; set; } = Array.Empty<float>();

        /// <summary>v2 only: the compression bitmask.</summary>
        public ushort LegacyCompressionType { get; set; }

        /// <summary>v2 only: true for 8-bit quantization.</summary>
        public bool LegacyQuantize8 { get; set; }

        /// <summary>Length of the audio the file covers.</summary>
        public TimeSpan Duration => FrameRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(TotalFrames / (double)FrameRate);

        /// <summary>Index of the first and one-past-last bar whose centre frequency lies in [minHz, maxHz).</summary>
        public (int Start, int End) BandRange(double minHz, double maxHz)
        {
            if (BandEdges.Length != NumBands + 1) return (0, NumBands);
            int start = NumBands, end = 0;
            for (int r = 0; r < NumBands; r++)
            {
                double centre = Math.Sqrt(Math.Max(BandEdges[r], 1e-3) * BandEdges[r + 1]);
                if (centre >= minHz && centre < maxHz)
                {
                    start = Math.Min(start, r);
                    end = r + 1;
                }
            }
            return start >= end ? (0, 0) : (start, end);
        }
    }
}
