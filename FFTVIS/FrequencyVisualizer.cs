namespace FFTVIS
{
    /// <summary>
    /// A decoded FFTVIS file.
    /// </summary>
    public class FrequencyVisualizer
    {
        /// <summary>
        /// The header metadata from the file
        /// </summary>
        public Metadata metadata;

        /// <summary>
        /// The Array of Arrays of doubles representing db amplitude in each bin from 0-1 for each frame.
        /// </summary>
        public double[][] frames;
        public FrequencyVisualizer(Metadata metadata, double[][] frames)
        {
            this.metadata = metadata;
            this.frames = frames;
        }

        /// <summary>
        /// Returns the current frame needing to be processed.
        /// </summary>
        /// <param name="timeInMs">The current time you intend to show, usually synced to your media player's time.</param>
        /// <returns>A frame of binned frequencies.</returns>
        public double[] GetFrameFromTime(double timeInMs)
        {
            double frameDurationMs = 1000.0 / metadata.frameRate;
            double exactFrame = timeInMs / frameDurationMs;

            int index = (int)Math.Round(exactFrame);

            if (index < 0) index = 0;
            if (index >= metadata.totalFrames) index = (int)metadata.totalFrames - 1;
            return (double[])frames[index].Clone();
        }

    }
}
