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
using System.IO;
using System.Diagnostics;
using OpenJpeg.Internal;

namespace OpenJpeg
{
    /// <summary>
    /// JPEG-2000 file format reader/writer
    /// </summary>
    /// <remarks>
    /// V.2.1 does things a bit differently. Basically,
    /// methods that are to be executed are put into a
    /// _procedure_list, then that list is executed.
    /// 
    /// I don't see the point. Sticking to the old v.1.4
    /// way of doing this.
    /// </remarks>
    internal sealed class JP2
    {
        #region Variables and properties

        /// <summary>
        /// The parent compression info obj.
        /// </summary>
        readonly CompressionInfo _cinfo;

        /// <summary>
        /// Code stream codex
        /// </summary>
        readonly J2K _j2k;

        //List<ProcedureDlg> _validation_list;
        //List<ProcedureDlg> _procedure_list;

        JP2_STATE _state;
        JP2_IMG_STATE _img_state;

        /// <summary>
        /// Width of the image
        /// </summary>
        uint _w;

        /// <summary>
        /// Height of the image
        /// </summary>
        uint _h;

        /// <summary>
        /// Number of componets in the image
        /// </summary>
        uint _numcomps;

        /// <summary>
        /// Bits per component
        /// </summary>
        uint _bpc;

        /// <summary>
        /// ColorSpecMethod
        /// </summary>
        uint _meth;

        /// <summary>
        /// ColorSpace
        /// </summary>
        uint _enumcs;

        uint _C, _approx, _precedence, _minversion;
        JP2_Marker _brand;

        /// <summary>
        /// Unknown color space
        /// </summary>
        bool _UnkC;

        /// <summary>
        /// Intellectual Property
        /// </summary>
        bool _IPR;

        JP2_Marker[] _cl;

        long _j2k_codestream_offset;

        JP2Comps[] _comps;

        CIO _cio;

        /// <summary>
        /// If the image being decoded has a ICC profile, it will be temporarily stored
        /// here. 
        /// </summary>
        JP2Color _color;

        bool _has_ihdr;
        bool _has_jp2h { get { return (_state & JP2_STATE.HEADER) != 0; } }

        #endregion

        #region Init

        internal JP2(CompressionInfo cinfo, J2K j2k)
        {
            _cinfo = cinfo;
            _j2k = j2k;
            
            //_validation_list = new List<ProcedureDlg>(1);
            //_procedure_list = new List<ProcedureDlg>();
        }

        //2.5.3 - opj_jp2_setup_encoder
        internal bool SetupEncoder(CompressionParameters parameters, JPXImage image)
        {
            if (parameters == null || image == null)
                return false;

            //
            // Sets up the J2K codec
            //

            // Checks if the number of components respects the standard
            if (image.numcomps < 1 || image.numcomps > 16384)
            {
                _cinfo.Error("Invalid number of components specified while setting up JP2 encoder");
                return false;
            }

            _j2k.SetupEncoder(parameters, image);

            //
            // Sets up the JP2 codec
            //

            // Profile box
            _brand = JP2_Marker.JP2;
            _minversion = 0;
            //numcl = _cl.Length
            _cl = new JP2_Marker[1];
            _cl[0] = _brand;

            // Image Header box 
            _numcomps = image.numcomps;
            _comps = new JP2Comps[_numcomps];
            _h = image.y1 - image.y0;
            _w = image.x1 - image.x0;
            //Setting bits per componet
            uint depth_0 = image.comps[0].prec - 1;
            uint sign = image.comps[0].sgnd ? 1u : 0u;
            _bpc = (depth_0 + (sign << 7));
            for (int i = 0; i < image.numcomps; i++)
            {
                uint depth = image.comps[i].prec - 1;
                sign = image.comps[i].sgnd ? 1u : 0u;

                //If the pits per component aren't uniform,
                //bpc is set to 255 to signal that.
                if (depth_0 != depth)
                    _bpc = 255;
            }
            _C = 7;
            _UnkC = false;
            _IPR = false;

            //BitsPerComponent box
            for (int i = 0; i < image.numcomps; i++)
                _comps[i].bpcc = image.comps[i].prec - 1u + ((image.comps[i].sgnd ? 1u : 0u) << 7);

            //Color Specification box
            if (image.icc_profile_buf != null)
            {
                _meth = 2;
                _enumcs = 0;
            }
            else
            {
                _meth = 1;
                _enumcs = (uint)image.color_space;
            }

            uint alpha_count = 0, alpha_channel = 0, color_channels = 0;
            for (uint i = 0; i < image.numcomps; i++)
            {
                if (image.comps[i].alpha != 0)
                {
                    alpha_count++;
                    alpha_channel = i;
                }
            }
            if (alpha_count == 1U)
            { /* no way to deal with more than 1 alpha channel */
                switch (_enumcs)
                {
                    case 16:
                    case 18:
                        color_channels = 3;
                        break;
                    case 17:
                        color_channels = 1;
                        break;
                    default:
                        alpha_count = 0U;
                        break;
                }
                if (alpha_count == 0U)
                {
                    _cinfo.Warn("Alpha channel specified but unknown enumcs. No cdef box will be created.");
                }
                else if (image.numcomps < (color_channels + 1))
                {
                    _cinfo.Warn("Alpha channel specified but not enough image components for an automatic cdef box creation.");
                    alpha_count = 0U;
                }
                else if (alpha_channel < color_channels)
                {
                    _cinfo.Warn("Alpha channel position conflicts with color channel. No cdef box will be created.");
                    alpha_count = 0U;
                }
            }
            else if (alpha_count > 1)
            {
                _cinfo.Warn("Multiple alpha channels specified. No cdef box will be created.");
            }
            if (alpha_count == 1U)
            { /* if here, we know what we can do */
                if (_color == null) _color = new JP2Color();
                _color.channel_definitions = new JP2cdefInfo[image.numcomps];

                uint i = 0;
                for (; i < color_channels; i++)
                {
                    _color.channel_definitions[i].cn = (ushort)i;
                    _color.channel_definitions[i].typ = 0;
                    _color.channel_definitions[i].asoc = (ushort)(i + 1U);
                }
                for (; i < image.numcomps; i++)
                {
                    if (image.comps[i].alpha != 0)
                    { /* we'll be here exactly once */
                        _color.channel_definitions[i].cn = (ushort) i;
                        _color.channel_definitions[i].typ = 1; // Opacity channel
                        _color.channel_definitions[i].asoc = 0; // Apply alpha channel to the whole image
                    }
                    else
                    {
                        /* Unknown channel */
                        _color.channel_definitions[i].cn = (ushort) i;
                        _color.channel_definitions[i].typ = (ushort) 65535U;
                        _color.channel_definitions[i].asoc = (ushort) 65535U;
                    }
                }
            }


            //C# PdfLib requires this.
            if (image.channel_definitions != null)
            {
                if (_color == null) _color = new JP2Color();
                
                //Overwrites any channeld definition set above, this because
                //the definitions supplied by Pdflib are the correct definitions.
                _color.channel_definitions = image.channel_definitions;
            }

            _precedence = 0;
            _approx = 0;

            //JPIP is not supported by this impl.
            return true;
        }

