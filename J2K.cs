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
using System;
using System.CodeDom;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using OpenJpeg.Internal;
using static System.Net.Mime.MediaTypeNames;
using static System.Net.WebRequestMethods;
using OPJ_UINT32 = System.UInt32;
#endregion

namespace OpenJpeg
{
    /// <summary>
    /// This class keeps track of the code block
    /// encoding/decoding process. 
    /// </summary>
    internal sealed class J2K
    {
        #region Variables and properties

        /// <summary>
        /// Size of the image.
        /// </summary>
        internal int ImageLength { get { return _cp.img_size; } }

        /// <summary>
        /// Coding parameters
        /// </summary>
        internal CodingParameters CP { get { return _cp; } }

        /// <summary>
        /// I've decided to keep these in a instance
        /// table to ease the situation with callbacks
        /// a little.
        /// </summary>
        readonly DecMstabent _dec_tab;

        /// <summary>
        /// The parent compression info obj.
        /// </summary>
        readonly CompressionInfo _cinfo;

        /// <summary>
        /// If this is a decompressoer
        /// </summary>
        bool _is_decompressor;

        /// <summary>
        /// Parameters relevant to encoding or decoding
        /// </summary>
        J2KEncOrDec _specific_param;
        internal int NumcompsToDecode { get { return _specific_param.decoder.numcomps_to_decode; } }

        /// <summary>
        /// The coding parameters
        /// </summary>
        CodingParameters _cp;

        /// <summary>
        /// Index structure of the codestream
        /// </summary>
        CodestreamIndex _cstr_index;

        /// <summary>
        /// Number of the tile curently concern by coding/decoding
        /// </summary>
        uint _current_tile_number;

        /// <summary>
        /// locate the start position of the SOT marker of the current coded tile:
        /// after encoding the tile, a jump (in j2k_write_sod) is done to the SOT marker to store the value of its length.
        /// </summary>
        //int _sot_start, _sod_start;

        /// <summary>
        /// as the J2K-file is written in several parts during encoding, 
	    /// it enables to make the right correction in position return by cio_tell
        /// </summary>
        int pos_correction;

	    /// <summary>
        /// array used to store the length of each tile
	    /// </summary>
	    int[] _tile_len;

        //Decompression only:

        /// <summary>
        /// Image being decoded/encoded (m_private_image)
        /// </summary>
        JPXImage _private_image, _output_image;

        /// <summary>
        /// Stream being decoded/encoded
        /// </summary>
        CIO _cio;
        BufferCIO _bcio;

        /// <summary>
        /// Current tile coder/decoder
        /// </summary>
        TileCoder _tcd;

        /// <summary>
        /// Image width coming from JP2 IHDR box. 0 from a pure codestream
        /// </summary>
        public uint _ihdr_w;

        /// <summary>
        /// Image height coming from JP2 IHDR box. 0 from a pure codestream
        /// </summary>
        public uint _ihdr_h;

#if SUPPORT_DUMP_FLAG
        /// <summary>
        /// Used to select what information to dump about the code stream to a file or the console
        /// </summary>
        bool _dump_state;
#endif

        #endregion

        #region Init

        private J2K(CompressionInfo cinfo)
        {
            _cinfo = cinfo;
            _dec_tab = new DecMstabent(this);
            _cp = new CodingParameters();
            _is_decompressor = cinfo.IsDecompressor;
            _specific_param = new J2KEncOrDec();
        }

        internal static J2K Create(CompressionInfo cinfo)
        {
            var j2k = new J2K(cinfo);
            if (j2k._is_decompressor)
            {
                //Default to using strict mode
                j2k._cp.strict = true;

                j2k._cp.IsDecoder = true;
                // in the absence of JP2 boxes, consider different bit depth / sign
                // per component is allowed
                j2k._cp.AllowDifferentBitDepthSign = true;

                j2k._specific_param.decoder.header_data = new byte[Constants.J2K_DEFAULT_HEADER_SIZE];
                j2k._specific_param.decoder.header_data_size = Constants.J2K_DEFAULT_HEADER_SIZE;
                j2k._specific_param.decoder.tile_ind_to_dec = -1;
                j2k._specific_param.decoder.last_sot_read_pos = 0;

                j2k._cstr_index = new CodestreamIndex();
            }
            else
            {
                //C# - This is from the opj_j2k_create_compress function
                //     We still use the OpenJpeg 1.4 API, so a few things
                //     have to be placed a little differently.
                j2k._specific_param.encoder.header_tile_data =
                    new byte[Constants.J2K_DEFAULT_HEADER_SIZE];
                j2k._specific_param.encoder.header_tile_data_size = Constants.J2K_DEFAULT_HEADER_SIZE;
            }

            return j2k;
        }

#endregion

#region Setup

        //2.5
        internal void SetupDecode(CIO cio, DecompressionParameters parameters)
        {
            _cio = cio;
            if (parameters != null)
            {
                _cp.specific_param.dec.layer = (uint)parameters.layer;
                _cp.specific_param.dec.reduce = (uint)parameters.reduce;

#if SUPPORT_DUMP_FLAG
                _dump_state = (parameters.flags & DecompressionParameters.DPARAMETERS.DUMP_FLAG) != 0;
#endif
            }
        }

        //2.5 - opj_j2k_encoder_set_extra_options
        internal bool SetExtraOptions(ExtraOption extra)
        {
            _specific_param.encoder.PLT = extra.PLT;
            _specific_param.encoder.TLM = extra.TLM;
            return true;
        }


        //2.5.1 - opj_j2k_setup_encoder
        internal bool SetupEncoder(CompressionParameters parameters, JPXImage image)
        {
            if (parameters == null || image == null)
            {
                _cinfo.Error("Arguments can't be null");
                return false;
            }

            //C# addition
            if (!(parameters.cp_fixed_alloc || parameters.cp_fixed_quality || parameters.cp_disto_alloc))
            {
                _cinfo.Error("Need allocation method. FixedAlloc, FixedQuality or DistroAlloc.");
                return false;
            }        

            if (parameters.numresolution <= 0 || parameters.numresolution > Constants.J2K_MAXRLVLS)
            {
                _cinfo.Error("Invalid number of resolutions : {0} not in range [1,{1}]",
                    parameters.numresolution, Constants.J2K_MAXRLVLS);
                return false;
            }

            if (parameters.cblockw_init < 4 || parameters.cblockw_init > 1024)
            {
                _cinfo.Error("Invalid value for cblockw_init: {0} not a power of 2 in range [4,1024]",
                              parameters.cblockw_init);
                return false;
            }

            if (parameters.cblockh_init < 4 || parameters.cblockh_init > 1024)
            {
                _cinfo.Error("Invalid value for cblockh_init: {0} not a power of 2 in range [4,1024]",
                              parameters.cblockh_init);
                return false;
            }

            if (parameters.cblockw_init * parameters.cblockh_init > 4096)
            {
                _cinfo.Error("Invalid value for cblockw_init * cblockh_init: should be <= 4096");
                return false;
            }
            uint cblkw = (uint)MyMath.int_floorlog2(parameters.cblockw_init);
            uint cblkh = (uint)MyMath.int_floorlog2(parameters.cblockh_init);
            if (parameters.cblockw_init != (1 << (int)cblkw))
            {
                _cinfo.Error("Invalid value for cblockw_init: {0} not a power of 2 in range [4,1024]",
                              parameters.cblockw_init);
                return false;
            }
            if (parameters.cblockh_init != (1 << (int)cblkh))
            {
                _cinfo.Error("Invalid value for cblockw_init: {0} not a power of 2 in range [4,1024]\n",
                              parameters.cblockh_init);
                return false;
            }

            if (parameters.cp_fixed_alloc)
            {
                if (parameters.matrice == null)
                {
                    _cinfo.Error("cp_fixed_alloc set, but cp_matrice missing\n");
                    return false;
                }

                if (parameters.tcp_numlayers > Constants.J2K_TCD_MATRIX_MAX_LAYER_COUNT)
                {
                    _cinfo.Error("tcp_numlayers when cp_fixed_alloc set should not exceed {0}\n",
                                  Constants.J2K_TCD_MATRIX_MAX_LAYER_COUNT);
                    return false;
                }
                if (parameters.numresolution > Constants.J2K_TCD_MATRIX_MAX_RESOLUTION_COUNT)
                {
                    _cinfo.Error("numresolution when cp_fixed_alloc set should not exceed {0}\n",
                                  Constants.J2K_TCD_MATRIX_MAX_RESOLUTION_COUNT);
                    return false;
                }
            }

            //C# Implementation note. To avoid changes to the public API, we do this here
            //instead of in our implementation of the opj_j2k_start_compress function
            _private_image = new JPXImage();
            _private_image.CopyImageHeader(image);
            // Find a better way
            if (image.comps != null)
            {
                for(int it_comp = 0; it_comp < image.numcomps; it_comp++)
                {
                    _private_image.comps[it_comp].data = image.comps[it_comp].data;
                    image.comps[it_comp].data = null;
                }
            }
            //Note that nothing in this function depends on this being done, so the code
            //can be moved to its propper place, if the API is altered.
            //-----

            _specific_param.encoder.nb_comps = image.numcomps;

            //set default values for cp
            _cp.tw = 1;
            _cp.th = 1;

            // to be removed once deprecated cp_cinema and cp_rsiz have been removed
            if (parameters.rsiz == J2K_PROFILE.NONE)
            {
                bool deprecated_used = false;
                switch (parameters.cp_cinema)
                {
                    case CINEMA_MODE.CINEMA2K_24:
                        parameters.rsiz = J2K_PROFILE.CINEMA_2K;
                        parameters.max_cs_size = Constants.CINEMA_24_CS;
                        parameters.max_comp_size = Constants.CINEMA_24_COMP;
                        deprecated_used = true;
                        break;
                    case CINEMA_MODE.CINEMA2K_48:
                        parameters.rsiz = J2K_PROFILE.CINEMA_2K;
                        parameters.max_cs_size = Constants.CINEMA_48_CS;
                        parameters.max_comp_size = Constants.CINEMA_48_COMP;
                        deprecated_used = true;
                        break;
                    case CINEMA_MODE.CINEMA4K_24:
                        parameters.rsiz = J2K_PROFILE.CINEMA_4K;
                        parameters.max_cs_size = Constants.CINEMA_24_CS;
                        parameters.max_comp_size = Constants.CINEMA_24_COMP;
                        deprecated_used = true;
                        break;
                }
                switch (parameters.cp_rsiz)
                {
                    case RSIZ_CAPABILITIES.CINEMA2K:
                        parameters.rsiz = J2K_PROFILE.CINEMA_2K;
                        deprecated_used = true;
                        break;
                    case RSIZ_CAPABILITIES.CINEMA4K:
                        parameters.rsiz = J2K_PROFILE.CINEMA_4K;
                        deprecated_used = true;
                        break;
                    case RSIZ_CAPABILITIES.MCT:
                        parameters.rsiz = J2K_PROFILE.PART2 | J2K_PROFILE.EXTENSION_MCT;
                        deprecated_used = true;
                        break;
                }
                if (deprecated_used)
                    _cinfo.Warn("Deprecated fields cp_cinema or cp_rsiz are used\n" +
                                "Please consider using only the rsiz field\n");

            }

            // If no explicit layers are provided, use lossless settings
            if (parameters.tcp_numlayers == 0)
            {
                parameters.tcp_numlayers = 1;
                parameters.cp_disto_alloc = true;
                parameters.tcp_rates[0] = 0;
            }

            if (parameters.cp_disto_alloc)
            {
                /* Emit warnings if tcp_rates are not decreasing */
                for (uint i = 1; i < (OPJ_UINT32)parameters.tcp_numlayers; i++)
                {
                    float rate_i_corr = parameters.tcp_rates[i];
                    float rate_i_m_1_corr = parameters.tcp_rates[i - 1];
                    if (rate_i_corr <= 1f)
                        rate_i_corr = 1f;
                    if (rate_i_m_1_corr <= 1f)
                        rate_i_m_1_corr = 1f;
                    if (rate_i_corr >= rate_i_m_1_corr)
                    {
                        if (rate_i_corr != parameters.tcp_rates[i] &&
                                rate_i_m_1_corr != parameters.tcp_rates[i - 1])
                        {
                            _cinfo.Warn("tcp_rates[{0}]={1} (corrected as {2}) should be strictly lesser "+
                                        "than tcp_rates[{4}]={5} (corrected as {6})",
                                        i, parameters.tcp_rates[i], rate_i_corr,
                                        i - 1, parameters.tcp_rates[i - 1], rate_i_m_1_corr);
                        }
                        else if (rate_i_corr != parameters.tcp_rates[i])
                        {
                            _cinfo.Warn("tcp_rates[{0}]={1} (corrected as {2}) should be strictly lesser "+
                                        "than tcp_rates[{3}]={4}",
                                        i, parameters.tcp_rates[i], rate_i_corr,
                                        i - 1, parameters.tcp_rates[i - 1]);
                        }
                        else if (rate_i_m_1_corr != parameters.tcp_rates[i - 1])
                        {
                            _cinfo.Warn("tcp_rates[{0}]={1} should be strictly lesser "+
                                        "than tcp_rates[{2}]={3} (corrected as {4})",
                                        i, parameters.tcp_rates[i],
                                        i - 1, parameters.tcp_rates[i - 1], rate_i_m_1_corr);
                        }
                        else
                        {
                            _cinfo.Warn("tcp_rates[{0}]={1} should be strictly lesser "+
                                         "than tcp_rates[{2}]={3}",
                                         i, parameters.tcp_rates[i],
                                         i - 1, parameters.tcp_rates[i - 1]);
                        }
                    }
                }
            }
            else if (parameters.cp_fixed_quality)
            {
                /* Emit warnings if tcp_distoratio are not increasing */
                for (uint i = 1; i < (OPJ_UINT32)parameters.tcp_numlayers; i++)
                {
                    if (parameters.tcp_distoratio[i] < parameters.tcp_distoratio[i - 1] &&
                            !(i == (OPJ_UINT32)parameters.tcp_numlayers - 1 &&
                              parameters.tcp_distoratio[i] == 0))
                    {
                        _cinfo.Warn("tcp_distoratio[{0}]={1} should be strictly greater "+
                                    "than tcp_distoratio[{2}]={3}",
                                    i, parameters.tcp_distoratio[i], i - 1,
                                    parameters.tcp_distoratio[i - 1]);
                    }
                }
            }

            //see if max_codestream_size does limit input rate
            if (parameters.max_cs_size <= 0)
            {
                if (parameters.tcp_rates[parameters.tcp_numlayers - 1] > 0)
                {
                    float temp_size = (image.numcomps * image.comps[0].w * image.comps[0].h * image.comps[0].prec) /
                        (parameters.tcp_rates[parameters.tcp_numlayers - 1] * 8 * image.comps[0].dx * image.comps[0].dy);
                    if (temp_size > int.MaxValue)
                        parameters.max_cs_size = int.MaxValue;
                    else
                        parameters.max_cs_size = (int)temp_size;
                }
                else
                    parameters.max_cs_size = 0;
            }
            else
            {
                bool cap = false;

                if (parameters.IsIMF && parameters.max_cs_size > 0 &&
                    parameters.tcp_numlayers == 1 && parameters.tcp_rates[0] == 0)
                {
                    parameters.tcp_rates[0] = (image.numcomps * image.comps[0].w *
                                               image.comps[0].h * image.comps[0].prec) /
                                               (float)(((uint)parameters.max_cs_size) * 8 * image.comps[0].dx *
                                               image.comps[0].dy);
                }

                float temp_rate = (image.numcomps * image.comps[0].w * image.comps[0].h * image.comps[0].prec) /
                    (parameters.max_cs_size * 8 * image.comps[0].dx * image.comps[0].dy);
                for (int i = 0; i < parameters.tcp_numlayers; i++)
                {
                    if (parameters.tcp_rates[i] < temp_rate)
                    {
                        parameters.tcp_rates[i] = temp_rate;
                        cap = true;
                    }
                }
                if (cap) _cinfo.Warn("The desired maximum codestream size has limited\n"+
                                     "at least one of the desired quality layers");
            }

            if (parameters.IsCinema || parameters.IsIMF)
                _specific_param.encoder.TLM = true;

            // Manage profiles and applications and set RSIZ
            // set cinema parameters if required
            if (parameters.IsCinema)
            {
                if (parameters.rsiz == J2K_PROFILE.CINEMA_S2K || parameters.rsiz == J2K_PROFILE.CINEMA_S4K)
                {
                    _cinfo.Warn("JPEG 2000 Scalable Digital Cinema profiles not yet supported\n");
                    parameters.rsiz = J2K_PROFILE.NONE;
                }
                else
                {
                    SetCinemaParameters(parameters, image);
                    if (!IsCinemaCompliant(image, parameters.rsiz))
                        parameters.rsiz = J2K_PROFILE.NONE;
                }
            }
            else if (parameters.IsStorage)
            {
                _cinfo.Warn("JPEG 2000 Long Term Storage profile not yet supported\n");
                parameters.rsiz = J2K_PROFILE.NONE;
            }
            else if (parameters.IsBroadcast)
            {
                _cinfo.Warn("JPEG 2000 Broadcast profiles not yet supported\n");
                parameters.rsiz = J2K_PROFILE.NONE;
            }
            else if (parameters.IsIMF)
            {
                SetIMF_Parameters(parameters, image);
                if (!IsIMF_Compliant(parameters, image))
                {
                    parameters.rsiz = J2K_PROFILE.NONE;
                }
            }
            else if (parameters.IsPart2)
            {
                if (parameters.rsiz == J2K_PROFILE.PART2)
                {
                    _cinfo.Warn("JPEG 2000 Part-2 profile defined\n"+
                                "but no Part-2 extension enabled.\n"+
                                "Profile set to NONE.\n");
                    parameters.rsiz = J2K_PROFILE.NONE;
                }
                else if (parameters.rsiz == (J2K_PROFILE.PART2 | J2K_PROFILE.EXTENSION_MCT))
                {
                    _cinfo.Warn("Unsupported Part-2 extension enabled\n"+
                                "Profile set to NONE.\n");
                    parameters.rsiz = J2K_PROFILE.NONE;
                }
            }

            //copy user encoding parameters
            _cp.specific_param.enc.max_comp_size = (uint)parameters.max_comp_size;
            _cp.rsiz = parameters.rsiz;


            _cp.specific_param.enc.disto_alloc = parameters.cp_disto_alloc;
            _cp.specific_param.enc.fixed_alloc = parameters.cp_fixed_alloc;
            _cp.specific_param.enc.fixed_quality = parameters.cp_fixed_quality;
            
            // mod fixed quality
            if (parameters.cp_fixed_alloc && parameters.matrice != null)
            {
                //Int64 in org. Impl, but C# don't support arrays > 2GB
                int array_size = parameters.tcp_numlayers * parameters.numresolution * 3;
                _cp.specific_param.enc.matrice = new int[array_size];
                Array.Copy(parameters.matrice, _cp.specific_param.enc.matrice, parameters.matrice.Length);
            }

            // tiles
            _cp.tdx = (uint)parameters.tdx;
            _cp.tdy = (uint)parameters.tdy;

            // tile offset
            _cp.tx0 = (uint)parameters.tx0;
            _cp.ty0 = (uint)parameters.ty0;

            // Comment
            if (parameters.comment != null)
            {
                _cp.comment = parameters.comment.Clone() as byte[];
            }

            // calculate other encoding parameters

            if (parameters.tile_size_on)
            {
                if (_cp.tdx == 0)
                {
                    _cinfo.Error("Invalid tile width");
                    return false;
                }
                if (_cp.tdy == 0)
                {
                    _cinfo.Error("Invalid tile height");
                    return false;
                }

                _cp.tw = (uint)MyMath.int_ceildiv((int) (image.x1 - _cp.tx0), (int)_cp.tdx);
                _cp.th = (uint)MyMath.int_ceildiv((int)( image.y1 - _cp.ty0), (int)_cp.tdy);

                if (_cp.tw > 65535 / CP.th)
                {
                    _cinfo.Error("Invalid number of tiles : {0} x {1} (maximum fixed by jpeg2000 norm is 65535 tiles)",
                        _cp.tw, _cp.th);
                    return false;
                }
            }
            else
            {
                _cp.tdx = (uint) image.x1 - _cp.tx0;
                _cp.tdy = (uint) image.y1 - _cp.ty0;
            }

            if (parameters.tp_on)
            {
                _cp.specific_param.enc.tp_flag = (byte) parameters.tp_flag;
                _cp.specific_param.enc.tp_on = true;
            }

            //C# implementation note:
            //Tilesize is used for buffering calcs later, so we're keeping it.
            // (It's automatically set to null if tiles are off)
            _cp.TileSize = parameters.TileSize;

            //C# implementation note:
            //Image size is also used for buffer calculations. CIO use it
            //to estimate a max size of the image, and if tiles are off
            //it's used for buffer calcs (though only CIO use _cp.img_size)
            _cp.img_size = image.ImageSize;

            //Initialize the tiles
            _cp.tcps = new TileCodingParams[_cp.tw * _cp.th];
            for (uint tileno = 0; tileno < _cp.tcps.Length; tileno++)
            {
                TileCodingParams tcp = new TileCodingParams();
                _cp.tcps[tileno] = tcp;
                tcp.numlayers = (uint) parameters.tcp_numlayers;

                //Using Array.Copy instead of for loop
                if (parameters.IsCinema || parameters.IsIMF)
                {
                    if (_cp.specific_param.enc.fixed_quality)
                        Array.Copy(parameters.tcp_distoratio, tcp.distoratio, tcp.numlayers);
                    Array.Copy(parameters.tcp_rates, tcp.rates, tcp.numlayers);
                }
                else
                {
                    if (_cp.specific_param.enc.fixed_quality)
                        Array.Copy(parameters.tcp_distoratio, tcp.distoratio, tcp.numlayers);
                    else
                        Array.Copy(parameters.tcp_rates, tcp.rates, tcp.numlayers);
                }
                if (!_cp.specific_param.enc.fixed_quality)
                {
                    for(int j=0; j<tcp.numlayers; j++)
                    {
                        if (tcp.rates[j] <= 1f)
                            tcp.rates[j] = 0; //Force lossless
                    }
                }

                tcp.csty = parameters.csty;
                tcp.prg = parameters.prog_order;
                tcp.mct = parameters.tcp_mct;

                uint numpocs_tile = 0;
                tcp.POC = false;

                if (parameters.numpocs > 0)
                {
                    // initialisation of POC
                    for (int i = 0; i < parameters.numpocs; i++)
                    {
                        if (tileno + 1 == parameters.POC[i].tile)
                        {
                            ProgOrdChang tcp_poc = new ProgOrdChang();
                            tcp.pocs[numpocs_tile] = tcp_poc;
                            tcp_poc.resno0  = parameters.POC[numpocs_tile].resno0;
                            tcp_poc.compno0 = parameters.POC[numpocs_tile].compno0;
                            tcp_poc.layno1  = parameters.POC[numpocs_tile].layno1;
                            tcp_poc.resno1  = parameters.POC[numpocs_tile].resno1;
                            tcp_poc.compno1 = Math.Min(parameters.POC[numpocs_tile].compno1, image.numcomps);
                            tcp_poc.prg1    = parameters.POC[numpocs_tile].prg1;
                            tcp_poc.tile    = parameters.POC[numpocs_tile].tile;
                            numpocs_tile++;
                        }
                    }

                    if (numpocs_tile != 0)
                    {
                        CheckPOCval(parameters.POC, tileno, parameters.numpocs,
                            (uint)parameters.numresolution, image.numcomps,
                            (uint)parameters.tcp_numlayers);

                        tcp.POC = true;
                        tcp.numpocs = numpocs_tile - 1;
                    }
                }
                else
                {
                    tcp.numpocs = 0;
                }

                tcp.tccps = TileCompParams.Create(image.numcomps);
                if (parameters.mct_data != null)
                {
                    uint lMctSize = image.numcomps * image.numcomps;
                    float[] lTmpBuf = new float[lMctSize];
                    IntOrFloat[] mct_data = parameters.mct_data;

                    tcp.mct = 2;
                    tcp.mct_coding_matrix = new float[lMctSize];

                    //C#: This is the two memcopy statements
                    for (int c = 0; c < lTmpBuf.Length; c++)
                        lTmpBuf[c] = tcp.mct_coding_matrix[c] = mct_data[c].F;

                    tcp.mct_decoding_matrix = new float[lMctSize];
                    var did_invert = Invert.MatrixInversion(lTmpBuf, tcp.mct_decoding_matrix, (int)image.numcomps);
                    if(!did_invert)
                    {
                        _cinfo.Error("Failed to inverse encoder MCT decoding matrix");
                        return false;
                    }

                    tcp.mct_norms = new double[image.numcomps];
                    MCT.CalculateNorms(tcp.mct_norms, image.numcomps, tcp.mct_decoding_matrix);

                    for (int i = 0; i < image.numcomps; i++)
                    {
                        TileCompParams tccp = tcp.tccps[i];
                        tccp.dc_level_shift = mct_data[lMctSize + i].I;
                    }

                    if (!SetupMCTencoding(tcp, image))
                    {
                        _cinfo.Error("Failed to setup j2k mct encoding");
                        return false;
                    }
                }
                else
                {
                    if (tcp.mct == 1 && image.numcomps >= 3)
                    { //RGB->YCC MCT is enabled
                        if ((image.comps[0].dx != image.comps[1].dx) ||
                            (image.comps[0].dx != image.comps[2].dx) ||
                            (image.comps[0].dy != image.comps[1].dy) ||
                            (image.comps[0].dy != image.comps[2].dy))
                        {
                            _cinfo.Warn("Cannot perform MCT on components with different sizes. Disabling MCT.");
                            tcp.mct = 0;
                        }
                    }

                    for (int i = 0; i < image.numcomps; i++)
                    {
                        ImageComp comp = image.comps[i];
                        if (!comp.sgnd)
                            tcp.tccps[i].dc_level_shift = 1 << ((int)comp.prec - 1);
                    }
                }

                for (int i = 0; i < image.numcomps; i++)
                {
                    TileCompParams tccp = tcp.tccps[i];

                    tccp.csty = parameters.csty 
                        & CP_CSTY.PRT;	// 0 => one precinct || 1 => custom precinct
                    tccp.numresolutions = (uint)parameters.numresolution;
                    tccp.cblkw = (uint)MyMath.int_floorlog2(parameters.cblockw_init);
                    tccp.cblkh = (uint)MyMath.int_floorlog2(parameters.cblockh_init);
                    tccp.cblksty = parameters.mode;
                    tccp.qmfbid = parameters.irreversible ? 0U : 1U;
                    tccp.qntsty = parameters.irreversible ? CCP_QNTSTY.SEQNT : 
                                  CCP_QNTSTY.NOQNT;
                    tccp.numgbits = 2;

                    if (i == parameters.roi_compno)
                        tccp.roishift = parameters.roi_shift;
                    else
                        tccp.roishift = 0;


                    if ((parameters.csty & CP_CSTY.PRT) != 0)
                    {
                        int p = 0;
                        Debug.Assert(tccp.numresolutions > 0);
                        for (int it_res = (int)tccp.numresolutions - 1; it_res >= 0; it_res--)
                        {
                            if (p < parameters.res_spec)
                            {
                                if (parameters.prcw_init[p] < 1)
                                    tccp.prcw[it_res] = 1;
                                else
                                    tccp.prcw[it_res] = (uint)MyMath.int_floorlog2(parameters.prcw_init[p]);

                                if (parameters.prch_init[p] < 1)
                                    tccp.prch[it_res] = 1;
                                else
                                    tccp.prch[it_res] = (uint)MyMath.int_floorlog2(parameters.prch_init[p]);
                            }
                            else
                            {
                                int res_spec = parameters.res_spec;

                                Debug.Assert(res_spec > 0);
                                int size_prcw = parameters.prcw_init[res_spec - 1] >> (p - (res_spec - 1));
                                int size_prch = parameters.prch_init[res_spec - 1] >> (p - (res_spec - 1));

                                if (size_prcw < 1)
                                    tccp.prcw[it_res] = 1;
                                else
                                    tccp.prcw[it_res] = (uint)MyMath.int_floorlog2(size_prcw);

                                if (size_prch < 1)
                                    tccp.prch[it_res] = 1;
                                else
                                    tccp.prch[it_res] = (uint)MyMath.int_floorlog2(size_prch);
                            }
                            p++;
                        }
                    }
                    else
                    {
                        for (int j = 0; j < tccp.numresolutions; j++)
                        {
                            tccp.prcw[j] = 15;
                            tccp.prch[j] = 15;
                        }
                    }


                    DWT.CalcExplicitStepsizes(tccp, image.comps[i].prec);
                }
            }

            if (parameters.mct_data != null)
                parameters.mct_data = null;

            return true;
        }

        //2.5 - opj_j2k_check_poc_val
        bool CheckPOCval(ProgOrdChang[] pocs, uint tileno, uint n_pocs, uint n_resolutions, uint num_comps, uint num_layers)
        {
            uint[] packet_array;
            uint index , resno, compno, layno;
            uint i;
            uint step_c = 1;
            uint step_r = num_comps * step_c;
            uint step_l = n_resolutions * step_r;
            bool loss = false;

            Debug.Assert(n_pocs > 0);

            packet_array = new uint[step_l * num_layers];

            for (i = 0; i < n_pocs; i++)
            {
                ProgOrdChang poc = pocs[i];
                if (tileno + 1 == poc.tile)
                {
                    index = step_r * poc.resno0;

                    /* take each resolution for each poc */
                    for (resno = poc.resno0; resno < Math.Min(poc.resno1, n_resolutions); ++resno)
                    {
                        uint res_index = index + poc.compno0 * step_c;

                        /* take each comp of each resolution for each poc */
                        for (compno = poc.compno0; compno < Math.Min(poc.compno1, num_comps); ++compno)
                        {
                            // The layer index always starts at zero for every progression
                            const uint layno0 = 0;
                            uint comp_index = res_index + layno0 * step_l;

                            /* and finally take each layer of each res of ... */
                            for (layno = layno0; layno < Math.Min(poc.layno1, num_layers); ++layno)
                            {
                                packet_array[comp_index] = 1;
                                comp_index += step_l;
                            }

                            res_index += step_c;
                        }

                        index += step_r;
                    }
                }
            }

            index = 0;
            for (layno = 0; layno < num_layers ; ++layno) {
                for (resno = 0; resno < n_resolutions; ++resno) {
                    for (compno = 0; compno < num_comps; ++compno) {
                            loss |= (packet_array[index]!=1);
                            index += step_c;
                    }
                }
            }

            if (loss) {
                    _cinfo.Error("Missing packets possible loss of data\n");
            }

            return !loss;
        }

