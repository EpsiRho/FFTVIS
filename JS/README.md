# JS Decoder

This is a simple conversion of the decoder from the C# implementation. I use this for the [Blog Post](https://epsirho.com/posts/fft-blog) on my site about this. There is also the crude implementation of the player used in my site. However this player simply times off of a timer, and you would likely want to base off an audio player.

Version 3 files are brotli compressed by default: pass a brotli function (e.g. Node's `zlib.brotliDecompressSync`, or a library such as `brotli-dec-wasm` in browsers); the decoder falls back to `DecompressionStream("brotli")` where the browser has it. zstd files (all v2 compressed files) need [fzstd](https://www.npmjs.com/package/fzstd).

## Usage
Using the Decoder:
```js
const arrayBuffer = await file.arrayBuffer(); // Read in your .fvz file
const { decompress } = fzstd; // Get a reference to the fzstd decompressor
const result = await AudioDecoder.readFile(arrayBuffer, { zstd: decompress, brotli: myBrotliDecompress });
const frame = AudioDecoder.frameAt(result, audio.currentTime * 1000); // interpolated frame
```
This returns `.header` (metadata, including `bandEdges` and `channels` for v3), `.channels` (frames per channel) and `.frames` (the first channel)

Using the player:
```js
// result - FV object given by Audio Decoder
// cnv - Canvas to display on
// playbtn - Button to use for play/pause toggle
// timeline - The slider to use as the progress/seek bar
// timestr - The text element to display total time on
// framecount - The text element to display total frames on
// currentTime - The text element to display currnt time on
// currentFrame - The text element to display currnt frame on
let visualizer = new VisualizerPlayer(result, cnv, playbtn, timeline, timestr, framecount, currentTime, currentFrame);
```
