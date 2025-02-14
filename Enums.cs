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
using System;

/*
 * Convention. If no value is set in the enum,
 * the actual value is irrelevant.
 * 
 * If the value is set, don't change it
 */
namespace OpenJpeg
{
    /// <summary>
    /// Supported, but not tested much. 
    /// </summary>
    public enum CINEMA_MODE
    {
        OFF = 0,
        CINEMA2K_24 = 1,
        CINEMA2K_48 = 2,
        CINEMA4K_24 = 3,
    }

    /// <summary>
    /// JPEG 2000 Profiles
    /// </summary>
    [Flags()]
    public enum J2K_PROFILE : ushort
    {
        /// <summary>
        /// No profile, conform to 15444-1
        /// </summary>
        NONE = 0x0000,

        /// <summary>
        /// Profile 0 as described in 15444-1,Table A.45
        /// </summary>
        ONE = 0x0001,

        /// <summary>
        /// Profile 1 as described in 15444-1,Table A.45
        /// </summary>
        TWO = 0x0002,

        /// <summary>
        /// At least 1 extension defined in 15444-2 (Part-2)
        /// </summary>
        PART2 = 0x8000,

        /// <summary>
        /// 2K cinema profile defined in 15444-1 AMD1 
        /// </summary>
        CINEMA_2K = 0x0003,

        /// <summary>
        /// 4K cinema profile defined in 15444-1 AMD1
        /// </summary>
        CINEMA_4K = 0x0004,

        /// <summary>
        /// Scalable 2K cinema profile defined in 15444-1 AMD2
        /// </summary>
        CINEMA_S2K = 0x0005,

        /// <summary>
        /// Scalable 4K cinema profile defined in 15444-1 AMD2
        /// </summary>
        CINEMA_S4K = 0x0006,

        /// <summary>
        /// Long term storage cinema profile defined in 15444-1 AMD2
        /// </summary>
        CINEMA_LTS = 0x0007,

        /// <summary>
        /// Single Tile Broadcast profile defined in 15444-1 AMD3
        /// </summary>
        BC_SINGLE = 0x0100,

        /// <summary>
        /// Multi Tile Broadcast profile defined in 15444-1 AMD3
        /// </summary>
        BC_MULTI = 0x0200,

        /// <summary>
        /// Multi Tile Reversible Broadcast profile defined in 15444-1
        /// </summary>
        BC_MULTI_R = 0x0300,

        /// <summary>
        /// 2K Single Tile Lossy IMF profile defined in 15444-1 AMD 8
        /// </summary>
        IMF_2K = 0x0400,

        /// <summary>
        /// 4K Single Tile Lossy IMF profile defined in 15444-1 AMD 8
        /// </summary>
        IMF_4K = 0x0500,

        /// <summary>
        /// 8K Single Tile Lossy IMF profile defined in 15444-1 AMD 8
        /// </summary>
        IMF_8K = 0x0600,

        /// <summary>
        /// 2K Single/Multi Tile Reversible IMF profile defined in 15444-1 AMD 8
        /// </summary>
        IMF_2K_R = 0x0700,

        /// <summary>
        /// 4K Single/Multi Tile Reversible IMF profile defined in 15444-1 AMD 8
        /// </summary>
        IMF_4K_R = 0x0800,

        /// <summary>
        /// 8K Single/Multi Tile Reversible IMF profile defined in 15444-1 AMD 8
        /// </summary>
        IMF_8K_R = 0x0900,

        /// <summary>
        /// Not technically part of this enum
        /// </summary>
        EXTENSION_NONE = 0,
        EXTENSION_MCT = 0x0100
    }

    //[Obsolete("Use J2K_PROFILE")]
    public enum RSIZ_CAPABILITIES
    {
        STD_RSIZ = 0,		/** Standard JPEG2000 profile*/
        CINEMA2K = 3,		/** Profile name for a 2K image*/
        CINEMA4K = 4,		/** Profile name for a 4K image*/
        MCT = 0x8100
    }

    /// <summary>
    /// Encoding format.
    /// </summary>
    public enum CodecFormat
    {
        Unknown,

        /// <summary>
        /// Jpeg-2000 codestream
        /// </summary>
        Jpeg2K,