        /// <summary>
        /// Configures for decoding
        /// </summary>
        /// <param name="cio">File data</param>
        /// <param name="parameters">Configuration</param>
        /// <remarks>
        /// 2.5
        /// </remarks>
        internal void SetupDecode(CIO cio, DecompressionParameters parameters)
        {
            _cio = cio;
            _j2k.SetupDecode(cio, parameters);

            _color = new JP2Color();
            _color.ignore_pclr_cmap_cdef = (parameters.flags & DecompressionParameters.DPARAMETERS.IGNORE_PCLR_CMAP_CDEF_FLAG) != 0;

            // This is a C# addition.
            _color.ignore_cmap = parameters.IgnoreColorLookupTable;
        }

        #endregion

        //2.5.1 - opj_jp2_read_header
        internal bool ReadHeader(out JPXImage image)
        {
            //Snip decoding validation (NOP)

            //Snip setup_header_reading, this creates a list over
            //what functions to call, which is then called in a for loop.
            //Here we call them directly.

            if (!ReadHeaderProcedure())
            {
                image = null;
                return false;
            }
            if (!_has_jp2h)
            {
                _cinfo.Error("JP2H box missing. Required");
                image = null;
                return false;
            }
            if (!_has_ihdr)
            {
                _cinfo.Error("IHDR box missing. Required");
                image = null;
                return false;
            }

            var ret = _j2k.ReadHeader(out image);

            // Set Image Color Space
            if (image != null)
            {
                if (11 <= _enumcs && _enumcs <= 18 && _enumcs != 15)
                    image.color_space = (COLOR_SPACE)_enumcs;
                else if (_enumcs == 24)
                    image.color_space = COLOR_SPACE.eYCC;
                else
                    image.color_space = COLOR_SPACE.UNKNOWN;

                if (_color.icc_profile_buf != null)
                {
                    image.icc_profile_buf = _color.icc_profile_buf;
                    _color.icc_profile_buf = null;
                }
            }
            return ret;
        }

        //2.5 - opj_jp2_read_header_procedure
        bool ReadHeaderProcedure()
        {
            JP2Box box; 
            int /* uint */ n_bytes_read;

            while (ReadBoxhdr(out box, out n_bytes_read))
            {
                //Codestream box
                if (box.type == JP2_Marker.JP2C)
                {
                    if ((_state & JP2_STATE.HEADER) != 0)
                    {
                        _state |= JP2_STATE.CODESTREAM;
                        return true;
                    }
                    else
                    {
                        _cinfo.Error("Badly placed jpeg codestream\n");
                        return false;
                    }
                }
                else if (box.length == 0)
                {
                    _cinfo.Error("Cannot handle box of undefined sizes\n");
                    return false;
                }
                else if (box.length < n_bytes_read)
                {
                    _cinfo.Error("invalid box size {0} ({1})\n", box.length, box.type.ToString());
                    return false;
                }

                var handler = FindHandler(box.type);
                uint current_data_size = box.length - (uint)n_bytes_read;
                if (current_data_size > _cio.BytesLeft)
                {
                    _cinfo.Error("Invalid box size {0} for box '{1}'. Need {2} bytes, {3} bytes remaining",
                        (int)box.length, box.type.ToString(), (int)current_data_size, _cio.BytesLeft);
                    return false;
                }

                if (handler != null)
                {
                    if (!handler(box))
                        return false;
                }
                else
                {
                    var hadler_misplaced = ImgFindHandler(box.type);
                    if (hadler_misplaced != null)
                    {
                        _cinfo.Warn("Found a misplaced {0} box outside jp2h box\n", box.type.ToString());
                        if ((_state & JP2_STATE.HEADER) != 0)
                        {
                            // Read anyway, we already have jp2h
                            box.data_length = current_data_size;
                            if (!hadler_misplaced(box))
                                return false;

                            continue;
                        }
                        else
                        {
                            _cinfo.Warn("JPEG2000 Header box not read yet, {0} box will be ignored\n", box.type.ToString());
                        }
                    }
                    
                    // Skip unkown boxes
                    _cio.Skip(current_data_size);
                }
            }

            return true;
        }

        /// <summary>
        /// Find handeler to use for interpeting a JP2 box
        /// </summary>
        /// <param name="type">Type of box</param>
        /// <returns>Handler or null if handler not found</returns>
        /// <remarks>
        /// 2.5
        /// The original implementation is more complex, but seeing as
        /// there are only 3 possible handlers, we keep this simple.
        /// </remarks>
        Handeler FindHandler(JP2_Marker type)
        {
            switch (type)
            {
                case JP2_Marker.JP: return new Handeler(ReadJP);
                case JP2_Marker.FTYP: return new Handeler(ReadFTYP);
                case JP2_Marker.JP2H: return new Handeler(ReadJP2H);
            }
            return null;
        }

        /// <summary>
        /// Find handeler to use for interpeting a JP2 box
        /// </summary>
        /// <param name="type">Type of box</param>
        /// <returns>Handler or null if handler not found</returns>
        /// <remarks>
        /// 2.5
        /// The original implementation is more complex, but seeing as
        /// there are only 6 possible handlers, we keep this simple.
        /// </remarks>
        Handeler ImgFindHandler(JP2_Marker type)
        {
            switch (type)
            {
                case JP2_Marker.IHDR: return new Handeler(ReadIHDR);
                case JP2_Marker.COLR: return new Handeler(ReadCOLR);
                case JP2_Marker.BPCC: return new Handeler(ReadBPCC);
                case JP2_Marker.PCLR: return new Handeler(ReadPCLR);
                case JP2_Marker.CMAP: return new Handeler(ReadCMAP);
                case JP2_Marker.CDEF: return new Handeler(ReadCDEF);
            }
            return null;
        }

