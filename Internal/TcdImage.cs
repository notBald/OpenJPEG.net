#region License
/*
 * Copyright (c) 2002-2007, Communications and Remote Sensing Laboratory, Universite catholique de Louvain (UCL), Belgium
 * Copyright (c) 2002-2007, Professor Benoit Macq
 * Copyright (c) 2001-2003, David Janssens
 * Copyright (c) 2002-2003, Yannick Verschueren
 * Copyright (c) 2003-2007, Francois-Olivier Devaux and Antonin Descampe
 * Copyright (c) 2005, Herve Drolon, FreeImage Team
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
using System;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Tile coder/decoder image
    /// </summary>
    internal sealed class TcdImage
    {
        #region Variables and properties

        /// <summary>
        /// Tile information
        /// </summary>
        internal TcdTile[] tiles;

        #endregion

        #region Init

        #endregion
    }

    /// <summary>
    /// Tile coder/decode tile
    /// </summary>
    /// <remarks>
    /// opj_tcd_tile
    /// </remarks>
    internal sealed class TcdTile
    {
        /// <summary>
        /// Dimension of the tile : 
        ///  - left upper corner (x0, y0) 
        ///  - right low corner (x1,y1)
        /// </summary>
        internal int x0, y0, x1, y1;

        /// <summary>
        /// Number of components in tile
        /// </summary>
        internal uint numcomps;

        /// <summary>
        /// Component information
        /// </summary>
        internal TcdTilecomp[] comps;

        /// <summary>
        /// Number of pixels of the tile
        /// </summary>
        internal uint numpix;

        /// <summary>
        /// Add fixed_quality
        /// </summary>
        internal double distotile;

        /// <summary>
        /// Add fixed_quality
        /// </summary>
        internal double[] distolayer = new double[100];

        /// <summary>
        /// Packet number
        /// </summary>
        internal uint packno;
    }

    /// <summary>
    /// Tile coder/decoder tile component
    /// </summary>
    /// <remarks>
    /// opj_tcd_tilecomp
    /// </remarks>
    internal sealed class TcdTilecomp
    {
        /// <summary>
        /// Dimension of the component: 
        ///  - left upper corner (x0, y0) 
        ///  - right low corner (x1,y1)
        /// </summary>
        internal int x0, y0, x1, y1;

        /// <summary>
        /// Component number
        /// </summary>
        internal uint compno;

        /// <summary>
        /// Number of resolutions level
        /// </summary>
        internal uint numresolutions;

        /// <summary>
        /// Number of resolutions level to decode (at max)
        /// </summary>
        internal uint minimum_num_resolutions;

        /// <summary>
        /// Resolution information
        /// </summary>
        internal TcdResolution[] resolutions;

        /// <summary>
        /// Data of the component
        /// </summary>
        /// <remarks>Beware of endianesss if this maps to a byte[]</remarks>
        internal int[] data;

        /// <summary>
        /// Used by the org. impl for memory management, not so
        /// important here.
        /// </summary>
        internal bool ownsData;

        /// <summary>
        /// We may either need to allocate this amount of data, or re-use image 
        /// data and ignore this value
        /// </summary>
        /// <remarks>C# Otg impl has this as the number of bytes, we have it as the number of ints.</remarks>
        internal long data_size_needed;

        /// <summary>
        /// Size of the data of the component
        /// </summary>
        /// <remarks>C# has this as number of ints, not bytes.</remarks>
        internal long data_size
        {
            get { return data.Length; }
            set
            {
                if (value != data.Length)
                    throw new NotImplementedException("Data.length != data_size");
            }
        }

        /// <summary>
        /// Data of the component limited to window of interest.
        /// </summary>
        /// <remarks>
        /// Only valid for decoding. 
        /// </remarks>
        internal int[] data_win;

        /// <summary>
        /// Dimension of the component limited to window of interest. 
        /// Only valid for decoding and  if tcd->whole_tile_decoding is NOT set
        /// </summary>
        internal uint win_x0;
        internal uint win_y0;
        internal uint win_x1;
        internal uint win_y1;

#if DEBUG
        float[] FloatData 
        {
            get 
            {
                var f_ar = new float[data.Length];
                Buffer.BlockCopy(data, 0, f_ar, 0, data.Length * sizeof(int));
                return f_ar;
            }
        }
#endif

        /// <summary>
        /// Number of pixels
        /// </summary>
        internal uint numpix;

        /// <summary>
        /// Allocates tile component data
        /// </summary>
        /// <remarks>2.5 - opj_alloc_tile_component_data</remarks>
        internal bool AllocTileComponentData()
        {
            //Console.WriteLine("Data needed: " + data_size_needed);
            if ((data == null) ||
                    ((data_size_needed > data_size) &&
                     (ownsData == false)))
            {
                //C# Note, data_size_needed is number of ints, not bytes like
                //   in org impl.
                data = new int[data_size_needed];
                data_size = data_size_needed;
                ownsData = true;
            }
            else if (data_size_needed > data_size)
            {
                /* We don't need to keep old data */
                data = new int[data_size_needed];
                data_size = data_size_needed;
                ownsData = true;
            }
            return true;
        }
    }

    internal sealed class TcdResolution
    {
        /// <summary>
        /// Dimension of the resolution level: 
        ///  - left upper corner (x0, y0) 
        ///  - right low corner (x1,y1)
        /// </summary>
        internal int x0, y0, x1, y1;

        /// <summary>
        /// Number of resolutions in width and height
        /// (I guess)
        /// </summary>
        internal uint pw, ph;

        /// <summary>
        /// Number sub-band for the resolution level
        /// </summary>
        internal int numbands;

        /// <summary>
        /// Subband information
        /// </summary>
        internal TcdBand[] bands = new TcdBand[3];

        /// <summary>
        /// Dimension of the resolution limited to window of interest.
        /// </summary>
        /// <remarks>
        /// Only valid if tcd.WholeTileDecoding isn't set
        /// </remarks>
        internal uint win_x0, win_y0, win_x1, win_y1;

        public TcdResolution()
        {
            for (int c = 0; c < bands.Length; c++)
                bands[c] = new TcdBand();
        }
    }

    /// <summary>
    /// Tile coder/decoder band
    /// </summary>
    internal sealed class TcdBand
    {
        internal bool IsEmpty
        {
            get { return (x1 - x0 == 0) || (y1 - y0 == 0); }
        }

        /// <summary>
        /// Dimension of the subband: 
        ///  - left upper corner (x0, y0) 
        ///  - right low corner (x1,y1)
        /// </summary>
        internal int x0, y0, x1, y1;

        /// <summary>
        /// Band number
        /// </summary>
        internal uint bandno;

        /// <summary>
        /// Precinct information 
        /// </summary>
        internal TcdPrecinct[] precincts;

        internal int numbps;
        internal float stepsize;
    }

    /// <summary>
    /// Tile coder/decoder precinct
    /// </summary>
    internal sealed class TcdPrecinct
    {
        /// <summary>
        /// Dimension of the precinct: 
        ///  - left upper corner (x0, y0) 
        ///  - right low corner (x1,y1)
        /// </summary>
        internal int x0, y0, x1, y1;

        /// <summary>
        /// Number of precinct in width and heigth
        /// </summary>
        internal uint cw, ch;

        //In the c code, these two are in a union block
        //with the tag "cblks" (Code blocks)
        internal TcdCblkEnc[] enc;
        internal TcdCblkDec[] dec;

        /// <summary>
        /// Inclusion tree
        /// </summary>
        internal TagTree incltree;

        /// <summary>
        /// LMSB tree (zero-bitplane tagtree)
        /// </summary>
        internal TagTree imsbtree;
    }

    /// <summary>
    /// Tile coder/decoder Codeblock Encode
    /// </summary>
    internal sealed class TcdCblkEnc
    {
        internal byte[] data;
        /// <summary>
        /// C# impl, pointer into the data array.
        /// </summary>
        internal int data_pt;
        internal uint data_size 
        { 
            get { return data != null ? (uint) data.Length : 0u; } 
        }
        internal TcdLayer[] layers;
        internal TcdPass[] passes;

        /// <summary>
        /// Dimension of the codeblock: 
        ///  - left upper corner (x0, y0) 
        ///  - right low corner (x1,y1)
        /// </summary>
        internal int x0, y0, x1, y1;

        internal uint numbps;
        internal uint numlenbits;

        /// <summary>
        /// Number of pass already done for the code-blocks
        /// </summary>
        internal uint numpasses;

        /// <summary>
        /// Number of passes in the layer
        /// </summary>
        internal uint numpassesinlayers;

        /// <summary>
        /// Total number of passes
        /// </summary>
        internal uint totalpasses;
    }

    /// <summary>
    /// Tile coder/decoder layer
    /// </summary>
    internal sealed class TcdLayer
    {
        /// <summary>
        /// Number of passes in the layer
        /// </summary>
        internal uint numpasses;

        /// <summary>
        /// Length of information
        /// </summary>
        internal uint len;

        /// <summary>
        /// Add for index
        /// </summary>
        internal double disto;

        /// <summary>
        /// Data
        /// </summary>
        internal byte[] data;
        internal int data_pos;
    }

    /// <summary>
    /// Tile coder/decoder pass
    /// </summary>
    internal sealed class TcdPass
    {
        internal uint rate;
        internal double distortiondec;
        internal uint term, len;
    }

    internal struct TcdSegDataChunk
    {
        //internal byte[] data_ar;
        // ^ This is simply TcdCblk data member, so no need to have ref to it.
        internal int data_pt;

        /// <summary>
        /// Usable length of data, counting from data_pr
        /// </summary>
        internal int len;
    }

    /// <summary>
    /// Tile coding/decoding codeblock decoder
    /// </summary>
    /// <remarks>2.5.3 - opj_tcd_cblk_dec</remarks>
    internal sealed class TcdCblkDec
    {
        /// <summary>
        /// Segment information
        /// </summary>
        internal TcdSeg[] segs;

        /// <summary>
        /// Array of chunks
        /// </summary>
        internal TcdSegDataChunk[] chunks;

        /// <summary>
        /// The pointers inside the chunk array points at this array.
        /// </summary>
        /// <remarks>
        /// C# This is not part of the org imp, as it simply uses pointers
        /// instead.
        /// </remarks>
        internal byte[] chunk_data;

        /// <summary>
        /// Position of the codeblock: 
        ///  - left upper corner (x0, y0) 
        ///  - right low corner (x1,y1)
        /// </summary>
        internal int x0, y0, x1, y1;

        /// <summary>
        /// Currently used only to check if HT decoding is correct
        /// </summary>
        internal uint Mb;

        /// <summary>
        /// Numbps is Mb - P as defined in Section B.10.5 of the standard
        /// </summary>
        internal uint numbps;

        /// <summary>
        /// Number of bits for len, for the current packet. Transitory value
        /// </summary>
        internal uint numlenbits;

        /// <summary>
        /// Number of pass added to the code-blocks
        /// </summary>
        internal uint numnewpasses;

        /// <summary>
        /// Number of segments, including those of packet we skip
        /// </summary>
        /// <remarks>
        /// I.e. segs.Length
        /// </remarks>
        internal uint numsegs;

        /// <summary>
        /// Number of segments, to be used for code block decoding
        /// </summary>
        internal uint real_num_segs;

        /// <summary>
        /// Allocated number of segs[] items
        /// </summary>
        internal uint current_max_segs
        {
            get { return (uint) segs.Length; }
            set
            {
                if (value != segs.Length)
                    throw new NotImplementedException("current_max_segs != segs.Length");
            }
        }

        /// <summary>
        /// Number of valid chunks items
        /// </summary>
        internal uint numchunks;

        /// <summary>
        /// Number of chunks item allocated
        /// </summary>
        internal uint numchunksalloc
        {
            get { return chunks != null ? (uint)chunks.Length : 0u; }
            set
            {
                if (numchunksalloc != value)
                    throw new NotImplementedException("Code asumes numchunksalloc eq chunks.Length");
            }
        }

        /// <summary>
        /// Only used for subtile decoding. Otherwise tilec->data is directly updated
        /// </summary>
        internal int[] decoded_data;

        /// <summary>
        /// Whether the code block data is corrupted
        /// </summary>
        internal bool corrupted;
        internal void Reset()
        {
            Mb = 0;
            segs = null; 
            chunks = null;
            x0 = y0 = x1 = y1 = 0;
            numbps = numlenbits = 0;
            numnewpasses = numsegs = 0;
            real_num_segs = numchunks = 0;
            numchunksalloc = 0;
            decoded_data = null;
        }
    }

    /// <summary>
    /// Tile coder/decoder segment
    /// </summary>
    internal sealed class TcdSeg
    {
        /// <remarks>
        /// In the code I see stuff like:
        /// 
        /// char **data set to char* data_ptr
        /// 
        /// I.e. data = &data_ptr;
        /// 
        /// My pointer-fu isn't brilliant, but from
        /// what I gather that's "in english":
        /// 
        /// set value of pointer [char *] to address
        /// of pointer [char * data_ptr]
        /// 
        /// or in C# tounge: data[0] = data_ptr
        /// 
        /// In c++ there should be no need to allocate
        /// memory for just this scenario. (while 
        /// data[1] = data_ptr would need an alloc)
        /// 
        /// What is important to remenber is that the c++
        /// impl make use of this in a way that's not
        /// supported by C#
        /// 
        /// i.e.
        /// data[0] = &data_ptr
        /// data_ptr = new byte[10]
        /// 
        /// In C++, data will not need to be updated. While in
        /// C# data will need to be updated each time data_ptr
        /// is set.
        /// 
        /// So make special notes of any variable that can be
        /// "referenced" by this array.
        /// 
        /// --
        /// Does this actaully need to be a [][] array? It
        /// makes sort of sense in C++, but here one end
        /// up doing [0] all the time. No other index is added.
        /// 
        /// Converted to using the new Ptr struct
        /// 
        /// Not relevant for 2.5
        /// </remarks>
        //internal Ptr<byte[]> data = new Ptr<byte[]>();

        //internal int dataindex;

        /// <summary>
        /// Number of passes decoded. Including those that we skip
        /// </summary>
        internal uint numpasses;

        /// <summary>
        /// Number of passes actually to be decoded. To be used for code-block decoding
        /// </summary>
        internal uint real_num_passes;

        /// <summary>
        /// Size of data related to this segment
        /// </summary>
        internal uint len;

        /// <summary>
        /// Maximum number of passes for this segment
        /// </summary>
        internal int maxpasses;

        /// <summary>
        /// Number of new passes for current packed. Transitory value
        /// </summary>
        internal uint numnewpasses;

        /// <summary>
        /// Codestream length for this segment for current packed. Transitory value
        /// </summary>
        internal uint newlen;
    }
}
