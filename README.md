# FFTVIS (.fvz) File Format
The FFTVIS file format is a compact, pre-generated frequency spectrum for visualizing audio files. At its base it's simple: a header describing how the frames were generated and how to play them back, then an array of frames, each holding one value from 0 to 1 per frequency band (per channel, for stereo files).

Playing back pre-generated frames is much lighter than running an FFT in real time, which matters on phones, watches and TVs.

Version 3 stores values quantized to 4–16 bits, delta encoded between frames and compressed with brotli (or zstd). It can also apply a peak-meter fall at encode time, store stereo, and records each band's frequency edges so clients can find bass, mids and highs.

This repo contains the C# library, a small demo tool, and a JavaScript decoder. If you're building something with the format, also see [FreqFreak](https://github.com/EpsiRho/FreqFreak), a Windows app with live visualizations and an FVZ encoder, decoder and player, and the [blog post](https://epsirho.com/posts/fft-blog) about the project.

# Quick Start
The C# library is on [NuGet](https://www.nuget.org/packages/FFTVIS). It relies on NAudio, FftSharp and ZstdNet (only needed for zstd files).
```bash
dotnet add package FFTVIS --version 2.0.0
```

Encoding:
```C#
var encoder = new AudioEncoder(new EncoderOptions
{
    BarCount = 64,          // bands per frame
    FPS = 60,               // clients interpolate, so 30-60 is smooth on any display
    QuantizeBits = 8,       // 4-16; 6 is ~35% smaller and hard to tell apart for bars
    DecayPerSecond = 2,     // optional peak-meter fall; ~35% smaller files
    Stereo = false,         // true stores left and right
    Codec = FvzCodec.Brotli,
});

encoder.LoadAudio(path);                 // or LoadSamples(float[][] channels, sampleRate)
encoder.GenerateFrames();                // optional IProgress<double> and CancellationToken
encoder.SaveToFile("song");              // adds .fvz
encoder.Save(stream);                    // or any stream
FrequencyVisualizer fv = encoder.GetGeneratedFrames();
```

Decoding (reads v2 and v3):
```C#
FrequencyVisualizer fv = AudioDecoder.ReadFile(path);   // or AudioDecoder.Decode(stream)
float[] frame = fv.GetFrameFromTime(playerTimeMs);       // blends the two nearest frames
float[] right = fv.GetFrameFromTime(playerTimeMs, channel: 1);
var (bassStart, bassEnd) = fv.metadata.BandRange(20, 150); // bands whose centre lies in 20-150 Hz
```

# Format, version 3
All numbers are little-endian. Fields are at fixed offsets; nothing depends on struct padding.

| Offset | Type | Field |
|---|---|---|
| 0 | char[8] | Magic `FFTVIS\0\0` |
| 8 | int32 | Version, `3` |
| 12 | uint16 | Header size: the offset of the payload length field |
| 14 | uint8 | Channels: 1 mono, 2 stereo |
| 15 | uint8 | Quantize bits, 4–16 |
| 16 | uint32 | FFT resolution |
| 20 | uint16 | Bands per frame (per channel) |
| 22 | uint16 | Frame rate |
| 24 | uint32 | Frames per channel |
| 28 | uint32 | Sample rate the frames were generated at |
| 32 | float32 | Max amplitude seen (0–1) |
| 36 | float32 | Decay per second applied at encode time (0 = none) |
| 40 | float32 | dB floor |
| 44 | float32 | dB range |
| 48 | float32 | Frequency min (Hz) |
| 52 | float32 | Frequency max (Hz) |
| 56 | uint8 | Codec: 0 none, 1 zstd, 2 brotli |
| 57 | uint8 | Flags: bit 0 delta encoded |
| 58 | uint8 | Mapping: 0 Normalized, 1 Mel, 2 Log10 (informational) |
| 59 | uint8 | Reserved |
| 60 | float32[bands + 1] | Band edges in Hz: band r covers [edge r, edge r+1) |
| header size | uint32 | Payload length, followed by the payload |

Readers should seek to the header size rather than assume 60 + 4·(bands + 1), so later revisions can add fields.