        //2.5 - opj_jp2_end_decompress
        internal bool EndDecompress()
        {
            if (_cio.BytesLeft > 8)
                ReadHeaderProcedure();

            return _j2k.EndDecompress();
        }

        delegate bool Handeler(JP2Box box);

        //2.5.1 - opj_jp2_apply_color_postprocessing
        bool ApplyColorPostprocessing(JPXImage image)
        {
            if (_j2k.NumcompsToDecode != 0)
            {
                // Bypass all JP2 component transforms
                return true;
            }

            if (!_color.ignore_pclr_cmap_cdef)
            {
                if (!CheckColor(image))
                    return false;

                if (_color.jp2_pclr != null)
                {
                    /* Part 1, I.5.3.4: Either both or none : */
                    if (_color.jp2_pclr.cmap == null)
                        _color.jp2_pclr = null;
                    else
                    {
                        if (!_color.ignore_cmap)
                        {
                            if (!ApplyPCLR(image, _color, _cinfo))
                                return false;
                        }
                        else
                        {
                            //Added for Pdflib
                            image.color_info = _color;
                        }
                    }
                }

                // Apply the color space if needed
                if (_color.channel_definitions != null)
                {
                    ApplyCDEF(image, _color);
                }
            }

            return true;
        }

        //2.5.1 - opj_jp2_decode
        internal bool Decode(JPXImage image)
        {
            if (image == null) 
                return false;

            if (!_j2k.Decode(image))
            {
                _cinfo.Error("Failed to decode the codestream in the JP2 file");
                return false;
            }

            return ApplyColorPostprocessing(image);
        }

        /// <summary>
        /// Decodes a single tile in the image
        /// </summary>
        /// <remarks>2.5.1 - opj_jp2_get_tile</remarks>
        internal bool Decode(JPXImage image, uint tile_nr)
        {
            if (image == null)
                return false;

            _cinfo.Warn("JP2 box which are after the codestream will not be read by this function.");

            if (!_j2k.Decode(image, tile_nr))
            {
                _cinfo.Error("Failed to decode the codestream in the JP2 file");
                return false;
            }

            return ApplyColorPostprocessing(image);
        }

        //2.5 - opj_jp2_check_color
        bool CheckColor(JPXImage image)
        {
            /* testcase 4149.pdf.SIGSEGV.cf7.3501 */
            if (_color.channel_definitions != null)
            {
                var info = _color.channel_definitions;
                uint nr_channels = image.numcomps;
                ushort i;

                // cdef applies to cmap channels if any
                if (_color.jp2_pclr != null && _color.jp2_pclr.cmap != null)
                {
                    nr_channels = (uint)_color.jp2_pclr.nr_channels;
                }

                for (i = 0; i < info.Length; i++)
                {
                    if (info[i].cn >= nr_channels)
                    {
                        _cinfo.Error("Invalid component index {0} (>= {1})", info[i].cn, nr_channels);
                        return false;
                    }
                    if (info[i].asoc == 65535U)
                    {
                        continue;
                    }
                    if (info[i].asoc > 0 && (info[i].asoc - 1) >= nr_channels)
                    {
                        _cinfo.Error("Invalid component index {0} (>= {1})", info[i].asoc - 1, nr_channels);
                        return false;
                    }
                }

                // issue 397
                // ISO 15444-1 states that if cdef is present, it shall contain a complete list of channel definitions. */
                ushort n = (ushort)_color.channel_definitions.Length;
                while (nr_channels > 0)
                {
                    for (i = 0; i < n; ++i)
                    {
                        if ((uint)info[i].cn == (nr_channels - 1U))
                        {
                            break;
                        }
                    }
                    if (i == n)
                    {
                        _cinfo.Error("Incomplete channel definitions.");
                        return false;
                    }
                    --nr_channels;
                }
            }

            /* testcases 451.pdf.SIGSEGV.f4c.3723, 451.pdf.SIGSEGV.5b5.3723 and
               66ea31acbb0f23a2bbc91f64d69a03f5_signal_sigsegv_13937c0_7030_5725.pdf */
            if (_color.jp2_pclr != null && _color.jp2_pclr.cmap != null)
            {
                var nr_channels = _color.jp2_pclr.nr_channels;
                var cmap = _color.jp2_pclr.cmap;
                bool[] pcol_usage; bool is_sane = true;

                /* verify that all original components match an existing one */
                for (int i = 0; i < nr_channels; i++)
                {
                    if (cmap[i].cmp >= image.numcomps)
                    {
                        _cinfo.Error("Invalid component index {0} (>= {1}).", cmap[i].cmp, image.numcomps);
                        is_sane = false;
                    }
                }

                pcol_usage = new bool[nr_channels];
                if (pcol_usage == null)
                {
                    _cinfo.Error("Unexpected OOM.");
                    return false;
                }
                
                /* verify that no component is targeted more than once */
                for (int i = 0; i < nr_channels; i++)
                {
                    var mtyp = cmap[i].mtyp;
                    var pcol = cmap[i].pcol;
                    if (mtyp != 0 && mtyp != 1)
                    {
                        _cinfo.Error("Invalid value for cmap[{0}].mtyp = {1}.", i, mtyp);
                        is_sane = false;
                    } 
                    else if (pcol >= nr_channels)
                    {
                        _cinfo.Error("Invalid component/palette index for direct mapping {0}.", pcol);
                        is_sane = false;
                    }
                    else if (pcol_usage[pcol] && mtyp == 1)
                    {
                        _cinfo.Error("Component {0} is mapped twice.", pcol);
                        is_sane = false;
                    }
                    else if (mtyp == 0 && pcol != 0)
                    {
                        /* I.5.3.5 PCOL: If the value of the MTYP field for this channel is 0, then
                         * the value of this field shall be 0. */
                        _cinfo.Error("Direct use at #{0} however pcol={1}.", i, pcol);
                        is_sane = false;
                    }
                    else if (mtyp == 1 && pcol != i)
                    {
                        // OpenJPEG implementation limitation. See assert(i == pcol);
                        // in opj_jp2_apply_pclr() 
                        _cinfo.Error("Implementation limitation: for palette mapping, "+
                                      "pcol[{0}] should be equal to {1}, but is equal "+
                                      "to {2}.", i, i, pcol);
                        is_sane = false;
                    }
                    else
                        pcol_usage[pcol] = true;
                }
                /* verify that all components are targeted at least once */
                for (int i = 0; i < nr_channels; i++)
                {
                    if (!pcol_usage[i] && cmap[i].mtyp != 0)
                    {
                        _cinfo.Error("Component {0} doesn't have a mapping.", i);
                        is_sane = false;
                    }
                }
                // Issue 235/447 weird cmap
                if (is_sane && (image.numcomps == 1U))
                {
                    for (int i = 0; i < nr_channels; i++)
                    {
                        if (!pcol_usage[i])
                        {
                            is_sane = false;
                            _cinfo.Warn("Component mapping seems wrong. Trying to correct.");
                            break;
                        }
                    }
                    if (!is_sane)
                    {
                        is_sane = true;
                        for (int i = 0; i < nr_channels; i++)
                        {
                            cmap[i].mtyp = 1;
                            cmap[i].pcol = (byte)i;
                        }
                    }
                }

                if (!is_sane)
                    return false;
            }

            return true;
        }

