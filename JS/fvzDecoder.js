class AudioDecoder {
    // Validates the format of the byte array
    static validateFormat(dataView) {
        // Header struct may change but the first bytes wont
        // We start with the magic string with 8 bytes and then an int version number

        // File is too small
        if (dataView.byteLength < 12) // 8 bytes magic + 4 bytes version
            return -1;

        // Magic is incorrect
        const magicBytes = new Uint8Array(dataView.buffer, 0, 6);
        const magic = String.fromCharCode(...magicBytes);
        if (magic !== "FFTVIS")
            return -1;

        // Version is incorrect
        // Version 1 supported compression types differently
        const version = dataView.getInt32(8, true);

        return version;
    }

    static parseHeader(dataView) {
        const magic = new Uint8Array(dataView.buffer, 0, 8);
        const magicString = String.fromCharCode(...magic.slice(0, 6)); // First 6 bytes are the magic
       
        const version = dataView.getInt32(8, true);
        const fftResolution = dataView.getUint32(12, true);
        const numBands = dataView.getUint16(16, true);
        const frameRate = dataView.getUint16(18, true);
        const totalFrames = dataView.getUint32(20, true);
        const maxAmplitude = dataView.getFloat32(24, true);
        const compressionType = dataView.getUint16(28, true);
        const quantizeLevel = dataView.getUint8(32) !== 0;
       
        const header = {
            magic: magicString,
            version,
            fftResolution,
            numBands,
            frameRate,
            totalFrames,
            maxAmplitude,
            compressionType,
            quantizeLevel
        };
               
        return header;
    }

    static async readFile(arrayBuffer, zstdDecompressor) {
        let fv = null;
        

        // File Validity Check
        // magic(8) + version(4) + fftResolution(4) + numBands(2) + frameRate(2) + totalFrames(4) + maxAmplitude(4) + compressionType(2) + padding(2) + quantizeLevel(1) + padding(3) = 36 bytes
        const headerSize = 36; // Size of Metadata struct in bytes with C# padding
        const headerBytes = arrayBuffer.slice(0, headerSize);
        const headerView = new DataView(headerBytes);

        const version = this.validateFormat(headerView);
        if (version !== 2) {
            throw new Error(`Unsupported file version: ${version}. Only version 2 is supported.`);
        }

        const header = this.parseHeader(headerView);

        const totalFrames = header.totalFrames;
        const numBands = header.numBands;
        const compressionType = header.compressionType;

        let frames;

        const bitmask = new Array(4);
        bitmask[0] = (compressionType & 0x01) !== 0; // Zstd
        bitmask[1] = (compressionType & 0x02) !== 0; // Quantized
        bitmask[2] = (compressionType & 0x04) !== 0; // Delta Encoded
        bitmask[3] = header.quantizeLevel; // Quant 16 or 8 (false or true)
        
        const dataBuffer = arrayBuffer.slice(headerSize);
        frames = await this.decodeCombinations(dataBuffer, totalFrames, numBands, bitmask, zstdDecompressor);

        fv = { header, frames };
        
        return fv;
    }

    static async decodeCombinations(dataBuffer, totalFrames, numBands, bitmask, zstdDecompressor) {
        const dataView = new DataView(dataBuffer);
        let offset = 0;
        let raw; // Raw bytes
        const sampleCount = totalFrames * numBands;
        const output = new Array(totalFrames);

        // Zstd
        if (bitmask[0]) {
            const compressedLength = dataView.getInt32(offset, true);
            offset += 4;
            const compressed = new Uint8Array(dataBuffer, offset, compressedLength);

            if (!zstdDecompressor) {
                throw new Error("Zstd decompressor is required for compressed files");
            }
            
            // Handle different zstd library APIs - check direct function first
            if (typeof zstdDecompressor === 'function') {
                // Direct function call (like fzstd)
                raw = zstdDecompressor(compressed);
            } else if (typeof zstdDecompressor.decompress === 'function') {
                raw = await zstdDecompressor.decompress(compressed);
            } else if (zstdDecompressor.ZstdInit && typeof zstdDecompressor.ZstdInit === 'function') {
                // zstd-wasm style
                const zstd = await zstdDecompressor.ZstdInit();
                raw = zstd.decompress(compressed);
            } else {
                throw new Error("Unknown zstd decompressor API");
            }
            
            // Ensure raw is Uint8Array
            if (!(raw instanceof Uint8Array)) {
                raw = new Uint8Array(raw);
            }
        }
        else // Uncompressed just read it in
        {
            let bytesToRead;
            if (bitmask[2] && bitmask[1] && !bitmask[3]) // Delta + Quantized 16-bit
                bytesToRead = sampleCount * 2;
            else if (bitmask[1]) // Just quantized (8-bit or 16-bit)
                bytesToRead = bitmask[3] ? sampleCount : sampleCount * 2;
            else // Uncompressed doubles
                bytesToRead = sampleCount * 8; // 8 bytes per double

            raw = new Uint8Array(dataBuffer, offset, bytesToRead);
        }

        // Reconstruct from Deltas
        if (bitmask[2]) {
            if (bitmask[1] && !bitmask[3]) // 16 bit 
            {
                // 16-bit delta encoding: deltas are shorts, accumulate into short current values
                const current = new Int16Array(numBands);
                const allDeltas = new Int16Array(raw.buffer, raw.byteOffset, raw.byteLength / 2);

                for (let f = 0; f < totalFrames; f++) {
                    output[f] = new Array(numBands);
                    for (let j = 0; j < numBands; j++) {
                        const idx = f * numBands + j;
                        current[j] += allDeltas[idx];
                        output[f][j] = (current[j] / 32767.0 + 1.0) / 2.0;
                    }
                }
                return output;
            }
            else // 8 bit
            {
                const current = new Int8Array(numBands);
                for (let f = 0; f < totalFrames; f++) {
                    output[f] = new Array(numBands);
                    for (let j = 0; j < numBands; j++) {
                        const delta = new Int8Array(raw.buffer, raw.byteOffset + f * numBands + j, 1)[0];
                        current[j] += delta;
                        output[f][j] = (current[j] / 127.0 + 1.0) / 2.0;
                    }
                }
                return output;
            }

            // If we are delta encoded, we don't need to dequantize again
            // Delta encoding uses a different quantization type (signed instead of unsigned) because to show the difference from large to small leads to negative deltas
        }

        // Dequantize 
        if (bitmask[1] && !bitmask[3]) // 16 bit 
        {
            const allShorts = new Uint16Array(raw.buffer, raw.byteOffset, raw.byteLength / 2);

            for (let f = 0; f < totalFrames; f++) {
                output[f] = new Array(numBands);
                for (let j = 0; j < numBands; j++) {
                    // Convert to 0-1 range
                    output[f][j] = allShorts[f * numBands + j] / 65535.0;
                }
            }
        }
        else if (bitmask[1] && bitmask[3]) // 8 bit
        {
            for (let f = 0; f < totalFrames; f++) {
                output[f] = new Array(numBands);
                for (let j = 0; j < numBands; j++) {
                    // Convert to 0-1 range
                    output[f][j] = raw[f * numBands + j] / 255.0;
                }
            }
        }

        if (output[0] == null) {
            // No quantization or delta encoding so take from raw and convert to double[][]
            const floatSize = 8; // sizeof(double)

            for (let f = 0; f < totalFrames; f++) {
                output[f] = new Array(numBands);
                for (let j = 0; j < numBands; j++) {
                    const idx = (f * numBands + j) * floatSize;
                    const rawView = new DataView(raw.buffer, raw.byteOffset + idx, floatSize);
                    const val = rawView.getFloat64(0, true);
                    // Clamp or scale if needed, otherwise just assign
                    output[f][j] = val;
                }
            }
            return output;
        }

        return output;
    }
}