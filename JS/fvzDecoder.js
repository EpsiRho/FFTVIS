// FFTVIS (.fvz) decoder for versions 2 and 3. See the README for the byte layout.
//
// Version 3 files are usually brotli compressed. Pass decompressors for the codecs you expect:
//   await AudioDecoder.readFile(buffer, { brotli: bytes => myBrotli.decompress(bytes), zstd: fzstd.decompress })
// Without a brotli function the decoder tries the browser's DecompressionStream("brotli") where it exists.
// The second argument may also be a bare zstd function, as in v2.
class AudioDecoder {
    static CODEC_NONE = 0;
    static CODEC_ZSTD = 1;
    static CODEC_BROTLI = 2;

    // Returns the format version, or -1 when the bytes aren't an .fvz header
    static validateFormat(dataView) {
        if (dataView.byteLength < 12) return -1;
        const magic = String.fromCharCode(...new Uint8Array(dataView.buffer, dataView.byteOffset, 6));
        if (magic !== "FFTVIS") return -1;
        return dataView.getInt32(8, true);
    }

    static async readFile(arrayBuffer, decompressors) {
        const view = new DataView(arrayBuffer);
        const version = this.validateFormat(view);
        const codecs = typeof decompressors === "function" ? { zstd: decompressors } : (decompressors || {});
        if (version === 3) return this.readV3(arrayBuffer, view, codecs);
        if (version === 2) return this.readV2(arrayBuffer, codecs.zstd);
        throw new Error(`Unsupported file version: ${version}. Versions 2 and 3 are supported.`);
    }

    // ---- Version 3 ----
    static parseHeaderV3(view) {
        const numBands = view.getUint16(20, true);
        const headerSize = view.getUint16(12, true);
        const bandEdges = new Float32Array(numBands + 1);
        for (let i = 0; i <= numBands && 60 + 4 * i + 4 <= headerSize; i++)
            bandEdges[i] = view.getFloat32(60 + 4 * i, true);
        return {
            magic: "FFTVIS",
            version: view.getInt32(8, true),
            headerSize,
            channels: view.getUint8(14),
            quantizeBits: view.getUint8(15),
            fftResolution: view.getUint32(16, true),
            numBands,
            frameRate: view.getUint16(22, true),
            totalFrames: view.getUint32(24, true),
            sampleRate: view.getUint32(28, true),
            maxAmplitude: view.getFloat32(32, true),
            decayPerSecond: view.getFloat32(36, true),
            dbFloor: view.getFloat32(40, true),
            dbRange: view.getFloat32(44, true),
            frequencyMin: view.getFloat32(48, true),
            frequencyMax: view.getFloat32(52, true),
            codec: view.getUint8(56),
            deltaEncoded: (view.getUint8(57) & 1) !== 0,
            mapping: view.getUint8(58),
            bandEdges,
        };
    }

    static async readV3(arrayBuffer, view, codecs) {
        const header = this.parseHeaderV3(view);
        const payloadLength = view.getUint32(header.headerSize, true);
        const compressed = new Uint8Array(arrayBuffer, header.headerSize + 4, payloadLength);
        const raw = await this.decompress(compressed, header.codec, codecs);

        const { channels, numBands, totalFrames, quantizeBits, deltaEncoded } = header;
        const wide = quantizeBits > 8;
        const mask = wide ? 0xFFFF : 0xFF;
        const scale = 1 / ((1 << quantizeBits) - 1);
        const rawView = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
        const current = new Int32Array(channels * numBands);
        const out = [];
        for (let c = 0; c < channels; c++) out.push(new Array(totalFrames));

        let p = 0;
        for (let f = 0; f < totalFrames; f++) {
            for (let c = 0; c < channels; c++) {
                const frame = new Float32Array(numBands);
                for (let b = 0; b < numBands; b++) {
                    let v;
                    if (wide) { v = rawView.getUint16(p, true); p += 2; }
                    else v = raw[p++];
                    if (deltaEncoded) {
                        const k = c * numBands + b;
                        v = (current[k] + v) & mask;
                        current[k] = v;
                    }
                    frame[b] = v * scale;
                }
                out[c][f] = frame;
            }
        }
        return { header, channels: out, frames: out[0] };
    }

