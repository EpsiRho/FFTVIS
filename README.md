# FFTVIS (.fvz) File Format
The FFTVIS file format is a compacted representation of a pre generated frequency spectrum for visualizing audio files. It's a simple format at it's base: a header describing the generation and how to play it back properly, and an array of arrays of doubles from 0->1 representing the amplitude of a frequency bin. It supports 3 extra encoding steps for compression: ZSTD 3, Delta Encoding, and Quantization (to 16 or 8 bit). NOTE: Delta Encoding makes a difference unless also encoding with ZSTD.

This repo contains a C# library that can encode and decode FVZ files. It also contains a simple demo app that makes use of the library to allow anyone to encode or decode files for testing. Additionally, if you are building something with the format, you may want to check out [FreqFreak](https://gtihub.com), a Windows application that shows live audio visualizations. I have built an FVZ encoder + decoder + player UI into it for my own testing.

This repo also contains a JavaScript implementation of the decoder that I used to display examples on the [Blog Post](https://epsirho.com/posts/fft-blog) about this project.

(Note v1.0.1 Fixes a missing depencency for NAudio)

# Quick Start
FFTVIS's C# libary can be installed from [Nuget](https://www.nuget.org/packages/FFTVIS/1.0.0). It relies on NAudio, FFTSharp, and ZstdNet.
```bash
dotnet add package FFTVIS --version 1.0.1
```

Encoding is very simple:
```C#
AudioEncoder encoder = new AudioEncoder(barCount, 0, 1, -60, 50, 20, 20000, 0, SpectrogramMapping.Normalized, res, fps, ct); // Create a new encoder

var chk = encoder.LoadAudio(input); // Load audio from a file path
chk = encoder.GenerateFrames(); // Generate frames from audio

encoder.SaveToFile($"{filename}"); // Auto adds .fvz extention if you don't
var ms = encoder.SaveFileToMemory(); // You can also save the file's bytes out to a MemoryStream

FrequencyVisualizer fv = GetGeneratedFrames(); // Or return the FV object with header info and frames array
```

Decoding is even simpler:
```C#
// FV object holds a metadata struct of the file's header and an array of frames
FrequencyVisualizer DecodedFV = AudioDecoder.ReadFile(CurrentFvzPath);

// FV objects expose an easy method for getting the current frame from the time in ms
double[] frame = DecodedFV.GetFrameFromTime(currentTimeInMilliseconds);
```


# FFTVIS Decoding Info
Much of the code is commented if you're looking to build your own decoder. It may be easiest to just following along the reference implementation in C#. But additionally here is some important info:

The header contains the following properties
- Magic
	- File type 'FFTVIS' with 2 bytes of padding at the end
- int32 Version
	- Currently Version 2
	- byte offset 8
- uint32 FFT Resolution
	- Expected to be 1024 -> 32768
	- byte offset 12
- uint16 Number of Bands
	- The bars per frame that the FFT output has been binned into
	- byte offset 16
- uint16 Frame Rate
	- The frame rate the file was generated at
	- byte offset 18
- uint32 Total Frames
	- The total number of frames in the file
	- byte offset 20
- float32 Max Amplitude
	- The maximum amplitude seen in the file's generation (0-1)
	- byte offset 24
- uint16 Compression Type
	- A bitmask that decides the encoder steps taken and decoder steps needed
	- 0001: ZSTD 3, 0010: Quantize, 0100: Delta Encode
	- byte offset 28
- uint8 Quantize Level
	- A boolean representing what level of quantization to use (if it is enabled in the bitmask)
	- 0/false: 16 bit, 1/true: 8 bit
	- byte offset 32

Also NOTE: Do not encode your delta values before you quantize. Quantize before you Delta Encode, otherwise you will get drift as your deltas wont return back to 0 cleanly as it is lossy compression.

# FVZ Settings
When generating .fvz files with the FFTVIS Library, you get lots of control over how visualizations should be created. The following settings are available:
- Bar Count
	- How many bars to bin frequencies into
- DB Floor
	- The floor to ignore sound below, a negative number. Lower lets in more sound, -70 -> -90 is typically recommended for music files.
- DB Range
	- The range of DB amplitudes being displayed. Lower values exaggerate values while higher values smooth the waveform out. 70-120 is typically normal.
- Frequency Min
	- The lowest frequency to display, typically 20hz.
- Frequency Max
	- The maximum frequency to display, typically 20000hz.
- Smoothness
	- How much to smooth out peaks, by averaging +/- Smoothness bars.
- Bin Map
	- How to map frequencies to bins 
	- FFTVIS's C# Library supports Log10, Mel, and the custom Normalized preset. .fvz files are agnostic to this though, all they need to know is what the data looks like after. So you could add custom bin maps if mine aren't what you're looking for.
- FFT Resolution
	- The window of samples to run FFT analysis on, MUST BE A MULTIPLE OF 2
	- Typically 2048, 4096, 8192, 16384
- FPS
	- The frame rate to render at. Typically no need to exceed 240 and not worth going below 60.
- Compression Type
	- A bitmask (in the C# lib, an Enum flag object) that describes which steps to use for encoding and decoding the file w/ compression.
	- 0001 - ZSTD, 0010 - Quant, 0100 - Deltas
- Quantization Level 
	- 16 bit or 8 bit, only used if the bitmask is set for Quantization

# Extra Notes
Version 2 was the release version. Version 1 used grouped compression stages, so you could only select None, Zstd, Zstd+Quant, Zstd+Quant+Deltas. Version 2 moved to a bitmask to enable toggling,

The library doesn't have explicit support for swapping to a custom bin map, but the file format is agnostic to it. If you were to edit the library or build your own custom frame builder, as long as you save it out the way the format expects it (frames of 0-1) then it should work just fine.

FFT Values of more 32k+, unless using audio with a high sample rate (88, 96, 129), will not look correct. This is because it takes up too much time inside each frame, averaging too much of the song at once to be accurate to the audio being played at that moment.