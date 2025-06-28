using FftSharp;
using FFTVIS;
using System;
using System.Diagnostics;
using System.Numerics;

// This is a very simplified tool for checking encoding decoding with the FFTVIS library.
// It is recommended to check out FreqFreak on GitHub, which has an FVZ file viewer UI, can show synced visuals as well as encode on the fly for testing and export.

Console.WriteLine("[FFTVIS Tool]");

Console.WriteLine("1-Decode FVZ File");
Console.WriteLine("2-Encode Audio File");
var choice = Console.ReadLine();

Console.Write("Enter a file:\n> ");
var input = Console.ReadLine();

if (string.IsNullOrEmpty(input))
{
    return;
}

if (input.StartsWith("\"") || input.EndsWith("\""))
{
    input = input.Trim('"');
}

if (!Path.Exists(input))
{
    Console.Write("Not a valid path!");
    return;
}

if (int.TryParse(choice, out int ch))
{
    if(ch == 1)
    {
        var fv = AudioDecoder.ReadFile(input);
        if (fv == null)
        {
            Console.WriteLine("Failed to load file.");
            return;
        }
        Console.WriteLine($"Loaded {input} with {fv.metadata.totalFrames} frames.");
        Console.WriteLine($"Version: {fv.metadata.version}");
        Console.WriteLine($"FFT Resolution: {fv.metadata.fftResolution}");
        Console.WriteLine($"Num Bands: {fv.metadata.numBands}");
        Console.WriteLine($"Frame Rate: {fv.metadata.frameRate}");
        Console.WriteLine($"Max Amplitude: {fv.metadata.maxAmplitude}");
        Console.WriteLine($"Compression Type: {fv.metadata.compressionType}");
        for (int i = 0; i < fv.frames.Length; i++)
        {
            foreach (var bin in fv.frames[i])
            {
                if (double.IsInfinity(bin))
                {
                    Console.WriteLine($"Frame {i}: {bin}");
                }

            }
        }
        return;
    }
    else if (ch == 2)
    {
        var watch = Stopwatch.StartNew();
        Console.WriteLine("Loading...");
        int fps = 120;
        int barCount = 250;
        int res = 8192;
        CompressionType ct = CompressionType.Zstd;
        AudioEncoder encoder = new AudioEncoder(barCount, -80, 90, 20, 20000, 0, SpectrogramMapping.Normalized, res, fps, ct);
        var chk = encoder.LoadAudio(input);
        if (!chk)
        {
            Console.WriteLine("Failed to load audio.");
            return;
        }

        Console.WriteLine("Rendering...");
        double percent = 0;
        double complete = 0;

        CancellationTokenSource cts = new();

        Thread t = new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                percent = complete / encoder.TotalFrames;
                Console.Write($"Progress: {percent * 100:F2}%      ");
                Console.CursorLeft = 0;
                Thread.Sleep(16);
            }
        });
        t.Start();

        chk = encoder.GenerateFrames(new Progress<double>((double index) =>
        {
            complete++;
        }));

        cts.Cancel();

        string filename = Path.GetFileNameWithoutExtension(input);

        Console.CursorLeft = 0;
        Console.WriteLine($"Progress: {1 * 100:F2}%      ");
        Console.WriteLine("Saving...");
        encoder.SaveToFile($"{filename}-{barCount}x{res}@{fps} [{ct}]");

        watch.Stop();

        Console.WriteLine($"Time: {watch.Elapsed}");
        Console.WriteLine($"Saved to {filename}-{barCount}x{res}@{fps} [{ct}].fvz at this exe's location.");
    }
    else
    {
        Console.WriteLine("Invalid choice.");
        return;
    }
}
else
{
    return;
}