        //2.5
        internal bool SetDecodeArea(JPXImage image, int start_x, int start_y, int end_x, int end_y)
        {
            return _j2k.SetDecodeArea(image, start_x, start_y, end_x, end_y);
        }

        /// <summary>
        /// Apply collected palette data
        /// </summary>
        /// <remarks>2.5 - opj_jp2_apply_pclr</remarks>
        internal static bool ApplyPCLR(JPXImage image, JP2Color color, CompressionInfo cinfo)
        {
	        ImageComp[] old_comps, new_comps;
	        byte[] channel_size, channel_sign;
	        uint[] entries;
	        JP2cmap_comp[] cmap;
	        int[] src, dst;
	        uint j, max;
	        ushort i, nr_channels, cmp, pcol;
	        int k, top_k;

	        channel_size = color.jp2_pclr.channel_size;
	        channel_sign = color.jp2_pclr.channel_sign;
	        entries = color.jp2_pclr.entries;
	        cmap = color.jp2_pclr.cmap;
	        nr_channels = color.jp2_pclr.nr_channels;

            for(i = 0; i < nr_channels; ++i)
            {
                //Palette mapping
                cmp = cmap[i].cmp;
                if (image.comps[cmp].data == null)
                {
                    if (cinfo != null)
                        cinfo.Error("image->comps[{0}].data == NULL in opj_jp2_apply_pclr()", i);
                    return false;
                }
            }

	        old_comps = image.comps;
	        new_comps = new ImageComp[nr_channels];

	        for(i = 0; i < nr_channels; ++i)
            {
	            pcol = cmap[i].pcol; cmp = cmap[i].cmp;

                /* Direct use */
                if (cmap[i].mtyp == 0)
                {
                    Debug.Assert(pcol == 0);
                    new_comps[i] = (ImageComp)old_comps[cmp].Clone();
                }
                else
                {
                    Debug.Assert(pcol == i);
                    new_comps[pcol] = (ImageComp)old_comps[cmp].Clone();    
                }

                /* Palette mapping: */
	            new_comps[pcol].data = new int[old_comps[cmp].w * old_comps[cmp].h];
	            new_comps[pcol].prec = channel_size[i];
	            new_comps[pcol].sgnd = channel_sign[i] != 0;
            }
	        top_k = color.jp2_pclr.nr_entries - 1;

	        for(i = 0; i < nr_channels; ++i)
            {
                /* Palette mapping: */
	            cmp = cmap[i].cmp; 
                pcol = cmap[i].pcol;
	            src = old_comps[cmp].data; 
	            max = (uint) new_comps[i].w * (uint) new_comps[i].h;

                /* Direct use: */
                if (cmap[i].mtyp == 0)
                {
                    dst = new_comps[i].data;
                    for (j = 0; j < max; j++)
                        dst[j] = src[j];
                }
                else
                {
                    dst = new_comps[pcol].data;

                    for (j = 0; j < max; ++j)
                    {
                        /* The index */
                        if ((k = src[j]) < 0)
                            k = 0;
                        else if (k > top_k)
                            k = top_k;

                        /* The colour */
                        dst[j] = (int)entries[k * nr_channels + pcol];
                    }
                }
            }
	        max = (uint) image.numcomps;
            for (i = 0; i < max; i++)
            {
                if (old_comps[i].data != null)
                    old_comps[i].data = null;
            }

	        image.comps = new_comps;
	        image.numcomps = nr_channels;

            color.jp2_pclr = null;

            return true;
        }

        //2.5 - opj_jp2_apply_cdef
        void ApplyCDEF(JPXImage image, JP2Color color)
        {
	        ushort i, cn, typ, asoc, acn;

	        var info = color.channel_definitions;
            if (info == null) return;

	        for(i = 0; i < info.Length; ++i)
            {
                asoc = info[i].asoc;
                cn = info[i].cn;

                if (cn >= image.numcomps)
                {
                    _cinfo.Warn("opj_jp2_apply_cdef: cn ={0}, numcomps ={1}",
                        cn, image.numcomps);
                    continue;
                }
                if (asoc == 0 || asoc == 65535)
                {
                    image.comps[cn].alpha = info[i].typ;
                    continue;
                }

                acn = (ushort) (asoc - 1);
                if (acn >= image.numcomps)
                {
                    _cinfo.Warn("opj_jp2_apply_cdef: acn={0}, numcomps={1}", 
                        acn, image.numcomps);
                    continue;
                }

                // Swap only if color channel
                if (cn != acn && info[i].typ == 0)
                {
                    //C# Org impl does memcopies, but it is dealing with structs.
	                ImageComp saved = image.comps[cn];
                    image.comps[cn] = image.comps[acn];
                    image.comps[acn] = saved;

                    // Swap channels in following channel definitions, don't
                    // bother with j <= i that are already processed
                    for (ushort j = (ushort)(i + 1); j < info.Length; j++)
                    {
                        if (info[j].cn == cn)
                            info[j].cn = acn;
                        else if (info[j].cn == acn)
                            info[j].cn = cn;
                        // asoc is related to color index. Do not update
                    }
                }

                image.comps[cn].alpha = info[i].typ;
            }
	        
	        color.channel_definitions = null;
        }

