namespace FFTVIS
{
    /// <summary>
    /// A decoded FFTVIS file: the header and, per channel, an array of frames of bar values from 0 to 1.
    /// </summary>
    public class FrequencyVisualizer
    {
        /// <summary>The header metadata from the file.</summary>
        public Metadata metadata;

        /// <summary>Frames per channel: Channels[channel][frame][band]. Mono files have one channel.</summary>
        public float[][][] Channels;

        /// <summary>The first channel's frames (mono, or left for stereo).</summary>
        public float[][] frames => Channels[0];

        public FrequencyVisualizer(Metadata metadata, float[][][] channels)
        {
            this.metadata = metadata;
            Channels = channels;
        }

        /// <summary>
        /// Returns the frame to show at a playback time. With <paramref name="interpolate"/> the two nearest frames are
        /// blended, which lets files stored at a low frame rate animate smoothly at the display's rate.
        /// </summary>
        /// <param name="timeInMs">The current time you intend to show, usually synced to your media player's time.</param>
        /// <param name="channel">0 for mono or left, 1 for right.</param>
        public float[] GetFrameFromTime(double timeInMs, int channel = 0, bool interpolate = true)
        {
            var result = new float[metadata.NumBands];
            GetFrameFromTime(timeInMs, result, channel, interpolate);
            return result;
        }

        /// <summary>Allocation-free version of <see cref="GetFrameFromTime(double, int, bool)"/>.</summary>
        public void GetFrameFromTime(double timeInMs, Span<float> destination, int channel = 0, bool interpolate = true)
        {
            var ch = Channels[Math.Clamp(channel, 0, Channels.Length - 1)];
            if (ch.Length == 0) { destination.Clear(); return; }

            double exact = Math.Max(0, timeInMs * metadata.FrameRate / 1000.0);
            int last = ch.Length - 1;
            if (!interpolate)
            {
                ch[Math.Min((int)Math.Round(exact), last)].AsSpan().CopyTo(destination);
                return;
            }

            int a = Math.Min((int)exact, last);
            int b = Math.Min(a + 1, last);
            float t = (float)(exact - Math.Floor(exact));
            var fa = ch[a];
            var fb = ch[b];
            for (int i = 0; i < destination.Length && i < fa.Length; i++)
                destination[i] = fa[i] + (fb[i] - fa[i]) * t;
        }
    }
}