        //2.5 - opj_j2k_setup_mct_encoding
        bool SetupMCTencoding(TileCodingParams tcp, JPXImage image)
        {
            uint i;
            uint indix = 1;
            ArPtr<MctData> mct_deco_data_ptr = null, mct_offset_data_ptr;
            SimpleMccDecorrelationData mcc_data;
            int mct_size,n_elem; // keeping as int, as these referes to an array's size
            float[] data;
            int current_data; //<-- pointer into data
            TileCompParams[] tccps;
            int tccp; //<-- pointer into tccps

            /* preconditions */
            Debug.Assert(tcp != null);

            if (tcp.mct != 2) {
                return true;
            }

            if (tcp.mct_decoding_matrix != null) 
            {
                if (tcp.n_mct_records == tcp.n_max_mct_records) 
                {
                    tcp.n_max_mct_records += Constants.MCT_DEFAULT_NB_RECORDS;
                    Array.Resize<MctData>(ref tcp.mct_records, (int) tcp.n_max_mct_records);

                    //Mesets to 0 (using length instead of tcp.n_max_mct_records - tcp.n_mct_records)
                    //it is ultimatly the same anyway (as memcopy starts from n_mct_records).
                    for (uint c = tcp.n_mct_records; c < tcp.mcc_records.Length; c++)
                        tcp.mct_records[c] = new MctData();
                }
                mct_deco_data_ptr = new ArPtr<MctData>(tcp.mct_records, (int) tcp.n_mct_records);
                var mct_deco_data = mct_deco_data_ptr.Deref;

                if (mct_deco_data.data.Type != ELEMENT_TYPE.NULL)
                    mct_deco_data.data.Null();

                mct_deco_data.index = (int)indix++;
                mct_deco_data.array_type = MCT_ARRAY_TYPE.MCT_TYPE_DECORRELATION;
                mct_deco_data.element_type = MCT_ELEMENT_TYPE.MCT_TYPE_FLOAT;
                n_elem = (int)(image.numcomps * image.numcomps);
                mct_size = n_elem; //<-- C# our struct is always the same size, so no MCT_ELEMENT_SIZE
                mct_deco_data.data = new ShortOrIntOrFloatOrDoubleAr(mct_deco_data.element_type, n_elem);

                mct_deco_data.data.CopyFromFloat(tcp.mct_decoding_matrix, n_elem);

                mct_deco_data.data_size = mct_size;
                ++tcp.n_mct_records;
            }

            if (tcp.n_mct_records == tcp.n_max_mct_records) {
                    tcp.n_max_mct_records += Constants.MCT_DEFAULT_NB_RECORDS;
                    Array.Resize<MctData>(ref tcp.mct_records, (int) tcp.n_max_mct_records);

                    for (uint c = tcp.n_mct_records; c < tcp.mct_records.Length; c++)
                        tcp.mct_records[c] = new MctData();

                    if (mct_deco_data_ptr != null)
                        mct_deco_data_ptr.Pos = (int) (tcp.n_mct_records - 1);
            }

            mct_offset_data_ptr = new ArPtr<MctData>(tcp.mct_records, (int) tcp.n_mct_records);
            var mct_offset_data = mct_offset_data_ptr.Deref;

            if (mct_offset_data.data.Type != ELEMENT_TYPE.NULL)
                mct_offset_data.data.Null();

            mct_offset_data.index = (int)indix++;
            mct_offset_data.array_type = MCT_ARRAY_TYPE.MCT_TYPE_OFFSET;
            mct_offset_data.element_type = MCT_ELEMENT_TYPE.MCT_TYPE_FLOAT;
            n_elem = (int)image.numcomps;
            mct_size = n_elem;// * (int)MCT.ELEMENT_SIZE[(int) mct_offset_data.element_type];
            mct_offset_data.data = new ShortOrIntOrFloatOrDoubleAr(mct_offset_data.element_type, n_elem);


            data = new float[n_elem];
            
            tccps = tcp.tccps;
            tccp = 0;
            current_data = 0;

            for (i = 0; i < n_elem; ++i)
            {
                data[current_data++] = (float)(tccps[tccp].dc_level_shift);
                ++tccp;
            }

            //C# note: No need to copy l_data, it gets freed anyway
            mct_offset_data.data.Type = ELEMENT_TYPE.FLOAT;
            mct_offset_data.data.FA = data;
            mct_offset_data.data_size = mct_size;

            ++tcp.n_mct_records;

            if (tcp.n_mcc_records == tcp.n_max_mcc_records)
            {
                tcp.n_max_mcc_records += Constants.MCT_DEFAULT_NB_RECORDS;
                Array.Resize(ref tcp.mcc_records, (int)tcp.n_max_mcc_records);

                //C# impl note. We "meset" tcp.mcc_records.Length, instead of
                //  (p_tcp->m_nb_max_mcc_records - p_tcp->m_nb_mcc_records)
                for (uint c = tcp.n_mcc_records; c < tcp.mcc_records.Length; c++)
                    tcp.mcc_records[c] = new SimpleMccDecorrelationData();
            }
            
            mcc_data = tcp.mcc_records[tcp.n_mcc_records];
            mcc_data.decorrelation_array = mct_deco_data_ptr;
            mcc_data.is_irreversible = true;
            mcc_data.n_comps = image.numcomps;
            mcc_data.index = indix++;
            mcc_data.offset_array = mct_offset_data_ptr;
            ++tcp.n_mcc_records;

            return true;
        }


        //2.5 - opj_j2k_set_imf_parameters
        void SetIMF_Parameters(CompressionParameters parameters, JPXImage image)
        {
            var rsiz = parameters.rsiz;
            var profile = parameters.GetIMF_Profile();

            // Override defaults set by opj_set_default_encoder_parameters
            if (parameters.cblockw_init == Constants.PARAM_DEFAULT_CBLOCKW &&
                parameters.cblockh_init == Constants.PARAM_DEFAULT_CBLOCKH)
            {
                parameters.cblockw_init = 32;
                parameters.cblockh_init = 32;
            }

            // One tile part for each component
            parameters.tp_flag = 'C';
            parameters.tp_on = true;

            if (parameters.prog_order == Constants.PARAM_DEFAULT_PROG_ORDER)
            {
                parameters.prog_order = PROG_ORDER.CPRL;
            }

            if (profile == J2K_PROFILE.IMF_2K ||
                profile == J2K_PROFILE.IMF_4K ||
                profile == J2K_PROFILE.IMF_8K)
            {
                /* 9-7 transform */
                parameters.irreversible = true;
            }

            /* Adjust the number of resolutions if set to its defaults */
            if (parameters.numresolution == Constants.PARAM_DEFAULT_NUMRESOLUTION &&
                    image.x0 == 0 &&
                    image.y0 == 0)
            {
                int max_NL = GetIMF_MaxNL(parameters, image);
                if (max_NL >= 0 && parameters.numresolution > max_NL)
                {
                    parameters.numresolution = max_NL + 1;
                }

                /* Note: below is generic logic */
                if (!parameters.tile_size_on)
                {
                    while (parameters.numresolution > 0)
                    {
                        if (image.x1 < (1U << (parameters.numresolution - 1)))
                        {
                            parameters.numresolution--;
                            continue;
                        }
                        if (image.y1 < (1U << (parameters.numresolution - 1)))
                        {
                            parameters.numresolution--;
                            continue;
                        }
                        break;
                    }
                }
            }

            /* Set defaults precincts */
            if (parameters.csty == 0)
            {
                parameters.csty |= CP_CSTY.PRT;
                if (parameters.numresolution == 1)
                {
                    parameters.res_spec = 1;
                    parameters.prcw_init[0] = 128;
                    parameters.prch_init[0] = 128;
                }
                else
                {
                    int i;
                    parameters.res_spec = parameters.numresolution - 1;
                    for (i = 0; i < parameters.res_spec; i++)
                    {
                        parameters.prcw_init[i] = 256;
                        parameters.prch_init[i] = 256;
                    }
                }
            }
        }

        //2.5 - opj_j2k_get_imf_max_NL
        int GetIMF_MaxNL(CompressionParameters parameters, JPXImage image)
        {
            /* Decomposition levels */
            var profile = parameters.GetIMF_Profile();
            OPJ_UINT32 XTsiz = parameters.tile_size_on ? (OPJ_UINT32)
                               parameters.tdx : image.x1;
            switch (profile)
            {
                case J2K_PROFILE.IMF_2K:
                    return 5;
                case J2K_PROFILE.IMF_4K:
                    return 6;
                case J2K_PROFILE.IMF_8K:
                    return 7;
                case J2K_PROFILE.IMF_2K_R:
                    {
                        if (XTsiz >= 2048)
                        {
                            return 5;
                        }
                        else if (XTsiz >= 1024)
                        {
                            return 4;
                        }
                        break;
                    }
                case J2K_PROFILE.IMF_4K_R:
                    {
                        if (XTsiz >= 4096)
                        {
                            return 6;
                        }
                        else if (XTsiz >= 2048)
                        {
                            return 5;
                        }
                        else if (XTsiz >= 1024)
                        {
                            return 4;
                        }
                        break;
                    }
                case J2K_PROFILE.IMF_8K_R:
                    {
                        if (XTsiz >= 8192)
                        {
                            return 7;
                        }
                        else if (XTsiz >= 4096)
                        {
                            return 6;
                        }
                        else if (XTsiz >= 2048)
                        {
                            return 5;
                        }
                        else if (XTsiz >= 1024)
                        {
                            return 4;
                        }
                        break;
                    }
                default:
                    break;
            }
            return -1;
        }

        //2.5 - opj_j2k_set_cinema_parameters
        void SetCinemaParameters(CompressionParameters parameters, JPXImage image)
        {
            /* Configure cinema parameters */
            int i;

            /* No tiling */
            parameters.tile_size_on = false;
            parameters.tdx = 1;
            parameters.tdy = 1;

            /* One tile part for each component */
            parameters.tp_flag = 'C';
            parameters.tp_on = true;

            /* Tile and Image shall be at (0,0) */
            parameters.tx0 = 0;
            parameters.ty0 = 0;
            parameters.image_offset_x0 = 0;
            parameters.image_offset_y0 = 0;

            /* Codeblock size= 32*32 */
            parameters.cblockw_init = 32;
            parameters.cblockh_init = 32;

            /* Codeblock style: no mode switch enabled */
            parameters.mode = 0;

            /* No ROI */
            parameters.roi_compno = -1;

            /* No subsampling */
            parameters.subsampling_dx = 1;
            parameters.subsampling_dy = 1;

            /* 9-7 transform */
            parameters.irreversible = true;

            /* Number of layers */
            if (parameters.tcp_numlayers > 1){
                _cinfo.Warn("JPEG 2000 Profile-3 and 4 (2k/4k dc profile) requires:\n"+
                            "1 single quality layer"+
                            "-> Number of layers forced to 1 (rather than {0})\n"+
                            "-> Rate of the last layer ({1}) will be used",
                            parameters.tcp_numlayers, parameters.tcp_rates[parameters.tcp_numlayers-1]);
                parameters.tcp_rates[0] = parameters.tcp_rates[parameters.tcp_numlayers-1];
                parameters.tcp_numlayers = 1;
            }

            /* Resolution levels */
            switch (parameters.rsiz)
            {
                case J2K_PROFILE.CINEMA_2K:
                    if(parameters.numresolution > 6){
                        _cinfo.Warn("JPEG 2000 Profile-3 (2k dc profile) requires:\n"+
                                    "Number of decomposition levels <= 5\n"+
                                    "-> Number of decomposition levels forced to 5 (rather than {0})\n",
                                    parameters.numresolution+1);
                    parameters.numresolution = 6;
                }
                break;
            case J2K_PROFILE.CINEMA_4K:
                if(parameters.numresolution < 2){
                    _cinfo.Warn("JPEG 2000 Profile-4 (4k dc profile) requires:\n"+
                                "Number of decomposition levels >= 1 && <= 6\n"+
                                "-> Number of decomposition levels forced to 1 (rather than {0})\n",
                                parameters.numresolution+1);
                    parameters.numresolution = 1;
                }else if(parameters.numresolution > 7){
                    _cinfo.Warn("JPEG 2000 Profile-4 (4k dc profile) requires:\n"+
                                "Number of decomposition levels >= 1 && <= 6\n"+
                                "-> Number of decomposition levels forced to 6 (rather than %d)\n",
                                parameters.numresolution+1);
                    parameters.numresolution = 7;
                }
                break;
            default :
                break;
            }

            /* Precincts */
            parameters.csty |= CP_CSTY.PRT;
            if (parameters.numresolution == 1)
            {
                parameters.res_spec = 1;
                parameters.prcw_init[0] = 128;
                parameters.prch_init[0] = 128;
            }
            else
            {
                parameters.res_spec = parameters.numresolution - 1;
                for (i = 0; i < parameters.res_spec; i++)
                {
                    parameters.prcw_init[i] = 256;
                    parameters.prch_init[i] = 256;
                }
            }

            /* The progression order shall be CPRL */
            parameters.prog_order = PROG_ORDER.CPRL;

            /* Progression order changes for 4K, disallowed for 2K */
            if (parameters.rsiz == J2K_PROFILE.CINEMA_4K) {
                if (parameters.POC == null)
                    parameters.POC = new ProgOrdChang[32];
                parameters.numpocs = (uint)Initialise4K_poc(parameters.POC,parameters.numresolution);
            } else {
                parameters.numpocs = 0;
            }

            /* Limited bit-rate */
            parameters.cp_disto_alloc = true;
            if (parameters.max_cs_size <= 0) {
                /* No rate has been introduced, 24 fps is assumed */
                parameters.max_cs_size = Constants.CINEMA_24_CS;
                _cinfo.Warn("JPEG 2000 Profile-3 and 4 (2k/4k dc profile) requires:\n"+
                            "Maximum 1302083 compressed bytes @ 24fps\n"+
                            "As no rate has been given, this limit will be used.\n");
            } else if (parameters.max_cs_size > Constants.CINEMA_24_CS) {
                _cinfo.Warn("JPEG 2000 Profile-3 and 4 (2k/4k dc profile) requires:\n"+
                            "Maximum 1302083 compressed bytes @ 24fps\n"+
                            "-> Specified rate exceeds this limit. Rate will be forced to 1302083 bytes.");
                parameters.max_cs_size = Constants.CINEMA_24_CS;
            }

            if (parameters.max_comp_size <= 0) {
                /* No rate has been introduced, 24 fps is assumed */
                parameters.max_comp_size = Constants.CINEMA_24_COMP;
                _cinfo.Warn("JPEG 2000 Profile-3 and 4 (2k/4k dc profile) requires:\n"+
                            "Maximum 1041666 compressed bytes @ 24fps\n"+
                            "As no rate has been given, this limit will be used.");
            }
            else if (parameters.max_comp_size > Constants.CINEMA_24_COMP)
            {
                _cinfo.Warn("JPEG 2000 Profile-3 and 4 (2k/4k dc profile) requires:\n"+
                            "Maximum 1041666 compressed bytes @ 24fps\n"+
                            ". Specified rate exceeds this limit. Rate will be forced to 1041666 bytes.");
                parameters.max_comp_size = Constants.CINEMA_24_COMP;
            }

            parameters.tcp_rates[0] = (image.numcomps * image.comps[0].w * image.comps[0].h * image.comps[0].prec)/
                    (parameters.max_cs_size * 8f * image.comps[0].dx * image.comps[0].dy);

        }

        //2.5 - opj_j2k_initialise_4K_poc
        int Initialise4K_poc(ProgOrdChang[] POC, int numres)
        {
            POC[0] = new ProgOrdChang();
            POC[0].tile = 1;
            POC[0].resno0 = 0;
            POC[0].compno0 = 0;
            POC[0].layno1 = 1;
            POC[0].resno1 = (uint)(numres - 1);
            POC[0].compno1 = 3;
            POC[0].prg1 = PROG_ORDER.CPRL;
            POC[1] = new ProgOrdChang();
            POC[1].tile = 1;
            POC[1].resno0 = (uint)(numres - 1);
            POC[1].compno0 = 0;
            POC[1].layno1 = 1;
            POC[1].resno1 = (uint)numres;
            POC[1].compno1 = 3;
            POC[1].prg1 = PROG_ORDER.CPRL;
            return 2;
        }