        /// <summary>
        /// Reads the Jpeg2000 file Header box - JP2 Header box (this box contains other boxes).
        /// </summary>
        /// <param name="box">The data contained in the file header box.</param>
        /// <returns>True if the JP2 Header box was successfully reconized</returns>
        /// <remarks>
        /// 2.5 - opj_jp2_read_jp2h
        /// </remarks>
        bool ReadJP2H(JP2Box box) 
        {
            // Make sure the box is well placed
            if ((_state & JP2_STATE.FILE_TYPE) != JP2_STATE.FILE_TYPE)
            {
                _cinfo.Error("The  box must be the first box in the file.");
                return false;
            }

            _img_state = JP2_IMG_STATE.NONE;

            // iterate while there is data
            uint header_size = box.length - 8;
            while (header_size > 0)
            {
                int box_size;
                if (!ReadBoxhdr_char(out box, out box_size, (int)header_size))
                {
                    _cinfo.Error("Stream error while reading JP2 Header box");
                    return false;
                }

                if (box.length > header_size)
                {
                    _cinfo.Error("Stream error while reading JP2 Header box: box length is inconsistent.");
                    return false;
                }

                var handler = ImgFindHandler(box.type);
                box.data_length = box.length - (uint) box_size;

                if (handler != null)
                {
                    var pos = _cio.Pos;
                    if (!handler(box))
                        return false;
                    if ((_cio.Pos - pos) < box.data_length)
                    {
                        //C# OpenJpeg 2.5 effectivly does this as it reads all the data
                        //   for a box before calling the handler. 
                        _cinfo.Warn("{0} box has {1} bytes of junk data",
                            box.type, box.data_length - (_cio.Pos - pos));
                        _cio.Skip((uint)(box.data_length - (_cio.Pos - pos)));
                    }
                }
                else
                {
                    _img_state |= JP2_IMG_STATE.UNKNOWN;
                    _cio.Skip(box.data_length);
                }

                header_size -= box.length;
            }

            if (!_has_ihdr)
            {
                _cinfo.Error("Stream error while reading JP2 Header box: no 'ihdr' box");
                return false;
            }

            _state |= JP2_STATE.HEADER;

            return true;
        }

        //2.5 - opj_jp2_read_cmap
        bool ReadCMAP(JP2Box box)
        {
	        JP2cmap_comp[] cmap;
	        ushort i, nr_channels;

            /* Need nr_channels: */
            if (_color.jp2_pclr == null)
            {
                _cinfo.Error("Need to read a PCLR box before the CMAP box.");
                return false;
            }

            /* Part 1, I.5.3.5: 'There shall be at most one Component Mapping box
             * inside a JP2 Header box' :
            */
            if (_color.jp2_pclr.cmap != null)
            {
                _cinfo.Error("Only one CMAP box is allowed.");
                return false;
            }

	        nr_channels = _color.jp2_pclr.nr_channels;
            if (box.data_length < nr_channels * 4)
            {
                _cinfo.Error("Insufficient data for CMAP box.");
                return false;
            }

            cmap = new JP2cmap_comp[nr_channels];

	        for(i = 0; i < nr_channels; ++i)
            {
                cmap[i].cmp = _cio.ReadUShort();
                cmap[i].mtyp = _cio.ReadByte();
                cmap[i].pcol = _cio.ReadByte();
            }
	        _color.jp2_pclr.cmap = cmap;

	        return true;
        }

        //2.5 - opj_jp2_read_pclr
        bool ReadPCLR(JP2Box box)
        {
	        JP2pclr jp2_pclr;
	        byte[] channel_size, channel_sign;
	        uint[] entries;
	        ushort nr_entries, nr_channels;
	        byte uc;
            var org_pos = _cio.Pos;

            /* Part 1, I.5.3.4: 'There shall be at most one Palette box inside
             * a JP2 Header box' :
            */
	        if(_color.jp2_pclr != null) return false;

            if (box.data_length < 3)
                return false;

	        nr_entries = _cio.ReadUShort(); /* NE */
            if (nr_entries == 0 || nr_entries > 1024)
            {
                _cinfo.Error("Invalid PCLR box. Reports {0} entries", nr_entries);
                return false;
            }

	        nr_channels = _cio.ReadByte(); /* NPC */
            if (nr_channels == 0)
            {
                _cinfo.Error("Invalid PCLR box. Reports 0 palette columns");
                return false;
            }

            if (box.data_length < 3 + nr_channels)
                return false;

	        entries = new uint[nr_channels * nr_entries];
	        channel_size = new byte[nr_channels];
	        channel_sign = new byte[nr_channels];

            jp2_pclr = new JP2pclr();
	        jp2_pclr.channel_sign = channel_sign;
	        jp2_pclr.channel_size = channel_size;
	        jp2_pclr.entries = entries;
	        jp2_pclr.nr_entries = nr_entries;
	        jp2_pclr.nr_channels = nr_channels;
	        jp2_pclr.cmap = null;

	        _color.jp2_pclr = jp2_pclr;

	        for(int i = 0; i < nr_channels; ++i)
            {
                uc = _cio.ReadByte(); // Bi
	            channel_size[i] = (byte) ((uc & 0x7f) + 1);
	            channel_sign[i] = (byte) (((uc & 0x80) == 0x80) ? 1 : 0);
            }

	        for(int j = 0, k = 0; j < nr_entries; ++j)
            {
	            for(int i = 0; i < nr_channels; ++i)
                {
                    uint bytes_to_read = (uint)(channel_size[i] + 7) >> 3;

                    //mem-b2ace68c-1381.jp2 triggers this condition. File decodes
                    //fine without this check.
                    if (box.data_length < (_cio.Pos - org_pos) + bytes_to_read)
                        return false;

                    /* Cji */
                    entries[k++] = unchecked((uint) _cio.Read(bytes_to_read));
                }
            }

	        return true;
        }

        /// <summary>
        /// Channel defenition box
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_jp2_read_cdef
        /// This box defines what channels are alpha channels and such
        /// </remarks>
        bool ReadCDEF(JP2Box box)
        {
	        JP2cdefInfo[] info;
	        ushort i, n;

            /* Part 1, I.5.3.6: 'The shall be at most one Channel Definition box
             * inside a JP2 Header box.' 
            */
	        if(_color.channel_definitions != null) return false;

            if (box.data_length < 2)
            {
                _cinfo.Error("Insufficient data for CDEF box.");
                return false;
            }

            n = _cio.ReadUShort();
            if (n == 0)
            {
                _cinfo.Error("Number of channel description is equal to zero in CDEF box.");
                return false;
            }

            if (box.data_length < 2 + n * 6)
            {
                _cinfo.Error("Insufficient data for CDEF box.");
                return false;
            }

	        info = new JP2cdefInfo[n];
	        _color.channel_definitions = info;

	        for(i = 0; i < n; ++i)
            {
                info[i].cn = _cio.ReadUShort();
                info[i].typ = _cio.ReadUShort();
                info[i].asoc = _cio.ReadUShort();
            }

	        return true;
        }

