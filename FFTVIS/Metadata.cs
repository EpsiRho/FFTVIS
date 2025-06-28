using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FFTVIS
{
    public struct Metadata
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] magic; // 'FFTVIS'
        public int version;
        public uint fftResolution;
        public ushort numBands;
        public ushort frameRate;
        public uint totalFrames;
        public float maxAmplitude;
        public ushort compressionType;
        public bool quantizeLevel;
    }
}
