using System;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Tile-component coding parameters
    /// </summary>
    internal class TileCompParams : ICloneable
    {
	    /// <summary>
	    /// Coding style
	    /// </summary>
        internal CP_CSTY csty;

	    /// <summary>
	    /// number of resolutions
	    /// </summary>
	    internal uint numresolutions;

	    /// <summary>
	    /// code-blocks width
	    /// </summary>
	    internal uint cblkw;

	    /// <summary>
	    /// code-blocks height
	    /// </summary>
	    internal uint cblkh;

	    /// <summary>
	    /// code-block coding style
	    /// </summary>
        internal CCP_CBLKSTY cblksty;
	    
        /// <summary>
        /// discrete wavelet transform identifier
        /// </summary>
        /// <remarks>
        /// Looks like this value could be a bool, but I'm leaving it as
        /// a int as there's no need to fix something that isn't broken.
        /// </remarks>
	    internal uint qmfbid;
	    
        /// <summary>
        /// quantisation style
        /// </summary>
        internal CCP_QNTSTY qntsty;
	    
        /// <summary>
        /// stepsizes used for quantization
        /// </summary>
	    internal StepSize[] stepsizes = new StepSize[Constants.J2K_MAXBANDS];
	    
        /// <summary>
        /// number of guard bits
        /// </summary>
	    internal uint numgbits;
	    
        /// <summary>
        /// Region Of Interest shift
        /// </summary>
	    internal int roishift;
	    
        /// <summary>
        /// precinct width
        /// </summary>
	    internal uint[] prcw = new uint[Constants.J2K_MAXRLVLS];
	    
        /// <summary>
        /// precinct height
        /// </summary>
	    internal uint[] prch = new uint[Constants.J2K_MAXRLVLS];

        /// <summary>
        /// the dc_level_shift
        /// </summary>
        internal int dc_level_shift;

        private TileCompParams() { }

        internal static TileCompParams[] Create(uint n)
        {
            var t = new TileCompParams[n];
            for (int c = 0; c < t.Length; c++)
                t[c] = new TileCompParams();
            return t;
        }

        #region ICloneable

        public object Clone()
        {
            var tcp = (TileCompParams)MemberwiseClone();
            tcp.stepsizes = (StepSize[]) stepsizes.Clone();
            tcp.prcw = (uint[])prcw.Clone();
            tcp.prch = (uint[])prch.Clone();
            return tcp;
        }

        #endregion
    }
}
