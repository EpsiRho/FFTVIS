using FFTVIS;
using System.Diagnostics;

// A small tool for checking encoding and decoding with the FFTVIS library.
// It is recommended to check out FreqFreak on GitHub, which has an FVZ file viewer UI, can show synced visuals as well as encode on the fly for testing and export.
//
// Non-interactive use:
//   FFTVISTool encode <audio> [bands] [fps] [bits] [decayPerSecond] [stereo:0|1] [codec:none|zstd|brotli]
//   FFTVISTool decode <file.fvz>

string? choice, input;
if (args.Length >= 2)
{
    choice = args[0] == "decode" ? "1" : "2";
    input = args[1];
}
else
{
    Console.WriteLine("[FFTVIS Tool]");
    Console.WriteLine("1-Decode FVZ File");
    Console.WriteLine("2-Encode Audio File");
    choice = Console.ReadLine();
    Console.Write("Enter a file:\n> ");
    input = Console.ReadLine();
}

if (string.IsNullOrEmpty(input)) return;
input = input.Trim('"');
if (!Path.Exists(input))
{
    Console.Write("Not a valid path!");
    return;
}

if (choice == "1")
{
    var watch = Stopwatch.StartNew();
    var fv = AudioDecoder.ReadFile(input);
    var md = fv.metadata;
    Console.WriteLine($"Loaded {input} in {watch.ElapsedMilliseconds} ms");
    Console.WriteLine($"Version: {md.Version}");
    Console.WriteLine($"Frames: {md.TotalFrames} x {md.Channels} channel(s) x {md.NumBands} bands @ {md.FrameRate} fps ({md.Duration})");
    Console.WriteLine($"FFT Resolution: {md.FftResolution}, sample rate {md.SampleRate}");
    Console.WriteLine($"Storage: {md.QuantizeBits}-bit, delta {md.DeltaEncoded}, {md.Codec}, decay {md.DecayPerSecond}/s");
    Console.WriteLine($"Mapping: {md.Mapping}, {md.FrequencyMin}-{md.FrequencyMax} Hz, floor {md.DbFloor} dB, range {md.DbRange} dB");
    Console.WriteLine($"Max Amplitude: {md.MaxAmplitude}");
    if (md.BandEdges.Length > 0)
        Console.WriteLine($"Band edges: {string.Join(", ", md.BandEdges.Select(e => e.ToString("F0")))}");
}
else if (choice == "2")
{
    int Arg(int i, int fallback) => args.Length > i && int.TryParse(args[i], out var v) ? v : fallback;
    var options = new EncoderOptions
    {
        BarCount = Arg(2, 64),
        FPS = Arg(3, 60),
        QuantizeBits = Arg(4, 8),
        DecayPerSecond = args.Length > 5 && double.TryParse(args[5], out var d) ? d : 0,
        Stereo = Arg(6, 0) == 1,
        Codec = args.Length > 7 ? Enum.Parse<FvzCodec>(args[7], true) : FvzCodec.Brotli,
    };

    var watch = Stopwatch.StartNew();
    var encoder = new AudioEncoder(options);
    if (!encoder.LoadAudio(input))
    {
        Console.WriteLine("Failed to load audio.");
        return;
    }
    Console.WriteLine($"Loaded in {watch.ElapsedMilliseconds} ms, rendering...");

    encoder.GenerateFrames(Console.IsOutputRedirected ? null : new Progress<double>(p =>
    {
        Console.Write($"Progress: {p * 100:F2}%      ");
        Console.CursorLeft = 0;
    }));
    Console.WriteLine($"Generated in {watch.ElapsedMilliseconds} ms, saving...");

    string name = $"{Path.GetFileNameWithoutExtension(input)}-{options.BarCount}b{options.FPS}f{options.QuantizeBits}q{(options.Stereo ? "-stereo" : "")}-{options.Codec}.fvz";
    encoder.SaveToFile(name);
    Console.WriteLine($"Time: {watch.Elapsed}");
    Console.WriteLine($"Saved {new FileInfo(name).Length / 1024.0:F1} KB to {Path.GetFullPath(name)}");
}
else
{
    Console.WriteLine("Invalid choice.");
}