        //2.5 - opj_jp2_read_colr
        bool ReadCOLR(JP2Box box) 
        {
            if (box.data_length < 3)
            {
                _cinfo.Error("Bad COLR header box (bad size)");
                return false;
            }

            /* Part 1, I.5.3.3 : 'A conforming JP2 reader shall ignore all Colour
             * Specification boxes after the first.' 
            */
            if (_color.HasColor)
            {
                _cinfo.Info("A conforming JP2 reader shall ignore all Colour Specification boxes after the first, so we ignore this one.");
                _cio.Skip(box.data_length);
                return true;
            }

	        _meth = _cio.ReadByte();
	        _precedence = _cio.ReadByte();
	        _approx = _cio.ReadByte();

	        if (_meth == 1)
            {
                if (box.data_length < 7)
                {
                    _cinfo.Error("Bad COLR header box (bad size: {0})", box.data_length);
                    return false;
                }
                if (box.data_length > 7 && _enumcs != 14)
                {
                    // Testcase Altona_Technical_v20_x4.pdf
                    _cinfo.Warn("Bad COLR header box (bad size: {0})", box.data_length);
                }
	            _enumcs = _cio.ReadUInt();

                if (_enumcs == 14)
                { // CIELab
                    var cielab = new uint[9];
                    cielab[0] = 14; // Enumcs

                    uint rl, ol, ra, oa, rb, ob, il;
                    rl = ra = rb = ol = oa = ob = 0;
                    il = 0x00443530; // D50
                    cielab[1] = 0x44454600; // DEF

                    if (box.data_length == 35)
                    {
                        rl = _cio.ReadUInt();
                        ol = _cio.ReadUInt();
                        ra = _cio.ReadUInt();
                        oa = _cio.ReadUInt();
                        rb = _cio.ReadUInt();
                        ob = _cio.ReadUInt();
                        il = _cio.ReadUInt();

                        cielab[1] = 0;
                    } else if (box.data_length != 7)
                    {
                        _cinfo.Warn("Bad COLR header box (CIELab, bad size: {0})", box.data_length);
                    }
                    cielab[2] = rl;
                    cielab[4] = ra;
                    cielab[6] = rb;
                    cielab[3] = ol;
                    cielab[5] = oa;
                    cielab[7] = ob;
                    cielab[8] = il;

                    _color.icc_cielab_buf = cielab;
                }

                _color.HasColor = true;
            } 
	        else if (_meth == 2)
            {
                /* ICC profile */
	            int icc_len = (int) box.data_length - 3;
                Debug.Assert((int) (box.init_pos + box.length - _cio.Pos) == box.data_length - 3);

	            _color.icc_profile_buf = new byte[icc_len];
                if (_cio.Read(_color.icc_profile_buf, 0, icc_len) != icc_len)
                    throw new EndOfStreamException();

                _color.HasColor = true;
            }
            else
            {
                /*	ISO/IEC 15444-1:2004 (E), Table I.9 ­ Legal METH values:
                    conforming JP2 reader shall ignore the entire Colour Specification box.*/
                _cio.Skip(box.data_length - 3);
            }

	        return true;
        }

        //2.5 - opj_jp2_read_bpcc
        bool ReadBPCC(JP2Box box)
        {
            if (_bpc != 255)
                _cinfo.Warn("A BPCC header box is available although BPC given by the IHDR box ({0}) indicate components bit depth is constant", _bpc);

            if (box.data_length != _numcomps)
            {
                _cinfo.Error("Bad BPCC header box (bad size)");
                return false;
            }

            for (int i = 0; i < _numcomps; i++)
            {
                _comps[i].bpcc = _cio.ReadByte();
            }

	        return true;
        }

        //2.5 - opj_jp2_read_ihdr
        bool ReadIHDR(JP2Box box)
        {
            if (_comps!= null)
            {
                _cinfo.Warn("Ignoring ihdr box. First ihdr box already read");
                return true;
            }

            if (box.data_length != 14)
            {
                _cinfo.Error("Bad image header box (bad size)");
                return false;
            }

            //Width and height
            _h = _cio.ReadUInt();
            _w = _cio.ReadUInt();

            _numcomps = _cio.ReadUShort();

            if (_h < 1 || _w < 1 || _numcomps < 1)
            {
                _cinfo.Error("Wrong values for: w{0}) h({1}) numcomps({2}) (ihdr)", _w, _h, _numcomps);
                return false;
            }
            if (_numcomps - 1U >= 16384U)
            {
                // Unsigned underflow is well defined: 1U <= jp2->numcomps <= 16384U
                _cinfo.Error("Invalid number of components (ihdr)");
                return false;

            }

            _comps = new JP2Comps[_numcomps];

            _bpc = _cio.ReadByte();

            _C = _cio.ReadByte();

            if (_C != 7)
            {
                _cinfo.Info("JP2 IHDR box: compression type indicate that the file is not a conforming JP2 file ({0}) ", _C);
            }

            _UnkC = _cio.ReadBool();
            _IPR = _cio.ReadBool();

            _j2k.CP.AllowDifferentBitDepthSign = (_bpc == 255);
            _j2k._ihdr_w = _w;
            _j2k._ihdr_h = _h;
            _has_ihdr = true;

            return true;
        }

        /// <summary>
        /// Reads a a FTYP box - File type box
        /// </summary>
        /// <param name="box">The data contained in the FTYP box</param>
        /// <returns>True if the FTYP box is valid</returns>
        /// <remarks>
        /// 2.5 - opj_jp2_read_ftyp
        /// </remarks>
        bool ReadFTYP(JP2Box box)
        {
            if (_state != JP2_STATE.SIGNATURE)
            {
                _cinfo.Error("The ftyp box must be the second box in the file.");
                return false;
            }

            if (box.length < 16)
            {
                _cinfo.Error("Error with FTYP signature Box size");
                return false;
            }

            _brand = (JP2_Marker)_cio.ReadUInt();
            _minversion = _cio.ReadUInt();

            int remaining_bytes = (int) box.length - 16;

            // Number of bytes must be a multiple of 4
            if ((remaining_bytes & 0x3) != 0)
            {
                _cinfo.Error("Error with FTYP signature Box size");
                return false;
            }

            _cl = new JP2_Marker[remaining_bytes / 4];

            for (int i = 0; i < _cl.Length; i++)
            {
                _cl[i] = (JP2_Marker)_cio.ReadUInt();
            }

            _state |= JP2_STATE.FILE_TYPE;

	        return true;
        }