    static async decompress(bytes, codec, codecs) {
        if (codec === this.CODEC_NONE) return bytes;
        if (codec === this.CODEC_ZSTD) {
            if (!codecs.zstd) throw new Error("This file is zstd compressed; pass a zstd decompressor.");
            return this.asBytes(await this.callDecompressor(codecs.zstd, bytes));
        }
        if (codec === this.CODEC_BROTLI) {
            if (codecs.brotli) return this.asBytes(await this.callDecompressor(codecs.brotli, bytes));
            if (typeof DecompressionStream !== "undefined") {
                try {
                    const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("brotli"));
                    return new Uint8Array(await new Response(stream).arrayBuffer());
                } catch (e) { /* not supported by this browser */ }
            }
            throw new Error("This file is brotli compressed; pass a brotli decompressor.");
        }
        throw new Error(`Unknown codec ${codec}`);
    }

    static async callDecompressor(d, bytes) {
        if (typeof d === "function") return d(bytes);
        if (typeof d.decompress === "function") return d.decompress(bytes);
        throw new Error("Unknown decompressor API");
    }

    static asBytes(x) {
        return x instanceof Uint8Array ? x : new Uint8Array(x);
    }

    // Frame at a playback time, blending the two nearest frames (files stored at low frame rates stay smooth).
    static frameAt(fv, timeMs, channel = 0) {
        const frames = (fv.channels || [fv.frames])[channel];
        const exact = Math.max(0, timeMs * fv.header.frameRate / 1000);
        const last = frames.length - 1;
        const a = Math.min(Math.floor(exact), last), b = Math.min(a + 1, last), t = exact - Math.floor(exact);
        const out = new Float32Array(frames[a].length);
        for (let i = 0; i < out.length; i++) out[i] = frames[a][i] + (frames[b][i] - frames[a][i]) * t;
        return out;
    }

    // ---- Version 2 ----
    // magic(8) + version(4) + fftResolution(4) + numBands(2) + frameRate(2) + totalFrames(4) + maxAmplitude(4)
    // + compressionType(2) + padding(2) + quantizeLevel(1) + padding(3) = 36 bytes
    static parseHeader(dataView) {
        return {
            magic: "FFTVIS",
            version: dataView.getInt32(8, true),
            fftResolution: dataView.getUint32(12, true),
            numBands: dataView.getUint16(16, true),
            frameRate: dataView.getUint16(18, true),
            totalFrames: dataView.getUint32(20, true),
            maxAmplitude: dataView.getFloat32(24, true),
            compressionType: dataView.getUint16(28, true),
            quantizeLevel: dataView.getUint8(32) !== 0,
            channels: 1,
        };
    }

    static async readV2(arrayBuffer, zstdDecompressor) {
        const headerSize = 36;
        const header = this.parseHeader(new DataView(arrayBuffer, 0, headerSize));
        const ct = header.compressionType;
        const bitmask = [(ct & 1) !== 0, (ct & 2) !== 0, (ct & 4) !== 0, header.quantizeLevel];
        const frames = await this.decodeCombinations(arrayBuffer.slice(headerSize), header.totalFrames, header.numBands, bitmask, zstdDecompressor);
        return { header, channels: [frames], frames };
    }

    static async decodeCombinations(dataBuffer, totalFrames, numBands, bitmask, zstdDecompressor) {
        const dataView = new DataView(dataBuffer);
        let raw;
        const sampleCount = totalFrames * numBands;
        const output = new Array(totalFrames);

        if (bitmask[0]) {
            const compressedLength = dataView.getInt32(0, true);
            const compressed = new Uint8Array(dataBuffer, 4, compressedLength);
            if (!zstdDecompressor) throw new Error("Zstd decompressor is required for compressed files");
            raw = this.asBytes(await this.callDecompressor(zstdDecompressor, compressed));
        } else {
            const bytes = bitmask[1] ? (bitmask[3] ? sampleCount : sampleCount * 2) : sampleCount * 8;
            raw = new Uint8Array(dataBuffer, 0, bytes);
        }
        const view = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
        for (let f = 0; f < totalFrames; f++) output[f] = new Float32Array(numBands);

        if (bitmask[2]) {
            if (bitmask[1] && !bitmask[3]) {
                const current = new Int16Array(numBands);
                for (let i = 0; i < sampleCount; i++) {
                    const j = i % numBands;
                    current[j] += view.getInt16(i * 2, true);
                    output[(i / numBands) | 0][j] = (current[j] / 32767 + 1) / 2;
                }
            } else if (bitmask[1]) {
                const current = new Int8Array(numBands);
                for (let i = 0; i < sampleCount; i++) {
                    const j = i % numBands;
                    current[j] += view.getInt8(i);
                    output[(i / numBands) | 0][j] = (current[j] / 127 + 1) / 2;
                }
            } else {
                const current = new Float64Array(numBands);
                for (let i = 0; i < sampleCount; i++) {
                    const j = i % numBands;
                    current[j] += view.getFloat64(i * 8, true);
                    output[(i / numBands) | 0][j] = current[j];
                }
            }
        } else if (bitmask[1] && !bitmask[3]) {
            for (let i = 0; i < sampleCount; i++) output[(i / numBands) | 0][i % numBands] = view.getUint16(i * 2, true) / 65535;
        } else if (bitmask[1]) {
            for (let i = 0; i < sampleCount; i++) output[(i / numBands) | 0][i % numBands] = raw[i] / 255;
        } else {
            for (let i = 0; i < sampleCount; i++) output[(i / numBands) | 0][i % numBands] = view.getFloat64(i * 8, true);
        }
        return output;
    }
}

if (typeof module !== "undefined") module.exports = { AudioDecoder };