        /// <summary>
        /// Jpeg-2000 file format
        /// </summary>
        Jpeg2P
    }

    /// <summary>
    /// How a CIO stream was opened
    /// </summary>
    internal enum OpenMode
    {
        Read = 1,
        Write
    }

    /// <summary>
    /// States for the J2K decoding process
    /// </summary>
    [Flags()]
    internal enum J2K_STATUS
    {
        NONE = 0x0,

        /// <summary>
        /// A SOC marker is expected
        /// </summary>
        MHSOC = 0x1,

        /// <summary>
        /// A SIZ marker is expected
        /// </summary>
        MHSIZ = 0x2,

        /// <summary>
        /// The decoding process is in the main header
        /// </summary>
        MH = 0x4,

        /// <summary>
        /// The decoding process is in a tile part header and expects 
        /// a SOT marker
        /// </summary>
        TPHSOT = 0x8,

        /// <summary>
        /// The decoding process is in a tile part header
        /// </summary>
        TPH = 0x10,

        /// <summary>
        /// The EOC marker has just been read
        /// </summary>
        MT = 0x20,

        /// <summary>
        /// The decoding process must not expect a EOC marker because 
        /// the codestream is truncated
        /// </summary>
        NEOC = 0x40,

        /// <summary>
        /// A tile header has been successfully read and codestream is expected
        /// </summary>
        DATA = 0x80,

        /// <summary>
        /// The decoding process has encountered the EOC marker
        /// </summary>
        EOC = 0x0100,

        /// <summary>
        /// The decoding process has encountered an error
        /// </summary>
        ERR = 0x8000
    }

    enum JP2_STATE
    {
      NONE            = 0x0,
      SIGNATURE       = 0x1,
      FILE_TYPE       = 0x2,
      HEADER          = 0x4,
      CODESTREAM      = 0x8,
      END_CODESTREAM  = 0x10,
      UNKNOWN         = 0x7fffffff /* ISO C restricts enumerator values to range of 'int' */
    }

    enum JP2_IMG_STATE
    {
        NONE        = 0x0,
        UNKNOWN     = 0x7fffffff
    }

    /// <summary>
    /// Markers for JP2 container
    /// </summary>
    enum JP2_Marker : uint
    {
        /// <summary>
        /// JPEG 2000 signature box
        /// </summary>
        JP = 0x6a502020,

        /// <summary>
        /// File type box
        /// </summary>
        FTYP = 0x66747970,

        /// <summary>
        /// JP2 header box
        /// </summary>
        JP2H = 0x6a703268,

        /// <summary>
        /// Image header box 
        /// </summary>
        IHDR = 0x69686472,

        /// <summary>
        /// Color specification box
        /// </summary>
        COLR = 0x636f6c72,

        /// <summary>
        /// Contiguous codestream box
        /// </summary>
        JP2C = 0x6a703263,

        /// <summary>
        /// URL box 
        /// </summary>
        URL = 0x75726c20,

        /// <summary>
        /// Data Reference box 
        /// </summary>
        DTBL = 0x6474626c,

        /// <summary>
        /// Bits per component box
        /// </summary>
        BPCC = 0x62706363,

        /// <summary>
        /// File type fields
        /// </summary>
        JP2 = 0x6a703220,

        /// <summary>
        /// Palette box
        /// </summary>
        PCLR = 0x70636c72,

        /// <summary>
        /// Component Mapping box
        /// </summary>
        CMAP = 0x636d6170,

        /// <summary>
        /// Channel Definition box
        /// </summary>
        CDEF = 0x63646566
    }

    /// <summary>
    /// Markers for J2K code segments
    /// </summary>
    internal enum J2K_Marker : ushort
    {
        /// <summary>
        /// Not a marker
        /// </summary>
        NONE = 0,

        /// <summary>
        /// Before any marker
        /// </summary>
        FIRST = 0xff00,

        /// <summary>
        /// Start of Codestream
        /// </summary>
        SOC = 0xff4f,

        /// <summary>
        /// Start of Tile-part
        /// </summary>
        SOT = 0xff90,

        /// <summary>
        /// Start of Data
        /// </summary>
        SOD = 0xff93,

        /// <summary>
        /// End of Codestream
        /// </summary>
        EOC = 0xffd9,

