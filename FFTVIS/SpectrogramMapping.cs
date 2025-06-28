using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFTVIS
{
    /// <summary>
    /// The map for frequencies to bars, Normalized is recommended for visuals, Mel for human hearing, and Log10 for spectrum accuracy.
    /// </summary>
    public enum SpectrogramMapping
    {
        Normalized = 0,
        Mel = 1,
        Log10 = 2
    }
}
