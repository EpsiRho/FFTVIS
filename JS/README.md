# JS Decoder

This is a simple conversion of the decoder from the C# implementation. I use this for the [Blog Post](https://epsirho.com/posts/fft-blog) on my site about this. There is also the crude implementation of the player used in my site. However this player simply times off of a timer, and you would likely want to base off an audio player.

This decoder depends on [fzstd](https://www.npmjs.com/package/fzstd) sourced from `https://unpkg.com/fzstd@0.1.1`

## Usage
Using the Decoder:
```js
const arrayBuffer = await file.arrayBuffer(); // Read in your .fvz file
const { decompress } = fzstd; // Get a reference to the fzstd decompressor
const result = await AudioDecoder.readFile(arrayBuffer, decompress); // Pass both to the AudioDecoder
```
This will return an object with `.header` to access metadata and `.frames` to access the frames array

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
