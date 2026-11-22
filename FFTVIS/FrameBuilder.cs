using FftSharp;
using FFTVIS;

namespace FreqFreak
{
    /// <summary>
    /// Builds frames from sample data. Used inside the <see cref="AudioEncoder"/> class to process audio samples into visualizer frames.
    /// <para>
    /// The mapping from FFT bins to bars depends only on the settings and the sample rate, so it is computed once per
    /// builder (see <see cref="Prepare"/>) instead of once per frame. A builder is not thread safe: use one per thread.
    /// </para>
    /// </summary>
    public class FrameBuilder
    {
        /// <summary>The number of bars to bin to</summary>
        public int BarCount;

        /// <summary>The floor to ignore sound below, a negative number. Lower lets in more sound, -70 -> -90 is typically recommended for music files</summary>
        public double DbFloor;

        /// <summary>The range of DB amplitudes being displayed. Lower values exaggerate values while higher values smooth the waveform out. 70-120 is typically normal.</summary>
        public double DbRange;

        /// <summary>The lowest frequency to display, typically 20(hz)</summary>
        public double FrequencyMin;

        /// <summary>The maximum frequency to display, typically 20000(hz). -1 means half the sample rate.</summary>
        public double FrequencyMax;

        /// <summary>How much to smooth out peaks, by averaging +/- Smoothness bars.</summary>
        public int Smoothness;

        /// <summary>The map for frequencies to bars, Normalized is recommended for visuals, Mel for human hearing, and Log10 for spectrum accuracy.</summary>
        public SpectrogramMapping SpectrogramMapping;

        /// <summary>
        /// The resolution of the FFT, how many samples to process at once. 1024, 2048, 4096, 8192, 16384, 32768 are all valid values<br></br>
        /// Anything else *may* work but will likely produce bad results. MUST BE A POWER OF 2!
        /// </summary>
        public int FFTResolution;

        /// <summary>The largest bar value this builder has produced, used for the header's max amplitude.</summary>
        public double _maxMagnitude = 0;

        private readonly FftSharp.Windows.Hanning _window = new();
        private int _preparedRate = -1;
        private double[] _edges = Array.Empty<double>();
        private double[] _fft = Array.Empty<double>();

        // Bin -> bar mapping. For Normalized/Log10 each bin is split linearly between bars _binBar[i] and _binBar[i] + 1
        // with weights (1 - _binT[i]) and _binT[i]. For Mel each bin feeds one triangle (_binBar) with weight _binT.
        private int[] _binIndex = Array.Empty<int>();
        private int[] _binBar = Array.Empty<int>();
        private double[] _binT = Array.Empty<double>();
        private double[] _binCount = Array.Empty<double>();

        /// <summary>
        /// The frequency edges of each bar in Hz (BarCount + 1 values): bar r covers [edges[r], edges[r + 1]).
        /// For Mel, whose triangles overlap, these are the midpoints between neighbouring triangle centres.
        /// </summary>
        public double[] GetBandEdges(int sampleRate)
        {
            Prepare(sampleRate);
            return (double[])_edges.Clone();
        }