        /// <summary>
        /// Reads a jpeg2000 file signature box.
        /// </summary>
        /// <param name="box">The data contained in the signature box</param>
        /// <returns>Rrue if the file signature box is valid</returns>
        /// <remarks>
        /// 2.5 - opj_jp2_read_jp
        /// </remarks>
        bool ReadJP(JP2Box box)
        {
            if (_state != JP2_STATE.NONE)
            {
                _cinfo.Error("The signature box must be the first box in the file.");
                return false;
            }

            if (box.length != 12)
            {
                _cinfo.Error("Error with JP signature Box size");
                return false;
            }

            if (0x0d0a870a != _cio.ReadInt())
            {
                _cinfo.Error("Error with JP Signature : bad magic number");
                return false;
            }

            _state |= JP2_STATE.SIGNATURE;

            return true;
        }

        /// <summary>
        /// Reads a box header. The box is the way data is packed inside a jpeg2000 file structure.
        /// </summary>
        /// <remarks>2.5 - opj_jp2_read_boxhdr</remarks>
        bool ReadBoxhdr(out JP2Box box, out int n_bytes_read)
        {
            box = new JP2Box();
            box.init_pos = _cio.Pos;

            if (_cio.BytesLeft < 8)
            {
                n_bytes_read = (int)_cio.BytesLeft;
                _cio.Skip((uint)n_bytes_read);
                return false;
            }

            box.length = _cio.ReadUInt();
            box.type = (JP2_Marker) _cio.ReadUInt();
            n_bytes_read = 8;

            // Do we have a "special very large box ?
            // read then the XLBo
            if (box.length == 1)
            {
                if (_cio.ReadInt() != 0)
                {
                    _cinfo.Error("Cannot handle box sizes higher than 2^32");
                    n_bytes_read += 4;
                    return false;
                }
                box.length = _cio.ReadUInt();
                n_bytes_read = 16;
            }
            else if (box.length == 0) // last box
            {
                var bleft = _cio.BytesLeft;
                if (bleft > 0xFFFFFFFFL - 8L)
                {
                    _cinfo.Error("Cannot handle box sizes higher than 2^32");
                    return false;
                }
                box.length = (uint) (bleft + 8);
            }

            return true;
        }

        //2.5 - opj_jp2_read_boxhdr_char
        bool ReadBoxhdr_char(out JP2Box box, out int n_bytes_read, int max_size)
        {
            box = new JP2Box();
            if (max_size < 8)
            {
                n_bytes_read = 0;
                _cinfo.Error("Cannot handle box of less than 8 bytes");
                return false;
            }

            box.init_pos = _cio.Pos;
            box.length = _cio.ReadUInt();
            box.type = (JP2_Marker)_cio.ReadUInt();
            n_bytes_read = 8;

            // Do we have a "special very large box
            // read then the XLBox
            if (box.length == 1)
            {
                if (max_size < 8)
                {
                    _cinfo.Error("Cannot handle XL box of less than 16 bytes");
                    return false;
                }

                if (_cio.ReadInt() != 0)
                {
                    _cinfo.Error("Cannot handle box sizes higher than 2^32");
                    n_bytes_read += 4;
                    return false;
                }
                box.length = _cio.ReadUInt();
                n_bytes_read = 16;

                if (box.length == 0)
                {
                    _cinfo.Error("Cannot handle box of undefined sizes");
                    return false;
                }
            }
            else if (box.length == 0)
            {
                _cinfo.Error("Cannot handle box of undefined sizes");
                return false;
            }
            if (box.length < n_bytes_read)
            {
                _cinfo.Error("Box length is inconsistent");
                return false;
            }

            return true;
        }

        //2.5 - opj_jp2_encode
        internal bool Encode()
        {
            return _j2k.Encode();
        }

        //2.5 - opj_jp2_default_validation
        bool DefaultValidation(Stream cio)
        {
            bool l_is_valid = true;
            int i;

            /* JPEG2000 codec validation */

            /* STATE checking */
            /* make sure the state is at 0 */
            l_is_valid &= (_state == JP2_STATE.NONE);

            /* make sure not reading a jp2h ???? WEIRD */
            l_is_valid &= (_img_state == JP2_IMG_STATE.NONE);

            /* POINTER validation */
            /* make sure a j2k codec is present */
            l_is_valid &= (_j2k != null);

            /* make sure a procedure list is present */
            //l_is_valid &= (_procedure_list != null);

            /* make sure a validation list is present */
            //l_is_valid &= (_validation_list != null);

            /* PARAMETER VALIDATION */
            /* number of components */
            l_is_valid &= (_cl != null);
            /* width */
            l_is_valid &= (_h > 0);
            /* height */
            l_is_valid &= (_w > 0);
            /* precision */
            for (i = 0; i < _numcomps; ++i)
            {
                l_is_valid &= ((_comps[i].bpcc & 0x7FU) < 38U); //Bug in org. impl?
            }

            /* METH */
            l_is_valid &= ((_meth > 0) && (_meth < 3));

            /* stream validation */
            /* back and forth is needed */
            l_is_valid &= cio.CanSeek;

            return l_is_valid;
        }

        //2.5 - opj_jp2_start_compress
        internal bool StartCompress(CIO cio)
        {
            var bcio = new BufferCIO(cio);
            {
                byte[] buf = new byte[256];
                bcio.SetBuffer(ref buf, 256);
            }

            if (!DefaultValidation(cio.Stream))
                return false;

            if (!WriteHeader(bcio))
                return false;

            //Makes room for the Code Stream marker, which will be
            //written later.
            Debug.Assert(bcio.BufferPos == 0);
            SkipJP2C(cio.Stream);

            return _j2k.StartCompress(bcio);
        }

        //2.5 - opj_jp2_end_compress
        internal bool EndCompress()
        {
            var bcio = _j2k.EndGetBCIO();

            // Writes header
            WriteJP2C(bcio);

            return true;
        }

