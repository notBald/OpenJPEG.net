#region License
/*
 * Copyright (c) 2002-2007, Communications and Remote Sensing Laboratory, Universite catholique de Louvain (UCL), Belgium
 * Copyright (c) 2002-2007, Professor Benoit Macq
 * Copyright (c) 2001-2003, David Janssens
 * Copyright (c) 2002-2003, Yannick Verschueren
 * Copyright (c) 2003-2007, Francois-Olivier Devaux and Antonin Descampe
 * Copyright (c) 2005, Herve Drolon, FreeImage Team
 * Copyright (c) 2006-2007, Parvatha Elangovan
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 * 1. Redistributions of source code must retain the above copyright
 *    notice, this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright
 *    notice, this list of conditions and the following disclaimer in the
 *    documentation and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS `AS IS'
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */
#endregion
#region Using
using OPJ_UINT32 = System.Int32;
#endregion

namespace OpenJpeg
{
    /// <summary>
    /// The decoding parameters
    /// </summary>
    internal struct DecodeParms
    {
        #region Variables and properties

        /// <summary>
        /// Set the number of highest resolution levels to be discarded. 
	    /// The image resolution is effectively divided by 2 to the power of the number of discarded levels. 
	    /// The reduce factor is limited by the smallest total number of decomposition levels among tiles.
	    /// if != 0, then original dimension divided by 2^(reduce); 
	    /// if == 0 or not used, image is decoded to the full resolution 
        /// </summary>
        public uint reduce;

        /// <summary>
        /// Set the maximum number of quality layers to decode. 
        /// If there are less quality layers than the specified number, all the quality layers are decoded.
        /// if != 0, then only the first "layer" layers are decoded; 
        /// if == 0 or not used, all the quality layers are decoded 
        /// </summary>
        public uint layer;

        /// <summary>
        /// Specify whether the decoding should be done on the entire codestream, or be limited to the main header
	    /// Limiting the decoding to the main header makes it possible to extract the characteristics of the codestream
	    /// if == NO_LIMITATION, the entire codestream is decoded; 
	    /// if == LIMIT_TO_MAIN_HEADER, only the main header is decoded; 
        /// </summary>
        //public LimitDecoding LimitDecoding = LimitDecoding.NO_LIMITATION;

        #endregion

        #region Init

        #endregion
    }

    //2.1
    public class DecompressionParameters
    {
        internal OPJ_UINT32 reduce;

        /// <summary>
        /// Set the number of highest resolution levels to be discarded. 
	    /// The image resolution is effectively divided by 2 to the power of the number of discarded levels. 
	    /// The reduce factor is limited by the smallest total number of decomposition levels among tiles.
	    /// if != 0, then original dimension divided by 2^(reduce); 
	    /// if == 0 or not used, image is decoded to the full resolution 
        /// </summary>
        public int Reduce { get { return reduce; } set { reduce = (OPJ_UINT32) value; } }

        internal OPJ_UINT32 layer;

        /// <summary>
        /// When set, the color lookup table is not applied to the image
        /// </summary>
        public bool IgnoreColorLookupTable { get; set; }

        public bool DisableMultiThreading { get; set; }

        /// <summary>
        /// Set the maximum number of quality layers to decode. 
	    /// If there are less quality layers than the specified number, all the quality layers are decoded.
	    /// if != 0, then only the first "layer" layers are decoded; 
	    /// if == 0 or not used, all the quality layers are decoded 
        /// </summary>
        public int MaxLayer { get { return layer; } set { layer = (OPJ_UINT32)value; } }

        /// <summary>
        /// Decoding area. For decoding parts of an image
        /// </summary>
        internal OPJ_UINT32 DA_x0, DAx1, DA_y0, DA_y1;

        /// <summary>
        /// Index of decoded tile
        /// </summary>
        internal OPJ_UINT32 tile_index;

        /// <summary>
        /// For decoding a particular tile
        /// </summary>
        internal OPJ_UINT32 n_tile_to_decode;

        internal DPARAMETERS flags;

        public enum DPARAMETERS
        {
            IGNORE_PCLR_CMAP_CDEF_FLAG = 1,
            DUMP_FLAG = 2
        }
    }
}