        /// <summary>
        /// Extended capabilities definition
        /// </summary>
        CAP = 0xff50,

        /// <summary>
        /// Image and tile size data
        /// </summary>
        SIZ = 0xff51,

        /// <summary>
        /// Coding style default
        /// </summary>
        COD = 0xff52,

        /// <summary>
        /// Coding style component
        /// </summary>
        COC = 0xff53,

        /// <summary>
        /// Region-of-interest
        /// </summary>
        /// <remarks>
        /// I belive this is used to facilitate
        /// "Random code-stream access and processing"
        /// </remarks>
        RGN = 0xff5e,

        /// <summary>
        /// Quantization default
        /// </summary>
        QCD = 0xff5c,

        /// <summary>
        /// Quantization component
        /// </summary>
        QCC = 0xff5d,

        /// <summary>
        /// Progression order Change
        /// </summary>
        /// <remarks>
        /// Still investigating, but maybe this control
        /// the order of "Resolution, Layer, Component, ProgOrder"
        /// </remarks>
        POC = 0xff5f,

        /// <summary>
        /// Tile-part lengths
        /// </summary>
        TLM = 0xff55,

        /// <summary>
        /// Packet length, main header
        /// </summary>
        PLM = 0xff57,

        /// <summary>
        /// Packet length, tile-part header
        /// </summary>
        PLT = 0xff58,

        /// <summary>
        /// Packed packet headers, main header
        /// </summary>
        PPM = 0xff60,

        /// <summary>
        /// Packed packet headers, tile-part header
        /// </summary>
        PPT = 0xff61,

        /// <summary>
        /// Start Of Packet marker
        /// 
        /// A optional marker for packets
        /// </summary>
        /// <remarks>
        /// C# impl. note:
        /// 
        /// I do not see the direct purpouse of this marker, but
        /// it may be of use for repairing damaged files. It is
        /// in any case optional.
        /// </remarks>
        SOP = 0xff91,

        /// <summary>
        /// End of packet header.
        /// 
        /// Used with the SOP marker
        /// </summary>
        EPH = 0xff92,

        /// <summary>
        /// Component registration
        /// </summary>
        CRG = 0xff63,

        /// <summary>
        /// Comment
        /// </summary>
        COM = 0xff64,

        /// <summary>
        /// Component bit depths
        /// </summary>
        CBD = 0xff78,
        MCC = 0xff75,

        /// <summary>
        /// Multi component transform
        /// </summary>
        MCT = 0xff74,
        MCO = 0xff77
    }

    public enum PROG_ORDER
    {
        /// <summary>
        /// Place-holder
        /// </summary>
        PROG_UNKNOWN = -1,

        /// <summary>
        /// Layer-resolution-component-precinct order
        /// </summary>
        LRCP = 0,

        /// <summary>
        /// Resolution-layer-component-precinct order
        /// </summary>
        RLCP = 1,

        /// <summary>
        /// Resolution-precinct-component-layer order
        /// </summary>
        RPCL = 2,

        /// <summary>
        /// Precinct-component-resolution-layer order
        /// </summary>
        PCRL = 3,

        /// <summary>
        /// Component-precinct-resolution-layer order
        /// </summary>
        CPRL = 4
    }

    public enum COLOR_SPACE
    {
        //http://www.sno.phy.queensu.ca/~phil/exiftool/TagNames/Jpeg2000.html
        UNKNOWN = -2,
        UNSPECIFIED = -1,
        BILEVEL = 0,
        YCbCr_1 = 1,
        YCbCr_2 = 2,
        YCbCr_3 = 4,
        PhotoYCC = 9,
        CMY = 11,
        CMYK = 12,
        YCCK = 13,
        CIELab = 14,
        sRGB = 16,
        GRAY = 17,
        sYCC = 18, // YUV
        eYCC = 24
    }

    [Flags()]
    internal enum CP_CSTY
    {
        NONE = 0,

        /// <remarks>
        /// The original source has J2K_CP_CSTY_PRT and
        /// J2K_CCP_CSTY_PRT. But both have the same value
        /// and is used the same way. Perhaps they want
        /// to be able to change it later? In any case I've
        /// made J2K_CCP_CSTY_PRT = PRT2
        /// </remarks>
        PRT = 0x01,
        PRT2 = 0x01,