        //2.5 - opj_j2k_is_imf_compliant
        bool IsIMF_Compliant(CompressionParameters parameters, JPXImage image)
        {
            OPJ_UINT32 i;
            var profile = parameters.GetIMF_Profile();
            var mainlevel = parameters.GetIMF_Mainlevel();
            var sublevel = parameters.GetIMF_Sublevel();
            int NL = parameters.numresolution - 1;
            OPJ_UINT32 XTsiz = parameters.tile_size_on ? (OPJ_UINT32)
                                     parameters.tdx : image.x1;
            bool ret = true;

            /* Validate mainlevel */
            if (mainlevel > Constants.IMF_MAINLEVEL_MAX)
            {
                _cinfo.Warn("IMF profile require mainlevel <= 11.\n"+
                              "-> {0} is thus not compliant\n"+
                              "-> Non-IMF codestream will be generated",
                              mainlevel);
                ret = false;
            }
            else
            {
                /* Validate sublevel */
                if (sublevel > J2KTables.tabMaxSubLevelFromMainLevel[mainlevel])
                {
                    _cinfo.Warn("IMF profile require sublevel <= {0} for mainlevel = %d.\n"+
                                  "-> {1} is thus not compliant\n"+
                                  "-> Non-IMF codestream will be generated",
                                  J2KTables.tabMaxSubLevelFromMainLevel[mainlevel],
                                  mainlevel,
                                  sublevel);
                    ret = false;
                }
            }

            /* Number of components */
            if (image.numcomps > 3)
            {
                _cinfo.Warn("IMF profiles require at most 3 components.\n"+
                             "-> Number of components of input image ({0}) is not compliant\n"+
                             "-> Non-IMF codestream will be generated",
                             image.numcomps);
                ret = false;
            }

            if (image.x0 != 0 || image.y0 != 0)
            {
                _cinfo.Warn("IMF profiles require image origin to be at 0,0.\n"+
                             "-> {0},{1} is not compliant\n"+
                             "-> Non-IMF codestream will be generated",
                             image.x0, image.y0 != 0);
                ret = false;
            }

            if (parameters.tx0 != 0 || parameters.ty0 != 0)
            {
                _cinfo.Warn("IMF profiles require tile origin to be at 0,0.\n"+
                              "-> {0},{1} is not compliant\n"+
                              "-> Non-IMF codestream will be generated",
                              parameters.tx0, parameters.ty0);
                ret = false;
            }

            if (parameters.tile_size_on)
            {
                if (profile == J2K_PROFILE.IMF_2K ||
                    profile == J2K_PROFILE.IMF_4K ||
                    profile == J2K_PROFILE.IMF_8K)
                {
                    if ((OPJ_UINT32)parameters.tdx < image.x1 ||
                        (OPJ_UINT32)parameters.tdy < image.y1)
                    {
                        _cinfo.Warn("IMF 2K/4K/8K single tile profiles require tile to be greater or equal to image size.\n"+
                                     "-> {0},{1} is lesser than {2},{3}\n"+
                                     "-> Non-IMF codestream will be generated",
                                     parameters.tdx,
                                     parameters.tdy,
                                     image.x1,
                                     image.y1);
                        ret = false;
                    }
                }
                else
                {
                    if ((OPJ_UINT32)parameters.tdx >= image.x1 &&
                        (OPJ_UINT32)parameters.tdy >= image.y1)
                    {
                        /* ok */
                    }
                    else if (parameters.tdx == 1024 &&
                             parameters.tdy == 1024)
                    {
                        /* ok */
                    }
                    else if (parameters.tdx == 2048 &&
                             parameters.tdy == 2048 &&
                             (profile == J2K_PROFILE.IMF_4K ||
                              profile == J2K_PROFILE.IMF_8K))
                    {
                        /* ok */
                    }
                    else if (parameters.tdx == 4096 &&
                             parameters.tdy == 4096 &&
                             profile == J2K_PROFILE.IMF_8K)
                    {
                        /* ok */
                    }
                    else
                    {
                        _cinfo.Warn("IMF 2K_R/4K_R/8K_R single/multiple tile profiles "+
                                      "require tile to be greater or equal to image size,\n"+
                                      "or to be (1024,1024), or (2048,2048) for 4K_R/8K_R "+
                                      "or (4096,4096) for 8K_R.\n"+
                                      "-> {0},{1} is non conformant\n"+
                                      "-> Non-IMF codestream will be generated",
                                      parameters.tdx,
                                      parameters.tdy);
                        ret = false;
                    }
                }
            }

            // Bitdepth
            for (i = 0; i < image.numcomps; i++)
            {
                if (!(image.comps[i].prec >= 8 && image.comps[i].prec <= 16) ||
                     (image.comps[i].sgnd))
                {
                    var tmp_str = image.comps[i].sgnd ? "signed" : "unsigned";
                    _cinfo.Warn("IMF profiles require precision of each component to b in [8-16] bits unsigned"+
                                "-> At least component {0} of input image ({1} bits, {2}) is not compliant\n"+
                                "-> Non-IMF codestream will be generated",
                                i, image.comps[i].prec, tmp_str);
                    ret = false;
                }
            }

            // Sub-sampling
            for (i = 0; i < image.numcomps; i++)
            {
                if (i == 0 && image.comps[i].dx != 1)
                {
                    _cinfo.Warn("IMF profiles require XRSiz1 == 1. Here it is set to {0}.\n"+
                                  "-> Non-IMF codestream will be generated",
                                  image.comps[i].dx);
                    ret = false;
                }
                if (i == 1 && image.comps[i].dx != 1 && image.comps[i].dx != 2)
                {
                    _cinfo.Warn("IMF profiles require XRSiz2 == 1 or 2. Here it is set to {0}.\n"+
                                  "-> Non-IMF codestream will be generated",
                                  image.comps[i].dx);
                    ret = false;
                }
                if (i > 1 && image.comps[i].dx != image.comps[i - 1].dx)
                {
                    _cinfo.Warn("IMF profiles require XRSiz{0} to be the same as XRSiz2. "+
                                "Here it is set to {1} instead of {2}.\n"+
                                "-> Non-IMF codestream will be generated",
                                i + 1, image.comps[i].dx, image.comps[i - 1].dx);
                    ret = false;
                }
                if (image.comps[i].dy != 1)
                {
                    _cinfo.Warn("IMF profiles require YRsiz == 1. "+
                                "Here it is set to {0} for component %d.\n"+
                                "-> Non-IMF codestream will be generated",
                                image.comps[i].dy, i);
                    ret = false;
                }
            }

            /* Image size */
            switch (profile)
            {
                case J2K_PROFILE.IMF_2K:
                case J2K_PROFILE.IMF_2K_R:
                    if (((image.comps[0].w > 2048) | (image.comps[0].h > 1556)))
                    {
                        _cinfo.Warn("IMF 2K/2K_R profile require:\n"+
                                    "width <= 2048 and height <= 1556\n"+
                                    "-> Input image size {0} x {1} is not compliant\n"+
                                    "-> Non-IMF codestream will be generated",
                                    image.comps[0].w, image.comps[0].h);
                        ret = false;
                    }
                    break;
                case J2K_PROFILE.IMF_4K:
                case J2K_PROFILE.IMF_4K_R:
                    if (((image.comps[0].w > 4096) | (image.comps[0].h > 3112)))
                    {
                        _cinfo.Warn("IMF 4K/4K_R profile require:\n"+
                                    "width <= 4096 and height <= 3112\n"+
                                    "-> Input image size {0} x {1} is not compliant\n"+
                                    "-> Non-IMF codestream will be generated",
                                    image.comps[0].w, image.comps[0].h);
                        ret = false;
                    }
                    break;
                case J2K_PROFILE.IMF_8K:
                case J2K_PROFILE.IMF_8K_R:
                    if (((image.comps[0].w > 8192) | (image.comps[0].h > 6224)))
                    {
                        _cinfo.Warn("IMF 8K/8K_R profile require:\n"+
                                    "width <= 8192 and height <= 6224\n"+
                                    "-> Input image size {0} x {1} is not compliant\n"+
                                    "-> Non-IMF codestream will be generated",
                                    image.comps[0].w, image.comps[0].h);
                        ret = false;
                    }
                    break;
                default:
                    Debug.Assert(false);
                    return false;
            }

            if (parameters.roi_compno != -1)
            {
                _cinfo.Warn("IMF profile forbid RGN / region of interest marker.\n"+
                            "-> Compression parameters specify a ROI\n"+
                            "-> Non-IMF codestream will be generated");
                ret = false;
            }

            if (parameters.cblockw_init != 32 || parameters.cblockh_init != 32)
            {
                _cinfo.Warn("IMF profile require code block size to be 32x32.\n"+
                            "-> Compression parameters set it to {0}x{1}.\n"+
                            "-> Non-IMF codestream will be generated",
                            parameters.cblockw_init,
                            parameters.cblockh_init);
                ret = false;
            }

            if (parameters.prog_order != PROG_ORDER.CPRL)
            {
                _cinfo.Warn("IMF profile require progression order to be CPRL.\n"+
                            "-> Compression parameters set it to {0}.\n"+
                            "-> Non-IMF codestream will be generated",
                            parameters.prog_order);
                ret = false;
            }

            if (parameters.numpocs != 0)
            {
                _cinfo.Warn("IMF profile forbid POC markers.\n"+
                            "-> Compression parameters set {0} POC.\n"+
                            "-> Non-IMF codestream will be generated",
                            parameters.numpocs);
                ret = false;
            }

            /* Codeblock style: no mode switch enabled */
            if (parameters.mode != 0)
            {
                _cinfo.Warn("IMF profile forbid mode switch in code block style.\n"+
                            "-> Compression parameters set code block style to {0}.\n"+
                            "-> Non-IMF codestream will be generated",
                            parameters.mode);
                ret = false;
            }

            if (profile == J2K_PROFILE.IMF_2K ||
                profile == J2K_PROFILE.IMF_4K ||
                profile == J2K_PROFILE.IMF_8K)
            {
                /* Expect 9-7 transform */
                if (!parameters.irreversible)
                {
                    _cinfo.Warn("IMF 2K/4K/8K profiles require 9-7 Irreversible Transform.\n"+
                                "-> Compression parameters set it to reversible.\n"+
                                "-> Non-IMF codestream will be generated");
                    ret = false;
                }
            }
            else
            {
                /* Expect 5-3 transform */
                if (parameters.irreversible)
                {
                    _cinfo.Warn("IMF 2K/4K/8K profiles require 5-3 reversible Transform.\n"+
                                "-> Compression parameters set it to irreversible.\n"+
                                "-> Non-IMF codestream will be generated");
                    ret = false;
                }
            }

            /* Number of layers */
            if (parameters.tcp_numlayers != 1)
            {
                _cinfo.Warn("IMF 2K/4K/8K profiles require 1 single quality layer.\n"+
                            "-> Number of layers is {0}.\n"+
                            "-> Non-IMF codestream will be generated",
                            parameters.tcp_numlayers);
                ret = false;
            }

            /* Decomposition levels */
            switch (profile)
            {
                case J2K_PROFILE.IMF_2K:
                    if (!(NL >= 1 && NL <= 5))
                    {
                        _cinfo.Warn("IMF 2K profile requires 1 <= NL <= 5:\n"+
                                    "-> Number of decomposition levels is {0}.\n"+
                                    "-> Non-IMF codestream will be generated",
                                    NL);
                        ret = false;
                    }
                    break;
                case J2K_PROFILE.IMF_4K:
                    if (!(NL >= 1 && NL <= 6))
                    {
                        _cinfo.Warn("IMF 4K profile requires 1 <= NL <= 6:\n"+
                                    "-> Number of decomposition levels is {0}.\n"+
                                    "-> Non-IMF codestream will be generated",
                                    NL);
                        ret = false;
                    }
                    break;
                case J2K_PROFILE.IMF_8K:
                    if (!(NL >= 1 && NL <= 7))
                    {
                        _cinfo.Warn("IMF 8K profile requires 1 <= NL <= 7:\n"+
                                    "-> Number of decomposition levels is {0}.\n"+
                                    "-> Non-IMF codestream will be generated",
                                    NL);
                        ret = false;
                    }
                    break;
                case J2K_PROFILE.IMF_2K_R:
                    {
                        if (XTsiz >= 2048)
                        {
                            if (!(NL >= 1 && NL <= 5))
                            {
                                _cinfo.Warn("IMF 2K_R profile requires 1 <= NL <= 5 for XTsiz >= 2048:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        else if (XTsiz >= 1024)
                        {
                            if (!(NL >= 1 && NL <= 4))
                            {
                                _cinfo.Warn("IMF 2K_R profile requires 1 <= NL <= 4 for XTsiz in [1024,2048[:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        break;
                    }
                case J2K_PROFILE.IMF_4K_R:
                    {
                        if (XTsiz >= 4096)
                        {
                            if (!(NL >= 1 && NL <= 6))
                            {
                                _cinfo.Warn("IMF 4K_R profile requires 1 <= NL <= 6 for XTsiz >= 4096:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        else if (XTsiz >= 2048)
                        {
                            if (!(NL >= 1 && NL <= 5))
                            {
                                _cinfo.Warn("IMF 4K_R profile requires 1 <= NL <= 5 for XTsiz in [2048,4096[:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        else if (XTsiz >= 1024)
                        {
                            if (!(NL >= 1 && NL <= 4))
                            {
                                _cinfo.Warn("IMF 4K_R profile requires 1 <= NL <= 4 for XTsiz in [1024,2048[:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        break;
                    }
                case J2K_PROFILE.IMF_8K_R:
                    {
                        if (XTsiz >= 8192)
                        {
                            if (!(NL >= 1 && NL <= 7))
                            {
                                _cinfo.Warn("IMF 4K_R profile requires 1 <= NL <= 7 for XTsiz >= 8192:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        else if (XTsiz >= 4096)
                        {
                            if (!(NL >= 1 && NL <= 6))
                            {
                                _cinfo.Warn("IMF 4K_R profile requires 1 <= NL <= 6 for XTsiz in [4096,8192[:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        else if (XTsiz >= 2048)
                        {
                            if (!(NL >= 1 && NL <= 5))
                            {
                                _cinfo.Warn("IMF 4K_R profile requires 1 <= NL <= 5 for XTsiz in [2048,4096[:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        else if (XTsiz >= 1024)
                        {
                            if (!(NL >= 1 && NL <= 4))
                            {
                                _cinfo.Warn("IMF 4K_R profile requires 1 <= NL <= 4 for XTsiz in [1024,2048[:\n"+
                                            "-> Number of decomposition levels is {0}.\n"+
                                            "-> Non-IMF codestream will be generated",
                                            NL);
                                ret = false;
                            }
                        }
                        break;
                    }
                default:
                    break;
            }

            if (parameters.numresolution == 1)
            {
                if (parameters.res_spec != 1 ||
                    parameters.prcw_init[0] != 128 ||
                    parameters.prch_init[0] != 128)
                {
                    _cinfo.Warn("IMF profiles require PPx = PPy = 7 for NLLL band, else 8.\n"+
                                "-> Supplied values are different from that.\n"+
                                "-> Non-IMF codestream will be generated");
                    ret = false;
                }
            }
            else
            {
                for (i = 0; i < parameters.res_spec; i++)
                {
                    if (parameters.prcw_init[i] != 256 ||
                        parameters.prch_init[i] != 256)
                    {
                        _cinfo.Warn("IMF profiles require PPx = PPy = 7 for NLLL band, else 8.\n"+
                                    "-> Supplied values are different from that.\n"+
                                    "-> Non-IMF codestream will be generated");
                        ret = false;
                    }
                }
            }

            return ret;
        }

        //2.5 - opj_j2k_is_cinema_compliant
        bool IsCinemaCompliant(JPXImage image, J2K_PROFILE rsiz)
        {
            /* Number of components */
            if (image.numcomps != 3){
                _cinfo.Warn("JPEG 2000 Profile-3 (2k dc profile) requires:\n"+
                            "3 components"+
                            "-> Number of components of input image ({0}) is not compliant\n"+
                            "-> Non-profile-3 codestream will be generated\n",
                            image.numcomps);
                return false;
            }

            /* Bitdepth */
            for (int i = 0; i < image.numcomps; i++) {
                if ((image.comps[i].bpp != 12) | (image.comps[i].sgnd)){
                    _cinfo.Warn("JPEG 2000 Profile-3 (2k dc profile) requires:\n"+
                                "Precision of each component shall be 12 bits unsigned"+
                                "-> At least component {0} of input image ({1} bits, {2}) is not compliant\n"+
                                "-> Non-profile-3 codestream will be generated\n",
                                i,image.comps[i].bpp, image.comps[i].sgnd?"signed":"unsigned");
                    return false;
                }
            }

            /* Image size */
            switch (rsiz)
            {
                case J2K_PROFILE.CINEMA_2K:
                    if (((image.comps[0].w > 2048) | (image.comps[0].h > 1080))){
                        _cinfo.Warn("JPEG 2000 Profile-3 (2k dc profile) requires:\n"+
                                    "width <= 2048 and height <= 1080\n"+
                                    "-> Input image size {0} x {1} is not compliant\n"+
                                    "-> Non-profile-3 codestream will be generated\n",
                                    image.comps[0].w,image.comps[0].h);
                    return false;
                }
                break;
                case J2K_PROFILE.CINEMA_4K:
                    if (((image.comps[0].w > 4096) | (image.comps[0].h > 2160))){
                         _cinfo.Warn("JPEG 2000 Profile-4 (4k dc profile) requires:\n"+
                                     "width <= 4096 and height <= 2160\n"+
                                     "-> Image size {0} x {1} is not compliant\n"+
                                     "-> Non-profile-4 codestream will be generated\n",
                                     image.comps[0].w,image.comps[0].h);
                    return false;
                }
                break;
            default :
                break;
            }

            return true;
        }

        #endregion

        #region Validation

        //2.5 - opj_j2k_encoding_validation
        bool EncodingValidation()
        {
            bool l_is_valid = true;

            /* STATE checking */
            /* make sure the state is at 0 */
            l_is_valid &= (!_is_decompressor || _specific_param.decoder.state == J2K_STATUS.NONE);

            /* POINTER validation */
            /* make sure a p_j2k codec is present */
            //l_is_valid &= (p_j2k->m_procedure_list != 00);
            /* make sure a validation list is present */
            //l_is_valid &= (p_j2k->m_validation_list != 00);

            if (_cp.tcps[0].tccps[0].numresolutions <= 0 ||
                _cp.tcps[0].tccps[0].numresolutions > 32)
            {
                _cinfo.Error("Number of resolutions is too high in comparison to the size of tiles");
                return false;
            }

            if ((_cp.tdx) < (1u << (int) (_cp.tcps[0].tccps[0].numresolutions - 1)) || 
                (_cp.tdy) < (1u << (int) (_cp.tcps[0].tccps[0].numresolutions - 1)))
            {
                _cinfo.Error("Number of resolutions is too high in comparison to the size of tiles\n");
                return false;
            }

            /* PARAMETER VALIDATION */
            return l_is_valid;
        }

        //2.5 - opj_j2k_mct_validation
        bool MctValidation()
        {
            bool l_is_valid = true;
            int i, j;

            if (((int) _cp.rsiz & 0x8200) == 0x8200)
            {
                TileCodingParams[] l_tcp = _cp.tcps;
                Debug.Assert((int)(_cp.th * _cp.tw) == l_tcp.Length);

                for (i = 0; i < l_tcp.Length; ++i)
                {
                    var tcp = l_tcp[i];
                    if (tcp.mct == 2)
                    {
                        TileCompParams[] l_tccp = tcp.tccps;
                        l_is_valid &= (tcp.mct_coding_matrix != null);

                        for (j = 0; j < _private_image.numcomps; ++j)
                        {
                            l_is_valid &= (l_tccp[j].qmfbid & 1) != 1;
                        }
                    }
                }
            }

            return l_is_valid;
        }

        #endregion

        /// <summary>
        /// Encodes an image into a JPEG-2000 codestream
        /// </summary>
        /// <returns></returns>
        /// <remarks>2.5 - opj_j2k_encode</remarks>
        internal bool Encode()
        {
            bool reuse_data = false;
            uint max_tile_size = 0;
            uint current_tile_size;
            byte[] current_data = null;

            uint n_tiles = _cp.tw * _cp.th;
            if (n_tiles == 1)
            {
                reuse_data = true;

                //C# Snip SSE related code that checks if data is 16 byte aligned
                //   AFAICT there's no way of doing this on .net without resorting
                //   to unsafe code.
            }
            for (uint i = 0; i < n_tiles; i++)
            {
                if (!PreWriteTile(i))
                    return false;

                // If we only have one tile, then simply set tile component data equal to image component data
                // otherwise, allocate the data
                for (uint j = 0; j < _tcd.Image.numcomps; ++j)
                {
                    TcdTilecomp l_tilec = _tcd.TcdImage.tiles[0].comps[j];
                    if (reuse_data)
                    {
                        ImageComp img_comp = _tcd.Image.comps[j];
                        l_tilec.data = img_comp.data;
                        
                        //Not relevant for C# as we don't call free.
                        l_tilec.ownsData = false;
                    }
                    else
                    {
                        l_tilec.AllocTileComponentData();
                    }
                }

                current_tile_size = _tcd.GetEncoderInputBufferSize();
                if (!reuse_data)
                {
                    if (current_tile_size > max_tile_size)
                    {
                        current_data = new byte[current_tile_size];
                        max_tile_size = current_tile_size;
                    }
                    if (current_data == null)
                        return false;

                    _tcd.GetTileData(current_data);

                    if (!_tcd.CopyTileData(current_data, current_tile_size))
                    {
                        _cinfo.Error("Size mismatch between tile data and sent data.");
                        return false;
                    }
                }

                if (!PostWriteTile())
                    return false;
            }

            return true;
        }

        //This is a C# only function
        internal bool StartCompress(CIO cio)
        {
            return StartCompress(new BufferCIO(cio));
        }

        //2.5 - opj_j2k_start_compress
        internal bool StartCompress(BufferCIO bcio)
        {
            _bcio = bcio;

            //C# impl note: To avoide changes to the public API, copying of
            //   the private image is handled in our impl of opj_j2k_setup_encoder

            //opj_j2k_setup_encoding_validation
            // C# Skipping opj_j2k_build_encoder, it only returns true
            if (!EncodingValidation() || !MctValidation())
                return false;

            //C# - opj_j2k_setup_header_writing
            bool ret;
            {
                ret = InitInfo();
                WriteSOC();
                WriteSIZ();
                ret &= WriteCOD();
                ret &= WriteQCD();
                ret &= WriteAllCOC();
                ret &= WriteAllQCC();

                if (_specific_param.encoder.TLM)
                {
                    ret &= WriteTLM();

                    if (_cp.rsiz == J2K_PROFILE.CINEMA_4K)
                        WritePOC();
                }

                WriteRegion();

                if (_cp.comment != null)
                    WriteCOM();

                if ((_cp.rsiz & (J2K_PROFILE.PART2 | J2K_PROFILE.EXTENSION_MCT)) ==
                    (J2K_PROFILE.PART2 | J2K_PROFILE.EXTENSION_MCT))
                    WriteMCTdataGroup();

                if (_cstr_index != null)
                    GetEndHeader();

                CreateTCD();

                UpdateRates();
            }

            return ret;
        }

        /// <summary>
        /// Ends the compression procedures and possibiliy add data to be read after the
        /// codestream
        /// </summary>
        /// <remarks>2.5 - opj_j2k_end_compress</remarks>
        internal bool EndCompress()
        {
            WriteEOC();
            if (_specific_param.encoder.TLM)
                WriteUpdatedTLM();
            //WriteEPC(); //JPWL stuff
            EndEncoding();
            DestroyHeaderMemory();

            return true;
        }
        internal BufferCIO EndGetBCIO()
        {
            var bcio = _bcio;
            EndCompress();
            return bcio;
        }

        //2.5
        private void WriteEOC()
        {
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, 2);

            _bcio.Write(J2K_Marker.EOC);

            _bcio.Commit();
        }

        //2.5
        private void DestroyHeaderMemory()
        {
            _specific_param.encoder.header_tile_data = null;
            _bcio = null;
        }

        /// <summary>
        /// Creates a tile-coder encoder.
        /// </summary>
        /// <remarks>2.5 - opj_j2k_create_tcd</remarks>
        private void CreateTCD()
        {
            _tcd = new TileCoder(_cinfo, _private_image, _cp);
        }

        /// <summary>
        /// Gets the offset of the header.
        /// </summary>
        /// <remarks>2.5 - opj_j2k_get_end_header</remarks>
        private void GetEndHeader()
        {
            _cstr_index.main_head_end = _cio.Pos;
        }

        //2.5 - opj_j2k_end_encoding
        void EndEncoding()
        {
            _tcd = null;
            _specific_param.encoder.tlm_sot_offsets_buffer = null;
            _specific_param.encoder.encoded_tile_data = null;
        }

        //2.5 - opj_j2k_pre_write_tile
        bool PreWriteTile (uint p_tile_index)
        {
            if (p_tile_index != _current_tile_number) {
                _cinfo.Error("The given tile index does not match." );
                return false;
            }

            _cinfo.Info("tile number {0} / {1}", _current_tile_number + 1, _cp.tw * _cp.th);

            _specific_param.encoder.current_tile_part_number = 0;
            _tcd.CurTotnumTp = _cp.tcps[p_tile_index].n_tile_parts;
            _specific_param.encoder.current_poc_tile_part_number = 0;

            return _tcd.InitEncodeTile(_current_tile_number); ;
        }

        //2.5 - opj_j2k_post_write_tile
        bool PostWriteTile()
        {            
            OPJ_UINT32 n_bytes_written;

            //C# impl note.
            //This is the buffer we will be writing into. Org. impl passes
            //this buffer around as a paremeter, we set it on the _bcio object.
            byte[] current_data = _specific_param.encoder.encoded_tile_data;
            uint available_data = (uint)current_data.Length;
            _bcio.SetBuffer(ref current_data, 0);

            if (!WriteFirstTilePart(out n_bytes_written, available_data))
                return false;

            available_data -= n_bytes_written;
            
            if (!WriteAllTileParts(out n_bytes_written, available_data))
                return false;
            available_data -= n_bytes_written;

            Debug.Assert(current_data.Length - available_data == _bcio.BufferPos);
            _bcio.Commit();

            _current_tile_number++;

            return true;
        }

        //2.5 - opj_j2k_write_first_tile_part
        bool WriteFirstTilePart(out OPJ_UINT32 data_written, uint total_data_size)
        {
            //Debug.Assert(avalible_data == _cio.BufferBytesLeft);
            OPJ_UINT32 current_n_bytes_written;
            uint n_bytes_written;

            _tcd.CurPino = 0;

            // Get number of tile parts
            _specific_param.encoder.current_poc_tile_part_number = 0;

            var begin_data = _bcio.BufferPos;
            if (!WriteSOT(out current_n_bytes_written, total_data_size))
            {
                data_written = 0;
                return false;
            }

            total_data_size -= current_n_bytes_written;
            n_bytes_written = current_n_bytes_written;

            if (!_cp.IsCinema)
            {
                if (_cp.tcps[_current_tile_number].POC)
                {
                    WritePOC_InMemory(out current_n_bytes_written);
                    n_bytes_written += current_n_bytes_written;
                    total_data_size -= current_n_bytes_written;
                }
            }

            if (!WriteSOD(_tcd, out current_n_bytes_written, total_data_size))
            {
                data_written = n_bytes_written;
                return false;
            }
            
            n_bytes_written += current_n_bytes_written;
            data_written = n_bytes_written;

            //Writing Psot in SOT marker
            _bcio.BufferPos = begin_data + 6;
            _bcio.Write(n_bytes_written);
            _bcio.BufferPos = begin_data + n_bytes_written;

            if (_specific_param.encoder.TLM)
            {
                UpdateTLM(n_bytes_written);
            }

            return true;
        }

        //2.5 - opj_j2k_write_all_tile_parts
        bool WriteAllTileParts(out uint data_written, OPJ_UINT32 total_data_size)
        {
            // Gets the number of tile parts
            OPJ_UINT32 tot_num_tp = GetNumTP(0, _current_tile_number);
            OPJ_UINT32 current_n_bytes_written;
            data_written = 0;

            // Writes remaining tile partss
            _specific_param.encoder.current_tile_part_number++;
            for (uint tilepartno = 1; tilepartno < tot_num_tp; tilepartno++)
            {
                _specific_param.encoder.current_poc_tile_part_number = tilepartno;
                OPJ_UINT32 part_tile_size = 0;
                var begin_data = _bcio.BufferPos;

                WriteSOT(out current_n_bytes_written, total_data_size);
                data_written += current_n_bytes_written;
                part_tile_size += current_n_bytes_written;
                total_data_size -= current_n_bytes_written;

                Debug.Assert(total_data_size == _bcio.BufferBytesLeft);
                if (!WriteSOD(_tcd, out current_n_bytes_written, total_data_size))
                    return false;

                Debug.Assert(current_n_bytes_written == total_data_size - _bcio.BufferBytesLeft);
                data_written += current_n_bytes_written;
                part_tile_size += current_n_bytes_written;
                total_data_size -= current_n_bytes_written;

                //Writing Psot in SOT marker
                _bcio.BufferPos = begin_data + 6;
                _bcio.Write(part_tile_size);
                _bcio.BufferPos = begin_data + part_tile_size;

                if (_specific_param.encoder.TLM)
                    UpdateTLM(part_tile_size);

                _specific_param.encoder.current_tile_part_number++;
            }

            var tcp = _cp.tcps[_current_tile_number];

            for (uint pino = 1; pino <= tcp.numpocs; pino++)
            {
                _tcd.CurPino = pino;

                //Get number of tile parts
                tot_num_tp = GetNumTP(0, _current_tile_number);

                for (uint tilepartno = 0; tilepartno < tot_num_tp; tilepartno++)
                {
                    _specific_param.encoder.current_poc_tile_part_number = tilepartno;
                    OPJ_UINT32 part_tile_size = 0;
                    var begin_data = _bcio.BufferPos;

                    WriteSOT(out current_n_bytes_written, total_data_size);
                    data_written += current_n_bytes_written;
                    part_tile_size += current_n_bytes_written;
                    total_data_size -= current_n_bytes_written;

                    Debug.Assert(total_data_size == _bcio.BufferBytesLeft);
                    if (!WriteSOD(_tcd, out current_n_bytes_written, total_data_size))
                        return false;

                    Debug.Assert(current_n_bytes_written == total_data_size - _bcio.BufferBytesLeft);
                    data_written += current_n_bytes_written;
                    part_tile_size += current_n_bytes_written;
                    total_data_size -= current_n_bytes_written;

                    //Writing Psot in SOT marker
                    _bcio.BufferPos = begin_data + 6;
                    _bcio.Write(part_tile_size);
                    _bcio.BufferPos = begin_data + part_tile_size;

                    if (_specific_param.encoder.TLM)
                        UpdateTLM(part_tile_size);

                    _specific_param.encoder.current_tile_part_number++;
                }
            }

            return true;
        }

        //2.5 - opj_j2k_init_info
        bool InitInfo()
        {
            return CalculateTP(out _specific_param.encoder.total_tile_parts);
        }

        /// <summary>
        /// Writes the SOC marker (Start Of Codestream)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_soc</remarks>
        void WriteSOC()
        {
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, 2);
            _bcio.Write(J2K_Marker.SOC);
            _bcio.Commit();
        }

        /// <summary>
        /// Writes a SIZ marker (image and tile size)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_siz</remarks>
        void WriteSIZ()
        {
            //Calculates how much data this marker needs.
            uint size_len = 40 + 3 * _private_image.numcomps;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, size_len);

            _bcio.Write(J2K_Marker.SIZ);
            _bcio.WriteUShort(size_len - 2);
            _bcio.WriteUShort((ushort)_cp.rsiz);
            _bcio.Write(_private_image.x1);
            _bcio.Write(_private_image.y1);
            _bcio.Write(_private_image.x0);
            _bcio.Write(_private_image.y0);
            _bcio.Write(_cp.tdx);
            _bcio.Write(_cp.tdy);
            _bcio.Write(_cp.tx0);
            _bcio.Write(_cp.ty0);
            _bcio.WriteUShort(_private_image.numcomps);
            for (int i = 0; i < _private_image.numcomps; i++)
            {
                _bcio.WriteByte(_private_image.comps[i].prec - 1U + ((_private_image.comps[i].sgnd ? 1U : 0U) << 7));
                _bcio.WriteByte(_private_image.comps[i].dx);
                _bcio.WriteByte(_private_image.comps[i].dy);
            }

            _bcio.Commit();
        }

        /// <summary>
        /// Writes the COD marker (Coding style default)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_cod</remarks>
        bool WriteCOD()
        {
            //Calculates how much data this marker needs.
            uint code_size = 9u + GetSPCodSPCocSize(_current_tile_number, 0);
            var remaining_size = code_size;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, code_size);

            var tcp = _cp.tcps[_current_tile_number];

            _bcio.Write(J2K_Marker.COD);
            _bcio.WriteUShort(code_size - 2);
            _bcio.WriteByte((int)tcp.csty);
            _bcio.WriteByte((int)tcp.prg);
            _bcio.WriteUShort(tcp.numlayers);
            _bcio.WriteByte(tcp.mct);

            remaining_size -= 9;

            if (!WriteSPCodSPCoc(0, ref remaining_size) || remaining_size != 0)
            {
                _cinfo.Error("Error writing COD marker");
                return false;
            }

            _bcio.Commit();

            return true;
        }

        /// <summary>
        /// Writes a SPCod or SPCoc element, i.e. the coding style of a given component of a tile.
        /// </summary>
        /// <param name="compno">the component number to output</param>
        /// <returns>Bytes written</returns>
        /// <remarks>2.5 - opj_j2k_write_SPCod_SPCoc</remarks>
        bool WriteSPCodSPCoc(int compno, ref uint header_size)
        {
            var tcp = _cp.tcps[_current_tile_number];
            var tccp = tcp.tccps[compno];

            if (header_size < 5)
            {
                _cinfo.Error("Error writing SPCod SPCoc element");
                return false;
            }

            _bcio.WriteByte(tccp.numresolutions - 1);
            _bcio.WriteByte(tccp.cblkw - 2);
            _bcio.WriteByte(tccp.cblkh - 2);
            _bcio.WriteByte((int)tccp.cblksty);
            _bcio.WriteByte(tccp.qmfbid);

            header_size -= 5;

            if ((tccp.csty & CP_CSTY.PRT) != 0)
            {
                if (header_size < tccp.numresolutions)
                {
                    _cinfo.Error("Error writing SPCod SPCoc element");
                    return false;
                }

                for (int i = 0; i < tccp.numresolutions; i++)
                    _bcio.WriteByte(tccp.prcw[i] + (tccp.prch[i] << 4));

                header_size -= tccp.numresolutions;
            }

            return true;
        }

        /// <summary>
        /// Writes QCC marker for each component
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_all_qcc</remarks>
        bool WriteAllQCC()
        {
            for (uint compno = 1; compno < _private_image.numcomps; compno++)
            {
                // cod is first component of first tile
                if (!CompareQCC(0, compno))
                {
                    WriteQCC(compno);
                }
            }

            return true;
        }

        /// <summary>
        /// Compare QCC markers (quantization component)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_compare_qcc</remarks>
        bool CompareQCC(uint first_comp_no, uint second_comp_no)
        {
            return CompareSQcdSQcc(_current_tile_number, first_comp_no, second_comp_no);
        }

        /// <summary>
        /// Compares 2 SQcd or SQcc element, i.e. the quantization 
        /// values of a band in the QCD or QCC
        /// </summary>
        /// <remarks>2.5 - opj_j2k_compare_SQcd_SQcc</remarks>
        bool CompareSQcdSQcc(uint tile_no, uint first_comp_no, uint second_comp_no)
        {
            uint num_bands;

            var tcp = _cp.tcps[tile_no];
            var tccp0 = tcp.tccps[first_comp_no];
            var tccp1 = tcp.tccps[second_comp_no];

            if (tccp0.qntsty != tccp1.qntsty ||
                tccp0.numgbits != tccp1.numgbits)
                return false;

            if (tccp0.qntsty == CCP_QNTSTY.SIQNT)
                num_bands = 1;
            else
            {
                num_bands = tccp0.numresolutions * 3 - 2;
                if (num_bands != tccp1.numresolutions * 3 - 2)
                    return false;
            }

            for(int band_no = 0; band_no < num_bands; band_no++)
            {
                if (tccp0.stepsizes[band_no].expn != tccp1.stepsizes[band_no].expn)
                    return false;
            }
            if (tccp0.qntsty == CCP_QNTSTY.SIQNT)
            {
                for (int band_no = 0; band_no < num_bands; band_no++)
                {
                    if (tccp0.stepsizes[band_no].mant != tccp1.stepsizes[band_no].mant)
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes COC marker for each component
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_all_coc</remarks>
        bool WriteAllCOC()
        {
            for(uint compno = 1; compno < _private_image.numcomps; compno++)
            {
                // cod is first component of first tile
                if (!CompareCOC(0, compno))
                {
                    WriteCOC(compno);
                }
            }

            return true;
        }

        /// <summary>
        /// Compares 2 COC markers (Coding style component)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_compare_coc</remarks>
        bool CompareCOC(uint first_comp_no, uint second_comp_no)
        {
            var tcp = _cp.tcps[_current_tile_number];
            if (tcp.tccps[first_comp_no].csty != tcp.tccps[second_comp_no].csty)
                return false;

            return CompareSPCodSPCoc(_current_tile_number, first_comp_no, second_comp_no);
        }

        /// <summary>
        ///  Compare 2 a SPCod/ SPCoc elements, i.e. the coding style of a given component of a tile.
        /// </summary>
        /// <remarks>2.5 - opj_j2k_compare_SPCod_SPCoc</remarks>
        bool CompareSPCodSPCoc(uint tile_no, uint first_comp_no, uint second_comp_no)
        {
            var tcp = _cp.tcps[tile_no];
            var tccp0 = tcp.tccps[first_comp_no];
            var tccp1 = tcp.tccps[second_comp_no];

            if (tccp0.numresolutions != tccp1.numresolutions ||
                tccp0.cblkw != tccp1.cblkw ||
                tccp0.cblkh != tccp1.cblkh ||
                tccp0.cblksty != tccp1.cblksty ||
                tccp0.qmfbid != tccp1.qmfbid) 
                return false;

            if ((tccp0.csty & CP_CSTY.PRT) != (tccp1.csty & CP_CSTY.PRT))
                return false;

            for(uint i=0; i <tccp0.numresolutions; i++)
            {
                if (tccp0.prcw[i] != tccp1.prcw[i] ||
                    tccp0.prch[i] != tccp1.prch[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Writes the COC marker (Coding style component)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_coc</remarks>
        void WriteCOC(uint compno)
        {
            uint comp_room = (_private_image.numcomps <= 256) ? 1u : 2u;

            uint coc_size = 5 + comp_room + GetSPCodSPCocSize(_current_tile_number, compno);
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, coc_size);

            WriteCOC_InMemory(compno);
            _bcio.Commit();
        }

        /// <summary>
        /// Writes the COC marker (Coding style component)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_coc_in_memory</remarks>
        uint WriteCOC_InMemory(uint comp_no)
        {
            var tcp = _cp.tcps[_current_tile_number];
            uint comp_room = (_private_image.numcomps <= 256) ? 1u : 2u;
            uint coc_size = 5 + comp_room + GetSPCodSPCocSize(_current_tile_number, comp_no);
            uint remaining_size = coc_size;

            _bcio.Write(J2K_Marker.COC);
            _bcio.WriteUShort(coc_size - 2);
            _bcio.Write(comp_no, (int)comp_room);
            _bcio.WriteByte((int)tcp.tccps[comp_no].csty);
            remaining_size -= 5 + comp_room;
            WiteSPCodSPCoc(_current_tile_number, 0, ref remaining_size);
            return coc_size;
        }

        /// <summary>
        /// Writes a SPCod or SPCoc element, i.e. the coding style of a given component of a tile.
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_SPCod_SPCoc</remarks>
        bool WiteSPCodSPCoc(uint tile_no, uint comp_no, ref uint header_size)
        {
            var tcp = _cp.tcps[_current_tile_number];
            var tccp = tcp.tccps[comp_no];

            if (header_size < 5)
            {
                _cinfo.Error("Error writing SPCod SPCoc element");
                return false;
            }

            _bcio.WriteByte(tccp.numresolutions - 1);
            _bcio.WriteByte(tccp.cblkw - 2);
            _bcio.WriteByte(tccp.cblkh - 2);
            _bcio.WriteByte((byte)tccp.cblksty);
            _bcio.WriteByte(tccp.qmfbid);

            header_size -= 5;

            if ((tccp.csty & CP_CSTY.PRT) != 0)
            {
                if (header_size < tccp.numresolutions)
                {
                    _cinfo.Error("Error writing SPCod SPCoc element");
                    return false;
                }

                for(uint i=0; i < tccp.numresolutions; i++)
                {
                    _bcio.WriteByte(tccp.prcw[i] + (tccp.prch[i] << 4));
                }

                header_size -= tccp.numresolutions;
            }

            return true;
        }

        /// <summary>
        /// Writes the QCD marker (quantization default)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_qcd</remarks>
        bool WriteQCD()
        {
            uint qcd_size = 4 + GetSQcdSQccSize(_current_tile_number, 0);
            uint remaining_size = qcd_size;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, qcd_size);

            _bcio.Write(J2K_Marker.QCD);
            _bcio.WriteUShort(qcd_size - 2);
            remaining_size -= 4;

            if (!WriteSQcdSQcc(_current_tile_number, 0, ref remaining_size) || remaining_size != 0)
            {
                _cinfo.Error("Error writing QCD marker");
                return false;
            }
            _bcio.Commit();

            return true;
        }

        uint GetSQcdSQccSize(uint tileno, uint compno)
        {
            var tcp = _cp.tcps[tileno];
            var tccp = tcp.tccps[compno];

            uint num_bands = (tccp.qntsty == CCP_QNTSTY.SIQNT) ? 1 :
                             tccp.numresolutions * 3 - 2;

            if (tccp.qntsty == CCP_QNTSTY.NOQNT)
                return 1 + num_bands;
            else
                return 1 + 2 * num_bands;
        }

        /// <summary>
        /// Writes a SQcd or SQcc element, i.e. the quantization values of a band in the QCD or QCC.
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_SQcd_SQcc</remarks>
        bool WriteSQcdSQcc(uint tileno, uint compno, ref uint header_size)
        {
            var tcp = _cp.tcps[tileno];
            var tccp = tcp.tccps[compno];
            uint l_header_size;

            uint numbands = tccp.qntsty == CCP_QNTSTY.SIQNT ? 1 : tccp.numresolutions * 3 - 2;
            if (tccp.qntsty == CCP_QNTSTY.NOQNT)
            {
                l_header_size = 1 + numbands;

                if (header_size < l_header_size)
                {
                    _cinfo.Error("Error writing SQcd SQcc element");
                    return false;
                }

                _bcio.WriteByte((int)tccp.qntsty + ((int)tccp.numgbits << 5));

                for (int bandno = 0; bandno < numbands; bandno++)
                {
                    int expn = tccp.stepsizes[bandno].expn;
                    _bcio.WriteByte(expn << 3);
                }
            }
            else
            {
                l_header_size = 1 + 2 * numbands;

                if (header_size < l_header_size)
                {
                    _cinfo.Error("Error writing SQcd SQcc element");
                    return false;
                }

                _bcio.WriteByte((int)tccp.qntsty + ((int)tccp.numgbits << 5));

                for (int bandno = 0; bandno < numbands; bandno++)
                {
                    int expn = tccp.stepsizes[bandno].expn;
                    int mant = tccp.stepsizes[bandno].mant;
                    _bcio.WriteUShort((expn << 11) + mant);
                }
            }

            header_size -= l_header_size;
            return true;
        }

        /// <summary>
        /// Writes the QCC marker (quantization component)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_qcc</remarks>
        void WriteQCC(uint compno)
        {
            uint qcc_size = 5 + GetSQcdSQccSize(_current_tile_number, compno);
            qcc_size += _private_image.numcomps <= 256 ? 0u : 1u;
            uint remaining_size = qcc_size;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, qcc_size);

            WriteQCC_InMemory(compno, ref remaining_size);
            _bcio.Commit();
        }

        void WriteQCC_InMemory(uint compno, ref uint header_size)
        {
            uint qcc_size = 6 + GetSQcdSQccSize(_current_tile_number, compno);
            uint remaining_size = qcc_size;

            _bcio.Write(J2K_Marker.QCC);
            if (_private_image.numcomps <= 256)
            {
                --qcc_size;
                _bcio.WriteUShort(qcc_size - 2);
                _bcio.WriteByte(compno);

                // In the case only one byte is sufficient the last byte allocated
                // is useless -> still do -6 for available
                remaining_size -= 6;
            }
            else
            {
                _bcio.WriteUShort(qcc_size - 2);
                _bcio.WriteUShort(compno);

                remaining_size -= 6;
            }

            WriteSQcdSQcc(_current_tile_number, compno, ref remaining_size);

            header_size = qcc_size;
        }

        /// <summary>
        /// Writes the CBD marker (Component bit depth definition)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_cbd</remarks>
        void WriteCBD()
        {
            uint cbd_size = 6 + _private_image.numcomps;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, cbd_size);

            _bcio.Write(J2K_Marker.CBD);
            _bcio.Write(cbd_size - 2);

            //Writes out the number of Component Bit Depths
            _bcio.WriteUShort(_private_image.numcomps);

            var comps = _private_image.comps;
            for(int i=0; i < _private_image.numcomps; i++)
            {
                var comp = comps[i];

                //Component bit depth
                _bcio.WriteByte(((comp.sgnd ? 1U : 0U) << 7) | (comp.prec - 1));
            }

            _bcio.Commit();
        }

        /// <summary>
        /// Writes the CBD-MCT-MCC-MCO markers (Multi components transform)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_mct_data_group</remarks>
        void WriteMCTdataGroup()
        {
            WriteCBD();

            var tcp = _cp.tcps[_current_tile_number];
            var mct_records = tcp.mct_records;

            for (int i = 0; i < tcp.n_mct_records; i++)
                WriteMCTRecord(mct_records[i]);

            var mcc_records = tcp.mcc_records;

            for (int i = 0; i < tcp.n_mct_records; i++)
                WriteMCCRecord(mcc_records[i]);

            WriteMCO();
        }

        /// <summary>
        /// Writes the MCT marker (Multiple Component Transform)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_mct_record</remarks>
        void WriteMCTRecord(MctData mct_record)
        {
            uint mct_size = 10u + (uint)mct_record.data_size;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, mct_size);

            _bcio.Write(J2K_Marker.MCT);
            _bcio.WriteUShort(mct_size - 2);

            _bcio.WriteUShort(0);

            // only one marker atm
            var tmp = (mct_record.index & 0xFF) | ((byte)mct_record.array_type << 8) | ((byte)mct_record.element_type << 10);
            _bcio.WriteUShort(tmp);

            _bcio.WriteUShort(0);

            var data = mct_record.data.ToArray();
            _bcio.Write(data, 0, data.Length);

            _bcio.Commit();
        }

        /// <summary>
        /// Writes the MCC marker (Multiple Component Collection)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_mcc_record</remarks>
        void WriteMCCRecord(SimpleMccDecorrelationData mcc_record)
        {
            uint mask, nbytes;
            if (mcc_record.n_comps > 255)
            {
                mask = 0x8000;
                nbytes = 2;
            }
            else
            {
                mask = 0;
                nbytes = 1;
            }
            uint mcc_size = mcc_record.n_comps * 2u * nbytes + 19u;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, mcc_size);

            _bcio.Write(J2K_Marker.MCC);
            _bcio.WriteUShort(mcc_size - 2);

            //Zmcc
            _bcio.WriteUShort(0);

            //Imcc
            _bcio.WriteByte(mcc_record.index);
            
            //Ymcc
            _bcio.WriteUShort(0);

            //Qmcc: number of collections
            _bcio.WriteUShort(1);

            //Xmcci type of component transformation -> array based decorrelation
            _bcio.WriteByte(1);

            //Nmcci number of input components involved and size for each component offset = 8 bits
            _bcio.WriteUShort(mcc_record.n_comps | mask);

            //Cmccij Component offset
            for (int i = 0; i < mcc_record.n_comps; i++)
                _bcio.Write(i, (int)nbytes);

            //Mmcci number of input components involved and size for each component offset = 8 bits
            _bcio.WriteUShort(mcc_record.n_comps | mask);

            //Wmccij Component offset
            for (int i = 0; i < mcc_record.n_comps; i++)
                _bcio.Write(i, (int)nbytes);

            uint tmcc = (mcc_record.is_irreversible ? 0u : 1u) << 16;

            if (mcc_record.decorrelation_array != null)
                tmcc |= (uint)mcc_record.decorrelation_array.Deref.index;

            if (mcc_record.offset_array != null)
                tmcc |= (uint)mcc_record.offset_array.Deref.index << 8;

            //Tmcci : use MCT defined as number 1 and irreversible array based.
            _bcio.Write(tmcc, 3);

            _bcio.Commit();
        }

        /// <summary>
        /// Writes the MCO marker (Multiple component transformation ordering)
        /// </summary>
        /// <remarks>2.5 - </remarks>
        void WriteMCO()
        {
            var tcp = _cp.tcps[_current_tile_number];
            uint mco_size = 5 + tcp.n_mcc_records;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, mco_size);

            _bcio.Write(J2K_Marker.MCO);
            _bcio.WriteUShort(mco_size - 2);

            //Nmco : only one tranform stage
            _bcio.WriteByte(tcp.n_mcc_records);

            var mcc_records = tcp.mcc_records;
            for (int i = 0; i < tcp.n_mcc_records; i++)
            {
                var mcc_record = mcc_records[i];
                _bcio.WriteByte(mcc_record.index);
            }

            _bcio.Commit();
        }

        /// <summary>
        /// Writes regions of interests
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_regions</remarks>
        void WriteRegion()
        {
            var tcps = _cp.tcps;
            for (uint compno = 0; compno < _private_image.numcomps; compno++)
            {
                var tcp = tcps[0];
                if (tcp.tccps[compno].roishift != 0)
                    WriteRGN(0, compno, _private_image.numcomps);
            }
        }

        /// <summary>
        /// Writes the RGN marker (Region Of Interest)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_rgn</remarks>
        void WriteRGN(int tileno, uint compno, uint ncomps)
        {
            var tcp = _cp.tcps[tileno];
            uint comp_room = ncomps <= 256 ? 1u : 2u;

            uint rng_size = 6 + comp_room;
            _bcio.Write(J2K_Marker.RGN);
            _bcio.WriteUShort(rng_size - 2);
            _bcio.Write(compno, (int) comp_room);
            _bcio.WriteByte(0);
            _bcio.WriteByte(tcp.tccps[compno].roishift);
            _bcio.Commit();
        }

        /// <summary>
        /// Writes the COM marker (comment)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_com</remarks>
        void WriteCOM()
        {
            int com_size = _cp.comment.Length + 6;
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, (uint)com_size);

            _bcio.Write(J2K_Marker.COM);
            _bcio.WriteUShort(com_size - 2);

            //General use (IS 8859-15:1999 (Latin) values)
            _bcio.WriteUShort(1);

            _bcio.Write(_cp.comment, 0, _cp.comment.Length);
            _bcio.Commit();
            
        }

        /// <summary>
        /// Merges all PPT markers read (Packed packet headers, tile-part header)
        /// </summary>
        /// <param name="tcp">The tile</param>
        /// <remarks>
        /// 2.5 - opj_j2k_merge_ppt
        /// </remarks>
        private bool MergePPT(TileCodingParams tcp)
        {
            OPJ_UINT32 i, ppt_data_size;

            // preconditions
            Debug.Assert(tcp != null);

            if (tcp.ppt_data != null)
            {
                _cinfo.Error("MergePPT() has already been called");
                return false;
            }

            if (!tcp.ppt)
            {
                return true;
            }

            ppt_data_size = 0;
            for (i = 0U; i < tcp.ppt_markers_count; ++i)
            {
                ppt_data_size +=
                    tcp.ppt_markers[i].DataSize; // can't overflow, max 256 markers of max 65536 bytes
            }

            tcp.ppt_data = new byte[ppt_data_size];
            tcp.ppt_buffer = 0;
            tcp.ppt_len = (int) ppt_data_size;
            ppt_data_size = 0U;
            for (i = 0U; i < tcp.ppt_markers_count; ++i)
            {
                if (tcp.ppt_markers[i].Data != null)
                { // standard doesn't seem to require contiguous Zppt
                    Buffer.BlockCopy(tcp.ppt_markers[i].Data, 0,
                        tcp.ppt_data, tcp.ppt_buffer + (int) ppt_data_size, 
                        (int) tcp.ppt_markers[i].DataSize);
                    ppt_data_size +=
                        tcp.ppt_markers[i].DataSize; // can't overflow, max 256 markers of max 65536 bytes

                    tcp.ppt_markers[i].Data = null;
                }
            }

            tcp.ppt_markers_count = 0;
            tcp.ppt_markers = null;

            tcp.ppt_data_start = tcp.ppt_buffer;
            tcp.ppt_data_size = tcp.ppt_len;
            return true;
        }

        /// <summary>
        /// Writes one or more PLT markers
        /// </summary>
        /// <returns></returns>
        bool WritePLT(TcdMarkerInfo marker_info, byte[] d, out uint data_written)
        {
            byte Zplt = 0;
            int data = 0; //Pointer to d
            int data_Lplt = data + 2; //Pointer to d

            BufferCIO.Write(d, data, J2K_Marker.PLT);
            data += 2;

            // Reserve space for Lplt
            data += 2;

            d[data] = Zplt;
            data += 1;

            ushort Lplt = 3;

            byte[] var_bytes = new byte[5];

            for (int i =0; i < marker_info.packet_count; i++)
            {
                byte var_bytes_size = 0;
                uint packet_size = marker_info.p_packet_size[i];

                // Packet size written in variable-length way, starting with LSB
                var_bytes[var_bytes_size] = (byte)(packet_size & 0x7f);
                var_bytes_size++;
                packet_size >>= 7;
                while (packet_size > 0)
                {
                    var_bytes[var_bytes_size] = (byte)((packet_size & 0x7f) | 0x80);
                    var_bytes_size++;
                    packet_size >>= 7;
                }

                // Check if that can fit in the current PLT marker. If not, finish
                // current one, and start a new one
                if (Lplt + var_bytes_size > 65535)
                {
                    if (Zplt == 255)
                    {
                        _cinfo.Error("More than 255 PLT markers would be needed for current tile-part !");
                        data_written = 0;
                        return false;
                    }

                    // Patch Lplt
                    BufferCIO.WriteUShort(d, data_Lplt, Lplt);

                    // Start new segment
                    BufferCIO.Write(d, data, J2K_Marker.PLT);
                    data += 2;

                    // Reserve space for Lplt
                    data_Lplt = data;
                    data += 2;

                    Zplt++;
                    d[data] = Zplt;
                    data += 1;

                    Lplt = 3;
                }

                Lplt = (ushort)(Lplt + var_bytes_size);

                // Serialize variable-length packet size, starting with MSB
                for (; var_bytes_size > 0; --var_bytes_size)
                {
                    d[data] = var_bytes[var_bytes_size - 1];
                    data += 1;
                }
            }

            data_written = (uint)data;

            // Patch Lplt
            BufferCIO.WriteUShort(d, data_Lplt, Lplt);

            return true;
        }

        /// <summary>
        /// Writes the TLM marker (Tile Length Marker)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_tlm</remarks>
        bool WriteTLM()
        {
            if (_specific_param.encoder.total_tile_parts > 10921)
            {
                _cinfo.Error("A maximum of 10921 tile-parts are supported currently " +
                             "when writing TLM marker");
                return false;
            }
            uint size_per_tile_part;
            if (_specific_param.encoder.total_tile_parts <= 255)
            {
                size_per_tile_part = 5;
                _specific_param.encoder.Ttlmi_is_byte = true;
            }
            else
            {
                size_per_tile_part = 6;
                _specific_param.encoder.Ttlmi_is_byte = false;
            }

            uint tlm_size = 2 + 4 + (size_per_tile_part * 
                                     _specific_param.encoder.total_tile_parts);
            Array.Clear(_specific_param.encoder.header_tile_data, 0, 
                Math.Min((int)tlm_size, _specific_param.encoder.header_tile_data.Length));
            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, tlm_size);

            _specific_param.encoder.tlm_start = _bcio.Pos;

            _bcio.Write(J2K_Marker.TLM);
            _bcio.WriteUShort(tlm_size - 2);
            _bcio.WriteByte(0);
            _bcio.WriteByte(size_per_tile_part == 5 ? 0x50 : 0x60);
            _bcio.Skip((int)tlm_size - 6);
            _bcio.Commit();

            return true;
        }

        //2.5 - opj_j2k_write_updated_tlm
        void WriteUpdatedTLM()
        {
            uint size_per_tile_part = _specific_param.encoder.Ttlmi_is_byte ? 5u : 6u;
            OPJ_UINT32 tlm_size = size_per_tile_part * _specific_param.encoder.total_tile_parts;
            long tlm_position = 6 + _specific_param.encoder.tlm_start;
            long current_position = _bcio.Pos;

            _bcio.Pos = tlm_position;
            _bcio.Write(_specific_param.encoder.tlm_sot_offsets_buffer, 0, (int)tlm_size);
            _bcio.Commit();
            _bcio.Pos = current_position;
        }

        //2.5 - opj_j2k_update_tlm
        void UpdateTLM(OPJ_UINT32 p_tile_part_size)
        {
            if (!_specific_param.encoder.Ttlmi_is_byte)
                _specific_param.encoder.tlm_sot_offsets_buffer[_specific_param.encoder.tlm_sot_offsets_current++] = (byte)(_current_tile_number >> 8);

            _specific_param.encoder.tlm_sot_offsets_buffer[_specific_param.encoder.tlm_sot_offsets_current++] = (byte)_current_tile_number;

            _specific_param.encoder.tlm_sot_offsets_buffer[_specific_param.encoder.tlm_sot_offsets_current++] = unchecked((byte)(p_tile_part_size >> 24));
            _specific_param.encoder.tlm_sot_offsets_buffer[_specific_param.encoder.tlm_sot_offsets_current++] = unchecked((byte)(p_tile_part_size >> 16));
            _specific_param.encoder.tlm_sot_offsets_buffer[_specific_param.encoder.tlm_sot_offsets_current++] = unchecked((byte)(p_tile_part_size >> 8));
            _specific_param.encoder.tlm_sot_offsets_buffer[_specific_param.encoder.tlm_sot_offsets_current++] = unchecked((byte)p_tile_part_size);
        }

        /// <summary>
        /// Writes the POC marker (Progression Order Change)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_write_poc</remarks>
        void WritePOC()
        {
            var tcp = _cp.tcps[_current_tile_number];

            var nb_comp = _private_image.numcomps;
            uint nb_poc = 1 + tcp.numpocs;
            uint poc_room = (nb_comp <= 256 ? 1u : 2u);
            uint poc_size = 4u + (5u + 2u * poc_room) * nb_poc;

            _bcio.SetBuffer(ref _specific_param.encoder.header_tile_data, poc_size);

            WritePOC_InMemory(out _);
            _bcio.Commit();
        }

        //2.5
        void WritePOC_InMemory(out uint data_written)
        {
            var tcp = _cp.tcps[_current_tile_number];
            var tccp = tcp.tccps[0];

            var nb_comp = _private_image.numcomps;
            uint nb_poc = 1 + tcp.numpocs;
            uint poc_room = (nb_comp <= 256 ? 1u : 2u);
            uint poc_size = 4u + (5u + 2u * poc_room) * nb_poc;

            _bcio.Write(J2K_Marker.POC);
            _bcio.WriteUShort(poc_size - 2);

            for (int i = 0; i < nb_poc; i++)
            {
                var poc = tcp.pocs[i];
                _bcio.WriteByte(poc.resno0);
                _bcio.Write(poc.compno0, (int)poc_room);
                _bcio.WriteUShort(poc.layno1);
                _bcio.WriteByte(poc.resno1);
                _bcio.Write(poc.compno1, (int)poc_room);
                _bcio.WriteByte((int) poc.prg);

                // Change the value of the max layer according to the actual number of
                // layers in the file, components and resolutions
                poc.layno1 = (uint)Math.Min((int)poc.layno1, (int)tcp.numlayers);
                poc.resno1 = (uint)Math.Min((int)poc.resno1, (int)tccp.numresolutions);
                poc.compno1 = (uint)Math.Min((int)poc.compno1, (int)nb_comp);
            }

            data_written = poc_size;
        }

        //2.5 - opj_j2k_write_sot
        bool WriteSOT(out OPJ_UINT32 data_written, uint total_data_size)
        {
            if (total_data_size < 12)
            {
                _cinfo.Error("Not enough bytes in output buffer to write SOT marker");
                data_written = 0;
                return false;
            }

            _bcio.Write(J2K_Marker.SOT);
            _bcio.WriteUShort(10);
            _bcio.WriteUShort(_current_tile_number);
            _bcio.Skip(4, true); //Written later in SOD
            _bcio.WriteByte(_specific_param.encoder.current_tile_part_number);
            _bcio.WriteByte(_cp.tcps[_current_tile_number].n_tile_parts);
            
            data_written = 12;

            return true;
        }

        /// <summary>
        /// Writes the SOD marker (Start of data)
        /// 
        /// This also writes optional PLT markers (before SOD)
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_j2k_write_sod
        /// 
        /// This marker contains the actual tile data.
        /// </remarks>
        bool WriteSOD(TileCoder tcd, out OPJ_UINT32 data_written, OPJ_UINT32 total_data_size)
        {
            TcdMarkerInfo marker_info = null;

            if (total_data_size < 4)
            {
                _cinfo.Error("Not enough bytes in output buffer to write SOD marker");
                data_written = 0;
                return false;
            }

            _bcio.Write(J2K_Marker.SOD);

            // Make room for EOF marker (2 bytes, and 2 bytes for SOD)
            uint remaining_data = total_data_size - 4;

            // Updates tile coder
            tcd.TpNum = _specific_param.encoder.current_poc_tile_part_number;
            tcd.CurTpNum = _specific_param.encoder.current_tile_part_number;

            if (_specific_param.encoder.current_tile_part_number == 0)
            {
                tcd.TcdImage.tiles[0].packno = 0;
            }

            if (_specific_param.encoder.PLT)
            {
                marker_info = new TcdMarkerInfo() { need_PLT = true };
            }

            if (remaining_data < _specific_param.encoder.reserved_bytes_for_PLT)
            {
                _cinfo.Error("Not enough bytes in output buffer to write SOD marke");
                data_written = 0;
                return false;
            }
            remaining_data -= _specific_param.encoder.reserved_bytes_for_PLT;

            //_bcio.BufferPos += 2;
            if (!tcd.EncodeTile(_current_tile_number, _bcio, out data_written, (int) remaining_data, marker_info))
            {
                _cinfo.Error("Cannot encode tile");
                return false;
            }

            //For SOD
            data_written += 2;

            if (_specific_param.encoder.PLT)
            {
                uint data_written_PLT;
                byte[] p_PLT_buffer = new byte[_specific_param.encoder.reserved_bytes_for_PLT];

                WritePLT(marker_info, p_PLT_buffer, out data_written_PLT);

                // Move PLT marker(s) before SOD
                var before_sod = _bcio.BufferPos - data_written;
                _bcio.Memcopymove(p_PLT_buffer, before_sod, data_written_PLT);

                data_written += data_written_PLT;
            }

            return true;
        }

        #region Decode image

        //2.5
        void AddTlmarker(OPJ_UINT32 tileno, J2K_Marker type, long pos, OPJ_UINT32 len)
        {
            Debug.Assert(_cstr_index != null);
            Debug.Assert(_cstr_index.tile_index != null);

            // expand the list?
            if ((_cstr_index.tile_index[tileno].marknum + 1) >
                    _cstr_index.tile_index[tileno].maxmarknum)
            {
                _cstr_index.tile_index[tileno].maxmarknum += 100;

                Array.Resize(ref _cstr_index.tile_index[tileno].marker,
                    (int)_cstr_index.tile_index[tileno].maxmarknum);
            }

            /* add the marker */
            _cstr_index.tile_index[tileno].marker[_cstr_index.tile_index[tileno].marknum].type
                = type;
            _cstr_index.tile_index[tileno].marker[_cstr_index.tile_index[tileno].marknum].pos
                = pos;
            _cstr_index.tile_index[tileno].marker[_cstr_index.tile_index[tileno].marknum].len
                = (int)len;
            _cstr_index.tile_index[tileno].marknum++;

            if (type == J2K_Marker.SOT)
            {
                OPJ_UINT32 current_tile_part = _cstr_index.tile_index[tileno].current_tpsno;

                if (_cstr_index.tile_index[tileno].tp_index != null)
                {
                    _cstr_index.tile_index[tileno].tp_index[current_tile_part].start_pos = pos;
                }

            }
        }

        //2.5 - opj_j2k_end_decompress
        internal bool EndDecompress()
        {
            return true;
        }

        /// <summary>
        /// Decodes image
        /// </summary>
        /// <param name="image">Dest image</param>
        /// <returns>False on error</returns>
        /// <remarks>2.5 - opj_j2k_decode</remarks>
        internal bool Decode(JPXImage image)
        {
            if (image == null)
                return false;

            // Heuristics to detect sequence opj_read_header(), opj_set_decoded_resolution_factor()
            // and finally opj_decode_image() without manual setting of comps[].factor
            // We could potentially always execute it, if we don't allow people to do
            // opj_read_header(), modify x0,y0,x1,y1 of returned image an call opj_decode_image()
            if (_cp.specific_param.dec.reduce > 0 &&
                _private_image != null &&
                _private_image.numcomps > 0 &&
                _private_image.comps[0].factor == _cp.specific_param.dec.reduce &&
                image.numcomps > 0 &&
                image.comps[0].factor == 0 &&
                /* Don't mess with image dimension if the user has allocated it */
                image.comps[0].data == null)
            {
                // Update the comps[].factor member of the output image with the one
                // of m_reduce
                for (var it_comp = 0; it_comp < image.numcomps; ++it_comp)
                {
                    image.comps[it_comp].factor = _cp.specific_param.dec.reduce;
                }
                if (!UpdateImageDimensions(image))
                {
                    return false;
                }
            }

            if (_output_image == null)
                _output_image = new JPXImage();
            _output_image.CopyImageHeader(image);

            // Decodes the codestream
            if (!DecodeTiles())
            {
                _private_image = null;
                return false;
            }

            //// Move data and copy one information from codec to output image
            //for (int compno = 0; compno < image.numcomps; compno++)
            //{
            //    image.comps[compno].resno_decoded = _output_image.comps[compno].resno_decoded;
            //    image.comps[compno].data = _output_image.comps[compno].data;

            //    _output_image.comps[compno].data = null;
            //}

            return move_data_from_codec_to_output_image(image);
        }

        /// <summary>
        /// Decodes image
        /// </summary>
        /// <param name="image">Dest image</param>
        /// <param name="tile_index">Tile to decode</param>
        /// <returns>False on error</returns>
        /// <remarks>2.5</remarks>
        internal bool Decode(JPXImage image, uint tile_index)
        {
            if (image == null)
            {
                _cinfo.Error("We need an image previously created.");
                return false;
            }

            if (image.numcomps < _private_image.numcomps)
            {
                _cinfo.Error("Image has less components than codestream.");
                return false;
            }

            if ((tile_index >= _cp.tw * _cp.th))
            {
                _cinfo.Error("Tile index provided by the user is incorrect {0} (max = {1}) \n", tile_index,
                              (_cp.tw * _cp.th) - 1);
                return false;
            }

            /* Compute the dimension of the desired tile*/
            uint tile_x = tile_index % _cp.tw;
            uint tile_y = tile_index / _cp.tw;

            image.x0 = tile_x * _cp.tdx + _cp.tx0;
            if (image.x0 < _private_image.x0)
            {
                image.x0 = _private_image.x0;
            }
            image.x1 = (tile_x + 1) * _cp.tdx + _cp.tx0;
            if (image.x1 > _private_image.x1)
            {
                image.x1 = _private_image.x1;
            }

            image.y0 = tile_y * _cp.tdy + _cp.ty0;
            if (image.y0 < _private_image.y0)
            {
                image.y0 = _private_image.y0;
            }
            image.y1 = (tile_y + 1) * _cp.tdy + _cp.ty0;
            if (image.y1 > _private_image.y1)
            {
                image.y1 = _private_image.y1;
            }
            
            for (uint compno = 0; compno < _private_image.numcomps; ++compno)
            {
                int comp_x1, comp_y1;
                var img_comp = image.comps[compno];

                img_comp.factor = _private_image.comps[compno].factor;

                img_comp.x0 = (uint)MyMath.int_ceildiv((int)image.x0,
                                 (int)img_comp.dx);
                img_comp.y0 = (uint)MyMath.int_ceildiv((int)image.y0,
                                 (int)img_comp.dy);
                comp_x1 = MyMath.int_ceildiv((int)image.x1, (int)img_comp.dx);
                comp_y1 = MyMath.int_ceildiv((int)image.y1, (int)img_comp.dy);

                img_comp.w = (OPJ_UINT32)(MyMath.int_ceildivpow2(comp_x1,
                                             (int)img_comp.factor) - MyMath.int_ceildivpow2((int)img_comp.x0,
                                                     (int)img_comp.factor));
                img_comp.h = (OPJ_UINT32)(MyMath.int_ceildivpow2(comp_y1,
                                             (int)img_comp.factor) - MyMath.int_ceildivpow2((int)img_comp.y0,
                                                     (int)img_comp.factor));
            }

            if (image.numcomps > _private_image.numcomps)
            {
                /* Can happen when calling repeatdly opj_get_decoded_tile() on an
                 * image with a color palette, where color palette expansion is done
                 * later in jp2.c */
                for (uint compno = _private_image.numcomps; compno < image.numcomps;
                        ++compno)
                {
                    image.comps[compno].data = null;
                }
                image.numcomps = _private_image.numcomps;
            }

            //Replace the previous output image, create new from information previously computed
            _output_image = new JPXImage();
            _output_image.CopyImageHeader(image);

            _specific_param.decoder.tile_ind_to_dec = (int)tile_index;

            // Decodes the codestream
            if (!DecodeOneTile())
            {
                _private_image = null;
                return false;
            }

            return move_data_from_codec_to_output_image(image);
        }

        //2.5
        private bool move_data_from_codec_to_output_image(JPXImage p_image)
        {
            OPJ_UINT32 compno;

            // Move data and copy one information from codec to output image
            if (_specific_param.decoder.numcomps_to_decode > 0)
            {
                ImageComp[] newcomps = new ImageComp[_specific_param.decoder.numcomps_to_decode];
                
                for (compno = 0; compno < p_image.numcomps; compno++)
                {
                    p_image.comps[compno].data = null;
                }
                for (compno = 0; compno < _specific_param.decoder.numcomps_to_decode; compno++)
                {
                    int src_compno = _specific_param.decoder.comps_indices_to_decode[compno];
                    newcomps[compno] = (ImageComp) _output_image.comps[src_compno].Clone();
                    newcomps[compno].resno_decoded =
                        _output_image.comps[src_compno].resno_decoded;
                    newcomps[compno].data = _output_image.comps[src_compno].data;
                    _output_image.comps[src_compno].data = null;
                }
                for (compno = 0; compno < p_image.numcomps; compno++)
                {
                    Debug.Assert(_output_image.comps[compno].data == null);
                    _output_image.comps[compno].data = null;
                }
                p_image.numcomps = (uint)_specific_param.decoder.numcomps_to_decode;
                p_image.comps = newcomps;
            }
            else
            {
                for (compno = 0; compno < p_image.numcomps; compno++)
                {
                    p_image.comps[compno].resno_decoded = _output_image.comps[compno].resno_decoded;
                    p_image.comps[compno].data = _output_image.comps[compno].data;

                    _output_image.comps[compno].data =null;
                }
            }
            return true;
        }

        //2.5
        private void AllocateTileElementCstrIndex()
        {
            _cstr_index.n_of_tiles = _cp.tw * _cp.th;
            _cstr_index.tile_index = new TileIndex[_cstr_index.n_of_tiles];

            for (var it_tile = 0; it_tile < _cstr_index.n_of_tiles; it_tile++)
            {
                _cstr_index.tile_index[it_tile].maxmarknum = 100;
                _cstr_index.tile_index[it_tile].marknum = 0;
                _cstr_index.tile_index[it_tile].marker =
                    new MarkerInfo[_cstr_index.tile_index[it_tile].maxmarknum];
            }
        }

        //2.5
        private bool AreAllUsedComponentsDecoded()
        {
            OPJ_UINT32 compno;
            bool decoded_all_used_components = true;

            if (_specific_param.decoder.numcomps_to_decode != 0)
            {
                for (compno = 0;
                        compno < _specific_param.decoder.numcomps_to_decode; compno++)
                {
                    var dec_compno =
                        _specific_param.decoder.comps_indices_to_decode[compno];
                    if (_output_image.comps[dec_compno].data == null)
                    {
                        _cinfo.Warn("Failed to decode component {0}",
                                      dec_compno);
                        decoded_all_used_components = false;
                    }
                }
            }
            else
            {
                for (compno = 0; compno < _output_image.numcomps; compno++)
                {
                    if (_output_image.comps[compno].data == null)
                    {
                        _cinfo.Warn("Failed to decode component {0}",
                                      compno);
                        decoded_all_used_components = false;
                    }
                }
            }

            if (decoded_all_used_components == false)
            {
                _cinfo.Error("Failed to decode all used components");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Read and decode one tile.
        /// </summary>
        /// <remarks>2.5 - opj_j2k_decode_one_tile</remarks>
        bool DecodeOneTile()
        {
            bool go_on = true;
            uint current_tile_no;
            uint? data_size = null; //Datasize has been removed from 2.5
            int tile_x0, tile_y0, tile_x1, tile_y1;
            uint n_comps;

            // Allocate and initialize some elements of codestrem index if not already done
            if (_cstr_index.tile_index == null)
                AllocateTileElementCstrIndex();

            // Move into the codestream to the first SOT used to decode the desired tile
            var tile_no_to_dec = (uint)_specific_param.decoder.tile_ind_to_dec;
            if (_cstr_index.tile_index != null)
            {
                if (_cstr_index.tile_index[0].tp_index != null)
                {
                    if (!_cio.CanSeek)
                    {
                        _cinfo.Error("Problem with seek function");
                        return false;
                    }

                    if (_cstr_index.tile_index[tile_no_to_dec].n_tps == 0)
                    {
                        // the index for this tile has not been built,
                        //  so move to the last SOT read
                        _cio.Pos = _specific_param.decoder.last_sot_read_pos + 2;
                    }
                    else
                    {
                        _cio.Pos = _cstr_index.tile_index[tile_no_to_dec].tp_index[0].start_pos + 2;
                    }
                    // Special case if we have previously read the EOC marker (if the previous tile getted is the last )
                    if (_specific_param.decoder.state == J2K_STATUS.EOC)
                    {
                        _specific_param.decoder.state = J2K_STATUS.TPHSOT;
                    }
                }
            }

            // Reset current tile part number for all tiles, and not only the one
            // of interest.
            // Not completely sure this is always correct but required for
            // ./build/bin/j2k_random_tile_access ./build/tests/tte1.j2k
            var nb_tiles = _cp.tw * _cp.th;
            for (int i = 0; i < nb_tiles; ++i)
            {
                _cp.tcps[i].current_tile_part_number = -1;
            }

            while(true)
            {
                if (!ReadTileHeader(out current_tile_no,
                                    ref data_size,
                                    out tile_x0, out tile_y0,
                                    out tile_x1, out tile_y1,
                                    out n_comps,
                                    out go_on))
                {
                    return false;
                }

                if (!go_on)
                    break;

                if (!DecodeTile(current_tile_no, null))
                    return false;
                _cinfo.Info("Tile {0}/{1} has been decoded.", current_tile_no + 1, _cp.th * _cp.tw);

                if (!UpdateImageData())
                    return false;
                _cp.tcps[current_tile_no].data = null;
                _cp.tcps[current_tile_no].data_size = 0;

                _cinfo.Info("Image data has been updated with tile {0}.\n", current_tile_no + 1);

                if (current_tile_no == tile_no_to_dec)
                {
                    /* move into the codestream to the first SOT (FIXME or not move?)*/
                    _cio.Pos = _cstr_index.main_head_end + 2;
                    break;
                }
                else
                {
                    _cinfo.Warn("Tile read, decoded and updated is not the desired one ({0} vs {1}).",
                                  current_tile_no + 1, tile_no_to_dec + 1);
                }
            }

            return AreAllUsedComponentsDecoded();
        }

        //2.5 - opj_j2k_decode_tiles
        bool DecodeTiles()
        {
            bool go_on = true;
            uint current_tile_no;
            uint? data_size = null; //Datasize has been removed from 2.5 (but C# needs something for the "ref")
            int tile_x0, tile_y0, tile_x1, tile_y1;
            uint n_comps;
            uint nr_tiles = 0;

            // Particular case for whole single tile decoding
            // We can avoid allocating intermediate tile buffers
            if (_cp.tw == 1 && _cp.th == 1 &&
                _cp.tx0 == 0 && _cp.ty0 == 0 &&
                _output_image.x0 == 0 &&
                _output_image.y0 == 0 &&
                _output_image.x1 == _cp.tdx &&
                _output_image.y1 == _cp.tdy)
            {
                OPJ_UINT32 i;
                if (!ReadTileHeader(out current_tile_no,
                                    ref data_size, 
                                    out tile_x0, out tile_y0,
                                    out tile_x1, out tile_y1,
                                    out n_comps,
                                    out go_on))
                {
                    return false;
                }

                if (!DecodeTile(current_tile_no, null))
                {
                    _cinfo.Error("Failed to decode tile 1/1");
                    return false;
                }

                /* Transfer TCD data to output image data */
                for (i = 0; i < _output_image.numcomps; i++)
                {
                    _output_image.comps[i].data =
                        _tcd.TcdImage.tiles[0].comps[i].data;
                    _output_image.comps[i].resno_decoded =
                        _tcd.Image.comps[i].resno_decoded;
                    _tcd.TcdImage.tiles[0].comps[i].data = null;
                }

                return true;
            }

            //int max_data_size = 1024;
            //byte[] current_data = new byte[max_data_size];
            //int[][] current_data = new int[_image.numcomps][];

            while (true)
            {
                if (_cp.tw == 1 && _cp.th == 1 &&
                    _cp.tcps[0].data != null)
                {
                    current_tile_no = 0;
                    _current_tile_number = 0;
                    _specific_param.decoder.state |= J2K_STATUS.DATA;
                } else
                {
                    if (!ReadTileHeader(out current_tile_no, 
                                        ref data_size, 
                                        out tile_x0, out tile_y0,
                                        out tile_x1, out tile_y1, 
                                        out n_comps, 
                                        out go_on))
                        return false;

                    if (!go_on)
                        break;
                }

                //if (current_tile_no == 0)
                //{
                //    current_tile_no = current_tile_no;
                //}

                if (!DecodeTile(current_tile_no, null))
                {
                    _cinfo.Error("Failed to decode tile {0}/{1}", 
                        current_tile_no + 1, _cp.th * _cp.tw);
                    return false;
                }

                _cinfo.Info("Tile {0}/{1} has been decoded.", 
                    current_tile_no + 1, _cp.th * _cp.tw);

                if (!UpdateImageData())
                    return false;

                if (_cp.tw == 1 && _cp.th == 1 &&
                        !(_output_image.x0 == _private_image.x0 &&
                          _output_image.y0 == _private_image.y0 &&
                          _output_image.x1 == _private_image.x1 &&
                          _output_image.y1 == _private_image.y1))
                {
                    /* Keep current tcp data */
                }
                else
                {
                    _cp.tcps[current_tile_no].data = null;
                }

                _cinfo.Info("Image data has been updated with tile {0}.", current_tile_no + 1);

                if (_cio.BytesLeft == 0 && _specific_param.decoder.state == J2K_STATUS.NEOC)
                    break;

                if (++nr_tiles == _cp.th * _cp.tw)
                    break;
            }

            return AreAllUsedComponentsDecoded();
        }

        //2.5
        bool UpdateImageData()
        {
            uint width_src, height_src;
            uint width_dest, height_dest;
            int offset_x0_src, offset_y0_src, offset_x1_src, offset_y1_src;
            long start_offset_src;
            uint start_x_dest, start_y_dest;
            uint x0_dest, y0_dest, x1_dest, y1_dest;
            long start_offset_dest;

            //C# impl note.
            //tilec is set insde the for loop, because in the org. impl it's a
            //incrementing pointer.
            //Same story with img_comp_src and img_comp_dest
            for (int i = 0; i < _tcd.Image.numcomps; i++)
            {
                int res_x0, res_x1, res_y0, res_y1;
                uint src_data_stride;
                int[] src_data;

                var img_comp_src = _tcd.Image.comps[i];
                var img_comp_dest = _output_image.comps[i];
                var tilec = _tcd.TcdImage.tiles[0].comps[i];

                //Copy info from decoded comp image to output image
                img_comp_dest.resno_decoded = img_comp_src.resno_decoded;

                if (_tcd.WholeTileDecoding)
                {
                    var res = tilec.resolutions[img_comp_src.resno_decoded];
                    res_x0 = res.x0;
                    res_y0 = res.y0;
                    res_x1 = res.x1;
                    res_y1 = res.y1;
                    src_data_stride = (uint)(
                        tilec.resolutions[tilec.minimum_num_resolutions - 1].x1 -
                        tilec.resolutions[tilec.minimum_num_resolutions - 1].x0);
                    src_data = tilec.data;
                }
                else
                {
                    var res = tilec.resolutions[img_comp_src.resno_decoded];
                    res_x0 = (int)res.win_x0;
                    res_y0 = (int)res.win_y0;
                    res_x1 = (int)res.win_x1;
                    res_y1 = (int)res.win_y1;
                    src_data_stride = res.win_x1 - res.win_x0;
                    src_data = tilec.data_win;
                }

                if (src_data == null)
                {
                    // Happens for partial component decoding
                    continue;
                }


                //Current tile component size
                width_src = (uint)(res_x1 - res_x0);
                height_src = (uint)(res_y1 - res_y0);

                //Border of the current output component
                x0_dest = MyMath.uint_ceildivpow2(img_comp_dest.x0, (int)img_comp_dest.factor);
                y0_dest = MyMath.uint_ceildivpow2(img_comp_dest.y0, (int)img_comp_dest.factor);
                x1_dest = x0_dest + img_comp_dest.w; // can't overflow given that image.x1 is uint32
                y1_dest = y0_dest + img_comp_dest.h;

                /* Compute the area (offset_x0_src, offset_y0_src, offset_x1_src, offset_y1_src)
                 * of the input buffer (decoded tile component) which will be move
                 * in the output buffer. Compute the area of the output buffer (start_x_dest,
                 * start_y_dest, width_dest, height_dest)  which will be modified
                 * by this input area.
                 * */
                if (x0_dest < res_x0)
                {
                    start_x_dest = (uint) (res_x0 - x0_dest);
                    offset_x0_src = 0;

                    if (x1_dest >= res_x1)
                    {
                        width_dest = width_src;
                        offset_x1_src = 0;
                    }
                    else
                    {
                        width_dest = (uint)(x1_dest - res_x0);
                        offset_x1_src = (int)(width_src - width_dest);
                    }
                }
                else
                {
                    start_x_dest = 0;
                    offset_x0_src = (int)(x0_dest - res_x0);

                    if (x1_dest >= res_x1)
                    {
                        width_dest = width_src - (uint)offset_x0_src;
                        offset_x1_src = 0;
                    }
                    else
                    {
                        width_dest = img_comp_dest.w;
                        offset_x1_src = res_x1 - (int)x1_dest;
                    }
                }

                if (y0_dest < res_y0)
                {
                    start_y_dest = (uint)(res_y0 - y0_dest);
                    offset_y0_src = 0;

                    if (y1_dest >= res_y1)
                    {
                        height_dest = height_src;
                        offset_y1_src = 0;
                    }
                    else
                    {
                        height_dest = y1_dest - (uint)res_y0;
                        offset_y1_src = (int)(height_src - height_dest);
                    }
                }
                else
                {
                    start_y_dest = 0;
                    offset_y0_src = (int)(y0_dest - res_y0);

                    if (y1_dest >= res_y1)
                    {
                        height_dest = height_src - (uint)offset_y0_src;
                        offset_y1_src = 0;
                    }
                    else
                    {
                        height_dest = img_comp_dest.h;
                        offset_y1_src = res_y1 - (int)y1_dest;
                    }
                }

                if ((offset_x0_src < 0) || (offset_y0_src < 0) || (offset_x1_src < 0) || (offset_y1_src < 0))
                {
                    return false;
                }

                if ((int)width_dest < 0 || (int)height_dest < 0)
                {
                    return false;
                }

                //Compute the input buffer offset
                start_offset_src = offset_x0_src + offset_y0_src * src_data_stride;

                //Compute the output buffer offset
                start_offset_dest = start_x_dest + start_y_dest * img_comp_dest.w;

                //Allocate output component buffer if necessary
                if (img_comp_dest.data == null &&
                    start_offset_src == 0 && start_offset_dest == 0 &&
                    src_data_stride == img_comp_dest.w &&
                    width_dest == img_comp_dest.w &&
                    height_dest == img_comp_dest.h)
                {
                    // If the final image matches the tile buffer, then borrow it
                    // directly to save a copy
                    if (_tcd.WholeTileDecoding)
                    {
                        img_comp_dest.data = tilec.data;
                        tilec.data = null;
                    }
                    else
                    {
                        img_comp_dest.data = tilec.data_win;
                        tilec.data_win = null;
                    }
                    continue;
                }
                else if (img_comp_dest.data == null)
                {
                    var width = img_comp_dest.w;
                    var height = img_comp_dest.h;

                    if ((height == 0) || (width > (Constants.SIZE_MAX / height)) ||
                            width * height > (Constants.SIZE_MAX / 4))
                    {
                        // will overflow
                        return false;
                    }
                    img_comp_dest.data = new int[width * height];

                    //C# Commented out. Arrays are always zeroed on creation, so this
                    //                  is already done.
                    //if (img_comp_dest.w != width_dest ||
                    //    img_comp_dest.h != height_dest)
                    //{
                    //    var data = img_comp_dest.data;
                    //    Array.Clear(data, 0, (int)(img_comp_dest.w * img_comp_dest.h));
                    //}
                }

                //Move the output buffer to the first place where we will write
                var dest_ptr = start_offset_dest;
                var dest_data = img_comp_dest.data;

                {
                    int src_ptr = (int)start_offset_src;

                    for (int j = 0; j < height_dest; j++)
                    {
                        Buffer.BlockCopy(src_data, src_ptr * 4, dest_data, (int)(dest_ptr * 4), (int)(width_dest * 4));
                        dest_ptr += img_comp_dest.w;
                        src_ptr += (int)src_data_stride;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Decode one tile
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_j2k_decode_tile
        /// 
        /// Note, in the org_impl the methods:
        ///    opj_jp2_decode_tile
        ///    opj_j2k_decode_tile
        /// calls this method with data != null, those two methods do not
        /// exist in this impl.
        /// 
        /// If someone wants to decode a particular tile, they can still do
        /// that through other means, and if someone later wish to use this
        /// functionality, the way it's implemented now saves you from 
        /// calculating the size of the buffer. 
        /// </remarks>
        bool DecodeTile(OPJ_UINT32 tile_index, /*byte[] data*/ int[][] data)
        {
            if ((_specific_param.decoder.state & J2K_STATUS.DATA) == 0 ||
                tile_index != _current_tile_number)
                return false;

            var tcp = _cp.tcps[tile_index];
            if (tcp.data == null)
                return false;

            var image_for_bounds = _output_image != null ? _output_image : _private_image;
            if (!_tcd.DecodeTile(
                image_for_bounds.x0,
                image_for_bounds.y0,
                image_for_bounds.x1,
                image_for_bounds.y1,
                _specific_param.decoder.numcomps_to_decode,
                _specific_param.decoder.comps_indices_to_decode,
                tcp.data, 
                tile_index,
                _cstr_index))
            {
                _cinfo.Error("Failed to decode.");
                return false;
            }

            // p_data can be set to NULL when the call will take care of using
            // itself the TCD data. This is typically the case for whole single
            // tile decoding optimization.
            if (data != null)
            {
                _tcd.UpdateTileData(data);
            }

            _specific_param.decoder.can_decode = false;
            _specific_param.decoder.state &= (J2K_STATUS) (~(0x0080));

            if (_cio.BytesLeft == 0 && _specific_param.decoder.state == J2K_STATUS.NEOC)
                return true;

            if (_specific_param.decoder.state != J2K_STATUS.EOC)
            {
                var current_marker = (J2K_Marker)_cio.ReadUShort();
                if (current_marker == J2K_Marker.EOC)
                {
                    _current_tile_number = 0;
                    _specific_param.decoder.state = J2K_STATUS.EOC;
                }
                else if (current_marker != J2K_Marker.SOT)
                {
                    _cinfo.Error("Stream too short, expected SOT");

                    if (_cio.BytesLeft == 0)
                    {
                        _specific_param.decoder.state = J2K_STATUS.NEOC;
                        return true;
                    }
                    return false;
                }
            }

            return true;
        }

        //2.5
        private bool NeedNTilePartsCorrection(OPJ_UINT32 tile_no, out bool p_correction_needed)
        {
            long l_stream_pos_backup;
            J2K_Marker l_current_marker;
            OPJ_UINT32 l_marker_size;
            OPJ_UINT32 l_tile_no, l_tot_len, l_current_part, l_num_parts;

            // initialize to no correction needed
            p_correction_needed = false;

            if (!_cio.CanSeek)
            {
                // We can't do much in this case, seek is needed
                return true;
            }

            l_stream_pos_backup = _cio.Pos;
            
            for (; ; )
            {
                // Try to read 2 bytes (the next marker ID) from stream and copy them into the buffer
                if (_cio.BytesLeft < 2)
                {
                    // Assume all is OK
                    return true;
                }

                l_current_marker = (J2K_Marker) _cio.ReadUShort();

                if (l_current_marker != J2K_Marker.SOT)
                {
                    // Assume all is OK
                    _cio.Pos = l_stream_pos_backup;
                    return true;
                }

                // Try to read 2 bytes (the marker size) from stream
                if (_cio.BytesLeft < 2)
                {
                    _cinfo.Error("Stream too short");
                    return false;
                }
                l_marker_size = _cio.ReadUShort();

                /* Check marker size for SOT Marker */
                if (l_marker_size != 10)
                {
                    _cinfo.Error("Inconsistent marker size");
                    return false;
                }
                l_marker_size -= 2;

                if (_cio.BytesLeft < l_marker_size)
                {
                    _cinfo.Error("Stream too short");
                    return false;
                }

                if (!GetSOTValues(l_marker_size, 
                                  out l_tile_no,
                                  out l_tot_len, 
                                  out l_current_part, 
                                  out l_num_parts))
                {
                    return false;
                }

                if (l_tile_no == tile_no)
                {
                    /* we found what we were looking for */
                    break;
                }

                if (l_tot_len < 14U)
                {
                    // last SOT until EOC or invalid Psot value
                    // assume all is OK
                    _cio.Pos = l_stream_pos_backup;
                    return true;
                }
                l_tot_len -= 12U;
                /* look for next SOT marker */
                _cio.Skip(((int) l_tot_len));
            }

            /* check for correction */
            if (l_current_part == l_num_parts)
            {
                p_correction_needed = true;
            }

            _cio.Pos = l_stream_pos_backup;
            return true;
        }

        //2.5 - opj_j2k_read_tile_header
        bool ReadTileHeader(out uint tile_index, ref uint? data_size, out int tile_x0, out int tile_y0,
            out int tile_x1, out int tile_y1, out uint n_comps, out bool go_on)
        {
            J2K_Marker current_marker = J2K_Marker.SOT;
            tile_index = n_comps = 0;
            tile_x0 = tile_x1 = tile_y0 = tile_y1 = 0;
            go_on = false;

            // Reach the End Of Codestream ?
            if (_specific_param.decoder.state == J2K_STATUS.EOC)
                current_marker = J2K_Marker.EOC;
            // We need to encounter a SOT marker (a new tile-part header)
            else if (_specific_param.decoder.state != J2K_STATUS.TPHSOT)
            {
                return false;
            }

            //Read into the codestream until reach the EOC
            while (!_specific_param.decoder.can_decode && current_marker != J2K_Marker.EOC)
            {
                //Try to read until the Start Of Data is detected
                while (current_marker != J2K_Marker.SOD)
                {
                    if (_cio.BytesLeft == 0)
                    {
                        _specific_param.decoder.state = J2K_STATUS.NEOC;
                        break;
                    }

                    //Size of this marker
                    uint marker_size = _cio.ReadUShort();

                    //Check marker size (does not include marker ID but includes marker size)
                    if (marker_size < 2)
                    {
                        _cinfo.Error("Inconsistent marker size");
                        return false;
                    }

                    //cf. https://code.google.com/p/openjpeg/issues/detail?id=226
                    if (current_marker == (J2K_Marker) 0x8080 && _cio.BytesLeft == 0)
                    { //testcase_2325.jp2
                        _specific_param.decoder.state = J2K_STATUS.NEOC;
                        break;
                    }

                    if ((_specific_param.decoder.state & J2K_STATUS.TPH) != 0)
                    {
                        _specific_param.decoder.sot_length -= marker_size + 2;
                    }
                    marker_size -= 2; // Subtract the size of the marker ID already read

                    var handler = _dec_tab[current_marker];

                    if ((_specific_param.decoder.state & handler.States) == 0 || handler.Handler == null)
                    {
                        if (handler.Handler == null)
                            _cinfo.Error("Not sure how that happened");
                        else
                            _cinfo.Error("Marker is not compliant with its position");
                        return false;
                    }

                    //Check if the marker size is compatible with the header data size
                    if (marker_size > _specific_param.decoder.header_data_size)
                    { 
                        // If we are here, this means we consider this marker as known & we will read it
                        // Check enough bytes left in stream before allocation 
                        if (marker_size > _cio.BytesLeft)
                        {
                            _cinfo.Error("Marker size inconsistent with stream length");
                            return false;
                        }
                        Array.Resize(ref _specific_param.decoder.header_data, (int)marker_size);
                        _specific_param.decoder.header_data_size = (int) marker_size;
                    }

                    if (!handler.Handler((int)marker_size))
                    {
                        _cinfo.Error("Fail to read the current marker segment ({0:X})", current_marker);
                        return false;
                    }

                    //Add the marker to the codestream index
                    AddTlmarker(_current_tile_number, handler.Mark, _cio.Pos - marker_size - 4, marker_size + 4);

                    //Keep the position of the last SOT marker read
                    if (handler.Mark == J2K_Marker.SOT)
                    {
                        var sot_pos = _cio.Pos - marker_size - 4;
                        if (sot_pos > _specific_param.decoder.last_sot_read_pos)
                            _specific_param.decoder.last_sot_read_pos = sot_pos;
                    }

                    if (_specific_param.decoder.skip_data)
                    {
                        //Skip the rest of the tile part header
                        _cio.Skip((int)_specific_param.decoder.sot_length);
                        current_marker = J2K_Marker.SOD;
                    }
                    else
                    {
                        current_marker = (J2K_Marker)_cio.ReadUShort();
                    }
                }
                if (_cio.BytesLeft == 0 && _specific_param.decoder.state == J2K_STATUS.NEOC)
                    break;

                //If we didn't skip data before, we need to read the SOD marker
                if (!_specific_param.decoder.skip_data)
                {
                    if (!ReadSOD())
                    {
                        return false;
                    }

                    if (_specific_param.decoder.can_decode &&
                        !_specific_param.decoder.n_tile_parts_correction_checked)
                    {
                        // Issue 254
                        bool correction_needed;

                        _specific_param.decoder.n_tile_parts_correction_checked = true;
                        if (!NeedNTilePartsCorrection(_current_tile_number, out correction_needed))
                        {
                            _cinfo.Error("opj_j2k_apply_nb_tile_parts_correction error");
                            return false;
                        }

                        if (correction_needed)
                        {
                            OPJ_UINT32 tile_no;
                            OPJ_UINT32 n_tiles = _cp.tw * _cp.th;

                            _specific_param.decoder.can_decode = false;
                            _specific_param.decoder.n_tile_parts_correction = true;
                            // correct tiles
                            for (tile_no = 0; tile_no < n_tiles; ++tile_no)
                            {
                                if (_cp.tcps[tile_no].n_tile_parts != 0)
                                {
                                    _cp.tcps[tile_no].n_tile_parts += 1;
                                }
                            }
                            _cinfo.Warn("Non conformant codestream TPsot==TNsot.");
                        }
                    }
                }
                else
                {
                    //Indicate we will try to read a new tile-part header
                    _specific_param.decoder.skip_data = false;
                    _specific_param.decoder.can_decode = false;
                    _specific_param.decoder.state = J2K_STATUS.TPHSOT;
                }

                if (!_specific_param.decoder.can_decode)
                {

                    if (_cio.BytesLeft < 2)
                    {
                        // Deal with likely non conformant SPOT6 files, where the last
                        // row of tiles have TPsot == 0 and TNsot == 0, and missing EOC,
                        // but no other tile-parts were found.
                        OPJ_UINT32 n_tiles = _cp.tw * _cp.th;
                        if (_current_tile_number + 1 == n_tiles)
                        {
                            OPJ_UINT32 l_tile_no;
                            for (l_tile_no = 0U; l_tile_no < n_tiles; ++l_tile_no)
                            {
                                if (_cp.tcps[l_tile_no].current_tile_part_number == 0 &&
                                        _cp.tcps[l_tile_no].n_tile_parts == 0)
                                {
                                    break;
                                }
                            }
                            if (l_tile_no < n_tiles)
                            {
                                _cinfo.Info("Tile {0} has TPsot == 0 and TNsot == 0, "+
                                            "but no other tile-parts were found. "+
                                            "EOC is also missing.",
                                              l_tile_no);
                                _current_tile_number = l_tile_no;
                                current_marker = J2K_Marker.EOC;
                                _specific_param.decoder.state = J2K_STATUS.EOC;
                                break;
                            }
                        }

                        _cinfo.Error("Stream too short");
                        return false;
                    }

                    current_marker = (J2K_Marker)_cio.ReadUShort();
                }
            }

            if (current_marker == J2K_Marker.EOC)
            {
                if (_specific_param.decoder.state != J2K_STATUS.EOC)
                {
                    _current_tile_number = 0;
                    _specific_param.decoder.state = J2K_STATUS.EOC;
                }
            }

            if (!_specific_param.decoder.can_decode)
            {
                var tcps = _cp.tcps;
                int n_tiles = (int)(_cp.th * _cp.tw);

                while (_current_tile_number < n_tiles && tcps[_current_tile_number].data == null)
                    _current_tile_number++;

                if (_current_tile_number == n_tiles)
                {
                    return true;
                }
            }

            if (!MergePPT(_cp.tcps[_current_tile_number]))
            {
                _cinfo.Error("Failed to merge PPT data");
                return false;
            }

            if (!_tcd.InitDecodeTile(_current_tile_number))
            {
                _cinfo.Error("Cannot decode tile, memory error");
                return false;
            }

            _cinfo.Info("Header of tile {0} / {1} has been read.",
                _current_tile_number + 1, (_cp.th * _cp.tw));

            tile_index = _current_tile_number;
            go_on = true;
            if (data_size != null)
            {
                data_size = _tcd.GetDecodedTileSize(false);
                if (data_size.Value == uint.MaxValue)
                {
                    return false;
                }
            }
            
            tile_x0 = _tcd.TcdImage.tiles[0].x0;
            tile_y0 = _tcd.TcdImage.tiles[0].y0;
            tile_x1 = _tcd.TcdImage.tiles[0].x1;
            tile_y1 = _tcd.TcdImage.tiles[0].y1;
            n_comps = _tcd.TcdImage.tiles[0].numcomps;

            _specific_param.decoder.state |= (J2K_STATUS) 0x0080; //J2K_DEC_STATE_DATA

            return true;
        }

        //2.5
        bool DecodingValidation()
        {
            return _specific_param.decoder.state == J2K_STATUS.NONE; 
        }

        //2.5
        internal bool ReadHeader(out JPXImage image)
        {
            _private_image = new JPXImage();

            if (!DecodingValidation())
            {
                image = null;
                return false;
            }

            if (!ReadHeaderProcedure())
            {
                image = null;
                return false;
            }

            if (!CopyDefaultTCPandCreateTcd())
            {
                image = null;
                return false;
            }

            image = new JPXImage();
            image.CopyImageHeader(_private_image);

            // Allocate and initialize some elements of codestrem index
            AllocateTileElementCstrIndex();

            return true;
        }

        //2.5
        bool CopyDefaultTCPandCreateTcd()
        {
            int n_tiles = (int) (_cp.th * _cp.tw);
            TileCodingParams[] tcps = _cp.tcps;
            var default_tcp = _specific_param.decoder.default_tcp;

            // For each tile
            for (int i = 0; i < n_tiles; i++)
            {
                var tcp = tcps[i];

                //Keeps the the tile-compo coding parameters
                TileCompParams[] tccps = tcp.tccps;

                //Copies default coding parameters into the current tile coding parameters
                tcp = (TileCodingParams) default_tcp.Clone();
                tcps[i] = tcp;

                //Initialize some values of the current tile coding parameters
                tcp.cod = false;
                tcp.ppt = false;
                tcp.ppt_data = null;
                tcp.current_tile_part_number = -1;
                //Remove memory not owned by this tile in case of early error return.
                tcp.mct_decoding_matrix = null;
                tcp.n_max_mct_records = 0;
                tcp.mct_records = null;
                tcp.n_max_mcc_records = 0;
                tcp.mcc_records = null;

                //Reconnect the tile-compo coding parameters pointer to the current tile coding parameters
                tcp.tccps = tccps;

                //Get the mct_decoding_matrix of the dflt_tile_cp and copy them into the current tile cp
                if (default_tcp.mct_decoding_matrix != null)
                {
                    tcp.mct_decoding_matrix = (float[])default_tcp.mct_decoding_matrix.Clone();
                }

                //Get the mct_record of the dflt_tile_cp and copy them into the current tile cp
                tcp.mct_records = new MctData[default_tcp.mct_records.Length];
                //Snip memcopy. What it does is essentially zero everything,
                //then it copies over data in the following loop. We do the
                //zeroing in the following loop. 

                //Copy the mct record data from dflt_tile_cp to the current tile
                // Notice, c < tcp.mct_records.Length, not c < n_mct_records
                for (int c = 0; c < tcp.mct_records.Length; c++)
                {
                    var mct = default_tcp.mct_records[c];
                    mct = mct == null ? new MctData() : (MctData)mct.Clone();
                    tcp.mct_records[c] = mct;
                }
                tcp.n_max_mct_records = default_tcp.n_mct_records;

                //Get the mcc_record of the dflt_tile_cp and copy them into the current tile cp
                tcp.mcc_records = new SimpleMccDecorrelationData[default_tcp.n_max_mcc_records];
                tcp.n_max_mcc_records = default_tcp.n_max_mcc_records;
                //Snip memcopy. Again, we do the work in the following loop instead.

                //Copy the mcc record data from dflt_tile_cp to the current tile
                for (int c = 0; c < tcp.mcc_records.Length; c++)
                {
                    var mcc = tcp.mcc_records[c];
                    mcc = mcc == null ? new SimpleMccDecorrelationData() : (SimpleMccDecorrelationData)mcc.Clone();
                    tcp.mcc_records[c] = mcc;
                }

                //Copy all the dflt_tile_compo_cp to the current tile cp
                for (int c = 0; c < _private_image.numcomps; c++)
                    tccps[c] = (TileCompParams) default_tcp.tccps[c].Clone();
            }

            //Create the current tile decoder
            _tcd = new TileCoder(_cinfo, _private_image, _cp);

            return true;
        }

        //2.5 - opj_j2k_read_header_procedure
        bool ReadHeaderProcedure()
        {
            _specific_param.decoder.state = J2K_STATUS.MHSOC;

            if (!ReadSOC())
            {
                _cinfo.Error("Expected a SOC marker ");
                return false;
            }

            J2K_Marker current_marker = (J2K_Marker) _cio.ReadUShort();
            bool has_siz = false;
            bool has_cod = false;
            bool has_qcd = false;

            while (current_marker != J2K_Marker.SOT)
            {
                if (current_marker < J2K_Marker.FIRST)
                {
                    _cinfo.Error("We expected to read a marker ID (0xff--) instead of 0x{0:X}", current_marker);
                    return false;
                }

                J2kMarker e = _dec_tab[current_marker];

                // Manage unknown marker
                if (e.Mark == J2K_Marker.NONE)
                {
                    if (!ReadUNK(out current_marker))
                    {
                        _cinfo.Error("Unknow marker has been detected and generated error.");
                        return false;
                    }

                    if (current_marker == J2K_Marker.SOT)
                        break; //marker is detected main header is completely read
                    else
                        e = _dec_tab[current_marker];
                }

                switch(e.Mark)
                {
                    case J2K_Marker.SIZ:
                        has_siz = true;
                        break;
                    case J2K_Marker.COD:
                        has_cod = true;
                        break;
                    case J2K_Marker.QCD:
                        has_qcd = true;
                        break;
                }

                // Check if the marker is known and if it is the right place to find it
                if ((_specific_param.decoder.state & e.States) == J2K_STATUS.NONE)
                {
                    _cinfo.Error("Marker is not compliant with its position");
                    return false;
                }

                int marker_size = _cio.ReadUShort();
                if (marker_size < 2)
                {
                    _cinfo.Error("Invalid marker size");
                    return false;
                }
                marker_size -= 2; // Subtract the size of the marker ID already read

                //Check if the marker size is compatible with the header data size
                if (marker_size > _specific_param.decoder.header_data_size)
                {
                    _specific_param.decoder.header_data = new byte[marker_size];
                    _specific_param.decoder.header_data_size = marker_size;
                }

                if (!e.Handler(marker_size))
                {
                    _cinfo.Error("Marker handler function failed to read the marker segment");
                    return false;
                }

                current_marker = (J2K_Marker)_cio.ReadUShort();
            }

            if (!has_siz)
            {
                _cinfo.Error("required SIZ marker not found in main header");
                return false;
            }
            if (!has_cod)
            {
                _cinfo.Error("required COD marker not found in main header");
                return false;
            }
            if (!has_qcd)
            {
                _cinfo.Error("required QCD marker not found in main header");
                return false;
            }

            if (!MergePPM())
            {
                _cinfo.Error("Failed to merge PPM data");
                return false;
            }

            _cinfo.Info("Main header has been correctly decoded");

            // Position of the last element if the main header
            _cstr_index.main_head_end = _cio.Pos - 2;

            // Next step: read a tile-part header
            _specific_param.decoder.state = J2K_STATUS.TPHSOT;

            return true;
        }

        /// <summary>
        /// Reads a SOC marker (Start of Codestream)
        /// </summary>
        /// <returns>True on success</returns>
        /// <remarks>
        /// 2.5
        /// </remarks>
        internal bool ReadSOC()
        {
            if (_cio.ReadUShort() != (ushort)J2K_Marker.SOC)
            {
                return false;
            }

            _specific_param.decoder.state = J2K_STATUS.MHSIZ;

            // FIXME move it in a index structure included in p_j2k
            _cstr_index.main_head_start = _cio.Pos - 2;

            _cinfo.Info("Start to read j2k main header ({0})", 
                _cstr_index.main_head_start);

            return true;
        }

        //2.5 - opj_j2k_get_sot_values
        private bool GetSOTValues(OPJ_UINT32 header_size,
                                  out OPJ_UINT32 tile_no,
                                  out OPJ_UINT32 tot_len,
                                  out OPJ_UINT32 current_part,
                                  out OPJ_UINT32 num_parts)
        {
            // Size of this marker is fixed = 12 (we have already read marker and its size)
            if (header_size != 8 || _cio.BytesLeft < 8)
            {
                _cinfo.Error("Error reading SOT marker");
                tile_no = tot_len = current_part = num_parts = 0;
                return false;
            }

            tile_no = _cio.ReadUShort();    // Isot
            tot_len = _cio.ReadUInt();      // Psot
            current_part = _cio.ReadByte(); // TPsot
            num_parts = _cio.ReadByte();    // TNsot
            return true;
        }

        //2.5 - opj_j2k_read_sot
        internal bool ReadSOT(int header_size)
        {
            if (header_size != 8)
            {
                _cinfo.Error("Error reading SOT marker");
                return false;
            }

            //Tile number
            _current_tile_number = _cio.ReadUShort();

            if (_current_tile_number >= _cp.tw * _cp.th)
            {
                _cinfo.Error("Invalid tile number {0}", _current_tile_number);
                return false;
            }

            var tcp = _cp.tcps[_current_tile_number];
            uint tile_x = _current_tile_number % _cp.tw;
            uint tile_y = _current_tile_number / _cp.tw;
            uint tot_len = _cio.ReadUInt();
            uint current_part = _cio.ReadByte();

            if (_specific_param.decoder.tile_ind_to_dec < 0 ||
                _current_tile_number == (OPJ_UINT32)
                _specific_param.decoder.tile_ind_to_dec)
            {
                // Do only this check if we decode all tile part headers, or if
                // we decode one precise tile. Otherwise the m_current_tile_part_number
                // might not be valid
                // Fixes issue with id_000020,sig_06,src_001958,op_flip4,pos_149
                // of https://github.com/uclouvain/openjpeg/issues/939
                // We must avoid reading twice the same tile part number for a given tile
                // so as to avoid various issues, like opj_j2k_merge_ppt being called
                // several times.
                // ISO 15444-1 A.4.2 Start of tile-part (SOT) mandates that tile parts
                // should appear in increasing order.
                if (tcp.current_tile_part_number + 1 != (int)current_part)
                {
                    _cinfo.Error("Invalid tile part index for tile number {0}. "+
                                  "Got {1}, expected {2}",
                                  _current_tile_number,
                                  current_part,
                                  tcp.current_tile_part_number + 1);
                    return false;
                }
            }

            tcp.current_tile_part_number = (int) current_part;

            if (tot_len != 0 && tot_len < 14)
            {
                if (tot_len == 12) //Special case for the PHR data which are read by kakadu
                    _cinfo.Warn("Empty SOT marker detected: Psot={0}.", tot_len);
                else
                {
                    _cinfo.Error("Psot value is not correct regards to the JPEG2000 norm: {0}.", tot_len);
                    return false;
                }
            }

            //Ref A.4.2: Psot could be equal zero if it is the last tile-part of the codestream.
            if (tot_len == 0)
            {
                _cinfo.Info("Psot value of the current tile-part is equal to zero, "+
                     "we assuming it is the last tile-part of the codestream.");
                _specific_param.decoder.last_tile_part = true;
            }

            if (tcp.n_tile_parts != 0 && current_part >= tcp.n_tile_parts)
            {
                _cinfo.Error("In SOT marker, TPSot ({0}) is not valid regards to the current " +
                             "number of tile-part ({1}), giving up\n", current_part, tcp.n_tile_parts);
                _specific_param.decoder.last_tile_part = true;
                return false;
            }

            uint num_parts = _cio.ReadByte();

            if (num_parts != 0)
            {
                //Number of tile-part header is provided by this tile-part header
                num_parts += _specific_param.decoder.n_tile_parts_correction ? 1u : 0u;

                if (tcp.n_tile_parts != 0)
                {
                    if (current_part >= tcp.n_tile_parts)
                    {
                        _cinfo.Error("In SOT marker, TPSot ({0}) is not valid regards to the current " +
                                     "number of tile-part ({1}), giving up\n", current_part, tcp.n_tile_parts);
                        _specific_param.decoder.last_tile_part = true;
                        return false;
                    }
                }

                if (current_part >= num_parts)
                {
                    _cinfo.Error("In SOT marker, TPSot ({0}) is not valid regards to the current " +
                        "number of tile-part (header) ({0}), giving up", current_part, num_parts);
                    _specific_param.decoder.last_tile_part = true;
                    return false;
                }
                tcp.n_tile_parts = num_parts;
            }

            //If know the number of tile part header we will check if we didn't read the last
            if (tcp.n_tile_parts != 0)
            {
                if (tcp.n_tile_parts == current_part + 1)
                {
                    //Process the last tile-part header
                    _specific_param.decoder.can_decode = true;
                }
            }

            if (!_specific_param.decoder.last_tile_part)
            {
                //Keep the size of data to skip after this marker
                _specific_param.decoder.sot_length = (OPJ_UINT32)(tot_len - 12);
            }
            else
            {
                /* FIXME: need to be computed from the number of bytes remaining in the codestream */
                _specific_param.decoder.sot_length = 0;
            }
            _specific_param.decoder.state = J2K_STATUS.TPH;

            //Check if the current tile is outside the area we want decode or not corresponding to the tile index
            if (_specific_param.decoder.tile_ind_to_dec == -1)
            {
                _specific_param.decoder.skip_data =
                    tile_x < _specific_param.decoder.start_tile_x ||
                    tile_x >= _specific_param.decoder.end_tile_x ||
                    tile_y < _specific_param.decoder.start_tile_y ||
                    tile_y >= _specific_param.decoder.end_tile_y;
            }
            else
            {
                _specific_param.decoder.skip_data = _current_tile_number != _specific_param.decoder.tile_ind_to_dec;
            }

            if (_cstr_index != null)
            {
                _cstr_index.tile_index[_current_tile_number].tileno = _current_tile_number;
                _cstr_index.tile_index[_current_tile_number].current_tpsno = current_part;

                if (num_parts != 0)
                {
                    _cstr_index.tile_index[_current_tile_number].n_tps = num_parts;
                    _cstr_index.tile_index[_current_tile_number].current_n_tps = num_parts;

                    if (_cstr_index.tile_index[_current_tile_number].tp_index == null)
                        _cstr_index.tile_index[_current_tile_number].tp_index = new TPIndex[num_parts];
                    else
                        Array.Resize(ref _cstr_index.tile_index[_current_tile_number].tp_index, (int) num_parts);
                }
                else
                {
                    {
                        if (_cstr_index.tile_index[_current_tile_number].tp_index == null)
                        {
                            const int SIZE = 10;
                            _cstr_index.tile_index[_current_tile_number].current_n_tps = SIZE;
                            _cstr_index.tile_index[_current_tile_number].tp_index = new TPIndex[SIZE];
                        }

                        if (current_part >=
                                _cstr_index.tile_index[_current_tile_number].current_n_tps)
                        {
                            _cstr_index.tile_index[_current_tile_number].current_n_tps = current_part + 1;
                            Array.Resize(ref _cstr_index.tile_index[_current_tile_number].tp_index,
                                (int) _cstr_index.tile_index[_current_tile_number].current_n_tps);
                        }
                    }

                }
            }

            return true;
        }

        //2.5
        internal bool ReadSOD()
        {
            TileCodingParams tcp = _cp.tcps[_current_tile_number];
            bool sot_length_pb_detected = false;

            if (_specific_param.decoder.last_tile_part)
            {
                /* opj_stream_get_number_byte_left returns OPJ_OFF_T
                // but we are in the last tile part,
                // so its result will fit on OPJ_UINT32 unless we find
                // a file with a single tile part of more than 2 GB...*/
                _specific_param.decoder.sot_length = (uint)(_cio.BytesLeft - 2);
            }
            else
            {
                if (_specific_param.decoder.sot_length >= 2)
                    _specific_param.decoder.sot_length -= 2;
            }

            int current_read_size;
            byte[] current_data = tcp.data;
            int tile_len = tcp.data_size;
            
            if (_specific_param.decoder.sot_length != 0)
            {
                if (_specific_param.decoder.sot_length > _cio.BytesLeft)
                {
                    var str = "Tile part length size inconsistent with stream length";
                    if (_cp.strict)
                    {
                        _cinfo.Error(str);
                        return false;
                    }
                    else
                    {
                        _cinfo.Warn(str);
                    }
                }

                if (_specific_param.decoder.sot_length > 
                    int.MaxValue - Constants.COMMON_CBLK_DATA_EXTRA)
                {
                    _cinfo.Error("j2k._specific_param.decoder.sot_length > " +
                                 "INT_MAX - COMMON_CBLK_DATA_EXTRA");
                    return false;
                }

                if (current_data == null)
                {
                    current_data = new byte[_specific_param.decoder.sot_length + Constants.COMMON_CBLK_DATA_EXTRA];
                    tcp.data = current_data;
                }
                else
                {
                    if (tile_len > int.MaxValue - Constants.COMMON_CBLK_DATA_EXTRA -
                        _specific_param.decoder.sot_length)
                    {
                        _cinfo.Error("tile_len > INT_MAX - COMMON_CBLK_DATA_EXTRA -" +
                            "j2k._specific_param.decoder.sot_length");
                        return false;
                    }

                    Array.Resize<byte>(ref current_data, (int)(tile_len + _specific_param.decoder.sot_length + 
                                                         Constants.COMMON_CBLK_DATA_EXTRA));
                    tcp.data = current_data;
                }
            }
            else
            {
                sot_length_pb_detected = true;
            }

            // Index
            if (_cstr_index != null)
            {
                long current_pos = _cio.Pos - 2;

                OPJ_UINT32 current_tile_part =
                    _cstr_index.tile_index[_current_tile_number].current_tpsno;
                _cstr_index.tile_index[_current_tile_number].tp_index[current_tile_part].end_header
                    =
                        current_pos;
                _cstr_index.tile_index[_current_tile_number].tp_index[current_tile_part].end_pos
                    =
                        current_pos + _specific_param.decoder.sot_length + 2;

                AddTlmarker(_current_tile_number, J2K_Marker.SOD, current_pos,
                    _specific_param.decoder.sot_length + 2);
            }

            // Patch to support new PHR data
            if (!sot_length_pb_detected)
                current_read_size = _cio.Read(current_data, tile_len, (int)_specific_param.decoder.sot_length);
            else
                current_read_size = 0;

            if (current_read_size != _specific_param.decoder.sot_length)
                _specific_param.decoder.state = J2K_STATUS.NEOC;
            else
                _specific_param.decoder.state = J2K_STATUS.TPHSOT;

            //Org impl. sets l_tile_len, but that is a pointer at tcp.data_size
            tcp.data_size += current_read_size;

            return true;
        }

        /// <summary>
        /// Reads a TLM marker (Tile Length Marker)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_read_tlm</remarks>
        internal bool ReadTLM(int header_size)
        {
            if (header_size < 2)
            {
                _cinfo.Error("Error reading TLM marker");
                return false;
            }
            header_size -= 2;

            uint Ztlm = _cio.ReadByte();
            uint Stlm = _cio.ReadByte();
            uint ST = ((Stlm >> 4) & 0x03);
            uint SP = (Stlm >> 6) & 0x01;

            uint Ptlm_size = (SP + 1) * 2;
            uint quotient = Ptlm_size + ST;

            if (header_size % quotient != 0)
            {
                _cinfo.Error("Error reading TLM marker");
                return false;
            }
            _cio.Skip(header_size);
            return true;
        }

        /// <summary>
        /// Reads a PLM marker (Packet length, main header marker)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_read_plm</remarks>
        internal bool ReadPLM(int header_size)
        {
            if (header_size < 1)
            {
                _cinfo.Error("Error reading PLM marker");
                return false;
            }
            //int Zplm = _cio.ReadByte();
            _cio.Skip(header_size);
            return true;
        }

        internal void ReadEOC()
        {/*
            if (_cp.limit_decoding != LimitDecoding.DECODE_ALL_BUT_PACKETS)
            {
                var tcd = new TileCoder(_cinfo, _image, _cp);

                for (int i = 0; i < _cp.tileno_size; i++)
                {
                    var tileno = _cp.tileno[i];
                    var success = tcd.DecodeTile(tileno, _tile_data[tileno], _tile_len[tileno]);
                    _tile_data[tileno] = null;
                    if (success == false)
                    {
                        _state |= J2K_STATUS.ERR;
                        break;
                    }
                }
            }
            else
            {
                //Clears data, just because. 
                for (int i = 0; i < _cp.tileno_size; i++)
                {
                    _tile_data[_cp.tileno[i]] = null;
                }
            }
            if ((_state & J2K_STATUS.ERR) == J2K_STATUS.ERR)
                _state = J2K_STATUS.MT | J2K_STATUS.ERR;
            else
                _state = J2K_STATUS.MT;   */
            throw new NotImplementedException();
        }

        /// <summary>
        /// Reads a PLT marker (Packet length, tile-part header)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_read_plt</remarks>
        internal bool ReadPLT(int header_size)
        {
            if (header_size < 1)
            {
                _cinfo.Error("Error reading PLT marker");
                return false;
            }

            int Zplt = _cio.ReadByte();
            int packet_len = 0;
            header_size--;
            for (int i = 0; i < header_size; i++)
            {
                int add = _cio.ReadByte();
                packet_len |= add & 0x7f;
                if ((add & 0x80) != 0)
                    packet_len <<= 7;
                else
                    packet_len = 0;
            }

            if (packet_len != 0)
            {
                _cinfo.Error("Error reading PLT marker");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads a PPM marker (Packed packet headers, main header)
        /// </summary>
        /// <remarks>
        /// 2.1 - opj_j2k_read_ppm
        /// 
        /// Uppgrading to 2.5 isn't trivial, so we added an error check 
        /// (_cp.ppm_data == null) and called it a day.
        /// </remarks>
        internal bool ReadPPM(int header_size)
        {
            // We need to have the Z_ppm element + 1 byte of Nppm/Ippm at minimum
            if (header_size < 2)
            {
                _cinfo.Error("Error reading PPM marker");
                return false;
            }

            _cp.ppm = true;

            int Z_ppm = _cio.ReadByte();
            header_size--;

            // Check allocation needed
            // The following code is for 2.5
            if (_cp.ppm_markers == null)
            {//This is the first PPM;
                int new_count = Z_ppm + 1; // can't overflow, l_Z_ppm is byte

                _cp.ppm_markers = new PPX[new_count];
                _cp.ppm_markers_count = (uint)new_count;
            }
            else if (_cp.ppm_markers_count <= Z_ppm)
            {
                int new_count = Z_ppm + 1; // can't overflow, l_Z_ppm is byte
                Array.Resize(ref _cp.ppm_markers, new_count);
                _cp.ppm_markers_count = (uint)new_count;
            }

            if (_cp.ppm_markers[Z_ppm].Data != null)
            {
                _cinfo.Error("Zppm {0} already read", Z_ppm);
                return false;
            }

            _cp.ppm_markers[Z_ppm].Data = new byte[header_size];
            //_cp.ppm_markers[Z_ppm].DataSize = header_size;
            _cio.Read(_cp.ppm_markers[Z_ppm].Data, 0, header_size);

            return true;
        }

        /// <summary>
        /// Merges all PPM markers read (Packed headers, main header)
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_j2k_merge_ppm
        /// </remarks>
        internal bool MergePPM()
        {
            uint i, ppm_data_size, N_ppm_remaining;

            if (!_cp.ppm)
                return true;

            ppm_data_size = 0;
            N_ppm_remaining = 0;
            for (i = 0U; i < _cp.ppm_markers_count; ++i)
            {
                if (_cp.ppm_markers[i].Data != null)
                { // standard doesn't seem to require contiguous Zppm
                    OPJ_UINT32 N_ppm;
                    CIO data = new CIO(_cinfo, new MemoryStream(_cp.ppm_markers[i].Data), OpenMode.Read);

                    if (N_ppm_remaining >= data.BytesLeft)
                    {
                        N_ppm_remaining -= (uint) data.BytesLeft;
                        data.Skip((int)data.BytesLeft);
                    }
                    else
                    {
                        data.Skip((int) N_ppm_remaining);
                        N_ppm_remaining = 0U;
                    }

                    if (data.BytesLeft > 0U)
                    {
                        do
                        {
                            /* read Nppm */
                            if (data.BytesLeft < 4)
                            {
                                /* clean up to be done on l_cp destruction */
                                _cinfo.Error("Not enough bytes to read Nppm");
                                return false;
                            }
                            N_ppm = data.ReadUInt();
                            ppm_data_size +=
                                N_ppm; /* can't overflow, max 256 markers of max 65536 bytes, that is when PPM markers are not corrupted which is checked elsewhere */

                            if (data.BytesLeft >= N_ppm)
                            {
                                data.Skip((int) N_ppm);
                            }
                            else
                            {
                                N_ppm_remaining = N_ppm - (uint) data.BytesLeft;
                                data.Skip((int) data.BytesLeft);
                            }
                        } while (data.BytesLeft > 0);
                    }
                }
            }

            if (N_ppm_remaining != 0U)
            {
                /* clean up to be done on l_cp destruction */
                _cinfo.Error("Corrupted PPM markers");
                return false;
            }

            var ppm_buffer =  new byte[ppm_data_size];
            _cp.ppm_buffer_pt = 0;
            _cp.ppm_len = ppm_data_size;
            ppm_data_size = 0U;
            N_ppm_remaining = 0U;
            for (i = 0U; i < _cp.ppm_markers_count; ++i)
            {
                if (_cp.ppm_markers[i].Data != null)
                { /* standard doesn't seem to require contiguous Zppm */
                    OPJ_UINT32 N_ppm;
                    int data_size = (int) _cp.ppm_markers[i].DataSize;
                    byte[] data = _cp.ppm_markers[i].Data;
                    int data_pt = 0;

                    if (N_ppm_remaining >= data_size)
                    {
                        Buffer.BlockCopy(data, data_pt, ppm_buffer, _cp.ppm_buffer_pt + (int)ppm_data_size, (int) data_size);
                        ppm_data_size += (uint) data_size;
                        N_ppm_remaining -= (uint) data_size;
                        data_size = 0;
                    }
                    else
                    {
                        Buffer.BlockCopy(data, data_pt, ppm_buffer, _cp.ppm_buffer_pt + (int)ppm_data_size, (int)N_ppm_remaining);
                        ppm_data_size += N_ppm_remaining;
                        data_pt += (int) N_ppm_remaining;
                        data_size -= (int) N_ppm_remaining;
                        N_ppm_remaining = 0;
                    }

                    if (data_size > 0)
                    {
                        do
                        {
                            /* read Nppm */
                            if (data_size < 4)
                            {
                                /* clean up to be done on l_cp destruction */
                                _cinfo.Error("Not enough bytes to read Nppm");
                                return false;
                            }
                            N_ppm = CIO.ReadUInt(data, data_pt);
                            data_pt += 4;
                            data_size -= 4;

                            if (data_size >= N_ppm)
                            {
                                Buffer.BlockCopy(data, data_pt, ppm_buffer, _cp.ppm_buffer_pt + (int)ppm_data_size, (int)N_ppm);
                                ppm_data_size += N_ppm;
                                data_size -= (int) N_ppm;
                                data_pt += (int) N_ppm;
                            }
                            else
                            {
                                Buffer.BlockCopy(data, data_pt, ppm_buffer, _cp.ppm_buffer_pt + (int)ppm_data_size, (int)data_size);
                                ppm_data_size += (uint) data_size;
                                N_ppm_remaining = N_ppm - (uint) data_size;
                                data_size = 0;
                            }
                        } while (data_size > 0);
                    }
                    _cp.ppm_markers[i].Data = null;
                }
            }

            _cp.ppm_data = ppm_buffer;
            //_cp.ppm_data_size = _cp.ppm_len;

            _cp.ppm_markers = null;
            _cp.ppm_markers_count = 0U;

            return true;
        }

        /// <summary>
        /// Reads a PPT marker (Packed packet headers, tile-part header)
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_j2k_read_ppt
        /// </remarks>
        internal bool ReadPPT(int header_size)
        {
            //We need to have the Z_ppt element at minimum
            if (header_size < 1)
            {
                _cinfo.Error("Error reading PPT marker");
                return false;
            }

            if (_cp.ppm)
            {
                _cinfo.Error("Error reading PPT marker: packet header have been previously found in the main header (PPM marker).");
                return false;
            }

            TileCodingParams tcp = _cp.tcps[_current_tile_number];
            tcp.ppt = true;

            int Z_ppt = _cio.ReadByte();
            header_size--;

            //Allocate buffer to read the packet header
            if (tcp.ppt_markers == null)
            {   // First PPT marker
                int count = Z_ppt + 1;
                tcp.ppt_markers = new PPX[count];
                tcp.ppt_markers_count = (uint)count;
            }
            else
            {   // NON-first PPT marker
                int count = Z_ppt + 1;
                Array.Resize(ref tcp.ppt_markers, count);
                Array.Clear(tcp.ppt_markers, (int)tcp.ppt_markers_count, count - (int)tcp.ppt_markers_count);
                tcp.ppt_markers_count = (uint)count;
            }

            if (tcp.ppt_markers[Z_ppt].Data != null)
            {
                _cinfo.Error("Zppt {0} already read", Z_ppt);
                return false;
            }

            tcp.ppt_markers[Z_ppt].Data = new byte[header_size];

            //Read packet header
            _cio.Read(tcp.ppt_markers[Z_ppt].Data, 0, header_size);

            return true;
        }

        /// <summary>
        /// Reads a SIZ marker(image and tile size)
        /// </summary>
        /// <param name="header_size">The size of the data contained in the SIZ marker</param>
        /// <remarks>
        /// 2.5 - opj_j2k_read_siz
        /// </remarks>
        internal bool ReadSIZ(int header_size)
        {
            // minimum size == 39 - 3 (= minimum component parameter)
            if (header_size < 36)
            {
                _cinfo.Error("Error with SIZ marker size");
                return false;
            }

            int remaining_size = header_size - 36;
            uint n_comps = (uint)remaining_size / 3u;
            if (remaining_size % 3 != 0)
            {
                _cinfo.Error("Error with SIZ marker size");
                return false;
            }

            _cp.rsiz = (J2K_PROFILE) _cio.ReadUShort();

            _private_image.x1 = _cio.ReadUInt();
            _private_image.y1 = _cio.ReadUInt();
            _private_image.x0 = _cio.ReadUInt();
            _private_image.y0 = _cio.ReadUInt();
            _cp.tdx = _cio.ReadUInt();
            _cp.tdy = _cio.ReadUInt();
            _cp.tx0 = _cio.ReadUInt();
            _cp.ty0 = _cio.ReadUInt();
            {
                var tmp = _cio.ReadUShort();

                if (tmp < 16385)
                {
                    _private_image.numcomps = tmp;
                } 
                else
                {
                    _cinfo.Error("Error with SIZ marker: number of component is illegal -> {0}", tmp);
                    return false;
                }
            }

            if (_private_image.numcomps != n_comps)
            {
                _cinfo.Error("Error with SIZ marker: number of component is not compatible with the remaining number of parameters ({0} vs {1})", _private_image.numcomps, n_comps);
                return false;
            }

            if (_private_image.x0 >= _private_image.x1 || _private_image.y0 >= _private_image.y1)
            {
                _cinfo.Error("Error with SIZ marker: negative image size ({0} x {1})", (int)(_private_image.x1 - _private_image.x0), (int)(_private_image.y1 - _private_image.y0));
                return false;
            }

            if (_cp.tdx == 0 || _cp.tdy == 0)
            {
                _cinfo.Error("Error with SIZ marker: invalid tile size (tdx: {0}, tdy: {1})", _cp.tdx, _cp.tdy);
                return false;
            }

            
            var tx1 = MyMath.uint_adds(_cp.tx0, _cp.tdx); // manage overflow
            var ty1 = MyMath.uint_adds(_cp.ty0, _cp.tdy); // manage overflow
            if ((_cp.tx0 > _private_image.x0) || (_cp.ty0 > _private_image.y0) ||
                    (tx1 <= _private_image.x0) || (ty1 <= _private_image.y0))
            {
                _cinfo.Error("Error with SIZ marker: illegal tile offset");
                return false;
            }
#if SUPPORT_DUMP_FLAG
            if (!_dump_state)
#endif
            {
                uint siz_w = (uint) (_private_image.x1 - _private_image.x0);
                uint siz_h = (uint) (_private_image.y1 - _private_image.y0);

                if (_ihdr_w > 0 && _ihdr_h > 0
                        && (_ihdr_w != siz_w || _ihdr_h != siz_h))
                {
                    _cinfo.Error("Error with SIZ marker: IHDR w({0}) h({1}) vs. SIZ w({2}) h({3})", _ihdr_w,
                                  _ihdr_h, siz_w, siz_h);
                    return false;
                }
            }

            // Allocate the resulting image components
            _private_image.comps = new ImageComp[_private_image.numcomps];

            // Read the component information
            OPJ_UINT32 prec0 = 0;
            bool sgnd0 = false;
            for (int i = 0; i < _private_image.comps.Length; i++)
            {
                var comp = new ImageComp();
                uint tmp = _cio.ReadByte();
                comp.prec = (tmp & 0x7f) + 1;
                comp.sgnd = (tmp >> 7) == 1;
#if SUPPORT_DUMP_FLAG
                if (!_dump_state)
#endif
                {
                    if (i == 0)
                    {
                        prec0 = comp.prec;
                        sgnd0 = comp.sgnd;
                    }
                    else if (!_cp.AllowDifferentBitDepthSign
                               && (comp.prec != prec0 || comp.sgnd != sgnd0))
                    {
                        _cinfo.Warn("Despite JP2 BPC!=255, precision and/or sgnd values for comp[{0}] is different than comp[0]:\n"+
                                      "        [0] prec({1}) sgnd({2}) [{3}] prec({4}) sgnd({5})", i, prec0, sgnd0,
                                      i, comp.prec, comp.sgnd);
                    }
                    // TODO: we should perhaps also check against JP2 BPCC values
                }

                comp.dx = _cio.ReadByte();
                comp.dy = _cio.ReadByte();

                // C# note: Org impl. checks if > 255, which is pointless.
                if (comp.dx == 0 || comp.dy == 0)
                {
                    _cinfo.Error("Invalid values for comp = {0} : dx={1} dy={2}\n (should be between 1 and 255 according the JPEG2000 norm)", i, comp.dx, comp.dy);
                    return false;
                }

                /* Avoids later undefined shift in computation of */
                /* p_j2k->m_specific_param.m_decoder.m_default_tcp->tccps[i].m_dc_level_shift = 1
                            << (l_image->comps[i].prec - 1); */
                if (comp.prec > 31)
                {
                    _cinfo.Error("Invalid values for comp = {0} : prec={1} (should be between 1 and 38 according to the JPEG2000 norm. OpenJpeg only supports up to 31)",
                        i, comp.prec);
                    return false;
                }

                comp.resno_decoded = 0; // number of resolution decoded
                comp.factor = _cp.specific_param.dec.reduce; // reducing factor per component

                _private_image.comps[i] = comp;
            }

            if (_cp.tdx == 0 || _cp.tdy == 0)
            {
                return false;
            }

            //Computes the number of tiles
            _cp.tw = (uint) MyMath.int_ceildiv((int)(_private_image.x1 - _cp.tx0), (int)_cp.tdx);
            _cp.th = (uint) MyMath.int_ceildiv((int)(_private_image.y1 - _cp.ty0), (int)_cp.tdy);

            //Check that the number of tiles is valid
            if (_cp.tw == 0 || _cp.th == 0 || _cp.tw > 65535 / _cp.th)
            {
                _cinfo.Error("Invalid number of tiles : {0} x {1} (maximum fixed by jpeg2000 norm is 65535 tiles)", _cp.tw, _cp.th);
                return false;
            }
            uint n_tiles = _cp.tw * _cp.th;

            //Define the tiles which will be decoded
            if (_specific_param.decoder.discard_tiles)
            {
                _specific_param.decoder.start_tile_x = ((_specific_param.decoder.start_tile_x - _cp.tx0) / _cp.tdx);
                _specific_param.decoder.start_tile_y = ((_specific_param.decoder.start_tile_y - _cp.ty0) / _cp.tdy);
                _specific_param.decoder.end_tile_x = (OPJ_UINT32)MyMath.int_ceildiv((int)(_specific_param.decoder.end_tile_x - _cp.tx0), (int) _cp.tdx);
                _specific_param.decoder.end_tile_y = (OPJ_UINT32)MyMath.int_ceildiv((int)(_specific_param.decoder.end_tile_y - _cp.ty0), (int) _cp.tdy);
            }
            else
            {
                _specific_param.decoder.start_tile_x = 0;
                _specific_param.decoder.start_tile_y = 0;
                _specific_param.decoder.end_tile_x = _cp.tw;
                _specific_param.decoder.end_tile_y = _cp.th;
            }

            // memory allocations
            _cp.tcps = new TileCodingParams[n_tiles];
            _specific_param.decoder.default_tcp = new TileCodingParams();
            _specific_param.decoder.default_tcp.tccps = TileCompParams.Create(_private_image.numcomps);
            _specific_param.decoder.default_tcp.mct_records = new MctData[Constants.MCT_DEFAULT_NB_RECORDS];
            _specific_param.decoder.default_tcp.n_max_mct_records = Constants.MCT_DEFAULT_NB_RECORDS;
            _specific_param.decoder.default_tcp.mcc_records = new SimpleMccDecorrelationData[Constants.MCC_DEFAULT_NB_RECORDS];
            _specific_param.decoder.default_tcp.n_max_mcc_records = Constants.MCC_DEFAULT_NB_RECORDS;

            // Set up default dc level shift
            for (int i = 0; i < _private_image.numcomps; i++)
            {
                if (!_private_image.comps[i].sgnd)
                    _specific_param.decoder.default_tcp.tccps[i].dc_level_shift = 1 << ((int)_private_image.comps[i].prec - 1);
            }

            for (int c = 0; c < _cp.tcps.Length; c++)
            {
                var tcp = new TileCodingParams();

                //C# impl. note.
                //The SOT marker will overwrite these, so one might
                //get away with a "new TileCompParams[_image.numcomps]"
                tcp.tccps = TileCompParams.Create(_private_image.numcomps);

                _cp.tcps[c] = tcp;
            }

            _specific_param.decoder.state = J2K_STATUS.MH;
            _private_image.CompHeaderUpdate(_cp);

            return true;
        }

        /// <summary>
        /// Reads a COD marker (Coding style defaults)
        /// </summary>
        /// <param name="header_size">The size of the data contained in the COD marker</param>
        /// <returns>True on success</returns>
        /// <remarks>
        /// 2.5 - opj_j2k_read_cod
        /// </remarks>
        internal bool ReadCOD(int header_size)
        {
            var tcp = _specific_param.decoder.state == J2K_STATUS.TPH ? 
                _cp.tcps[_current_tile_number] : _specific_param.decoder.default_tcp;
            tcp.cod = true;

            if (header_size < 5)
            {
                _cinfo.Error("Error reading COD marker");
                return false;
            }

            tcp.csty = (CP_CSTY) _cio.ReadByte();
            tcp.prg = (PROG_ORDER)_cio.ReadByte();

            if (tcp.prg > PROG_ORDER.CPRL)
            {
                _cinfo.Error("Unknown progression order in COD marker");
                tcp.prg = PROG_ORDER.PROG_UNKNOWN;
            }

            tcp.numlayers = _cio.ReadUShort();
            if (tcp.numlayers == 0)
            {
                _cinfo.Error("Invalid number of layers in COD marker : {0} not in range [1-65535]",
                    tcp.numlayers);
                return false;
            }

            //If user didn't set a number layer to decode take the max specify in the codestream.
            if (_cp.specific_param.dec.layer != 0)
                tcp.num_layers_to_decode = _cp.specific_param.dec.layer;
            else
                tcp.num_layers_to_decode = tcp.numlayers;

            tcp.mct = _cio.ReadByte();
            if (tcp.mct > 1)
            {
                _cinfo.Error("Invalid multiple component transformation");
                return false;
            }

            header_size -= 5;
            for (int i = 0; i < _private_image.numcomps; i++)
                tcp.tccps[i].csty = tcp.csty & CP_CSTY.PRT;

            if (!ReadSPCod_SPCoc(0, ref header_size))
            {
                _cinfo.Error("Error reading COD marker");
                return false;
            }

            if (header_size != 0)
            {
                _cinfo.Error("Error reading COD marker");
                return false;
            }

            CopyTileComponentParameters();

            return true;
        }

        //2.5
        void CopyTileComponentParameters()
        {
            var tcp = _specific_param.decoder.state == J2K_STATUS.TPH ? _cp.tcps[_current_tile_number] : _specific_param.decoder.default_tcp;

            var tccps = tcp.tccps;
            var ref_tccp = tccps[0];
            var prc_size = ref_tccp.numresolutions * sizeof(int);

            for (int i = 1; i < _private_image.numcomps; i++)
            {
                var copied_tccp = tccps[i];
                copied_tccp.numresolutions = ref_tccp.numresolutions;
                copied_tccp.cblkw = ref_tccp.cblkw;
                copied_tccp.cblkh = ref_tccp.cblkh;
                copied_tccp.cblksty = ref_tccp.cblksty;
                copied_tccp.qmfbid = ref_tccp.qmfbid;
                Buffer.BlockCopy(ref_tccp.prcw, 0, copied_tccp.prcw, 0, (int)prc_size);
                Buffer.BlockCopy(ref_tccp.prch, 0, copied_tccp.prch, 0, (int)prc_size);
            }
        }

        //2.5 - opj_j2k_read_SPCod_SPCoc
        internal bool ReadSPCod_SPCoc(int compno, ref int header_size)
        {
            var tcp = _specific_param.decoder.state == J2K_STATUS.TPH ? _cp.tcps[_current_tile_number] : _specific_param.decoder.default_tcp;
            var tccp = tcp.tccps[compno];

            if (header_size < 5)
            {
                _cinfo.Error("Error reading SPCod SPCoc element");
                return false;
            }

            tccp.numresolutions = _cio.ReadByte() + 1u;
            if (tccp.numresolutions > Constants.J2K_MAXRLVLS)
            {
                _cinfo.Error("Invalid value for numresolutions : {0}, max value is set in Constants.cs at {1}",
                    tccp.numresolutions, Constants.J2K_MAXRLVLS);
                return false;
            }

            if (_cp.specific_param.dec.reduce >= tccp.numresolutions)
            {
                _cinfo.Error("Error decoding component {0}.\n"+
                    "The number of resolutions to remove is higher than the number "+
                    "of resolutions of this component\nModify the cp_reduce parameter.\n", compno);
                _specific_param.decoder.state |= J2K_STATUS.ERR;
                return false;
            }

            tccp.cblkw = _cio.ReadByte() + 2u;
            tccp.cblkh = _cio.ReadByte() + 2u;

            if (tccp.cblkw > 10 || tccp.cblkh > 10 || tccp.cblkw + tccp.cblkh > 12)
            {
                _cinfo.Error("Error reading SPCod SPCoc element, Invalid cblkw/cblkh combination");
                return false;
            }

            tccp.cblksty = (CCP_CBLKSTY) _cio.ReadByte();

            if ((tccp.cblksty & CCP_CBLKSTY.HTMIXED) != 0)
            {
                _cinfo.Error("Error reading SPCod SPCoc element. Unsupported Mixed HT code-block style found");
                return false;
            }

            tccp.qmfbid = _cio.ReadByte();

            if (tccp.qmfbid > 1)
            {
                _cinfo.Error("Error reading SPCod SPCoc element, Invalid transformation found");
                return false;
            }

            header_size -= 5;

            if ((tccp.csty & CP_CSTY.PRT) == CP_CSTY.PRT)
            {
                if (header_size < tccp.numresolutions)
                {
                    _cinfo.Error("Error reading SPCod SPCoc element");
                    return false;
                }

                for (int i = 0; i < tccp.numresolutions; i++)
                {
                    uint tmp = _cio.ReadByte();
                    //Precinct exponent 0 is only allowed for lowest resolution level (Table A.21)
                    if (i != 0 && ((tmp & 0xf) == 0 || (tmp >> 4) == 0))
                    {
                        _cinfo.Error("Invalid precinct size");
                        return false;
                    }
                    tccp.prcw[i] = tmp & 0xf;
                    tccp.prch[i] = tmp >> 4;
                }

                header_size -= (int) tccp.numresolutions;
            }
            else
            {
                //Set default size for the precinct width and height
                for (int i = 0; i < tccp.numresolutions; i++)
                {
                    tccp.prcw[i] = 15;
                    tccp.prch[i] = 15;
                }
            }

            return true;
        }

        //2.5 - opj_j2k_read_SQcd_SQcc
        internal bool ReadSQcd_SQcc(int compno, ref int header_size)
        {
            var tcp = _specific_param.decoder.state == J2K_STATUS.TPH ? _cp.tcps[_current_tile_number] : _specific_param.decoder.default_tcp;
            var tccp = tcp.tccps[compno];

            if (header_size < 1)
            {
                _cinfo.Error("Error reading SQcd or SQcc element");
                return false;
            }
            header_size -= 1;

            uint tmp = _cio.ReadByte();
            tccp.qntsty = (CCP_QNTSTY) (tmp & 0x1f);
            tccp.numgbits = tmp >> 5;
            int num_bands;
            if (tccp.qntsty == CCP_QNTSTY.SIQNT)
                num_bands = 1;
            else
            {
                num_bands = tccp.qntsty == CCP_QNTSTY.NOQNT ? header_size : header_size / 2;

                if (num_bands > Constants.J2K_MAXBANDS)
                {
                    _cinfo.Warn("While reading CCP_QNTSTY element inside QCD or QCC marker segment, " +
                        "number of subbands ({0}) is greater to OPJ_J2K_MAXBANDS ({1}). So we limit the number of elements stored to " +
                         "OPJ_J2K_MAXBANDS ({1}) and skip the rest. ", num_bands, Constants.J2K_MAXBANDS);
                }
            }

            if (tccp.qntsty == CCP_QNTSTY.NOQNT)
            {
                for (int bandno = 0; bandno < num_bands; bandno++)
                {
                    tmp = _cio.ReadByte();
                    if (bandno < Constants.J2K_MAXBANDS)
                        tccp.stepsizes[bandno] = new StepSize((int)(tmp >> 3), 0);
                }
                header_size -= num_bands;
            }
            else
            {
                for (int bandno = 0; bandno < num_bands; bandno++)
                {
                    tmp = _cio.ReadUShort();
                    if (bandno < Constants.J2K_MAXBANDS)
                        tccp.stepsizes[bandno] = new StepSize((int)(tmp >> 11), (int)(tmp & 0x7ff));
                }
                header_size -= 2 * num_bands;
            }

            // if scalar_derived -> compute other stepsizes
            if (tccp.qntsty == CCP_QNTSTY.SIQNT)
            {
                for (int bandno = 1; bandno < Constants.J2K_MAXBANDS; bandno++)
                {
                    int tmp2 = (bandno - 1) / 3;
                    tccp.stepsizes[bandno] = new StepSize(
                        tccp.stepsizes[0].expn - tmp2 > 0 ?
                            tccp.stepsizes[0].expn - tmp2 : 0, tccp.stepsizes[0].mant);
                }
            }

            return true;
        }

        /// <summary>
        /// Gets the maximum size taken by a coc
        /// </summary>
        /// <returns>2.5 - opj_j2k_get_max_coc_size</returns>
        uint GetMaxCocSize()
        {
            uint max = 0;

            uint n_tiles = _cp.tw * _cp.th;
            uint n_comp = (uint) _private_image.numcomps;

            for (uint i = 0; i < n_tiles; i++)
            {
                for (uint j = 0; j < n_comp; ++j)
                {
                    max = Math.Max(max, GetSPCodSPCocSize(i, j));
                }
            }

            return 6 + max;
        }

        /// <summary>
        /// Reads a COC marker (Coding Style Component)
        /// </summary>
        /// <param name="header_size">The size of the data contained in the COC marker</param>
        /// <remarks>2.5 - opj_j2k_read_coc</remarks>
        internal bool ReadCOC(int header_size)
        {
            var tcp = _specific_param.decoder.state == J2K_STATUS.TPH ? _cp.tcps[_current_tile_number] : 
                _specific_param.decoder.default_tcp;

            int comp_room = _private_image.numcomps <= 256 ? 1 : 2;
            if (header_size < comp_room + 1)
            {
                _cinfo.Error("Error reading COC marker");
                return false;
            }
            header_size -= comp_room + 1;

            int compno = (comp_room == 1) ? _cio.ReadByte() : _cio.ReadUShort();
            if (compno >= _private_image.numcomps)
            {
                _cinfo.Error("Error reading COC marker (bad number of components)");
                return false;
            }
            tcp.tccps[compno].csty = (CP_CSTY)_cio.ReadByte();

            if (!ReadSPCod_SPCoc(compno, ref header_size) || header_size != 0)
            {
                _cinfo.Error("Error reading COC marker");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads a CRG marker (Component registration)
        /// </summary>
        internal bool ReadCRG(int header_size)
        {
            uint numcomps = _private_image.numcomps;
            if (header_size != numcomps * 4)
            {
                _cinfo.Error("Error reading CRG marker");
                return false;
            }

            _cio.Skip(header_size);
            return true;
        }

        /// <summary>
        /// Reads a COM marker (comments)
        /// </summary>
        //2.5
        internal bool ReadCOM(int header_size)
        {
            _cio.Skip(header_size);
            return true;
        }

        /// <summary>
        /// Reads a CBD marker (Component bit depth definition)
        /// </summary>
        //2.1
        internal bool ReadCBD(int header_size)
        {
            OPJ_UINT32 n_comps, num_comps;

            num_comps = _private_image.numcomps;

            if (header_size != (_private_image.numcomps + 2))
            {
                _cinfo.Error("Error reading CBD marker");
                return false;
            }

            n_comps = _cio.ReadUShort();                             /* Ncbd */

            if (n_comps != num_comps)
            {
                _cinfo.Error("Error reading CBD marker");
                return false;
            }

            for (int i = 0; i < num_comps; i++)
            {
                ImageComp comp = _private_image.comps[i];
                OPJ_UINT32 comp_def = _cio.ReadByte();                    /* Component bit depth */
                comp.sgnd = ((comp_def >> 7) & 1) != 0;
                comp.prec = (comp_def & 0x7f) + 1;
            }

            return true;
        }

        /// <summary>
        /// Reads a CAP marker (extended capabilities definition). Empty implementation.
        /// Found in HTJ2K files.
        /// </summary>
        /// <remarks>
        /// 2.5
        /// </remarks>
        internal bool ReadCAP(int header_size)
        {
            _cio.Skip(header_size);

            return true;
        }

        /// <summary>
        /// Reads a MCO marker (Multiple Component Transform Ordering)
        /// </summary>
        //2.1
        internal bool ReadMCO(int p_header_size)
        {
            OPJ_UINT32 l_tmp, i;
            OPJ_UINT32 l_nb_stages;
            TileCodingParams l_tcp;
            TileCompParams l_tccp;

            l_tcp = _specific_param.decoder.state == J2K_STATUS.TPH ?
                            _cp.tcps[_current_tile_number] :
                            _specific_param.decoder.default_tcp;

            if (p_header_size < 1)
            {
                _cinfo.Error("Error reading MCO marker");
                return false;
            }

            l_nb_stages = _cio.ReadByte();                           /* Nmco : only one tranform stage*/

            if (l_nb_stages > 1)
            {
                _cinfo.Warn("Cannot take in charge multiple transformation stages.");
                _cio.Skip(p_header_size - 1);
                return true;
            }

            if (p_header_size != l_nb_stages + 1)
            {
                _cinfo.Error("Error reading MCO marker");
                return false;
            }

            for (i = 0; i < _private_image.numcomps; ++i)
            {
                l_tcp.tccps[i].dc_level_shift = 0;
            }

            if (l_tcp.mct_decoding_matrix != null)
            {
                l_tcp.mct_decoding_matrix = null;
            }

            for (i = 0; i < l_nb_stages; ++i)
            {
                l_tmp = _cio.ReadByte();

                if (!AddMCT(l_tcp, _private_image, l_tmp))
                {
                    return false;
                }
            }

            return true;
        }

        //2.5 - opj_j2k_add_mct
        bool AddMCT(TileCodingParams tcp, JPXImage image, OPJ_UINT32 index)
        {
            OPJ_UINT32 i;
            SimpleMccDecorrelationData mcc_record;
            ArPtr<MctData> deco_array, offset_array_ptr;
            OPJ_UINT32 data_size,mct_size, offset_size;
            OPJ_UINT32 nb_elem;
            OPJ_UINT32[] offset_data;
            TileCompParams tccp;
            mcc_record = tcp.mcc_records[0];

            for (i=0;i<tcp.n_mcc_records;i++) {
                mcc_record = tcp.mcc_records[i];
                if (mcc_record.index == index) {
                    break;
                }
            }

            if (i == tcp.n_mcc_records) {
                // element discarded
                return true;
            }

            if (mcc_record.n_comps != image.numcomps) {
                // do not support number of comps != image
                return true;
            }

            deco_array = mcc_record.decorrelation_array;

            if (deco_array != null)
            {
                data_size = MCT.ELEMENT_SIZE[(int) deco_array.Deref.element_type] * image.numcomps * image.numcomps;
                if (deco_array.Deref.data_size != data_size) {
                        return false;
                }

                nb_elem = image.numcomps * image.numcomps;
                mct_size = nb_elem;
                tcp.mct_decoding_matrix = new float[mct_size];

                deco_array.Deref.data.CopyToFloat(tcp.mct_decoding_matrix, (int) mct_size);
            }

            offset_array_ptr = mcc_record.offset_array;

            if (offset_array_ptr != null) 
            {
                data_size = MCT.ELEMENT_SIZE[(int) offset_array_ptr.Deref.element_type] * image.numcomps;
                if (offset_array_ptr.Deref.data_size != data_size) {
                    return false;
                }

                nb_elem = image.numcomps;
                offset_size = nb_elem;
                offset_data = new uint[offset_size];

                deco_array.Deref.data.CopyToInt(offset_data, (int)offset_size);

                int current_offset_data = 0;

                for (i=0;i<image.numcomps;++i) 
                {
                    tccp = tcp.tccps[i];
                    tccp.dc_level_shift = (int) offset_data[current_offset_data++];
                }
            }

            return true;
        }

        /// <summary>
        /// Reads a MCC marker (Multiple Component Collection)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_read_mcc</remarks>
        internal bool ReadMCC(int header_size)
        {
            OPJ_UINT32 i, j;
            OPJ_UINT32 tmp;
            OPJ_UINT32 indix;
            TileCodingParams tcp;
            SimpleMccDecorrelationData[] mcc_records;
            SimpleMccDecorrelationData mcc_record;
            OPJ_UINT32 n_collections;
            OPJ_UINT32 n_comps;
            OPJ_UINT32 n_bytes_by_comp;
            bool new_mcc = false;

            tcp = _specific_param.decoder.state == J2K_STATUS.TPH ?
                            _cp.tcps[_current_tile_number] :
                            _specific_param.decoder.default_tcp;

            if (header_size < 2)
            {
                _cinfo.Error("Error reading MCC marker");
                return false;
            }

            // first marker
            tmp = _cio.ReadUShort();                         // Zmcc
            if (tmp != 0)
            {
                _cinfo.Error("Cannot take in charge multiple data spanning");
                _cio.Skip(header_size - 2);
                return true;
            }

            if (header_size < 7)
            {
                _cinfo.Error("Error reading MCC marker");
                return false;
            }

            indix = _cio.ReadByte(); // Imcc . no need for other values, take the first

            mcc_records = tcp.mcc_records;

            for (i = 0; i < tcp.n_mcc_records; ++i)
            {
                mcc_record = mcc_records[i];
                if (mcc_record.index == indix)
                {
                    break;
                }
            }

            // NOT FOUND
            if (i == tcp.n_mcc_records)
            {
                if (tcp.n_mcc_records == tcp.n_max_mcc_records)
                {
                    tcp.n_max_mcc_records += Constants.MCC_DEFAULT_NB_RECORDS;
                    Array.Resize(ref tcp.mcc_records, (int)tcp.n_max_mcc_records);
                    mcc_records = tcp.mcc_records;
                    for (uint c = i; i < mcc_records.Length; c++)
                        mcc_records[c] = new SimpleMccDecorrelationData();
                }
                i = tcp.n_mcc_records;
                new_mcc = true;
            }
            mcc_record = mcc_records[i];
            mcc_record.index = indix;

            // only one marker atm
            tmp = _cio.ReadUShort();                         // Ymcc
            if (tmp != 0)
            {
                _cinfo.Warn("Cannot take in charge multiple data spanning");
                _cio.Skip(header_size - 5);
                return true;
            }

            n_collections = _cio.ReadUShort();                              // Qmcc -> number of collections -> 1

            if (n_collections > 1)
            {
                _cinfo.Warn("Cannot take in charge multiple data spanning");
                _cio.Skip(header_size - 7);
                return true;
            }

            header_size -= 7;

            for (i = 0; i < n_collections; ++i)
            {
                if (header_size < 3)
                {
                    _cinfo.Error("Error reading MCC marker");
                    return false;
                }

                tmp = _cio.ReadByte(); // Xmcci type of component transformation -> array based decorrelation

                if (tmp != 1)
                {
                    _cinfo.Warn("Cannot take in charge collections other than array decorrelation");
                    _cio.Skip(header_size - 1);
                    return true;
                }

                n_comps = _cio.ReadUShort();;

                header_size -= 3;

                n_bytes_by_comp = 1 + (n_comps >> 15);
                mcc_record.n_comps = n_comps & 0x7fff;

                if (header_size < (n_bytes_by_comp * mcc_record.n_comps + 2))
                {
                    _cinfo.Error("Error reading MCC marker");
                    return false;
                }

                header_size -= (int) (n_bytes_by_comp * mcc_record.n_comps + 2);

                for (j = 0; j < mcc_record.n_comps; ++j)
                {
                    tmp = _cio.ReadByte();        // Cmccij Component offset

                    if (tmp != j)
                    {
                        _cinfo.Warn("Cannot take in charge collections with indix shuffle");
                        _cio.Skip((int)(header_size + mcc_record.n_comps - j));
                        return true;
                    }
                }

                n_comps = _cio.ReadUShort();

                n_bytes_by_comp = 1 + (n_comps >> 15);
                n_comps &= 0x7fff;

                if (n_comps != mcc_record.n_comps)
                {
                    _cinfo.Warn("Cannot take in charge collections without same number of indixes");
                    _cio.Skip(header_size - 2);
                    return true;
                }

                if (header_size < (n_bytes_by_comp * mcc_record.n_comps + 3))
                {
                    _cinfo.Error("Error reading MCC marker");
                    return false;
                }

                header_size -= (int)(n_bytes_by_comp * mcc_record.n_comps + 3);

                for (j = 0; j < mcc_record.n_comps; ++j)
                {
                    tmp = _cio.Read(n_bytes_by_comp);        // Wmccij Component offset

                    if (tmp != j)
                    {
                        _cinfo.Warn("Cannot take in charge collections with indix shuffle");
                        _cio.Skip((int)(header_size + (mcc_record.n_comps - j) * n_bytes_by_comp));
                        return true;
                    }
                }

                tmp = _cio.Read(3u); /* Wmccij Component offset*/

                mcc_record.is_irreversible = ((tmp >> 16) & 1) != 0;
                mcc_record.decorrelation_array = null;
                mcc_record.offset_array = null;

                indix = tmp & 0xff;
                if (indix != 0)
                {
                    for (j = 0; j < tcp.n_mct_records; j++)
                    {
                        var mct_data = tcp.mct_records[j];
                        if (mct_data.index == indix)
                        {
                            mcc_record.decorrelation_array = new ArPtr<MctData>(tcp.mct_records, (int)j);
                            break;
                        }
                    }

                    if (mcc_record.decorrelation_array == null)
                    {
                        _cinfo.Error("Error reading MCC marker");
                        return false;
                    }
                }

                indix = (tmp >> 8) & 0xff;
                if (indix != 0)
                {
                    for (j = 0; j < tcp.n_mct_records; ++j)
                    {
                        var mct_data = tcp.mct_records[j];
                        if (mct_data.index == indix)
                        {
                            mcc_record.offset_array = new ArPtr<MctData>(tcp.mct_records, (int)j);
                            break;
                        }
                    }

                    if (mcc_record.offset_array == null)
                    {
                        _cinfo.Error("Error reading MCC marker");
                        return false;
                    }
                }
            }

            if (header_size != 0)
            {
                _cinfo.Error("Error reading MCC marker");
                return false;
            }

            if (new_mcc)
                tcp.n_mcc_records++;

            return true;
        }

        /// <summary>
        /// Reads a MCT marker (Multiple Component Transform)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_read_mct</remarks>
        internal bool ReadMCT(int header_size)
        {
            OPJ_UINT32 i;
            TileCodingParams tcp;
            OPJ_UINT32 tmp;
            OPJ_UINT32 indix;
            MctData mct_data;

            tcp = _specific_param.decoder.state == J2K_STATUS.TPH ?
                            _cp.tcps[_current_tile_number] :
                            _specific_param.decoder.default_tcp;

            if (header_size < 2)
            {
                _cinfo.Error("Error reading MCT marker");
                return false;
            }

            // first marker
            tmp = _cio.ReadUShort(); // Zmct
            if (tmp != 0)
            {
                _cinfo.Error("Cannot take in charge mct data within multiple MCT records");
                return true;
            }

            if (header_size <= 6)
            {
                _cinfo.Error("Error reading MCT marker");
                return false;
            }

            // Imct -> no need for other values, take the first, type is double with decorrelation x0000 1101 0000 0000
            tmp = _cio.ReadUShort();                         // Imct

            indix = tmp & 0xff;
            var l_mct_data_ar = tcp.mct_records;
            mct_data = l_mct_data_ar[0];

            for (i = 0; i < tcp.n_mct_records; ++i)
            {
                Debug.Assert(l_mct_data_ar[i] != null, "Whops, forgot to initialize");
                mct_data = l_mct_data_ar[i];
                if (mct_data.index == indix)
                {
                    break;
                }
            }

            /* NOT FOUND */
            if (i == tcp.n_mct_records)
            {
                if (tcp.n_mct_records == tcp.n_max_mct_records)
                {
                    tcp.n_max_mct_records += Constants.MCT_DEFAULT_NB_RECORDS;

                    Array.Resize<MctData>(ref tcp.mct_records, (int) tcp.n_max_mct_records);
                    mct_data = tcp.mct_records[tcp.n_mct_records];
                    for (uint c = tcp.n_mct_records; c < tcp.n_max_mct_records; c++)
                        tcp.mct_records[c] = new MctData();
                }

                mct_data = tcp.mct_records[tcp.n_mct_records];
                ++tcp.n_mct_records;
            }

            if (!mct_data.data.IsNull)
            {
                mct_data.data.Null();
            }

            mct_data.index = (int)indix;
            mct_data.array_type = (MCT_ARRAY_TYPE)((tmp >> 8) & 3);
            mct_data.element_type = (MCT_ELEMENT_TYPE)((tmp >> 10) & 3);

            tmp = _cio.ReadUShort();                         // Ymct
            if (tmp != 0)
            {
                _cinfo.Warn("Cannot take in charge multiple MCT markers");
                return true;
            }

            header_size -= 6;

            mct_data.data = new ShortOrIntOrFloatOrDoubleAr(mct_data.element_type,
                ShortOrIntOrFloatOrDoubleAr.SizeDiv(mct_data.element_type, header_size));
            mct_data.data_size = header_size;
            byte[] tmp_buffer = new byte[header_size];
            _cio.Read(tmp_buffer, 0, header_size);
            mct_data.data.SetBytes(tmp_buffer);

            return true;
        }

        //2.5 - opj_j2k_read_unk
        internal bool ReadUNK(out J2K_Marker next_marker)
        {
            _cinfo.Warn("Unknown marker");
            uint l_size_unk = 2;

            while (true)
            {
                if (_cio.BytesLeft < 2)
                {
                    _cinfo.Error("Stream too short");
                    next_marker = J2K_Marker.NONE;
                    return false;
                }
                next_marker = (J2K_Marker)_cio.ReadUShort();

                if (!((ushort)next_marker < 0xFF00))
                {
                    var handler = _dec_tab[next_marker];
                    if ((_specific_param.decoder.state & handler.States) == J2K_STATUS.NONE)
                    {
                        _cinfo.Error("Marker is not compliant with its position");
                        return false;
                    }
                    else
                    {
                        if (handler.Mark != J2K_Marker.NONE)
                        {
                            // next marker is known and well located
                            break;
                        }
                        else
                        {
                            l_size_unk += 2;
                        }
                    }
                }      
            }

            return true;
        }

        /// <summary>
        ///  Reads a RGN marker (Region Of Interest)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_read_rgn</remarks>
        internal bool ReadRGN(int header_size)
        {
            int comp_room = (_private_image.numcomps <= 256) ? 1 : 2;
            if (header_size != 2 + comp_room)
            {
                _cinfo.Error("Error reading RGN marker");
                return false;
            }
            var tcp = _specific_param.decoder.state == J2K_STATUS.TPH ? 
                _cp.tcps[_current_tile_number] : 
                _specific_param.decoder.default_tcp;
            int compno = (comp_room == 1) ? _cio.ReadByte() : _cio.ReadUShort();
            int roisty = _cio.ReadByte();
            if (compno >= _private_image.numcomps)
            {
                _cinfo.Error("bad component number in RGN ({0} when there are only {1})",
                    compno, _private_image.numcomps);
                return false;
            }
            tcp.tccps[compno].roishift = _cio.ReadByte();
            return true;
        }

        /// <summary>
        /// Reads a QCD marker (Quantization defaults)
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_j2k_read_qcd
        /// </remarks>
        internal bool ReadQCD(int header_size)
        {
            if (!ReadSQcd_SQcc(0, ref header_size))
            {
                _cinfo.Error("Error reading QCD marker");
                return false;
            }

            if (header_size != 0)
            {
                _cinfo.Error("Error reading QCD marker");
                return false;
            }

            // Apply the quantization parameters to other components of the current tile
            // or the m_default_tcp
            CopyTileQuantizationParameters();

            return true;
        }

        //2.5
        void CopyTileQuantizationParameters()
        {
            var tcp = _specific_param.decoder.state == J2K_STATUS.TPH ? 
                _cp.tcps[_current_tile_number] : _specific_param.decoder.default_tcp;

            var tccps = tcp.tccps;
            var ref_tccp = tccps[0];
            const int size = Constants.J2K_MAXBANDS;

            for (int i = 1; i < _private_image.numcomps; i++)
            {
                var copied_tccp = tccps[i];
                copied_tccp.qntsty = ref_tccp.qntsty;
                copied_tccp.numgbits = ref_tccp.numgbits;
                Array.Copy(ref_tccp.stepsizes, 0, copied_tccp.stepsizes, 0, size);
            }
        }

        /// <summary>
        /// Gets the maximum size taken by a qcc
        /// </summary>
        /// <remarks>2.5 - opj_j2k_get_max_qcc_size</remarks>
        uint GetMaxQccSize()
        {
            return GetMaxCocSize();
        }

        /// <summary>
        /// Reads a QCC marker (Quantization component)
        /// </summary>
        /// <param name="header_size">The size of the data contained in the QCC marker.</param>
        /// <returns>False on failure</returns>
        /// <remarks>2.5 - opj_j2k_read_qcc</remarks>
        internal bool ReadQCC(int header_size)
        {
            uint num_comp = _private_image.numcomps;
            int compno;

            if (num_comp <= 256)
            {
                if (header_size < 1)
                {
                    _cinfo.Error("Error reading QCC marker");
                    return false;
                }
                compno = _cio.ReadByte();
                header_size -= 1;
            }
            else
            {
                if (header_size < 2)
                {
                    _cinfo.Error("Error reading QCC marker");
                    return false;
                }
                compno = _cio.ReadUShort();
                header_size -= 2;
            }

            if (compno >= _private_image.numcomps)
            {
                _cinfo.Error("Invalid component number: {0}, regarding the number of components {1}",
                    compno, _private_image.numcomps);
                return false;
            }

            if (!ReadSQcd_SQcc(compno, ref header_size) || header_size != 0)
            {
                _cinfo.Error("Error reading QCC marker");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Gets the maximum size taken by the writing of a POC
        /// </summary>
        /// <remarks>2.5 - opj_j2k_get_max_poc_size</remarks>
        uint GetMaxPocSize()
        {
            uint max_poc = 0;

            var tcps = _cp.tcps;
            uint n_tiles = _cp.th * _cp.tw;

            for (uint i = 0; i < n_tiles; i++)
            {
                var tcp = tcps[i];
                max_poc = Math.Max(max_poc, (uint) tcp.numpocs);
            }

            max_poc++;

            return 4 + 9 * max_poc;
        }

        /// <summary>
        /// Gets the maximum size taken by the toc headers of all the tile parts of any given tile
        /// </summary>
        /// <returns>2.5 - opj_j2k_get_max_toc_size</returns>
        uint GetMaxTocSize()
        {
            uint n_tiles;
            uint max = 0;
            var tcps = _cp.tcps;

            n_tiles = _cp.tw * _cp.th;

            for (uint i = 0; i < n_tiles; i++)
            {
                var tcp = tcps[i];
                max = Math.Max(max, tcp.n_tile_parts);
            }

            return 12 * max;
        }

        /// <summary>
        /// Gets the maximum size taken by the headers of the SOT
        /// </summary>
        /// <remarks>2.5 - opj_j2k_get_specific_header_sizes</remarks>
        uint GetSpecificHeaderSizes()
        {
            uint n_bytes = 0;
            uint n_comps;
            uint coc_bytes, qcc_bytes;

            n_comps = (uint) _private_image.numcomps - 1u;
            n_bytes += GetMaxTocSize();

            if (!_cp.IsCinema)
            {
                coc_bytes = GetMaxCocSize();
                n_bytes += n_comps * coc_bytes;

                qcc_bytes = GetMaxQccSize();
                n_bytes += n_comps * qcc_bytes;
            }

            n_bytes += GetMaxPocSize();

            if (_specific_param.encoder.PLT)
            {
                uint max_packet_count = 0;
                for(uint i = 0; i < _cp.th * _cp.tw; i++)
                {
                    max_packet_count = Math.Max(max_packet_count,
                        opj_get_encoding_packet_count(_private_image, _cp, i));
                }

                _specific_param.encoder.reserved_bytes_for_PLT +=
                    n_bytes += 5 * max_packet_count;
                _specific_param.encoder.reserved_bytes_for_PLT += 1;
                n_bytes += _specific_param.encoder.reserved_bytes_for_PLT;
            }

            return n_bytes;
        }

        /// <summary>
        /// Return the number of packets in the tile
        /// </summary>
        /// <remarks>2.5 - opj_get_encoding_packet_count</remarks>
        uint opj_get_encoding_packet_count(JPXImage image, CodingParameters cp, uint tile_no)
        {
            OPJ_UINT32 max_res;
            OPJ_UINT32 max_prec;

            PacketIterator.GetAllEncodingParameters(image, cp, tile_no, out _, out _, out _, out _, out _, out _, out max_prec, out max_res, null);

            return cp.tcps[tile_no].numlayers * max_prec * image.numcomps * max_res;
        }

        /// <summary>
        /// Reads a POC marker (Progression Order Change)
        /// </summary>
        /// <remarks>2.5 - opj_j2k_read_poc</remarks>
        internal bool ReadPOC(int header_size)
        {
            uint numcomps = _private_image.numcomps;

            uint comp_room = numcomps <= 256 ? 1u : 2u;
            uint chunk_size = 5u + 2u * comp_room;
            uint current_poc_nb = (uint)header_size / chunk_size;

            if (current_poc_nb <= 0 || header_size % chunk_size != 0)
            {
                _cinfo.Error("Error reading POC marker");
                return false;
            }

            TileCodingParams tcp = _specific_param.decoder.state == J2K_STATUS.TPH ? _cp.tcps[_current_tile_number] : _specific_param.decoder.default_tcp;
            uint old_poc = tcp.POC ? tcp.numpocs + 1 : 0u;
            current_poc_nb += old_poc;

            if (current_poc_nb >= 32)
            {
                _cinfo.Error("Too many POCs {0}", current_poc_nb);
                return false;
            }

            // Now poc is in use
            tcp.POC = true;

            for (uint i = old_poc; i < current_poc_nb; i++)
            {
                ProgOrdChang poc = tcp.pocs[i];
                poc.resno0 = _cio.ReadByte();
                poc.compno0 = _cio.Read(comp_room);
                poc.layno1 = _cio.ReadUShort();
                poc.resno1 = _cio.ReadByte();
                poc.compno1 = Math.Min(
                    _cio.Read(comp_room), numcomps);
                poc.prg = (PROG_ORDER)_cio.ReadByte();
            }

            tcp.numpocs = current_poc_nb - 1;
            return true;
        }

        /// <summary>
        /// Gets the size taken by writing a SPCod or SPCoc for the given tile and component
        /// </summary>
        /// <remarks>2.5 - opj_j2k_get_SPCod_SPCoc_size</remarks>
        uint GetSPCodSPCocSize(uint tile_no, uint comp_no)
        {
            var tcp = _cp.tcps[tile_no];
            var tccp = tcp.tccps[comp_no];

            /* preconditions again */
            Debug.Assert(tile_no < (_cp.tw * _cp.th));
            Debug.Assert(comp_no < _private_image.numcomps);

            if ((tccp.csty & CP_CSTY.PRT) != 0)
            {
                return 5u + (uint) tccp.numresolutions;
            }
            else
            {
                return 5u;
            }
        }

        #endregion

        #region Helper functions

        //2.5 - opj_j2k_update_image_dimensions
        private bool UpdateImageDimensions(JPXImage image)
        {
            uint it_comp;
            int comp_x1, comp_y1;
            ImageComp[] img_comp_ar = null;

            img_comp_ar = image.comps;
            for (it_comp = 0; it_comp < image.numcomps; ++it_comp)
            {
                int h, w;
                if (image.x0 > (OPJ_UINT32)int.MaxValue ||
                    image.y0 > (OPJ_UINT32)int.MaxValue ||
                    image.x1 > (OPJ_UINT32)int.MaxValue ||
                    image.y1 > (OPJ_UINT32)int.MaxValue)
                {
                    _cinfo.Error("Image coordinates above INT_MAX are not supported");
                    return false;
                }
                var img_comp = img_comp_ar[it_comp];

                img_comp.x0 = (OPJ_UINT32)MyMath.int_ceildiv((int)image.x0,
                                 (int)img_comp.dx);
                img_comp.y0 = (OPJ_UINT32)MyMath.int_ceildiv((int)image.y0,
                                 (int)img_comp.dy);
                comp_x1 = MyMath.int_ceildiv((int)image.x1, (int)img_comp.dx);
                comp_y1 = MyMath.int_ceildiv((int)image.y1, (int)img_comp.dy);

                w = MyMath.int_ceildivpow2(comp_x1, (int)img_comp.factor)
                      - MyMath.int_ceildivpow2((int)img_comp.x0, (int)img_comp.factor);
                if (w < 0)
                {
                    _cinfo.Error("Size x of the decoded component image is incorrect (comp[{0}].w={0}).",
                                  it_comp, w);
                    return false;
                }
                img_comp.w = (OPJ_UINT32)w;

                h = MyMath.int_ceildivpow2(comp_y1, (int)img_comp.factor)
                      - MyMath.int_ceildivpow2((int)img_comp.y0, (int)img_comp.factor);
                if (h < 0)
                {
                    _cinfo.Error("Size y of the decoded component image is incorrect (comp[{0}].h={1}).\n",
                                  it_comp, h);
                    return false;
                }
                img_comp.h = (OPJ_UINT32)h;
            }

            return true;
        }

        //2.5
        internal bool SetDecodeArea(JPXImage image, int start_x, int start_y, int end_x, int end_y)
        {
            if (_cp.tw == 1 && _cp.th == 1 &&
                _cp.tcps[0].data != null)
            {
                // In the case of a single-tiled image whose codestream we have already
                // ingested, go on
            }
            else if (_specific_param.decoder.state != J2K_STATUS.TPHSOT)
            {
                _cinfo.Error("Need to decode the main header before begin to decode the remaining codestream");
                return false;
            }

            // Update the comps[].factor member of the output image with the one
            // of m_reduce
            for (var it_comp = 0; it_comp < _private_image.numcomps; ++it_comp)
            {
                image.comps[it_comp].factor = _cp.specific_param.dec.reduce;
            }

            if (start_x == 0 && start_y == 0 && end_x == 0 && end_y == 0)
            {
                _cinfo.Info("No decoded area parameters, set the decoded area to the whole image");

                _specific_param.decoder.start_tile_x = 0;
                _specific_param.decoder.start_tile_y = 0;
                _specific_param.decoder.end_tile_x = _cp.tw;
                _specific_param.decoder.end_tile_y = _cp.th;

                image.x0 = _private_image.x0;
                image.y0 = _private_image.y0;
                image.x1 = _private_image.x1;
                image.y1 = _private_image.y1;

                return UpdateImageDimensions(image);
            }

            //Left
            if (start_x < 0)
            {
                _cinfo.Error("Left position of the decoded area (region_x0={0}) should be >= 0.",
                              start_x);
                return false;
            }
            else if (start_x > _private_image.x1)
            {
                _cinfo.Error("Left position of the decoded area (region_x0={0}) is outside the image area (Xsiz={1}).",
                    start_x, _private_image.x1);

                return false;
            }
            if (start_x < _private_image.x0)
            {
                _cinfo.Warn("Left position of the decoded area (region_x0={0}) is outside the image area (XOsiz={1}).",
                    start_x, _private_image.x0);

                _specific_param.decoder.start_tile_x = 0;
                image.x0 = _private_image.x0;
            }
            else
            {
                _specific_param.decoder.start_tile_x = (OPJ_UINT32) ((start_x - _cp.tx0) / _cp.tdx);
                image.x0 = (uint)start_x;
            }

            //Up
            if (start_y < 0)
            {
                _cinfo.Error("Up position of the decoded area (region_y0={0}) should be >= 0.",
                              start_y);
                return false;
            }
            else if (start_y > _private_image.y1)
            {
                _cinfo.Error("Up position of the decoded area (region_y0={0}) is outside the image area (Ysiz={1}).",
                    start_y, _private_image.y1);

                return false;
            }
            if (start_y < _private_image.y0)
            {
                _cinfo.Warn("Up position of the decoded area (region_y0={0}) is outside the image area (YOsiz={1}).",
                    start_y, _private_image.y0);

                _specific_param.decoder.start_tile_y = 0;
                image.y0 = _private_image.x0;
            }
            else
            {
                _specific_param.decoder.start_tile_y = (OPJ_UINT32)((start_y - _cp.ty0) / _cp.tdy);
                image.y0 = (uint)start_y;
            }

            //Right
            if (end_x < 0)
            {
                _cinfo.Error("Right position of the decoded area (region_x1={0}) should be >= 0.",
                              end_x);
                return false;
            }
            else if(end_x < _private_image.x0)
            {
                _cinfo.Error("Right position of the decoded area (region_x1={0}) is outside the image area (XOsiz={1}).",
                    end_x, _private_image.x0);

                return false;
            }
            if (end_x > _private_image.x1)
            {
                _cinfo.Warn("Right position of the decoded area (region_x1={0}) is outside the image area (Xsiz={1}).",
                    end_x, _private_image.x1);

                _specific_param.decoder.end_tile_x = _cp.tw;
                image.x1 = _private_image.x1;
            }
            else
            {
                _specific_param.decoder.end_tile_x = (OPJ_UINT32)MyMath.int_ceildiv((end_x - (int)_cp.tx0) , (int) _cp.tdx);
                image.x1 = (uint)end_x;
            }

            //Bottom
            if (end_y < 0)
            {
                _cinfo.Error("Bottom position of the decoded area (region_y1={0}) should be >= 0.",
                              end_y);
                return false;
            }
            else if (end_y < _private_image.y0)
            {
                _cinfo.Error("Bottom position of the decoded area (region_y1={0}) is outside the image area (YOsiz={1}).",
                    end_y, _private_image.y0);

                return false;
            }
            if (end_y > _private_image.y1)
            {
                _cinfo.Warn("Bottom position of the decoded area (region_y1={0}) is outside the image area (Ysiz={1}).",
                    end_y, _private_image.y1);

                _specific_param.decoder.end_tile_y = _cp.th;
                image.y1 = _private_image.y1;
            }
            else
            {
                _specific_param.decoder.end_tile_y = (OPJ_UINT32)MyMath.int_ceildiv((end_y - (int)_cp.ty0), (int)_cp.tdy);
                image.y1 = (uint)end_y;
            }

            _specific_param.decoder.discard_tiles = true;

            var ret = UpdateImageDimensions(image);

            if (ret)
                _cinfo.Info("Setting decoding area to {0},{1},{2},{3}", image.x0, image.y0, image.x1, image.y1);

            return ret;
        }

        /// <summary>
        /// Updates the rates of the tcp
        /// </summary>
        /// <remarks>2.5 - opj_j2k_update_rates</remarks>
        void UpdateRates()
        {
            CodingParameters cp;
            JPXImage image;
            TileCodingParams[] tcps;
            ImageComp[] img_comps;

            int x0,y0,x1,y1;
            float[] rates;
            float sot_remove;
            uint bits_empty, size_pixel;
            long tile_size;
            uint last_res;
            StrideFunc stride_func;

            cp = _cp;
            image = _private_image;
            tcps = cp.tcps;

            bits_empty = (uint) (8 * image.comps[0].dx * image.comps[0].dy);
            size_pixel = (uint) (image.numcomps * image.comps[0].prec);
            sot_remove = (float) _bcio.Pos / (float)(cp.th * cp.tw);

            if (cp.specific_param.enc.tp_on)
                stride_func = new StrideFunc(GetTpStride);
            else
                stride_func = new StrideFunc(GetDefaultStride);

            for (int i = 0, tcp_ptr = 0; i < cp.th; i++)
            {
                for (int j = 0; j < cp.tw; ++j)
                {
                    var tcp = tcps[tcp_ptr++];

                    float l_offset = stride_func(tcp) / (float)tcp.numlayers;

                    /* 4 borders of the tile rescale on the image if necessary */
                    x0 = Math.Max((int)(cp.tx0 + j * cp.tdx), (int)image.x0);
                    y0 = Math.Max((int)(cp.ty0 + i * cp.tdy), (int)image.y0);
                    x1 = Math.Min((int)(cp.tx0 + (j + 1) * cp.tdx), (int)image.x1);
                    y1 = Math.Min((int)(cp.ty0 + (i + 1) * cp.tdy), (int)image.y1);

                    rates = tcp.rates;

                    /* Modification of the RATE >> */
                    for (int k = 0; k < tcp.numlayers; k++)
                    {
                        if (rates[k] > 0)
                        {
                            rates[k] = (float)(((double)size_pixel * (uint)(x1 - x0) * (uint)(y1 - y0))
                                                                    /
                                                                    (rates[k] * (float)bits_empty)
                                                                    -
                                                                    l_offset);
                        }
                    }
                }
            }

            for (int i = 0, tcp_ptr = 0; i < cp.th; i++)
            {
                for (int j = 0; j < cp.tw; ++j)
                {
                    var tcp = tcps[tcp_ptr++];
                    rates = tcp.rates;

                    if (rates[0] > 0)
                    {
                        rates[0] -= sot_remove;

                        if (rates[0] < 30)
                        {
                            rates[0] = 30;
                        }
                    }

                    int rates_ptr = 1;

                    last_res = tcp.numlayers - 1;

                    for (int k = 1; k < last_res; k++)
                    {
                        if (rates[rates_ptr] > 0)
                        {
                            rates[rates_ptr] -= sot_remove;

                            if (rates[rates_ptr] < rates[rates_ptr - 1] + 10)
                            {
                                rates[rates_ptr] = rates[rates_ptr - 1] + 20;
                            }
                        }

                        rates_ptr++;
                    }

                    if (rates[rates_ptr] > 0)
                    {
                        rates[rates_ptr] -= sot_remove + 2f;

                        if (rates[rates_ptr] < rates[rates_ptr - 1] + 10)
                        {
                            rates[rates_ptr] = rates[rates_ptr - 1] + 20;
                        }
                    }
                }
            }

            img_comps = image.comps;
            tile_size = 0;

            for (int i=0; i < image.numcomps; i++)
            {
                var img_comp = img_comps[i];
                tile_size += (MyMath.uint_ceildiv(cp.tdx, img_comp.dx)
                                    *
                                    MyMath.uint_ceildiv(cp.tdy, img_comp.dy)
                                    *
                                    img_comp.prec
                             );
            }

            tile_size = (uint) (tile_size * 1.4 / 8);

            // Arbitrary amount to make the following work:
            // bin/test_tile_encoder 1 256 256 17 16 8 0 reversible_no_precinct.j2k 4 4 3 0 0 1
            tile_size += 500;

            tile_size += GetSpecificHeaderSizes();

            if (tile_size > int.MaxValue)
            {
                //C# can't create arrays this big, or even approaching this big.
                throw new NotSupportedException("Big tiles");
            }

            _specific_param.encoder.encoded_tile_data = new byte[tile_size];
            _specific_param.encoder.encoded_tile_size = (uint) tile_size;

            if (_specific_param.encoder.TLM) 
            {
                _specific_param.encoder.tlm_sot_offsets_buffer = new byte[6 * _specific_param.encoder.total_tile_parts];

                _specific_param.encoder.tlm_sot_offsets_current = 0;
            }
        }

        delegate float StrideFunc(TileCodingParams tcp);

        //2.5 - opj_j2k_get_tp_stride
        float GetTpStride(TileCodingParams tcp)
        {
            return (float)((tcp.n_tile_parts - 1) * 14);
        }

        //2.5 - opj_j2k_get_default_stride
        float GetDefaultStride(TileCodingParams tcp)
        {
            return 0;
        }

        //2.5 - opj_j2k_calculate_tp
        bool CalculateTP(out uint n_tiles)
        {
            int cn_tiles = (int) (_cp.tw * _cp.th);
            n_tiles = 0;
            TileCodingParams[] tcps = _cp.tcps;

            for (uint tileno = 0; tileno < cn_tiles; tileno++)
            {
                uint cur_totnum_tp = 0;
                PacketIterator.UpdateEncodingParameters(_private_image, _cp, tileno);

                var tcp = tcps[tileno];
                for (uint pino = 0; pino <= tcp.numpocs; pino++)
                {
                    uint tp_num = GetNumTP(pino, tileno);

                    cur_totnum_tp += tp_num;
                }
                //C# org impl. adds this value in the loop. End result is the same.
                n_tiles += cur_totnum_tp;
                tcp.n_tile_parts = (uint) cur_totnum_tp;
            }

            return true;
        }

        //2.5 - opj_j2k_get_num_tp
        uint GetNumTP(uint pino, uint tileno)
        {
            Debug.Assert(pino < _cp.tcps[tileno].numpocs + 1);

            var tcp = _cp.tcps[tileno];
            ProgOrdChang current_poc = tcp.pocs[pino];

            //get the progression order as a character string
            string prog = ConvertProgressionOrder(tcp.prg);
            Debug.Assert(prog.Length > 0);

            if (_cp.specific_param.enc.tp_on)
            {
                uint tpnum = 1;

                for (int i = 0; i < 4; i++)
                {
                    switch (prog[i])
                    {
                        case 'C':
                            tpnum *= current_poc.compE;
                            break;
                        case 'R':
                            tpnum *= current_poc.resE;
                            break;
                        case 'P':
                            tpnum *= current_poc.prcE;
                            break;
                        case 'L':
                            tpnum *= current_poc.layE;
                            break;
                    }

                    if (_cp.specific_param.enc.tp_flag == (byte)prog[i])
                    {
                        _cp.specific_param.enc.tp_pos = i;
                        break;
                    }
                }
                return tpnum;
            }
            else
            {
                return 1;
            }
        }

        //2.5 - opj_j2k_convert_progression_order
        internal static string ConvertProgressionOrder(PROG_ORDER prg_order)
        {
            //prg_order.ToString() should work just as well
            switch (prg_order)
            {
                case PROG_ORDER.CPRL: return "CPRL";
                case PROG_ORDER.LRCP: return "LRCP";
                case PROG_ORDER.PCRL: return "PCRL";
                case PROG_ORDER.RLCP: return "RLCP";
                case PROG_ORDER.RPCL: return "RPCL";
                default: return string.Empty;
            }
	    }

#endregion

#region Spesific params

        //[System.Runtime.InteropServices.StructLayoutAttribute(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct J2KEncOrDec
        {
            //[System.Runtime.InteropServices.FieldOffsetAttribute(0)]
            public DecParams decoder;

            //[System.Runtime.InteropServices.FieldOffsetAttribute(0)]
            public EncParams encoder;
        }

        struct EncParams
        {
            /// <summary>
            /// Total num of tile parts in whole image = num tiles* num tileparts in each tile
            /// used in TLMmarker
            /// </summary>
            internal uint total_tile_parts;

            /// <summary>
            /// whether the Ttlmi field in a TLM marker is a byte (otherwise a uint16)
            /// </summary>
            internal bool Ttlmi_is_byte;

            /// <summary>
            /// Encoded data for a tile
            /// </summary>
            internal byte[] encoded_tile_data;

            /// <summary>
            /// Size of the tiles in bytes
            /// </summary>
            internal uint encoded_tile_size
            {
                get { return (uint)encoded_tile_data.Length; }
                set 
                {
                    if (encoded_tile_size != value)
                        throw new NotSupportedException("Content tile length different from array");
                }
            }

            /// <summary>
            /// Encoded data for a tile
            /// </summary>
            internal byte[] header_tile_data;

            /// <summary>
            /// size of the encoded_data
            /// </summary>
            internal uint header_tile_data_size
            {
                get { return (uint) header_tile_data.Length; }
                set
                {
                    if (header_tile_data_size != value)
                        throw new NotSupportedException("Header tile length different from array");
                }
            }

            /// <summary>
            /// locate the start position of the TLM marker  
            /// after encoding the tilepart, a jump (in j2k_write_sod) 
            /// is done to the TLM marker to store the value of its length. 
            /// </summary>
            internal long tlm_start;

            /// <summary>
            /// Stores the sizes of the tlm.
            /// </summary>
            internal byte[] tlm_sot_offsets_buffer;

            /// <summary>
            /// Position in the tlm_sot_offsets_buffer array
            /// </summary>
            internal int tlm_sot_offsets_current;

            /// <summary>
            /// Tile part number, regardless of poc, for each new poc, tp is reset to 1
            /// </summary>
            internal uint current_poc_tile_part_number;

            /// <summary>
            /// Tile part number currently coding, taking into account POC. 
            /// m_current_tile_part_number holds the total number of tile parts while encoding the last tile part.
            /// </summary>
            internal uint current_tile_part_number;

            /// <summary>
            /// Whether to generate TLM markers 
            /// </summary>
            internal bool TLM;

            /// <summary>
            /// Whether to generate PLT markers
            /// </summary>
            internal bool PLT;

            /// <summary>
            /// Reserved bytes in m_encoded_tile_size for PLT markers
            /// </summary>
            internal uint reserved_bytes_for_PLT;

            /// <summary>
            /// Number of components
            /// </summary>
            internal uint nb_comps;
        }

        struct DecParams
        {
            /// <summary>
            /// Locate in which part of the codestream the decoder is (main header, tile header, end)
            /// </summary>
            public J2K_STATUS state;

            /**
             * store decoding parameters common to all tiles (information like COD, COC in main header)
             */
            public TileCodingParams default_tcp;

            internal byte[] header_data;
            internal int header_data_size;

            /// <summary>
            /// To tell the tile part length
            /// </summary>
            public OPJ_UINT32 sot_length;

            /// <summary>
            /// Only tiles index in the correct range will be decoded
            /// </summary>
            public OPJ_UINT32 start_tile_x, start_tile_y, end_tile_x, end_tile_y;

            /// <summary>
            /// Decoded area set by the user
            /// </summary>
            public OPJ_UINT32 DA_x0, DA_y0, DA_x1, DA_y1;

            /// <summary>
            /// Index of the tile to decode (used in get_tile)
            /// </summary>
            public int tile_ind_to_dec;

            /// <summary>
            /// Position of the last SOT marker read
            /// </summary>
            public long last_sot_read_pos;

            /// <summary>
            /// Indicate that the current tile-part is assume as the last tile part of the codestream.
	        /// It is useful in the case of PSot is equal to zero. The sot length will be compute in the
	        /// SOD reader function. FIXME NOT USED for the moment
            /// </summary>
            public bool last_tile_part;

            /// <remarks>
            /// Set internal since if numcomps_to_decode is set,
            /// then comps_indices_to_decode must also be set.
            /// 
            /// This isn't obvious, so a better API is needed.
            /// </remarks>
            internal int numcomps_to_decode;
            internal int[] comps_indices_to_decode;

            uint bitvector1;

            /// <summary>
            /// To tell that a tile can be decoded.
            /// </summary>
            public bool can_decode
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
            public bool discard_tiles
            {
                get
                {
                    return (this.bitvector1 & 2u) != 0;
                }
                set
                {
                    this.bitvector1 = value ? 2u | bitvector1 : ~2u & bitvector1;
                }
            }

            public bool skip_data
            {
                get
                {
                    return (bitvector1 & 4u) != 0;
                }
                set
                {
                    bitvector1 = value ? 4u | bitvector1 : ~4u & bitvector1;
                }
            }

            public bool n_tile_parts_correction_checked
            {
                get
                {
                    return (bitvector1 & 8u) != 0;
                }
                set
                {
                    bitvector1 = value ? 8u | bitvector1 : ~8u & bitvector1;
                }
            }

            public bool n_tile_parts_correction
            {
                get
                {
                    return (bitvector1 & 16u) != 0;
                }
                set
                {
                    bitvector1 = value ? 16u | bitvector1 : ~16u & bitvector1;
                }
            }
        }

#endregion
    }
}
