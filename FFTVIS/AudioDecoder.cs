using ZstdNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel.Design;

namespace FFTVIS
{
    /// <summary>
    /// Handles decoding of FFTVIS files (.fvz).
    /// Static class, just call <see cref="ReadFile(string)"/> or <see cref="DecodeMemoryStream(MemoryStream)"/> to recieve a decoded FrequencyVisulizer object.
    /// </summary>
    public static class AudioDecoder
    {
        // Converts a set of bytes to a struct of type T
        private static T BytesToStruct<T>(byte[] arr) where T : struct
        {
            T str;
            int size = Marshal.SizeOf<T>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(arr, 0, ptr, size);
                str = Marshal.PtrToStructure<T>(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return str;
        }

        private static int ValidateFormat(byte[] arr)
        {
            // Header struct may change but the first bytes wont
            // We start with the magic string with 8 bytes and then an int version number

            // File is too small
            if (arr.Length < 12) // 8 bytes magic + 4 bytes version
                return -1;

            // Magic is incorrect
            string magic = Encoding.UTF8.GetString(arr, 0, 6);
            if (magic != "FFTVIS")
                return -1;

            // Version is incorrect
            // Version 1 supported compression types differently
            int version = BitConverter.ToInt32(arr, 8);

            return version;
        }

        /// <summary>
        /// Reads in a .fvz file from disk.
        /// </summary>
        /// <param name="filePath">The path of the file to decode.</param>
        /// <returns>A FrequencyVisualizer object containing the metadata and frames.</returns>
        /// <exception cref="NotSupportedException">The file or format version isn't supported</exception>
        public static FrequencyVisualizer ReadFile(string filePath)
        {
            FrequencyVisualizer fv = null;
            using (FileStream fs = new FileStream(filePath, FileMode.Open))
            {
                // File Validity Check
                int headerSize = Marshal.SizeOf<Metadata>();
                byte[] headerBytes = new byte[headerSize];
                fs.Read(headerBytes, 0, headerSize);

                var version = ValidateFormat(headerBytes);
                if(version != 2)
                {
                    throw new NotSupportedException($"Unsupported file version: {version}. Only version 2 is supported.");
                }

                var header = BytesToStruct<Metadata>(headerBytes);

                uint totalFrames = header.totalFrames;
                ushort numBands = header.numBands;
                ushort compressionType = header.compressionType;

                double[][] frames;

                // Decode bitmask
                bool[] bitmask = new bool[4];
                bitmask[0] = (compressionType & 0x01) != 0; // Zstd
                bitmask[1] = (compressionType & 0x02) != 0; // Quantized
                bitmask[2] = (compressionType & 0x04) != 0; // Delta Encoded
                bitmask[3] = header.quantizeLevel; // Quant 16 or 8 (false or true)

                // Decode file
                frames = DecodeCombinations(fs, totalFrames, numBands, bitmask);

                fv = new(header, frames);
            }
            return fv;
        }

        /// <summary>
        /// Reads in a .fvz file from a MemoryStream.
        /// </summary>
        /// <param name="ms">The MemoryStream of the file to decode.</param>
        /// <returns>A FrequencyVisualizer object containing the metadata and frames.</returns>
        /// <exception cref="NotSupportedException">The file or format version isn't supported</exception>
        public static FrequencyVisualizer DecodeMemoryStream(MemoryStream ms)
        {
            FrequencyVisualizer fv = null;
            // File Validity Check
            int headerSize = Marshal.SizeOf<Metadata>();
            byte[] headerBytes = new byte[headerSize];
            ms.Read(headerBytes, 0, headerSize);

            var version = ValidateFormat(headerBytes);
            if (version != 2)
            {
                throw new NotSupportedException($"Unsupported file version: {version}. Only version 2 is supported.");
            }

            var header = BytesToStruct<Metadata>(headerBytes);

            uint totalFrames = header.totalFrames;
            ushort numBands = header.numBands;
            ushort compressionType = header.compressionType;

            double[][] frames;

            // Decode bitmask
            bool[] bitmask = new bool[4];
            bitmask[0] = (compressionType & 0x01) != 0; // Zstd
            bitmask[1] = (compressionType & 0x02) != 0; // Quantized
            bitmask[2] = (compressionType & 0x04) != 0; // Delta Encoded
            bitmask[3] = header.quantizeLevel; // Quant 16 or 8 (false or true)

            // Decode file
            frames = DecodeCombinations(ms, totalFrames, numBands, bitmask);

            fv = new(header, frames);
            return fv;
        }

        private static double[][] DecodeCombinations(Stream fs, uint totalFrames, ushort numBands, bool[] bitmask)
        {
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            byte[] raw; // Raw bytes
            int sampleCount = (int)(totalFrames * numBands);
            double[][] output = new double[totalFrames][];

            // Zstd
            if (bitmask[0])
            {
                int compressedLength = br.ReadInt32();
                byte[] compressed = br.ReadBytes(compressedLength);

                using (var decompressor = new Decompressor())
                {
                    raw = decompressor.Unwrap(compressed);
                }
            }
            else // Uncompressed just read it in
            {
                int bytesToRead;
                if (bitmask[2] && bitmask[1] && !bitmask[3]) // Delta + Quantized 16 bit
                    bytesToRead = sampleCount * 2;
                else if (bitmask[1]) // Just quantized (8 bit or 16 bit)
                    bytesToRead = bitmask[3] ? sampleCount : sampleCount * 2;
                else // Uncompressed doubles
                    bytesToRead = sampleCount * sizeof(double); // 8 bytes per double

                raw = br.ReadBytes(bytesToRead);
            }

            // Reconstruct from Deltas
            if (bitmask[2])
            {
                if (bitmask[1] && !bitmask[3]) // 16 bit 
                {
                    // 16-bit delta encoding: deltas are shorts, accumulate into short current values
                    short[] current = new short[numBands];
                    short[] allDeltas = new short[sampleCount];
                    Buffer.BlockCopy(raw, 0, allDeltas, 0, raw.Length);

                    for (int f = 0; f < totalFrames; f++)
                    {
                        output[f] = new double[numBands];
                        for (int j = 0; j < numBands; j++)
                        {
                            int idx = f * numBands + j;
                            current[j] += allDeltas[idx];
                            output[f][j] = (current[j] / 32767.0 + 1.0) / 2.0;
                        }
                    }
                    return output;
                }
                else if (bitmask[1] && bitmask[3]) // 8 bit
                {
                    sbyte[] current = new sbyte[numBands];
                    for (int f = 0; f < totalFrames; f++)
                    {
                        output[f] = new double[numBands];
                        for (int j = 0; j < numBands; j++)
                        {
                            sbyte delta = unchecked((sbyte)raw[f * numBands + j]);
                            current[j] += delta;
                            output[f][j] = (current[j] / 127.0 + 1.0) / 2.0;
                        }
                    }
                    return output;
                }
                else // no quant, just double
                {
                    double[] current = new double[numBands];
                    for (int f = 0; f < totalFrames; f++)
                    {
                        output[f] = new double[numBands];
                        for (int j = 0; j < numBands; j++)
                        {
                            int idx = (f * numBands + j) * sizeof(double); // 8 bytes per double
                            double delta = BitConverter.ToDouble(raw, idx);
                            current[j] += delta;
                            output[f][j] = current[j];
                        }
                    }
                    return output;
                }

                // If we are delta encoded, we don't need to dequantize again
                // Delta encoding uses a different quantization type (signed instead of unsigned) because we need negatives to show the difference from large to small
            }

            // Dequantize 
            if (bitmask[1] && !bitmask[3]) // 16 bit 
            {
                ushort[] allShorts = new ushort[sampleCount];
                Buffer.BlockCopy(raw, 0, allShorts, 0, raw.Length);

                for (int f = 0; f < totalFrames; f++)
                {
                    output[f] = new double[numBands];
                    for (int j = 0; j < numBands; j++)
                    {
                        // Convert to 0-1 range
                        output[f][j] = allShorts[f * numBands + j] / 65535.0;
                    }
                }
            }
            else if (bitmask[1] && bitmask[3]) // 8 bit
            {
                for (int f = 0; f < totalFrames; f++)
                {
                    output[f] = new double[numBands];
                    for (int j = 0; j < numBands; j++)
                    {
                        // Convert to 0-1 range
                        output[f][j] = raw[f * numBands + j] / 255.0;
                    }
                }
            }

            if(output[0] == null)
            {
                // No quantization or delta encoding so take from raw and convert to double[][]
                int floatSize = sizeof(double); 

                for (int f = 0; f < totalFrames; f++)
                {
                    output[f] = new double[numBands];
                    for (int j = 0; j < numBands; j++)
                    {
                        int idx = (f * numBands + j) * floatSize;
                        double val = BitConverter.ToDouble(raw, idx);
                        // Clamp or scale if needed, otherwise just assign
                        output[f][j] = val;
                    }
                }
                return output;

            }

            return output;
        }
    }
}
