using System;
using OpenJpeg.Internal;

namespace OpenJpeg
{
    public class ExtraOption
    {
        public bool PLT, TLM;
    }

    /// <summary>
    /// Class that holds the parameters needed for encoding to a JP2000
    /// format.
    /// </summary>
    /// <remarks>opj_cparameters</remarks>
    public class CompressionParameters
    {
        #region Variables and properties

        public int NumberOfResolutions
        {
            get { return this.numresolution; }
            set { numresolution = value; }
        }

        public int NumberOfLayers
        {
            get { return tcp_numlayers; }
            set
            {
                tcp_numlayers = value;
                Array.Resize<float>(ref tcp_rates, value);
                Array.Resize<float>(ref tcp_distoratio, value);
                UpdateMatrice();
            }
        }

        public bool MultipleComponentTransform
        {
            get { return tcp_mct != 0; }
            set
            {
                tcp_mct = (byte) (value ? 1 : 0);
            }
        }
        public bool CustomMultipleComponentTransform
        {
            get { return tcp_mct != 2; }
            set { tcp_mct = (byte)(value ? 2 : 0); }
        }

        /// <summary>
        /// Tries to check if the coding parameter settings are valid
        /// </summary>
        public bool Valid
        {
            get
            {
                if (NumberOfLayers < 1)
                    return false;
                if (cp_fixed_quality)
                {
                    if (cp_disto_alloc)
                        return false;
                    double last = 0;
                    for (int c = 0; c < tcp_distoratio.Length; c++)
                    {
                        var cur = tcp_distoratio[c];
                        if (cur < last) return false;
                        last = cur;
                    }
                }
                else if (cp_disto_alloc)
                {
                    double last = double.MaxValue;
                    for (int c = 0; c < tcp_distoratio.Length; c++)
                    {
                        var cur = tcp_distoratio[c];
                        if (cur > last) return false;
                        last = cur;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Compression rates. Remeber to set NumberOfLayers first. 
        /// Must decrease and DistroAlloc must be true.
        /// </summary>
        public float[] Rates
        {
            get
            {
                return tcp_rates;
            }
        }

        /// <summary>
        /// Set different psnr for successive layers. Must increase.
        /// Remeber to set "fixed quality"
        /// </summary>
        public float[] DistoRatio
        {
            get
            {
                return tcp_distoratio;
            }
        }

        /// <summary>
        /// Sets Fixed Quality
        /// </summary>
        public bool FixedQuality 
        { 
            get { return cp_fixed_quality; }
            set { cp_fixed_quality = value; } 
        }

        /// <summary>
        /// Sets fixed allocation.
        /// 
        /// When setting FixedAlloc there's also a "matrice" that needs to
        /// be filled out.
        /// </summary>
        /// <remarks>
        /// As far as I can figure FixedAlloc lets you set the compression
        /// level of each layer and resolution. I didn't see any documentation
        /// for this feature in the org. impl., and it's rather hard to use.
        /// 
        /// It does appear to be working though. Just fill out the "matrice"
        /// after setting this true (and setting number of layers and resolutions)
        /// 
        /// Note that setting values too high in the matrice will lead to a
        /// debug.assert or null pointer. On my test image I got away with setting
        /// all values up to 17. Leaving it at Zero works too, which is the lowest 
        /// quality.
        /// </remarks>
        public bool FixedAlloc
        {
            get { return cp_fixed_alloc; }
            set { cp_fixed_alloc = value; UpdateMatrice(); }
        }

        /// <summary>
        /// Allocation by rate/distortion
        /// </summary>
        public bool DistroAlloc 
        { 
            get { return cp_disto_alloc; }
            set { cp_disto_alloc = value; } 
        }

        /// <summary>
        /// Whenever the ireversable DWT transform is to be used
        /// </summary>
        public bool Irreversible { get { return irreversible; } set { irreversible = value; } }

        /// <summary>
        /// The subsampeling should also be set on dx/dy on the input image.
        /// </summary>
        public SubsamplingFactor Subsampling
        {
            get { return new SubsamplingFactor(subsampling_dx, subsampling_dy); }
            set
            {
                if (value == null)
                {
                    subsampling_dx = subsampling_dy = 1;
                }
                else
                {
                    subsampling_dx = value.dX;
                    subsampling_dy = value.dY;
                }
            }
        }

        public TileSize TileSize
        {
            get
            {
                if (!tile_size_on) return null;
                return new TileSize(tdx, tdy);
            }
            set
            {
                if (value == null) tile_size_on = false;
                else
                {
                    tdx = value.tdx;
                    tdy = value.tdy;
                    tile_size_on = true;
                }
            }
        }

        /// <summary>
        /// The image offset is not used by the encoder, at all.
        /// </summary>
        /// <remarks>
        /// Used by opj_compress to set the x0/y0 values on the
        /// input image.
        /// </remarks>
        public ImageOffset ImageOffset
        {
            get
            {
                return new ImageOffset(image_offset_x0, image_offset_y0);
            }
            set
            {
                if (value == null)
                {
                    image_offset_x0 = image_offset_y0 = 0;
                }
                else
                {
                    image_offset_x0 = value.x0;
                    image_offset_y0 = value.y0;
                }
            }
        }

        public CodeblockSize CodeblockSize
        {
            get
            {
                return new CodeblockSize(cblockw_init, cblockh_init);
            }
            set
            {
                if (value == null)
                {
                    cblockw_init = Constants.PARAM_DEFAULT_CBLOCKW;
                    cblockh_init = Constants.PARAM_DEFAULT_CBLOCKH;
                }
                else
                {
                    cblockw_init = value.w;
                    cblockh_init = value.h;
                }
            }
        }

        /// <summary>
        /// Returns a copy of the precinct sizes. To set, replace
        /// the whole array.
        /// </summary>
        public PrecinctSize[] PrecinctSizes
        {
            get
            {
                var r = new PrecinctSize[prch_init.Length];
                for (int c = 0; c < r.Length; c++)
                    r[c] = new PrecinctSize(prcw_init[c], prch_init[c]);
                return r;
            }
            set
            {
                if (value == null) return;
                for (int c = 0; c < value.Length && c < prch_init.Length; c++)
                {
                    var v = value[c];
                    prcw_init[c] = v.X;
                    prch_init[c] = v.Y;
                }
                csty |= CP_CSTY.PRT;
                res_spec = value.Length;
            }
        }

        /// <summary>
        /// For used with fixed alloc. Higer values gives better results, but set it too high and
        /// you'll crash. 
        /// </summary>
        public int[] Matrice { get { return matrice; } }

        /// <summary>
        /// Writes a SOP (Start of Packet) marker before each packet
        /// </summary>
        public bool WriteSOP { get { return (csty & CP_CSTY.SOP) != 0; } set { if (value) csty |= CP_CSTY.SOP; else csty &= ~CP_CSTY.SOP; } }

        /// <summary>
        /// Writes a EPH (End packet header) marker after each header packet
        /// </summary>
        public bool WriteEPH { get { return (csty & CP_CSTY.EPH) != 0; } set { if (value) csty |= CP_CSTY.EPH; else csty &= ~CP_CSTY.EPH; } }

        public PROG_ORDER ProgressionOrder
        {
            get { return prog_order; }
            set { prog_order = value; }
        }

        public ProgOrdChang[] ProgressionOrderChange
        {
            get
            {
                if (POC == null)
                    POC = new ProgOrdChang[32];
                return POC;
            }
        }

        public uint NumberOfPocs
        {
            get { return numpocs; }
            set { numpocs = value; }
        }

        /// <summary>
        /// Mode can only be set, not cleared.
        /// </summary>
        public int Mode
        {
            get { return (int)mode; }
            set
            {
                for (int i = 0; i <= 5; i++)
                {
                    int cache = value & (1 << i);
                    if (cache != 0)
                        mode |= (CCP_CBLKSTY)(1 << i);
                }
            }
        }

        #region JPEG 2000 profile macros

        public J2K_PROFILE Profile
        {
            get { return rsiz; }
            set { rsiz = value; }
        }

        public int MaxCompSize
        {
            get { return max_comp_size; }
            set { max_comp_size = value; }
        }

        public int MaxCS_Size
        {
            get { return max_cs_size; }
            set { max_cs_size = value; }
        }

        public bool IsCinema
        {
            get { return rsiz >= J2K_PROFILE.CINEMA_2K && rsiz <= J2K_PROFILE.CINEMA_S4K; }
        }

        public bool IsStorage
        {
            get { return rsiz == J2K_PROFILE.CINEMA_LTS; }
        }

        public bool IsBroadcast
        {
            get { return rsiz >= J2K_PROFILE.BC_SINGLE && ((int)rsiz) <= ((int)J2K_PROFILE.BC_MULTI_R | 0x000b); }
        }

        public bool IsIMF
        {
            get { return rsiz >= J2K_PROFILE.IMF_2K && ((int)rsiz) <= ((int)J2K_PROFILE.IMF_8K_R | 0x009b); }
        }

        public bool IsPart2
        {
            get { return (rsiz & J2K_PROFILE.PART2) == J2K_PROFILE.PART2; }
        }

        #endregion

        /// <summary>
        /// Stores IS 8859-15:1999 (Latin) values
        /// </summary>
        public string Comment
        {
            get 
            {
                if (comment == null) return null;
                return System.Text.ASCIIEncoding.GetEncoding("iso-8859-15").GetString(comment);
                //var ca = new char[comment.Length];
                //Array.Copy(comment, ca, ca.Length);
                //return new String(ca);
            }
            set
            {
                if (value == null)
                    comment = null;
                else
                {
                    comment = System.Text.ASCIIEncoding.GetEncoding("iso-8859-15").GetBytes(value);
                    //comment = new byte[value.Length];
                    //for (int c = 0; c < comment.Length; c++)
                    //    comment[c] = (byte) value[c];
                }
            }
        }

        /// <summary>
        /// Comment to put into the output file
        /// </summary>
        internal byte[] comment;

        /// <summary>
        /// Size of tile: tile_size_on = false (not in argument) or = true (in argument)
        /// </summary>
        internal bool tile_size_on;

        /// <summary>
        /// number of layers
        /// </summary>
        /// <remarks>
        /// Adjustments to this parameter must also
        /// adjust tcp_distoratio and rates</remarks>
        internal int tcp_numlayers;

        /// <summary>
        /// Rates of layers
        /// </summary>
        /// <remarks>
        /// TileCodingParameter_rates
        /// </remarks>
        internal float[] tcp_rates;

        /// <summary>
        /// Different psnr for successive layers
        /// </summary>
        /// <remarks>Size equals the number of layers</remarks>
	    internal float[] tcp_distoratio;

        /// <summary>
        /// Number of resolutions
        /// </summary>
        internal int numresolution;

        /// <summary>
        /// Initial code block width, default to 64
        /// </summary>
        internal int cblockw_init;

        /// <summary>
        /// Mode switch (cblk_style)
        /// </summary>
        internal CCP_CBLKSTY mode;

        /// <summary>
        /// True : use the irreversible DWT 9-7, 
        /// False : use lossless transform (default)
        /// </summary>
        internal bool irreversible;

        /// <summary>
        /// Region of interest: affected component in [0..3], -1 means no ROI
        /// </summary>
        /// <remarks>
        /// AFAICT this simply means that one component can be marked
        /// as more important than the others. 
        /// </remarks>
        internal int roi_compno;
        
        /// <summary>
        /// Region of interest: upshift value
        /// </summary>
        /// <remarks>
        /// How much to shift the importance of a component. 
        /// </remarks>
        internal int roi_shift;

        /// <summary>
        /// Number of precinct size specifications
        /// </summary>
        /// <remarks>
        /// Remeber to init:
        ///  prcw_init = new int[Constants.J2K_MAXRLVLS];
        ///  prch_init = new int[Constants.J2K_MAXRLVLS];
        /// </remarks>
        internal int res_spec;

        /// <summary>
        /// initial precinct width
        /// </summary>
        internal int[] prcw_init = new int[Constants.J2K_MAXRLVLS];

        /// <summary>
        /// initial precinct height
        /// </summary>
        internal int[] prch_init = new int[Constants.J2K_MAXRLVLS];

        internal int image_offset_x0;
        /** subimage encoding: origin image offset in y direction */
        internal int image_offset_y0;
        /** subsampling value for dx */
        internal int subsampling_dx;
        /** subsampling value for dy */
        internal int subsampling_dy;

        /// <summary>
        /// initial code block height, default to 64
        /// </summary>
        internal int cblockh_init;

        /// <summary>
        /// Allocation by rate/distortion
        /// </summary>
        internal bool cp_disto_alloc;

        /// <summary>
        /// Allocation by fixed layer
        /// </summary>
        internal bool cp_fixed_alloc;

        /// <summary>
        /// Add fixed_quality
        /// </summary>
        internal bool cp_fixed_quality;

        internal CINEMA_MODE cp_cinema;

        internal J2K_PROFILE rsiz;
        internal RSIZ_CAPABILITIES cp_rsiz;

        /// <summary>
        /// Enabling Tile part generation
        /// </summary>
        internal bool tp_on;

        /// <summary>
        /// Flag determining tile part generation
        /// </summary>
        internal char tp_flag;

        /// <summary>
        /// Position of tile part flag in progression order
        /// </summary>
        internal int tp_pos;

        /// <summary>
        /// Maximum rate for each component. 
        /// If == 0, component size limitation is not considered
        /// </summary>
        internal int max_comp_size;

        /// <summary>
        /// Maximum code stream size
        /// If == 0, codestream size limitation is not considered
        /// </summary>
        internal int max_cs_size;

        /// <summary>
        /// Size of the image in bits
        /// </summary>
        internal int img_size;

        /// <summary>
        /// if != 0, then original dimension divided by 2^(reduce); 
        /// if == 0 or not used, image is decoded to the full resolution
        /// </summary>
        internal int reduce;

        /// <summary>
        /// if != 0, then only the first "layer" layers are decoded; 
        /// if == 0 or not used, all the quality layers are decoded
        /// </summary>
        internal int layer;

        /// <summary>
        /// ID number of the tiles present in the codestream
        /// </summary>
        internal int[] tileno;

        /// <summary>
        /// Size of the vector tileno
        /// </summary>
        internal int tileno_size;

        /// <summary>
        /// Number of tiles in width
        /// </summary>
        internal int tw;

        /// <summary>
        /// Number of tiles in heigth
        /// </summary>
        internal int th;

        /// <summary>
        /// XTOsiz
        /// </summary>
        internal int tx0;

        /// <summary>
        /// YTOsiz
        /// </summary>
        internal int ty0;

        /// <summary>
        /// XTsiz
        /// </summary>
        internal int tdx;

        /// <summary>
        /// YTsiz
        /// </summary>
        internal int tdy;

        /// <summary>
        /// Coding style
        /// </summary>
        internal CP_CSTY csty;

        /// <summary>
        /// Progreasion order
        /// </summary>
        internal PROG_ORDER prog_order;

        /// <summary>
        /// number of progression order changes (POC), default to 0
        /// </summary>
        /// <remarks>Remember to adjust the pocs array</remarks>
        internal uint numpocs;

        /// <summary>
        /// Progression order changes
        /// </summary>
        internal ProgOrdChang[] POC;

        /// <summary>
        /// Tile coding parameters
        /// </summary>
        internal TileCodingParams[] tcps;

        /// <summary>
        /// Used for fixed layer. (FixedAlloc = true)
        /// </summary>
        internal int[] matrice;

        /// <summary>
        /// if ppm == 1 --> there was a PPM marker for the present tile
        /// </summary>
        internal bool ppm;

        /// <summary>
        /// Use in case of multiple marker PPM (number of info already store)
        /// </summary>
        internal int ppm_store;

        /// <summary>
        /// use in case of multiple marker PPM (case on non-finished previous info)
        /// </summary>
        internal int ppm_previous;

        /// <summary>
        /// Length of the ppm data.
        /// </summary>
        /// <remarks>
        /// C# impl. note:
        /// 
        /// This is always from ppm_position to ppm_data.Length. Though
        /// this may be changed if there's a need, but if there's no need
        /// ppm_len can be dropped.
        /// </remarks>
        internal int ppm_len;

        /// <summary>
        /// packet header store there for futur use in t2_decode_packet
        /// </summary>
        internal byte[] ppm_data;

        /// <summary>
        /// Position from where to begin reading in the ppm_data array
        /// </summary>
        /// <remarks>
        /// C# impl. note:
        /// 
        /// The data arrays are passed from class to class. It would be
        /// inefficient to make copies of the data, however for the moment
        /// I don't need to store the end of this data. (so ppm_len is
        /// redundant with ppm_data.Length - ppm_position)
        /// </remarks>
        internal int ppm_position;

        /// <summary>
        /// pointer remaining on the first byte of the first header if ppm is used
        /// </summary>
        /// <remarks>
        /// This was a poiner. Not sure how best to best represent it.
        /// </remarks>
        internal int ppm_data_first;

        /// <summary>
        /// MCT (multiple component transform)
        /// </summary>
        internal byte tcp_mct;

        /// <summary>
        /// Naive implementation of MCT restricted
        /// </summary>
        /// <remarks>
        /// First there's a float array with MCT data,
        /// then there's an int[] with dc level shifts.
        /// 
        /// There's no technical reason why these can't be
        /// sepparated int two arrays over using IntOrFloat.
        /// </remarks>
        public IntOrFloat[] mct_data;

        public bool DisableMultiThreading { get; set; }

        #endregion

        #region Init

        /// <summary>
        /// Sets up coding parameters by copying from an existing CodingParameters object.
        /// In the original implementation there are two sepperate CodingParameter objects,
        /// so this makes sense there.
        /// 
        /// This does have the advantage of preventing users for altering values they're
        /// not suppose to alter, as only allowed values are copied over.
        /// </summary>
        /// <param name="cp"></param>
        public CompressionParameters(CompressionParameters cp)
        {
            tcp_distoratio = new float[cp.tcp_numlayers];
            tcp_rates = new float[cp.tcp_numlayers];

            //Sets default values
            tw = 1;
            th = 1;

            //Copies over user data
            cp_cinema = cp.cp_cinema;
            max_comp_size = cp.max_comp_size;
            rsiz = cp.rsiz;
            cp_disto_alloc = cp.cp_disto_alloc;
            cp_fixed_alloc = cp.cp_fixed_alloc;
            cp_fixed_quality = cp.cp_fixed_quality;

            
            if (cp.matrice != null)
                matrice = (int[]) cp.matrice.Clone();

            /* tiles */
            tdx = cp.tdx;
            tdy = cp.tdy;

            /* tile offset */
            tx0 = cp.tx0;
            ty0 = cp.ty0;

            /* comment string */
            if (cp.comment != null)
                comment = (byte[]) cp.comment.Clone();

            if (cp.tp_on)
            {
                tp_flag = cp.tp_flag;
                tp_on = true;
            }
        }

        //2.5 - opj_set_default_encoder_parameters
        public CompressionParameters()
        {
            //C# these are set to 100 in the org impl, we
            //   do things a little differently.
            tcp_distoratio = new float[0];
            tcp_rates = new float[0];

            cp_cinema = CINEMA_MODE.OFF; /* DEPRECATED */
            rsiz = J2K_PROFILE.NONE;
            max_comp_size = 0;
            numresolution = Constants.PARAM_DEFAULT_NUMRESOLUTION;
            cp_rsiz = RSIZ_CAPABILITIES.STD_RSIZ; /* DEPRECATED */
            cblockw_init = Constants.PARAM_DEFAULT_CBLOCKW;
            cblockh_init = Constants.PARAM_DEFAULT_CBLOCKW;
            prog_order = Constants.PARAM_DEFAULT_PROG_ORDER;
            roi_compno = -1;
            subsampling_dx = 1;
            subsampling_dy = 1;
            tp_on = false;
            /* decod_format = -1;*/ //<-- Not supported
            /* cod_format = -1; */ //<-- Not supported
            //tcp_rates[0] = 0; //<-- Array is zero length as this point
            tcp_numlayers = 0;
            cp_disto_alloc = false;
            cp_fixed_alloc = false;
            cp_fixed_quality = false;
            /* jpip_on = false */ //<-- Not supported
        }

        #endregion

        /// <summary>
        /// Extract IMF profile without mainlevel/sublevel
        /// </summary>
        internal J2K_PROFILE GetIMF_Profile()
        {
            return rsiz & (J2K_PROFILE)0xff00;
        }

        /// <summary>
        /// Extract IMF main level
        /// </summary>
        internal ushort GetIMF_Mainlevel()
        {
            return (ushort)(rsiz & (J2K_PROFILE)0xf);
        }

        /// <summary>
        /// Extract IMF sub level
        /// </summary>
        internal ushort GetIMF_Sublevel()
        {
            return (ushort)(((ushort)rsiz >> 4) & 0xf);
        }

        private void UpdateMatrice()
        {
            if (cp_fixed_alloc)
            {
                matrice = new int[tcp_numlayers * numresolution * 3];

                //Debug code
                //Values 0-17 works. Setting them to high will trigger
                //a debug assert in Tier2Coding.EncodePacket. 
                //Higher numbers give better quality, lower gives smaller
                //files. 
                //for (int c = 0; c < matrice.Length; c++)
                //    matrice[c] = (int) (8-c/1.9);
            }
            else
                matrice = null;
        }
    }

    internal class CodingParameters
    {
        #region C# impl

        /// <summary>
        /// C# Used for buffer size estimation
        /// </summary>
        public int img_size;
        public TileSize TileSize;

        #region JPEG 2000 profile macros

        public bool IsCinema
        {
            get { return rsiz >= J2K_PROFILE.CINEMA_2K && rsiz <= J2K_PROFILE.CINEMA_S4K; }
        }

        public bool IsStorage
        {
            get { return rsiz == J2K_PROFILE.CINEMA_LTS; }
        }

        public bool IsBroadcast
        {
            get { return rsiz >= J2K_PROFILE.BC_SINGLE && ((int)rsiz) <= ((int)J2K_PROFILE.BC_MULTI_R | 0x000b); }
        }

        public bool IsIMF
        {
            get { return rsiz >= J2K_PROFILE.IMF_2K && ((int)rsiz) <= ((int)J2K_PROFILE.IMF_8K_R | 0x009b); }
        }

        public bool IsPart2
        {
            get { return (rsiz & J2K_PROFILE.PART2) == J2K_PROFILE.PART2; }
        }

        #endregion

        #endregion

        public J2K_PROFILE rsiz;

        public uint tx0;

        public uint ty0;

        public uint tdx;

        public uint tdy;

        public byte[] comment;

        /// <summary>
        /// Number of tiles in width
        /// </summary>
        public uint tw;

        /// <summary>
        /// Number of tiles in heigth
        /// </summary>
        public uint th;

        /// <summary>
        /// Number of ppm markers
        /// </summary>
        public uint ppm_markers_count
        {
            get { return ppm_markers != null ? (uint)ppm_markers.Length : 0u; }
            set
            {
                if (value != ppm_markers_count)
                    throw new NotImplementedException("ppm_markers_count != ppm_markers.Length");
            }
        }

        /// <summary>
        /// PPM markers data (table indexed by Zppm) 
        /// </summary>
        public PPX[] ppm_markers;

        /// <summary>
        /// Packet header store there for futur use in t2_decode_packet
        /// </summary>
        /// <remarks>For length, remeber to substract ppm_data_start</remarks>
        public byte[] ppm_data;
        //private byte[] _ppm_buffer;
        /// <summary>
        /// Size of ppm data.
        /// </summary>
        /// <remarks>
        /// Use of this is a little odd, so don't assume ppm_len == ppm_data.Length
        /// </remarks>
        public uint ppm_len;

        /// <summary>
        /// C# impl. From where one must start reading in the ppm_data array
        /// </summary>
        /// <remarks>To avoid having to shrink the array when there's
        /// a need to move the start position</remarks>
        public int ppm_data_start, ppm_data_current, ppm_buffer_pt;

        /// <summary>
        /// How much data has been read from ppm_data
        /// </summary>
        /// <remarks>Current pos = ppm_data_start + ppm_data_read. It may be possible to drop ppm_data_start</remarks>
        public int ppm_data_read;

        /// <summary>
        /// Tile Coding Parameters
        /// </summary>
        internal TileCodingParams[] tcps;

        internal EncOrDec specific_param;

        /// <summary>
        /// True: entire bit stream must be decoded
        /// False: partial bitstream decoding allowed
        /// </summary>
        public bool strict;

        uint bitvector1;

        public bool ppm
        {
            get
            {
                return (this.bitvector1 & 1u) != 0;
            }
            set
            {
                this.bitvector1 = value ? 1u | bitvector1 : ~1u & bitvector1;
            }
        }

        public bool IsDecoder
        {
            get
            {
                return (this.bitvector1 & 2u) == 2u;
            }
            set
            {
                this.bitvector1 = value ? 2u | bitvector1 : ~2u & bitvector1;
            }
        }

        public bool AllowDifferentBitDepthSign
        {
            get
            {
                return (bitvector1 & 4u) == 4u;
            }
            set
            {
                bitvector1 = value ? 4u | bitvector1 : ~4u & bitvector1;
            }
        }

        //[System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Explicit)]
        internal struct EncOrDec
        {
            //[System.Runtime.InteropServices.FieldOffsetAttribute(0)]
            public DecodeParms dec;

            //[System.Runtime.InteropServices.FieldOffsetAttribute(0)]
            public EncodingParams enc;
        }
    }

    //opj_encoding_param
    internal struct EncodingParams
    {
        public uint max_comp_size;
        public int tp_pos;

        public int[] matrice;
        public byte tp_flag;

        public J2K_QUALITY_LAYER_ALLOCATION_STRATEGY quality_layer_alloc_strategy;
        public bool tp_on;
    }

    public struct PPX
    {
        public uint DataSize { get { return (uint) Data.Length; } }

        /// <summary>
        /// If null => Zppx not read yet 
        /// </summary>
        public byte[] Data;
    }
}
