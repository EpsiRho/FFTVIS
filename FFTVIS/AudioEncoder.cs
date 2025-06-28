using FftSharp;
using FreqFreak;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading.Tasks;
using ZstdNet;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace FFTVIS
{
    /// <summary>
    /// Audio file to .fvz encoder.<br></br>
    /// Initialize with desired parameters, then call <see cref="LoadAudio(string)"/> to load the audio file.<br></br>
    /// Next Generate frames with <see cref="GenerateFrames(IProgress{double}?)"/>.<br></br>
    /// Lastly you can pull out the data using <see cref="GetGeneratedFrames()"/> to get a FrequencyVisualizer object, or save the data to a file using <see cref="SaveToFile(string)"/>.<br></br>
    /// </summary>
    public sealed class AudioEncoder
    {
        // Variables that get saved in the file header metadata
        /// <summary>
        /// The resolution of the FFT, how many samples to process at once. 1024, 2048, 4096, 8192, 16384, 32768 are all valid values<br></br> 
        /// Anything else *may* work but will likely produce bad results. MUST BE A POWER OF 2!
        /// </summary>
        public int FFTResolution { get => (int)_md.fftResolution; set { if (_md.fftResolution != value) _md.fftResolution = (uint)value; } }

        /// <summary>
        /// Frame Rate to generate at.
        /// </summary>
        public int FPS { get => _md.frameRate; set { if (_md.frameRate != value) _md.frameRate = (ushort)value; } }

        /// <summary>
        /// The total number of frames that will be generated (set when generation starts).
        /// </summary>
        public uint TotalFrames { get => _md.totalFrames; private set { if (_md.totalFrames != value) _md.totalFrames = (ushort)value; } }

        /// <summary>
        /// The type of compress to use when saving the file out to disk or memory.<br></br>
        /// Takes in the enum as flags, add multiple to enable multiple compression steps.<br></br>
        /// FrequencyVisualizer objects do not get compressed, they are always uncompressed.
        /// </summary>
        public CompressionType Compression { get => (CompressionType)_md.compressionType; set { if (_md.compressionType != (ushort)value) _md.compressionType = (ushort)value; } }
        /// <summary>
        /// The level of quantization, if it is enabled. 16 Bit or 8 bit are available.
        /// </summary>
        public QuantizeLevel QuantizeLevel 
        { 
            get => _md.quantizeLevel ? QuantizeLevel.Q8 : QuantizeLevel.Q16; 
            set {
                var val = (value == QuantizeLevel.Q8);
                if (_md.quantizeLevel != val)
                {
                    _md.quantizeLevel = val;
                }
            } 
        }

        /// <summary>
        /// The version number of the file, place in the header. Do not adjust this unless you are messing with the format.<br></br>
        /// Currently, the format is on version 2. This will be set automatically for you.
        /// </summary>
        public int Version { get => _md.version; private set => _md.version = value; }

        /// <summary>
        /// The number of bars to bin  frequencies into.
        /// </summary>
        public int BarCount { get => _md.numBands; set { if (_md.numBands != value) _md.numBands = (ushort)value; } }

        /// <summary>
        /// The maximum amplitude found by the encoder during generation.<br></br>
        /// </summary>
        public float MaxAmplitude { get => _md.maxAmplitude; private set => _md.maxAmplitude = value; }

        // Variables used for generation
        /// <summary>
        /// The floor to ignore sound below, a negative number. Lower lets in more sound, -70 -> -90 is typically recommended for music files.
        /// </summary>
        public double DbFloor;

        /// <summary>
        /// The range of DB amplitudes being displayed. Lower values exaggerate values while higher values smooth the waveform out. 70-120 is typically normal.
        /// </summary>
        public double DbRange;

        /// <summary>
        /// The lowest frequency to display, typically 20hz.
        /// </summary>
        public double FrequencyMin;

        /// <summary>
        /// The maximum frequency to display, typically 20000hz.
        /// </summary>
        public double FrequencyMax;

        /// <summary>
        /// How much to smooth out peaks, by averaging +/- Smoothness bars.
        /// </summary>
        public int Smoothness;

        /// <summary>
        /// The map for frequencies to bars, Normalized is recommended for visuals, Mel for human hearing, and Log10 for spectrum accuracy.
        /// </summary>
        public SpectrogramMapping BinMap;

        // The file Meatadata object
        private Metadata _md = new();
        // The generated result
        /// <summary>
        /// The frames generated, it is recommended to use <see cref="GetGeneratedFrames()"/> to get a FrequencyVisualizer object for this.<br></br>
        /// </summary>
        public double[][] GeneratedFrames { get; private set; } = Array.Empty<double[]>();

        // The audio data loaded from the file
        private float[] _audio = Array.Empty<float>();
        private WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);

        private static readonly ArrayPool<float> _floatPool = ArrayPool<float>.Shared;

        /// <summary>
        /// Creates a new AudioEncoder with the specified parameters.<br></br>
        /// </summary>
        /// <param name="barCount">The number of bars to bin  frequencies into.</param>
        /// <param name="dbFloor">The floor to ignore sound below, a negative number. Lower lets in more sound, -70 -> -90 is typically recommended for music files.</param>
        /// <param name="dbRange"> The range of DB amplitudes being displayed. Lower values exaggerate values while higher values smooth the waveform out. 70-120 is typically normal.</param>
        /// <param name="freqMin">The lowest frequency to display, typically 20hz.</param>
        /// <param name="freqMax">The maximum frequency to display, typically 20000hz.</param>
        /// <param name="smoothness">How much to smooth out peaks, by averaging +/- Smoothness bars.</param>
        /// <param name="binMap">The map for frequencies to bars, Normalized is recommended for visuals, Mel for human hearing, and Log10 for spectrum accuracy.</param>
        /// <param name="fftRes">The resolution of the FFT, how many samples to process at once. 1024, 2048, 4096, 8192, 16384, 32768 are all valid values<br></br>Anything else *may* work but will likely produce bad results. MUST BE A POWER OF 2!</param>
        /// <param name="fps">The frame rate to render at. Typically no need to exceed 240 and not worth going below 60.</param>
        /// <param name="compressionType">The type of compress to use when saving the file out to disk or memory.<br></br>Takes in the enum as flags, add multiple to enable multiple compression steps.</param>
        /// <param name="quantLevel">16 bit or 8 bit, only used if Quantization is enabled</param>
        public AudioEncoder(int barCount, double dbFloor, double dbRange,
                             double freqMin, double freqMax, int smoothness, SpectrogramMapping binMap,
                             int fftRes, int fps, CompressionType compressionType, QuantizeLevel quantLevel = QuantizeLevel.Q16)
        {
            BarCount = barCount;
            DbFloor = dbFloor;
            DbRange = dbRange;
            FrequencyMin = freqMin;
            FrequencyMax = freqMax;
            Smoothness = smoothness;
            BinMap = binMap;
            FFTResolution = fftRes;
            FPS = fps;
            Compression = compressionType;
        }

        /// <summary>
        /// Loads an audio file in and prepares it for visualization generation.
        /// </summary>
        /// <param name="path">The audio file path</param>
        /// <returns></returns>
        public bool LoadAudio(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = path.Trim('"');

            try
            {
                using var reader = new AudioFileReader(path);
                ISampleProvider mono = reader.WaveFormat.Channels == 2
                    ? new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f }
                    : reader;

                _format = WaveFormat.CreateIeeeFloatWaveFormat(mono.WaveFormat.SampleRate, 1);

                // Heuristic initial size (may grow for VBR/metadata inaccuracies)
                long est = (long)(reader.TotalTime.TotalSeconds * _format.SampleRate) + 1024;
                if (est <= 0) est = 1024;
                _audio = new float[est];

                int offset = 0;
                float[] buf = _floatPool.Rent(16384);
                try
                {
                    int read;
                    while ((read = mono.Read(buf, 0, buf.Length)) > 0)
                    {
                        // ensure capacity
                        if (offset + read > _audio.Length)
                        {
                            int newSize = Math.Max(_audio.Length * 2, offset + read);
                            Array.Resize(ref _audio, newSize);
                        }
                        Array.Copy(buf, 0, _audio, offset, read);
                        offset += read;
                    }
                }
                finally { _floatPool.Return(buf); }

                Array.Resize(ref _audio, offset); // trim to actual size

                return offset > 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"LoadAudio failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Generate frames from the loaded audio data.
        /// </summary>
        /// <param name="progress">A progress object that runs an action whenever a frame has finished generating.</param>
        /// <returns></returns>
        public bool GenerateFrames(IProgress<double>? progress = null)
        {
            if (_audio.Length == 0) return false;

            double HopExact = _format.SampleRate / (double)FPS;
            TotalFrames = (uint)Math.Ceiling(Math.Max(0, (_audio.Length - FFTResolution) / HopExact + 1));
            GeneratedFrames = new double[TotalFrames][];

            Parallel.For(0, TotalFrames, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                var fb = CreateBuilder();
                float[] window = _floatPool.Rent(FFTResolution);
                try
                {
                    //int src = (int)Math.Floor(i * hop);
                    double exactSrc = i * HopExact;
                    int src = (int)Math.Round(exactSrc);
                    if (_audio.Length < src + FFTResolution)
                    {
                        // Copy last section
                        int dif = src + FFTResolution - _audio.Length;
                        Array.Copy(_audio, src, window, 0, dif);
                    }
                    else
                    { 
                        // Copy normally
                        Array.Copy(_audio, src, window, 0, FFTResolution);
                    }
                    GeneratedFrames[i] = fb.ProcessData(window, _format) ?? new double[BarCount];
                    MaxAmplitude = MaxAmplitude > (float)fb._maxMagnitude ? MaxAmplitude : (float)fb._maxMagnitude;
                }
                finally { _floatPool.Return(window); }
                progress?.Report(i);
            });
            return true;
        }
        /// <summary>
        /// Saves the FVZ object as a file.
        /// </summary>
        /// <param name="fileName">The file path/name to save to. Excluding the .fvz file will cause the function to add it for you.</param>
        /// <returns>A bool representing if saving was successful</returns>
        public bool SaveToFile(string fileName)
        {
            // Setting up the last few paramters in the header, the "magic" bytes telling the file format, the version of the format (currently v2), and the total frames generated
            if (GeneratedFrames.Length == 0) return false;
            _md.magic = Encoding.ASCII.GetBytes("FFTVIS\0\0");
            _md.version = 2;
            _md.totalFrames = (uint)GeneratedFrames.Length;

            // Convert the header struct into it's byte representation (more details below)
            var header = StructToBytes(_md);

            // Check if the filename needs it's file extension
            if (fileName.EndsWith(".fvz", StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileName.Substring(0, fileName.Length - 4);
            }

            // Creating the file stream to write to
            using var fs = File.Create($"{fileName}.fvz");
            fs.Write(header);

            int frameLen = BarCount;
            int totalSamples = frameLen * GeneratedFrames.Length;
            byte[] bytes = Array.Empty<byte>(); // final bytes to be written / compressed


            if(Compression == CompressionType.Uncompressed) 
            {
                double[] all = new double[totalSamples];
                for (int i = 0; i < GeneratedFrames.Length; i++)
                    Array.Copy(GeneratedFrames[i], 0, all, i * frameLen, frameLen);

                bytes = new byte[all.Length * sizeof(double)];
                Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);
                fs.Write(bytes, 0, bytes.Length);

                return true;
            }
            else if (Compression == CompressionType.Zstd) 
            {
                double[] all = new double[totalSamples];
                for (int i = 0; i < GeneratedFrames.Length; i++)
                    Array.Copy(GeneratedFrames[i], 0, all, i * frameLen, frameLen);

                bytes = new byte[all.Length * sizeof(double)];
                Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);

                using (var compressor = new Compressor())
                {
                    byte[] compressed = compressor.Wrap(bytes);
                    fs.Write(BitConverter.GetBytes(compressed.Length));
                    fs.Write(compressed, 0, compressed.Length);
                }

                return true;
            }

            // Quantize
            // Values MUST be quantized first, quantizing delta values leads to drift (bars rise and never fully return back to 0)
            if (Compression.HasFlag(CompressionType.Quantize))
            {
                if (!Compression.HasFlag(CompressionType.DeltaEncode)) // If delta encoding we'll handle quant there.
                {
                    if (!_md.quantizeLevel) // 16 bit
                    {
                        ushort[] all = new ushort[totalSamples];
                        for (int i = 0; i < GeneratedFrames.Length; i++)
                        {
                            Quantize16(GeneratedFrames[i]).CopyTo(all, i * frameLen);
                        }

                        bytes = new byte[all.Length * sizeof(ushort)];
                        Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);

                    }
                    else // 8 bit
                    {
                        bytes = new byte[totalSamples];
                        for (int i = 0; i < GeneratedFrames.Length; i++)
                            Quantize8(GeneratedFrames[i]).CopyTo(bytes, i * frameLen);
                    }
                }
            }

            // Delta Encode
            if (Compression.HasFlag(CompressionType.DeltaEncode))
            {
                // FIX: Buffer size and delta type must match quantization level
                if (Compression.HasFlag(CompressionType.Quantize) && !_md.quantizeLevel) // 16 bit
                {
                    short[] prevQuantized = new short[frameLen];
                    short[] allDeltas = new short[totalSamples];

                    for (int i = 0; i < GeneratedFrames.Length; i++)
                    {
                        var frame = GeneratedFrames[i];

                        // Quantize the frame values FIRST
                        short[] quantized = QuantizeDelta16(frame);

                        // Compute deltas between quantized values (quantizing deltas leads to drift)
                        for (int j = 0; j < frameLen; j++)
                        {
                            allDeltas[i * frameLen + j] = (short)(quantized[j] - prevQuantized[j]);
                        }

                        // Update previous frame
                        prevQuantized = quantized;
                    }

                    bytes = new byte[allDeltas.Length * sizeof(short)];
                    Buffer.BlockCopy(allDeltas, 0, bytes, 0, bytes.Length);
                }
                else if (Compression.HasFlag(CompressionType.Quantize) && _md.quantizeLevel) // 8 bit
                {
                    sbyte[] prevQuantized = new sbyte[frameLen];
                    sbyte[] allDeltas = new sbyte[totalSamples];

                    for (int i = 0; i < GeneratedFrames.Length; i++)
                    {
                        var frame = GeneratedFrames[i];

                        // Quantize the frame values FIRST
                        sbyte[] quantized = QuantizeDelta8(frame);

                        // Compute deltas between quantized values (quantizing deltas leads to drift)
                        for (int j = 0; j < frameLen; j++)
                        {
                            allDeltas[i * frameLen + j] = (sbyte)(quantized[j] - prevQuantized[j]);
                        }

                        // Update previous frame
                        prevQuantized = quantized;
                    }

                    bytes = new byte[allDeltas.Length];
                    Buffer.BlockCopy(allDeltas, 0, bytes, 0, bytes.Length);
                }
                else // No quant
                {
                    double[] prevFrame = new double[frameLen]; // Last frame to get difference frome
                    double[] allDeltas = new double[totalSamples]; // output deltas

                    for (int i = 0; i < GeneratedFrames.Length; i++)
                    {
                        var frame = GeneratedFrames[i];

                        // Compute deltas between values 
                        // (quantizing deltas leads to drift so quantize first)
                        for (int j = 0; j < frameLen; j++)
                        {
                            allDeltas[i * frameLen + j] = frame[j] - prevFrame[j];
                        }

                        // Update previous frame
                        prevFrame = frame;
                    }

                    bytes = new byte[allDeltas.Length * sizeof(double)];
                    Buffer.BlockCopy(allDeltas, 0, bytes, 0, bytes.Length);
                }
            }

            if (Compression.HasFlag(CompressionType.Zstd))
            {
                // Zstd 3 compression using ZstdNet
                // Compression type 3 was chosen because any higher compromised too much on speed for not enough extra space
                using (var compressor = new Compressor(new CompressionOptions(3)))
                {
                    byte[] compressed = compressor.Wrap(bytes);
                    fs.Write(BitConverter.GetBytes(compressed.Length));
                    fs.Write(compressed, 0, compressed.Length);
                }
            }
            else
            {
                fs.Write(bytes);
            }
            return true;
        }
        /// <summary>
        /// Returns the FVZ object as a C# object.
        /// </summary>
        /// <returns></returns>
        public FrequencyVisualizer GetGeneratedFrames()
        {
            if (GeneratedFrames.Length == 0) return null;
            return new FrequencyVisualizer(_md, GeneratedFrames);
        }
        /// <summary>
        /// Returns the FVZ object back as it's raw bytes representation.
        /// </summary>
        /// <returns>A MemoryStream object filled with bytes representing the resulting .fvz file</returns>
        public MemoryStream SaveFileToMemory()
        {
            // Setting up the last few paramters in the header, the "magic" bytes telling the file format, the version of the format (currently v2), and the total frames generated
            if (GeneratedFrames.Length == 0) return null;
            _md.magic = Encoding.ASCII.GetBytes("FFTVIS\0\0");
            _md.version = 2;
            _md.totalFrames = (uint)GeneratedFrames.Length;

            // Convert the header struct into it's byte representation (more details below)
            var header = StructToBytes(_md);

            // Creating the file stream to write to
            using var fs = new MemoryStream();
            fs.Write(header);

            int frameLen = BarCount;
            int totalSamples = frameLen * GeneratedFrames.Length;
            byte[] bytes = Array.Empty<byte>(); // final bytes to be written / compressed

            // Quantize
            // Values MUST be quantized first, quantizing delta values leads to drift (bars rise and never fully return back to 0)
            if (Compression.HasFlag(CompressionType.Quantize))
            {
                if (!Compression.HasFlag(CompressionType.DeltaEncode)) // If delta encoding we'll handle quant there.
                {
                    if (!_md.quantizeLevel) // 16 bit
                    {
                        ushort[] all = new ushort[totalSamples];
                        for (int i = 0; i < GeneratedFrames.Length; i++)
                        {
                            Quantize16(GeneratedFrames[i]).CopyTo(all, i * frameLen);
                        }

                        bytes = new byte[all.Length * sizeof(ushort)];
                        Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);

                    }
                    else // 8 bit
                    {
                        bytes = new byte[totalSamples];
                        for (int i = 0; i < GeneratedFrames.Length; i++)
                            Quantize8(GeneratedFrames[i]).CopyTo(bytes, i * frameLen);
                    }
                }
            }

            // Delta Encode
            if (Compression.HasFlag(CompressionType.DeltaEncode))
            {
                bytes = new byte[totalSamples];
                if (Compression.HasFlag(CompressionType.Quantize) && !_md.quantizeLevel) // 16 bit
                {
                    short[] prevQuantized = new short[frameLen];

                    for (int i = 0; i < GeneratedFrames.Length; i++)
                    {
                        var frame = GeneratedFrames[i];

                        // Quantize the frame values FIRST
                        short[] quantized = QuantizeDelta16(frame);

                        // Compute deltas between quantized values (quantizing deltas leads to drift)
                        sbyte[] deltas = new sbyte[frameLen];
                        for (int j = 0; j < frameLen; j++)
                        {
                            deltas[j] = (sbyte)(quantized[j] - prevQuantized[j]);
                        }

                        // Store deltas
                        Buffer.BlockCopy(deltas, 0, bytes, i * frameLen, frameLen);

                        // Update previous frame
                        prevQuantized = quantized;
                    }
                }
                else if (Compression.HasFlag(CompressionType.Quantize) && _md.quantizeLevel) // 8 bit
                {
                    sbyte[] prevQuantized = new sbyte[frameLen];

                    for (int i = 0; i < GeneratedFrames.Length; i++)
                    {
                        var frame = GeneratedFrames[i];

                        // Quantize the frame values FIRST
                        sbyte[] quantized = QuantizeDelta8(frame);

                        // Compute deltas between quantized values (quantizing deltas leads to drift)
                        sbyte[] deltas = new sbyte[frameLen];
                        for (int j = 0; j < frameLen; j++)
                        {
                            deltas[j] = (sbyte)(quantized[j] - prevQuantized[j]);
                        }

                        // Store deltas
                        Buffer.BlockCopy(deltas, 0, bytes, i * frameLen, frameLen);

                        // Update previous frame
                        prevQuantized = quantized;
                    }
                }
                else // No quant
                {
                    double[] prevFrame = new double[frameLen];

                    for (int i = 0; i < GeneratedFrames.Length; i++)
                    {
                        var frame = GeneratedFrames[i];

                        // Compute deltas between quantized values (quantizing deltas leads to drift)
                        double[] deltas = new double[frameLen];
                        for (int j = 0; j < frameLen; j++)
                        {
                            deltas[j] = frame[j] - prevFrame[j];
                        }

                        // Store deltas
                        Buffer.BlockCopy(deltas, 0, bytes, i * frameLen, frameLen);

                        // Update previous frame
                        prevFrame = frame;
                    }
                }

            }

            if (Compression.HasFlag(CompressionType.Zstd))
            {
                // Zstd 3 compression using ZstdNet
                // Compression type 3 was chosen because any higher compromised too much on speed for not enough extra space
                using (var compressor = new Compressor(new CompressionOptions(3)))
                {
                    byte[] compressed = compressor.Wrap(bytes);
                    fs.Write(BitConverter.GetBytes(compressed.Length));
                    fs.Write(compressed, 0, compressed.Length);
                }
            }
            else
            {
                fs.Write(bytes);
            }

            return fs;
        }
        private FrameBuilder CreateBuilder() => new()
        {
            FFTResolution = FFTResolution,
            BarCount = BarCount,
            DbFloor = DbFloor,
            DbRange = DbRange,
            FrequencyMin = FrequencyMin,
            FrequencyMax = FrequencyMax,
            Smoothness = Smoothness,
            SpectrogramMapping = BinMap
        };
        
        // Converts a struct into it's bytes
        private byte[] StructToBytes<T>(T str) where T : struct
        {
            int size = Marshal.SizeOf(str);
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(str, ptr, true);
                Marshal.Copy(ptr, arr, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return arr;
        }

        // Quantization funcs
        private static ushort[] Quantize16(double[] input)
        {
            ushort[] output = new ushort[input.Length];
            for (int i = 0; i < input.Length; i++)
                output[i] = (ushort)Math.Clamp((int)Math.Round(input[i] * 65535), 0, 65535);
            return output;
        }
        private static byte[] Quantize8(double[] input)
        {
            byte[] output = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
                output[i] = (byte)Math.Clamp((int)Math.Round(input[i] * 255), 0, 255);
            return output;
        }
        private static short[] QuantizeDelta16(double[] frame)
        {
            short[] output = new short[frame.Length];

            for (int i = 0; i < frame.Length; i++)
            {
                double value = frame[i];

                // Map [0,1] to [-32767,32767]
                output[i] = (short)Math.Clamp((int)Math.Round((value * 2.0 - 1.0) * 32767.0), -32767, 32767);
            }
            return output;
        }
        private static sbyte[] QuantizeDelta8(double[] frame) 
        {
            sbyte[] output = new sbyte[frame.Length];

            for (int i = 0; i < frame.Length; i++)
            {
                double value = frame[i];

                // Map [0,1] to [-127,127]
                output[i] = (sbyte)Math.Clamp((int)Math.Round((value * 2.0 - 1.0) * 127.0), -127, 127);
            }
            return output;
        }
    }
}