        /// <summary>
        /// Start of Packet marker
        /// </summary>
        SOP = 0x02,

        /// <summary>
        /// End of Packet Header marker
        /// </summary>
        EPH = 0x04
    }

    [Flags()]
    enum CCP_CBLKSTY
    {
        NONE = 0x00,

        /// <summary>
        /// Selective arithmetic coding bypass
        /// </summary>
        LAZY = 0x01,
        /// <summary>
        /// Reset context probabilities on coding pass boundaries
        /// </summary>
        RESET = 0x02,
        /// <summary>
        /// Termination on each coding pass
        /// </summary>
        TERMALL = 0x04,
        /// <summary>
        /// Vertically stripe causal context
        /// </summary>
        VSC = 0x08,
        /// <summary>
        /// Predictable termination
        /// </summary>
        PTERM = 0x10,
        /// <summary>
        /// Segmentation symbols are used
        /// </summary>
        SEGSYM = 0x20,
        /// <summary>
        /// (high throughput) HT codeblocks
        /// </summary>
        HT = 0x40,
        /// <summary>
        /// MIXED mode HT codeblocks
        /// </summary>
        HTMIXED = 0x80
    }

    enum CCP_QNTSTY
    {
        NOQNT = 0,
        SIQNT = 1,
        SEQNT = 2
    }

    enum T2_MODE
    {
        /// <summary>
        /// Function called in Rate allocation process
        /// </summary>
        THRESH_CALC = 0,

        /// <summary>
        /// Function called in Tier 2 process
        /// </summary>
        FINAL_PASS = 1
    }

    /// <summary>
    /// Flags used by the T1 coding. (various defines in t1.h)
    /// </summary>
    [Flags()]
    enum T1 : uint
    {
        /// <summary>
        /// Context orientation : North-East direction
        /// </summary>
        SIG_NE = 0x0001,

        /// <summary>
        /// Context orientation : South-East direction
        /// </summary>
        SIG_SE = 0x0002,

        /// <summary>
        /// Context orientation : South-West direction
        /// </summary>
        SIG_SW = 0x0004,

        /// <summary>
        /// Context orientation : North-West direction
        /// </summary>
        SIG_NW = 0x0008,

        /// <summary>
        /// Context orientation : North direction
        /// </summary>
        SIG_N = 0x0010,

        /// <summary>
        /// Context orientation : East direction
        /// </summary>
        SIG_E = 0x0020,

        /// <summary>
        /// Context orientation : South direction
        /// </summary>
        SIG_S = 0x0040,

        /// <summary>
        /// Context orientation : West direction
        /// </summary>
        SIG_W = 0x0080,

        SIG_OTH = SIG_N|SIG_NE|SIG_E|SIG_SE|SIG_S|SIG_SW|SIG_W|SIG_NW,
        SIG_PRIM = SIG_N| SIG_E|SIG_S|SIG_W,

        SGN_N = 0x0100,
        SGN_E = 0x0200,
        SGN_S = 0x0400,
        SGN_W = 0x0800,
        SGN = SGN_N|SGN_E|SGN_S|SGN_W,

        NONE = 0,
        SIG = 0x1000,
        REFINE = 0x2000,
        VISIT = 0x4000,

        /// <summary>
        /// SIG | VISIT | SIG_OTH
        /// </summary>
        SGS = SIG | VISIT | SIG_OTH,

        SIGMA_0 = 1U << 0,
        SIGMA_1 = 1U << 1,
        SIGMA_2 = 1U << 2,
        SIGMA_3 = 1U << 3,
        SIGMA_4 = 1U << 4,
        SIGMA_5 = 1U << 5,
        SIGMA_6 = 1U << 6,
        SIGMA_7 = 1U << 7,
        SIGMA_8 = 1U << 8,
        SIGMA_9 = 1U << 9,
        SIGMA_10 = 1U << 10,
        SIGMA_11 = 1U << 11,
        SIGMA_12 = 1U << 12,
        SIGMA_13 = 1U << 13,
        SIGMA_14 = 1U << 14,
        SIGMA_15 = 1U << 15,
        SIGMA_16 = 1U << 16,
        SIGMA_17 = 1U << 17,
        CHI_0 = 1U << 18,
        CHI_0_I = 18,
        CHI_1 = 1U << 19,
        CHI_1_I = 19,
        MU_0 = 1U << 20,
        PI_0 = 1U << 21,
        CHI_2 = 1U << 22,
        CHI_2_I = 22,
        MU_1 = 1U << 23,
        PI_1 = 1U << 24,
        CHI_3 = 1U << 25,
        MU_2 = 1U << 26,
        PI_2 = 1U << 27,
        CHI_4 = 1U << 28,
        MU_3 = 1U << 29,
        PI_3 = 1U << 30,
        CHI_5 = 1U << 31,
        CHI_5_I = 31,