        //2.5 - opj_jp2_setup_header_writing
        bool WriteHeader(BufferCIO bcio)
        {
            WriteJP(bcio);
            WriteFTYP(bcio);
            if (!WriteJP2H(bcio))
                return false;

            //C# Skip is called by the parent function
            return true;
        }

        /// <summary>
        /// Makes room for the Code Stream marker and
        /// store away the position.
        /// </summary>
        /// <remarks>2.5 - opj_jp2_skip_jp2c</remarks>
        void SkipJP2C(Stream cio)
        {
            _j2k_codestream_offset = cio.Position;
            cio.Seek(8, SeekOrigin.Current);
        }


        /// <summary>
        /// Writes the Jpeg2000 codestream Header box - JP2C Header box. This function must be called AFTER the coding has been done.
        /// </summary>
        /// <remarks>2.5 - opj_jp2_write_jp2c</remarks>
        void WriteJP2C(BufferCIO bcio) 
        {
            long j2k_codestream_exit, j2k_codestream_length;

	        /* J2K encoding */
            j2k_codestream_exit = bcio.Pos;
            j2k_codestream_length = j2k_codestream_exit - _j2k_codestream_offset;

            bcio.Pos = _j2k_codestream_offset;
            bcio.Write((int) j2k_codestream_length);
            bcio.Write(JP2_Marker.JP2C);
            bcio.Commit();
            bcio.Pos = j2k_codestream_exit;
        }

        /// <summary>
        /// JP2 header
        /// </summary>
        /// <remarks>2.5 - opj_jp2_write_jp2h</remarks>
        bool WriteJP2H(BufferCIO bcio)
        {
            uint jp2h_size = 8;

            //C# - Calculate the needed buffer size
            //IHDR needs 22 bytes
            jp2h_size += 22;

            //Adds for Bit per Component
            uint bpcc_size = 0;
            if (_bpc == 255)
                bpcc_size += 8 + _numcomps;
            jp2h_size += bpcc_size;

            //Adds for the color info
            uint colr_size = 11;
            switch(_meth)
            {
                case 1:
                    colr_size += 4; break;
                case 2:
                    colr_size += (uint) _color.icc_profile_buf.Length; break;
                default:
                    return false;
            }
            jp2h_size += colr_size;

            uint cdef_size = 0;
            if (_color != null && _color.channel_definitions != null)
                cdef_size = 10u + 6u * (uint)_color.channel_definitions.Length;
            jp2h_size += cdef_size;

            bcio.SetBuffer(jp2h_size);

            //Writes out the length
            bcio.Write(jp2h_size);

            //Signature
            bcio.Write(JP2_Marker.JP2H);

            WriteIHDR(bcio);

            if (_bpc == 255)
                Write_BPCC(bcio, bpcc_size);
            WriteCOLR(bcio, colr_size);

            if (_color != null && _color.channel_definitions != null)
                WriteCDEF(bcio, cdef_size);

            Debug.Assert(bcio.BufferPos == jp2h_size);
            bcio.Commit();

            return true;
        }

        /// <summary>
        ///  Writes the Channel Definition box.
        /// </summary>
        /// <remarks>2.5 - opj_jp2_write_cdef</remarks>
        void WriteCDEF(BufferCIO bcio, uint cdef_size)
        {
            var channel_definitions = _color.channel_definitions;

            bcio.Write(cdef_size);
            bcio.Write(JP2_Marker.CDEF);

            //Writes number of definitions
            bcio.WriteUShort(channel_definitions.Length);

            for (int c = 0; c < channel_definitions.Length; c++)
            {
                bcio.WriteUShort(channel_definitions[c].cn);
                bcio.WriteUShort(channel_definitions[c].typ);
                bcio.WriteUShort(channel_definitions[c].asoc);
            }
        }

        /// <summary>
        /// Writes the Image Header box - Image Header box.
        /// </summary>
        /// <remarks>2.5 - opj_jp2_write_ihdr</remarks>
        void WriteIHDR(BufferCIO bcio)
        {
            bcio.Write(22); // Size of the box
            bcio.Write(JP2_Marker.IHDR);

            //Writes out the height and width
            bcio.Write(_h);
            bcio.Write(_w);

            bcio.Write(_numcomps, 2);

            bcio.Write(_bpc, 1);	

            bcio.Write(_C, 1);
            bcio.Write(_UnkC);
            bcio.Write(_IPR);
        }

        /// <summary>
        /// Writes the Bit per Component box
        /// </summary>
        /// <remarks>2.5 - opj_jp2_write_bpcc</remarks>
        void Write_BPCC(BufferCIO bcio, uint bpcc_size)
        {
            bcio.Write(bpcc_size);
            bcio.Write(JP2_Marker.BPCC);

	        for (int i = 0; i < _numcomps; i++)
		        bcio.Write(_comps[i].bpcc, 1);
        }

        /// <summary>
        /// Writes the Colour Specification box
        /// </summary>
        /// <remarks>2.5 - opj_jp2_write_colr</remarks>
        void WriteCOLR(BufferCIO bcio, uint colr_size)
        {
            bcio.Write(colr_size);
            bcio.Write(JP2_Marker.COLR);

            bcio.Write(_meth, 1);
            bcio.Write(_precedence, 1);
            bcio.Write(_approx, 1);

            if (_meth == 1)
                bcio.Write(_enumcs);
            else
                bcio.Write(_color.icc_profile_buf, 0, _color.icc_profile_buf.Length);
        }

        /// <summary>
        /// File type
        /// </summary>
        /// <remarks>2.5 - opj_jp2_write_ftyp</remarks>
        void WriteFTYP(BufferCIO bcio)
        {
            int ftyp_size = 16 + 4 * _cl.Length;
            bcio.SetBuffer((uint)ftyp_size);

            //Writes the length
            bcio.Write(ftyp_size);

            //Signature
	        bcio.Write(JP2_Marker.FTYP);

	        bcio.Write(_brand);
	        bcio.Write(_minversion);

	        for (int i = 0; i < _cl.Length; i++)
		        bcio.Write(_cl[i]);

            bcio.Commit();
        }

        /// <summary>
        /// Signature
        /// </summary>
        /// <remarks>2.5 - opj_jp2_write_jp</remarks>
        bool WriteJP(BufferCIO bcio)
        {
            //Writes the length
            bcio.Write(12);

            //Writes out the JP2 signature
            bcio.Write(JP2_Marker.JP);

            //Writes magic number
            bcio.Write(0x0d0a870a);

            bcio.Commit();

            return true;
        }
    }
}
