using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Windows;
using FftSharp;
using FFTVIS;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dmo;
using NAudio.Wave;

namespace FreqFreak
{
    /// <summary>
    /// Builds frames from sample data. Used inside the <see cref="AudioEncoder"/> class to process audio samples into visualizer frames.
    /// </summary>
    public class FrameBuilder
    {
        /// <summary>
        /// The number of bars to bin to
        /// </summary>
        public int BarCount;

        /// <summary>
        /// The floor to ignore sound below, a negative number. Lower lets in more sound, -70 -> -90 is typically recommended for music files
        /// </summary>
        public double DbFloor;

        /// <summary>
        /// The range of DB amplitudes being displayed. Lower values exaggerate values while higher values smooth the waveform out. 70-120 is typically normal.
        /// </summary>
        public double DbRange;

        /// <summary>
        /// The lowest frequency to display, typically 20(hz)
        /// </summary>
        public double FrequencyMin;

        /// <summary>
        /// The maximum frequency to display, typically 20000(hz)
        /// </summary>
        public double FrequencyMax;

        /// <summary>
        /// How much to smooth out peaks, by averaging +/- Smoothness bars.
        /// </summary>
        public int Smoothness;

        /// <summary>
        /// The map for frequencies to bars, Normalized is recommended for visuals, Mel for human hearing, and Log10 for spectrum accuracy.
        /// </summary>
        public SpectrogramMapping SpectrogramMapping;

        /// <summary>
        /// The resolution of the FFT, how many samples to process at once. 1024, 2048, 4096, 8192, 16384, 32768 are all valid values<br></br> 
        /// Anything else *may* work but will likely produce bad results. MUST BE A POWER OF 2!
        /// </summary>
        public int FFTResolution;
        private FftSharp.Windows.Hanning _window = new();

        public double _maxMagnitude = 0;