**Payload.** Decompress with the codec, then read values frame by frame: for each frame, for each channel, for each band. Each value is one byte when quantize bits are 8 or fewer, otherwise a uint16. With delta encoding, add the value to the previous frame's value for the same channel and band and wrap to the value's width (`& 0xFF` or `& 0xFFFF`); the first frame's "previous" is 0. The band's level is `value / (2^bits − 1)`.

**Timing.** Frame `i` is centred on `i / frameRate` seconds. To draw at time `t`, blend frames `floor(t·fps)` and the next by the fractional part.

# FVZ Settings
- **Bar Count**: how many bands to bin frequencies into. Clients wanting fewer bars can average neighbours.
- **DB Floor**: sound below this is ignored, a negative number. -70 to -90 suits music.
- **DB Range**: the dB range shown above the floor. Lower exaggerates, higher smooths. 70–120 is typical.
- **Frequency Min / Max**: typically 20 Hz to 20,000 Hz.
- **Smoothness**: average each bar with ± this many neighbours.
- **Bin Map**: how frequencies map to bars. Normalized (recommended for visuals) gives lows, mids and highs a fair share of bars; Mel follows human pitch perception; Log10 is evenly spaced on a log scale. The file stores the resulting band edges, so it is agnostic to the mapping; custom maps work too.
- **FFT Resolution**: the window of samples analysed per frame, a power of two. 2048–8192 is typical. Above 16384 the window spans so much audio that frames stop matching what you hear, unless the sample rate is high.
- **FPS**: frames stored per second. Because clients should interpolate, 30–60 is smooth; it's the biggest lever on file size after bar count.
- **Stereo**: store left and right separately (double the data) for mirrored or split visuals.
- **Quantize Bits**: 8 is visually lossless; 6 saves ~35%; 5 saves ~55%; 12–16 for analysis uses.
- **Delta Encode**: lossless, always worth it with a codec.
- **Codec / Level**: brotli (default level 5; levels 6–9 are 2–7× slower for under 1% gain, 11 gives ~7% more at ~15× the time) or zstd.
- **Decay Per Second**: bars rise instantly and fall at most this many full-scale units per second. 1.5–3 looks natural.

Typical sizes for a 7.4-minute track (64 bands, 60 fps, mono): 8-bit 1.2 MB, 6-bit with decay 0.56 MB; 16 bands at 30 fps 6-bit: 0.12 MB.

# Changelog
- v2.0.0 (format v3)
	- New v3 container: explicit little-endian header with band edges, sample rate, generation settings and a header size for future fields.
	- Brotli codec (default), 4–16-bit quantization, encode-time decay, stereo.
	- Frames are centred on their timestamps; `GetFrameFromTime` interpolates by default.
	- `EncoderOptions`, `LoadSamples`, `Save(Stream)`, cancellation and a thread limit for `GenerateFrames`; `ReadHeader` reads only the header.
	- Faster generation: the bin-to-band mapping is computed once instead of per frame.
	- Fixes: songs over 65,535 frames were truncated; the constructor ignored the quantize level; max amplitude was updated without locking from several threads; the last window copied the wrong number of samples; Log10 used the Normalized curve.
	- Breaking: `Metadata` is a class with PascalCase properties, frames are `float[]`, and the encoder writes v3 only. v2 files still decode.
- v1.0.2
	- Added an overload for LoadAudio allowing resampling to a target sample rate
- v1.0.1
	- Fixed dependency issue

# Extra Notes
Version 1 used grouped compression stages (None, Zstd, Zstd+Quant, Zstd+Quant+Deltas). Version 2 moved to a bitmask. Version 3 replaced the bitmask with separate codec, bit depth and delta fields.

## Version 2 header
For reading old files: magic (8), int32 version at 8, uint32 FFT resolution at 12, uint16 bands at 16, uint16 frame rate at 18, uint32 frames at 20, float32 max amplitude at 24, uint16 compression bitmask at 28 (1 zstd, 2 quantize, 4 delta), uint8 quantize level at 32 (0 = 16-bit, 1 = 8-bit); payload at 36. Delta-encoded quantized values are signed and map back with `(v / 32767 + 1) / 2` (or `/127` for 8-bit).