        //C# These are for OPJ 2.5 and probably raplace some of the above
        SIGMA_NW = SIGMA_0,
        SIGMA_N = SIGMA_1,
        SIGMA_NE = SIGMA_2,
        SIGMA_W = SIGMA_3,
        SIGMA_THIS = SIGMA_4,
        SIGMA_E = SIGMA_5,
        SIGMA_SW = SIGMA_6,
        SIGMA_S = SIGMA_7,
        SIGMA_SE = SIGMA_8,
        SIGMA_NEIGHBOURS = SIGMA_NW | SIGMA_N | SIGMA_NE | SIGMA_W | SIGMA_E | SIGMA_SW | SIGMA_S | SIGMA_SE,

        CHI_THIS = CHI_1,
        CHI_THIS_I = CHI_1_I,
        MU_THIS = MU_0,
        PI_THIS = PI_0,
        CHI_S = CHI_2
    }

    enum T1_TYPE : sbyte
    {
        /// <summary>
        /// Normal coding using entropy coder
        /// </summary>
        MQ = 0,

        /// <summary>
        /// No encoding the information is store 
        /// under raw format in codestream (mode switch RAW)
        /// </summary>
        RAW = 1
    }

    enum T1_NUMCTXS
    {
        ZC = 9,
        SC = 5,
        MAG = 3,
        AGG = 1,
        UNI = 1
    }

    enum T1_CTXNO : byte
    {
        ZC = 0,
        SC = ZC + T1_NUMCTXS.ZC,
        MAG = SC + T1_NUMCTXS.SC,
        AGG = MAG + T1_NUMCTXS.MAG,
        UNI = AGG + T1_NUMCTXS.AGG
    }

    //Note, equal to MCT_ELEMENT_TYPE + 1
    internal enum ELEMENT_TYPE
    {
        NULL = 0,

        /// <summary>
        /// Data is stored as signed shorts
        /// </summary>
        INT16,

        /// <summary>
        /// MCT data is stored as signed integers
        /// </summary>
        INT32,

        /// <summary>
        /// MCT data is stored as floats
        /// </summary>
        FLOAT,

        /// <summary>
        /// MCT data is stored as doubles
        /// </summary>
        DOUBLE,
    }

    //Should be placed in another source file
    public class TileSize
    {
        public readonly int tdx, tdy;
        public TileSize(int tdx, int tdy)
        {
            if (tdx < 1 || tdy < 1) throw new ArgumentException("Tile is too small");
            this.tdx = tdx; this.tdy = tdy; 
        }
    }
    public class PrecinctSize
    {
        public readonly int X, Y;
        public PrecinctSize(int x, int y)
        {
            if (x % 2 != 0 || y % 2 != 0)
                throw new ArgumentException("Must be a multiple of two");
            X = x; Y = y;
        }
    }
    public class SubsamplingFactor
    {
        public readonly int dX, dY;
        public SubsamplingFactor(int dx, int dy)
        {
            dX = dx; dY = dy;
        }
    }
    public class ImageOffset
    {
        public readonly int x0, y0;
        public ImageOffset(int x0, int y0)
        {
            this.x0 = x0; this.y0 = y0;
        }
    }

    public class CodeblockSize
    {
        public readonly int w, h;

        public CodeblockSize(int width, int height)
        {
            w = width; h = height;
            if (w > 1024 || w < 4 || h > 1024 || h < 4 ||
                w * h > 4096)
                throw new ArgumentException("Invalid codeblock size");
        }
    }
}