        /// <summary>Precomputes the bin to bar mapping for a sample rate. Called automatically; cheap if already prepared.</summary>
        public void Prepare(int sampleRate)
        {
            if (_preparedRate == sampleRate) return;
            _preparedRate = sampleRate;
            _fft = new double[FFTResolution];

            double fMin = FrequencyMin;
            double fMax = FrequencyMax == -1 ? sampleRate / 2.0 : Math.Min(FrequencyMax, sampleRate / 2.0);
            int rows = BarCount;
            int bins = FFTResolution / 2 + 1;
            var index = new List<int>(bins);
            var bar = new List<int>(bins);
            var weight = new List<double>(bins);
            _binCount = new double[rows];

            if (SpectrogramMapping == SpectrogramMapping.Mel)
            {
                static double Mel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
                static double InvMel(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

                double melMin = Mel(fMin);
                double melMax = Mel(fMax);
                // One extra point on either side so we have enough rows for triangular filters
                double[] melEdges = new double[rows + 2];
                double melStep = (melMax - melMin) / (rows + 1);
                for (int i = 0; i < melEdges.Length; i++)
                    melEdges[i] = InvMel(melMin + i * melStep);

                _edges = new double[rows + 1];
                for (int r = 0; r <= rows; r++)
                    _edges[r] = InvMel(melMin + (r + 0.5) * melStep);

                for (int b = 1; b < bins; b++)
                {
                    double freq = b * sampleRate / (double)FFTResolution;
                    if (freq < fMin || freq >= fMax) continue;

                    // Which two edges sandwich this bin?
                    int k = Array.BinarySearch(melEdges, freq);
                    if (k < 0) k = ~k - 1;
                    if (k <= 0 || k >= melEdges.Length - 1) continue;

                    double left = melEdges[k - 1], centre = melEdges[k], right = melEdges[k + 1];
                    double w = freq < centre ? (freq - left) / (centre - left) : (right - freq) / (right - centre);
                    if (w < 0) continue;

                    index.Add(b); bar.Add(k - 1); weight.Add(w);
                    _binCount[k - 1] += 1; // RMS normalisation counts bins, not weights
                }
            }
            else
            {
                // Logarithmic edges. Human hearing is roughly logarithmic, so higher bars span more Hz.
                // Normalized additionally eases the split (TriEase) so lows, mids and highs each get a fair share of bars.
                double logMin = Math.Log10(fMin);
                double logMax = Math.Log10(fMax);
                _edges = new double[rows + 1];
                for (int r = 0; r <= rows; r++)
                {
                    double t = r / (double)rows;
                    double eased = SpectrogramMapping == SpectrogramMapping.Normalized ? TriEase(t) : t;
                    _edges[r] = Math.Pow(10, logMin + eased * (logMax - logMin));
                }

                for (int b = 1; b < bins; b++)
                {
                    double f = b * sampleRate / (double)FFTResolution;
                    if (f < _edges[0] || f >= _edges[^1]) continue;

                    int k = Array.BinarySearch(_edges, f);
                    if (k < 0) k = ~k - 1;
                    double t = (f - _edges[k]) / (_edges[k + 1] - _edges[k]); // 0->1 position between the two edges

                    index.Add(b); bar.Add(k); weight.Add(t);
                    _binCount[k] += 1 - t;
                    if (k + 1 < rows) _binCount[k + 1] += t;
                }
            }

            _binIndex = index.ToArray();
            _binBar = bar.ToArray();
            _binT = weight.ToArray();
        }

        /// <summary>
        /// Takes in float samples and runs FFT, then builds a frame based on the FrameBuilder class settings.
        /// Samples shorter than <see cref="FFTResolution"/> are zero padded.
        /// </summary>
        public double[] ProcessData(ReadOnlySpan<float> samples, int sampleRate)
        {
            Prepare(sampleRate);
            int n = Math.Min(samples.Length, FFTResolution);

            // Copy, replace NaN with silence, and remove the DC offset
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float s = samples[i];
                double v = float.IsFinite(s) ? s : 0.0;
                _fft[i] = v;
                sum += v;
            }
            Array.Clear(_fft, n, FFTResolution - n);
            double mean = sum / FFTResolution;
            for (int i = 0; i < FFTResolution; i++) _fft[i] -= mean;

            _window.ApplyInPlace(_fft);
            var magnitudes = FFT.Magnitude(FFT.Forward(_fft));
            for (int i = 0; i < magnitudes.Length; i++)
                if (!double.IsFinite(magnitudes[i])) magnitudes[i] = 0.0;

            return BuildFrame(magnitudes);
        }

        /// <summary>Backwards compatible overload.</summary>
        public double[] ProcessData(float[] samples, NAudio.Wave.WaveFormat fmt) => ProcessData(samples, fmt.SampleRate);