        /// <summary>
        /// Takes in float samples and runs FFT, then builds a frame based on the FrameBuilder class settings.
        /// </summary>
        /// <param name="samples">The float audio samples.</param>
        /// <param name="fmt">The WaveFormat of the audio being processed.</param>
        /// <returns></returns>
        public double[] ProcessData(float[] samples, WaveFormat fmt)
        {
            try
            {
                // For prerendered processing, we already have the exact samples we want
                int sampleCount = Math.Min(samples.Length, FFTResolution);

                // Create array of the correct size for FFT
                float[] processingSamples = new float[FFTResolution];

                // Copy samples and pad with zeros if needed
                Array.Copy(samples, processingSamples, sampleCount);

                // Zero out any remaining samples
                for (int i = sampleCount; i < FFTResolution; i++)
                {
                    processingSamples[i] = 0f;
                }

                return ComputeFFT(processingSamples, fmt.SampleRate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ProcessData error: {ex.Message}");
                return null;
            }
        }

        private double[] ComputeFFT(float[] samples, int sampleRate)
        {
            for (int n = 0; n < samples.Length; n++)
            {
                if (float.IsNaN(samples[n]))
                {
                    samples[n] = 0f; // Replace NaN with silence because it wont leave me alone
                }
            }

            // Remove DC offset
            float sum = 0;
            for (int n = 0; n < samples.Length; n++)
                sum += samples[n];
            float mean = sum / samples.Length;

            if (!float.IsNaN(mean))
            {
                for (int n = 0; n < samples.Length; n++)
                    samples[n] -= mean;
            }

            // Apply window function
            double[] upscaledSamples = samples.Select(x => (double)x).ToArray();
            _window.ApplyInPlace(upscaledSamples);

            // FFT processing
            var spectrum = FFT.Forward(upscaledSamples);
            var magnitudes = FFT.Magnitude(spectrum);

            // Validate FFT output
            for (int i = 0; i < magnitudes.Length; i++)
            {
                if (double.IsNaN(magnitudes[i]) || double.IsInfinity(magnitudes[i]))
                {
                    magnitudes[i] = 0.0;
                }
            }

            // Build frame with current scale mode
            return BuildFrame(magnitudes, sampleRate);
        }

        // Frame builder. This makes our visualizer frames!
        private double[] BuildFrame(double[] mags, int sampleRate)
        {
            switch (SpectrogramMapping)
            {
                case SpectrogramMapping.Mel:
                    var melFrame = BuildFrameMel(mags, sampleRate);
                    return melFrame;
                case SpectrogramMapping.Log10:
                    var frame = BuildFrameLog(mags, sampleRate);
                    return frame;
                default:
                    var normalizedFrame = BuildFrameNormalized(mags, sampleRate);
                    return normalizedFrame;
            }
        }

        private double[] SmoothFrame(double[] frame)
        {
            var smoothed = new double[BarCount];
            for (int r = 0; r < BarCount; r++)
            {
                double sum = 0;
                int cnt = 0;
                // For -Smoothness to +Smoothness (2 smooth would be -2 -> 2)
                for (int s = -Smoothness; s <= Smoothness; s++)
                {
                    // If row +/- smooth is not negative and not more than the rows we have
                    if (r + s >= 0 && r + s < BarCount)
                    {
                        // Take that row at add it to our sum (and increase our total)
                        sum += frame[r + s];
                        cnt++;
                    }
                }

                // Avg the sum
                smoothed[r] = (sum / cnt);
                _maxMagnitude = _maxMagnitude > smoothed[r] ? _maxMagnitude : smoothed[r];
            }
            return smoothed;
        }
        private static double TriEase(double t)
        {
            // We split by percent, so 40% of the frequencies from 20-20000 are considered the low end
            var lowMid = 0.40; // 40% Lows
            var highMid = 0.95; // 85% (55% mid section, 5% highs)
            var transitionWidth = 0.02; // Smoothness value between low / mid / high

            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            // Compute derivatives at boundary points for smooth matching
            if (t < lowMid - transitionWidth)
            {
                // Low section
                double x = t / lowMid;
                return 0.5 * Math.Pow(x, 0.5);
            }
            else if (t < lowMid + transitionWidth)
            {
                // Smooth transition from low to mid
                double t1 = lowMid - transitionWidth;
                double t2 = lowMid + transitionWidth;

                // Values at transition points
                double v1 = 0.5 * Math.Pow(t1 / lowMid, 0.5);
                double v2 = 0.5 + ((t2 - lowMid) / (highMid - lowMid)) * 0.4;

                // Derivatives at transition points  
                double d1 = 0.5 * 0.5 * Math.Pow(t1 / lowMid, -0.5) / lowMid;
                double d2 = 0.4 / (highMid - lowMid);

                return CubicHermite(t, t1, v1, d1, t2, v2, d2);
            }
            else if (t < highMid - transitionWidth)
            {
                // Mid section
                double x = (t - lowMid) / (highMid - lowMid);
                return 0.5 + x * 0.4;
            }
            else if (t < highMid + transitionWidth)
            {
                // Smooth transition from mid to high
                double t1 = highMid - transitionWidth;
                double t2 = highMid + transitionWidth;

                // Values at transition points
                double v1 = 0.5 + ((t1 - lowMid) / (highMid - lowMid)) * 0.4;
                double v2 = 0.9 + 0.1 * Math.Pow((t2 - highMid) / (1 - highMid), 0.9);

                // Derivatives at transition points
                double d1 = 0.4 / (highMid - lowMid);
                double d2 = 0.1 * 0.9 * Math.Pow((t2 - highMid) / (1 - highMid), -0.1) / (1 - highMid);

                return CubicHermite(t, t1, v1, d1, t2, v2, d2);
            }
            else
            {
                // High section  
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
        private static double ApplySoftKnee(double x, double t)
        {
            double center = 0.4;
            double steep = 15.0;
            return 1.0 / (1.0 + Math.Exp(-steep * (x - center)));
            //double steepness = 15.0;
            //double center = 0.4;
            //return 1.0 / (1.0 + Math.Exp(-steepness * (x - center)));
        }

        // Frame Builder Part 1, Normalized
        private double[] BuildFrameNormalized(double[] mags, int sampleRate)
        {
            // Preparing Variables
            double fMin = FrequencyMin;
            double fMax = FrequencyMax == -1 ? sampleRate / 2.0 : FrequencyMax;
            int rows = BarCount;
            var power = new double[rows]; // Stores Accumulated Power sums for each bar
            var binCnt = new double[rows]; // Stores count of FFT bins -> Visualizer bins

            // Logarithmic edges 
            // Human hearing is said to be logarithmic, so we compensate by making the spectrum of frequencies take up more space as you get higher up. 
            // This means at the low end a bar might be 20-40, but higher up will be 1600-3200. By contrast Linear would take the same ammount of fft bins into each visualizer bar, 20-40 at the bottom and 1600-1620 at the top.
            double logMin = Math.Log10(fMin);
            double logMax = Math.Log10(fMax);
            double logStep = (logMax - logMin) / rows;

            // Pre-compute bar edges 
            // Here we are calculating the boundaries between bars, what frequencies do they start/stop at
            // Edges contains the left edge of boundary (the start freq) at i, and the right edge at i+1
            //double[] edges = new double[rows + 1];
            //for (int r = 0; r <= rows; r++)
            //    edges[r] = Math.Pow(10, logMin + r * logStep);
            double[] edges = new double[rows + 1];
            for (int r = 0; r <= rows; r++)
            {
                double t = r / (double)rows;
                double easedT = TriEase(t); // gamma < 1 boosts high end
                double logF = logMin + easedT * (logMax - logMin);
                edges[r] = Math.Pow(10, logF);
            }

            // Loop over each FFT bin and for each:
            //  - Find which left and right edge it is between
            //  - Distribute it's power if it sits between/on an edge
            for (int bin = 1; bin < mags.Length; bin++)
            {
                double f = bin * sampleRate / (double)FFTResolution;
                if (f < edges[0] || f >= edges[^1]) continue;

                // Locate left edge index k so that edges[k] <= f < edges[k+1]
                int k = Array.BinarySearch(edges, f);
                if (k < 0) k = ~k - 1; // BinarySearch peculiarity

                double l = edges[k];
                double rEdge = edges[k + 1];
                double t = (f - l) / (rEdge - l); // 0->1 position between the two edges

                // distribute energy into the two adjacent bars
                double e = mags[bin] * mags[bin]; // energy to distribute

                power[k] += e * (1 - t); // left bar distribution
                binCnt[k] += (1 - t);

                if (k + 1 < rows) // right bar distribution (still inside range)
                {
                    power[k + 1] += e * t;
                    binCnt[k + 1] += t;
                }
            }


            // Clamp in range + Normalize
            // - Normalize the power by the ammount of FFT bins that contributed to a specific bar.
            // - Convert Amplitude to RMS to show the "average energy" of the bins that were added into a bar
            // - Clamp in the range of _dbRange where 0 is _dbFloor and 1 is _dbFloor + _dbRange
            var frame = new double[rows];
            for (int r = 0; r < rows; r++)
            {
                if (binCnt[r] == 0) { frame[r] = 0; continue; }

                // power -> amplitude -> dB
                double rms = Math.Sqrt(power[r] / binCnt[r]) * Math.Sqrt(binCnt[r]);
                double db = 20 * Math.Log10(rms + 1e-20);

                // Normalize the DB within the range
                double topDb = DbFloor + DbRange;
                double dbNorm = Math.Clamp((db - DbFloor) / DbRange, 0, 1);

                // Apply gain compensation as frequency increases (so high ends don't get washed)
                //dbNorm *= FrequencyWeight(r, rows);
                double t = r / (double)(rows - 1);
                //double comp = double.Lerp(1.1, 0.95, t);
                //dbNorm /= comp;

                // Use "Soft Knee" to gate out noise and an exponential smoothstep to smooth out the missing cliff
                frame[r] = Math.Clamp(ApplySoftKnee(dbNorm, t), 0, 1);

            }

            // At this point we have values usable in our visualizer and format, 0.0->1.0 where 0 is no sound in(/around) that frequency and 1 is very loud sound in that frequency (clamped max, but can technically go "out of bounds")

            // Last part here is smoothing, we take each bar and "blur" it to the bars on either side
            var smoothed = SmoothFrame(frame);

            return smoothed;
        }
        // Frame Builder Part 2, Melectric Boogaloo
        private double[] BuildFrameMel(double[] mags, int sampleRate)
        {
            double fMin = FrequencyMin;
            double fMax = FrequencyMax == -1 ? sampleRate / 2.0 : FrequencyMax;

            // Mel Math
            static double Mel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
            static double InvMel(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

            int rows = BarCount;
            double melMin = Mel(fMin);
            double melMax = Mel(fMax);

            // One extra point on either side so we have enough rows for triangular filters
            double[] melEdges = new double[rows + 2];
            double melStep = (melMax - melMin) / (rows + 1);
            for (int i = 0; i < melEdges.Length; i++)
            {
                melEdges[i] = InvMel(melMin + i * melStep);
            }

            var power = new double[rows];
            var binCnt = new int[rows];


            // Accumulate power through the filters
            for (int bin = 1; bin < mags.Length; bin++)
            {
                double freq = bin * sampleRate / (double)FFTResolution;
                if (freq < fMin || freq >= fMax) continue;

                // Which two edges sandwich this bin?
                int k = Array.FindLastIndex(melEdges, e => e <= freq);
                if (k <= 0 || k >= melEdges.Length - 1) continue;

                double left = melEdges[k - 1];
                double centre = melEdges[k];
                double right = melEdges[k + 1];

                // Triangular weight for this bin in the Mel filter
                double weight = freq < centre
                                ? (freq - left) / (centre - left)
                                : (right - freq) / (right - centre);

                if (weight < 0) continue; // Outside current triangle

                int row = k - 1; // Rows correspond to triangles
                power[row] += (mags[bin] * mags[bin]) * weight;
                binCnt[row] += 1; // For later RMS normalisation
            }

            // Convert to dB normalised 
            var frame = new double[rows];
            for (int r = 0; r < rows; r++)
            {
                if (binCnt[r] == 0) { frame[r] = 0; continue; }

                double rms = Math.Sqrt(power[r] / binCnt[r]) * Math.Sqrt(binCnt[r]);
                double db = 20 * Math.Log10(rms + 1e-20);

                double dbNorm = Math.Clamp(
                    (db - DbFloor) / DbRange, 0, 1);
                frame[r] = dbNorm;
            }

            var smoothed = SmoothFrame(frame);

            return smoothed;
        }

        // Frame Builder Part 3, Log10  
        private double[] BuildFrameLog(double[] mags, int sampleRate)
        {
            // Preparing Variables
            double fMin = FrequencyMin;
            double fMax = FrequencyMax == -1 ? sampleRate / 2.0 : FrequencyMax;
            int rows = BarCount;
            var power = new double[rows]; // Stores Accumulated Power sums for each bar
            var binCnt = new double[rows]; // Stores count of FFT bins -> Visualizer bins

            // Logarithmic edges 
            // Human hearing is said to be logarithmic, so we compensate by making the spectrum of frequencies take up more space as you get higher up. 
            // This means at the low end a bar might be 20-40, but higher up will be 1600-3200. By contrast Linear would take the same ammount of fft bins into each visualizer bar, 20-40 at the bottom and 1600-1620 at the top.
            double logMin = Math.Log10(fMin);
            double logMax = Math.Log10(fMax);
            double logStep = (logMax - logMin) / rows;

            // Pre-compute bar edges 
            // Here we are calculating the boundaries between bars, what frequencies do they start/stop at
            // Edges contains the left edge of boundary (the start freq) at i, and the right edge at i+1
            double[] edges = new double[rows + 1];
            for (int r = 0; r <= rows; r++)
            {
                double t = r / (double)rows;
                double easedT = TriEase(t);
                double logF = logMin + easedT * (logMax - logMin);
                edges[r] = Math.Pow(10, logF);
            }

            // Loop over each FFT bin and for each:
            //  - Find which left and right edge it is between
            //  - Distribute it's power if it sits between/on an edge
            for (int bin = 1; bin < mags.Length; bin++)
            {
                double f = bin * sampleRate / (double)FFTResolution;
                if (f < edges[0] || f >= edges[^1]) continue;

                // Locate left edge index k so that edges[k] <= f < edges[k+1]
                int k = Array.BinarySearch(edges, f);
                if (k < 0) k = ~k - 1; // BinarySearch peculiarity

                double l = edges[k];
                double rEdge = edges[k + 1];
                double t = (f - l) / (rEdge - l); // 0->1 position between the two edges

                // distribute energy into the two adjacent bars
                double e = mags[bin] * mags[bin]; // energy to distribute

                power[k] += e * (1 - t); // left bar distribution
                binCnt[k] += (1 - t);

                if (k + 1 < rows) // right bar distribution (still inside range)
                {
                    power[k + 1] += e * t;
                    binCnt[k + 1] += t;
                }
            }


            // Clamp in range + Normalize
            // - Normalize the power by the ammount of FFT bins that contributed to a specific bar.
            // - Convert Amplitude to RMS to show the "average energy" of the bins that were added into a bar
            // - Clamp in the range of _dbRange where 0 is _dbFloor and 1 is _dbFloor + _dbRange
            var frame = new double[rows];
            for (int r = 0; r < rows; r++)
            {
                if (binCnt[r] == 0) { frame[r] = 0; continue; }

                // power -> amplitude -> dB
                double rms = Math.Sqrt(power[r] / binCnt[r]) * Math.Sqrt(binCnt[r]);
                double db = 20 * Math.Log10(rms + 1e-20);

                // Normalize the DB within the range
                double topDb = DbFloor + DbRange;
                double dbNorm = Math.Clamp((db - DbFloor) / DbRange, 0, 1);

                // Apply gain compensation as frequency increases (so high ends don't get washed)
                double t = r / (double)(rows - 1);
                //double comp = double.Lerp(1.2, 1.1, t);
                //dbNorm /= comp;

                // Use Soft Gate to gate out noise and an exponential smoothstep to smooth out the missing cliff
                frame[r] = Math.Clamp(ApplySoftKnee(dbNorm, t), 0, 1);

            }

            // At this point we have values usable in our visualizer and format, 0.0->1.0 where 0 is no sound in(/around) that frequency and 1 is very loud sound in that frequency (clamped max, but can technically go "out of bounds")

            // Last part here is smoothing, we take each bar and "blur" it to the bars on either side
            var smoothed = SmoothFrame(frame);

            return smoothed;
        }
    }
}