        private double[] BuildFrame(double[] mags)
        {
            int rows = BarCount;
            var power = new double[rows];
            bool mel = SpectrogramMapping == SpectrogramMapping.Mel;

            for (int i = 0; i < _binIndex.Length; i++)
            {
                double m = mags[_binIndex[i]];
                double e = m * m;
                int k = _binBar[i];
                double t = _binT[i];
                if (mel)
                {
                    power[k] += e * t;
                }
                else
                {
                    power[k] += e * (1 - t);
                    if (k + 1 < rows) power[k + 1] += e * t;
                }
            }

            // power -> RMS amplitude -> dB, normalised into [DbFloor, DbFloor + DbRange]
            var frame = new double[rows];
            for (int r = 0; r < rows; r++)
            {
                if (_binCount[r] == 0) { frame[r] = 0; continue; }
                double rms = Math.Sqrt(power[r] / _binCount[r]) * Math.Sqrt(_binCount[r]);
                double db = 20 * Math.Log10(rms + 1e-20);
                double dbNorm = Math.Clamp((db - DbFloor) / DbRange, 0, 1);
                // Normalized and Log10 use a soft knee to gate out noise; Mel stays linear in dB
                frame[r] = mel ? dbNorm : Math.Clamp(ApplySoftKnee(dbNorm), 0, 1);
            }

            return SmoothFrame(frame);
        }

        // "Blur" each bar with +/- Smoothness neighbours
        private double[] SmoothFrame(double[] frame)
        {
            var smoothed = new double[BarCount];
            for (int r = 0; r < BarCount; r++)
            {
                double sum = 0;
                int cnt = 0;
                for (int s = -Smoothness; s <= Smoothness; s++)
                {
                    if (r + s >= 0 && r + s < BarCount)
                    {
                        sum += frame[r + s];
                        cnt++;
                    }
                }
                smoothed[r] = sum / cnt;
                if (smoothed[r] > _maxMagnitude) _maxMagnitude = smoothed[r];
            }
            return smoothed;
        }

        private static double TriEase(double t)
        {
            // We split by percent, so 40% of the bars cover the low end, 55% the mids and 5% the highs
            var lowMid = 0.40;
            var highMid = 0.95;
            var transitionWidth = 0.02; // Smoothness value between low / mid / high

            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            if (t < lowMid - transitionWidth)
            {
                double x = t / lowMid;
                return 0.5 * Math.Pow(x, 0.5);
            }
            else if (t < lowMid + transitionWidth)
            {
                double t1 = lowMid - transitionWidth;
                double t2 = lowMid + transitionWidth;
                double v1 = 0.5 * Math.Pow(t1 / lowMid, 0.5);
                double v2 = 0.5 + ((t2 - lowMid) / (highMid - lowMid)) * 0.4;
                double d1 = 0.5 * 0.5 * Math.Pow(t1 / lowMid, -0.5) / lowMid;
                double d2 = 0.4 / (highMid - lowMid);
                return CubicHermite(t, t1, v1, d1, t2, v2, d2);
            }
            else if (t < highMid - transitionWidth)
            {
                double x = (t - lowMid) / (highMid - lowMid);
                return 0.5 + x * 0.4;
            }
            else if (t < highMid + transitionWidth)
            {
                double t1 = highMid - transitionWidth;
                double t2 = highMid + transitionWidth;
                double v1 = 0.5 + ((t1 - lowMid) / (highMid - lowMid)) * 0.4;
                double v2 = 0.9 + 0.1 * Math.Pow((t2 - highMid) / (1 - highMid), 0.9);
                double d1 = 0.4 / (highMid - lowMid);
                double d2 = 0.1 * 0.9 * Math.Pow((t2 - highMid) / (1 - highMid), -0.1) / (1 - highMid);
                return CubicHermite(t, t1, v1, d1, t2, v2, d2);
            }
            else
            {
                double x = (t - highMid) / (1 - highMid);
                return 0.9 + 0.1 * Math.Pow(x, 0.9);
            }
        }

        private static double CubicHermite(double t, double t0, double p0, double m0, double t1, double p1, double m1)
        {
            double dt = t1 - t0;
            double h = (t - t0) / dt;
            double h2 = h * h;
            double h3 = h2 * h;
            double h00 = 2 * h3 - 3 * h2 + 1;
            double h10 = h3 - 2 * h2 + h;
            double h01 = -2 * h3 + 3 * h2;
            double h11 = h3 - h2;
            return h00 * p0 + h10 * dt * m0 + h01 * p1 + h11 * dt * m1;
        }

        private static double ApplySoftKnee(double x)
        {
            const double center = 0.4;
            const double steep = 15.0;
            return 1.0 / (1.0 + Math.Exp(-steep * (x - center)));
        }
    }
}
