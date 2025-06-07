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
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using OPJ_UINT32 = System.UInt32;
#endregion

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Tile coder/decoder
    /// </summary>
    internal sealed class TileCoder
    {
        private delegate void ThreadWorker(object job);
        private delegate IEnumerable<object> CreateJobs(bool mt, Tier1Coding.SetRet ret);

        #region Variables and properties

        internal bool DisableMultiThreading => _cinfo.DisableMultiThreading;

        /// <summary>
        /// Total number of tileparts of the current tile
        /// </summary>
        internal uint CurTotnumTp 
        { get { return _cur_totnum_tp; } set { _cur_totnum_tp = value; } }

        internal uint CurPino
        { get { return _cur_pino; } set { _cur_pino = value; } }

        internal int TpPos
        { get { return _tp_pos; } set { _tp_pos = value; } }

        internal uint TpNum
        { get { return _tp_num; } set { _tp_num = value; } }

        internal uint CurTpNum
        { get { return _cur_tp_num; } set { _cur_tp_num = value; } }

        internal TcdImage TcdImage
        { get { return _tcd_image; } }

        internal JPXImage Image
        { get { return _image; } }

        /// <summary>
        /// Coordinates of the window of interest, in grid reference space
        /// </summary>
        private uint _win_x0, _win_y0, _win_x1, _win_y1;

        /// <summary>
        /// Whether the whole tile is decoded, or just the region in win_x0/win_y0/win_x1/win_y1
        /// </summary>
        /// <remarks>
        /// Only valid for decoding. 
        /// </remarks>
        internal bool WholeTileDecoding { get; set; }

        private bool[] _used_component;

        /// <summary>
        /// Position of the tilepart flag in Progression order
        /// </summary>
        int _tp_pos;

        /// <summary>
        /// Tile part number
        /// </summary>
        uint _tp_num;

        /// <summary>
        /// Current tile part number
        /// </summary>
        uint _cur_tp_num;

        /// <summary>
        /// Total number of tileparts of the current tile
        /// </summary>
        uint _cur_totnum_tp;

        /// <summary>
        /// Current Packet iterator number
        /// </summary>
        uint _cur_pino;

        /// <summary>
        /// Codec context
        /// </summary>
        readonly CompressionInfo _cinfo;

        /// <summary>
        /// Info on each image tile
        /// </summary>
        TcdImage _tcd_image;

        /// <summary>
        /// Image
        /// </summary>
        JPXImage _image;

        /// <summary>
        /// Coding parameters
        /// </summary>
        CodingParameters _cp;

        /// <summary>
        /// Pointer to the current encoded/decoded tile
        /// </summary>
        //TcdTile _tcd_tile;

        /// <summary>
        /// Coding/decoding parameters common to all tiles
        /// </summary>
        TileCodingParams _tcp;

        /// <summary>
        /// Current encoded/decoded tile
        /// </summary>
        uint _tcd_tileno;

        /// <summary>
        /// Time taken to encode a tile
        /// </summary>
        double _encoding_time;

        #endregion

        #region Init

        /// <summary>
        /// Creates a tile-coder encoder
        /// </summary>
        /// <remarks>2.5 - opj_j2k_create_tcd</remarks>
        internal TileCoder(CompressionInfo cinfo, JPXImage image, CodingParameters cp)
        {
            _cinfo = cinfo;
            _tcd_image = new TcdImage();
            Init(image, cp);
        }

        /// <summary>
        /// Initialize the tile coder and may reuse some memory
        /// </summary>
        /// <remarks>2.5 - opj_tcd_init</remarks>
        void Init(JPXImage image, CodingParameters cp)
        {
            _cp = cp;
            _image = image;

            var tile = new TcdTile();

            _tcd_image.tiles = new TcdTile[1] { tile };
            tile.comps = new TcdTilecomp[image.numcomps];
            for(int i = 0; i < tile.comps.Length; i++)
                tile.comps[i] = new TcdTilecomp();

            tile.numcomps = image.numcomps;
            _tp_pos = cp.specific_param.enc.tp_pos;
        }

        /// <summary>
        /// Encodes a tile from the raw image into the given buffer
        /// </summary>
        /// <param name="tileno">Index of the tile to encode</param>
        /// <param name="dest">Destination position</param>
        /// <param name="data_written">pointer to an int that is incremented by the number of bytes really written on p_dest</param>
        /// <param name="max_length">Maximum length of the destination buffer</param>
        /// <param name="marker_info">Marker information structure</param>
        /// <returns>true if the coding is successful</returns>
        /// <remarks>2.5 - opj_tcd_encode_tile</remarks>
        internal bool EncodeTile(uint tileno, BufferCIO dest, out uint data_written, int max_length, TcdMarkerInfo marker_info)
        {
            if (_cur_tp_num == 0)
            {
                _tcd_tileno = tileno;
                _tcp = _cp.tcps[tileno];

                //C# snip cstr_info code. In OpenJpeg 2.5, the cstr_info is always null. I.e. the code will never execute
                //                        unless you remove commented out code in the calling method (opj_j2k_write_sod)

                //---------------TILE-------------------
                //DumpTilcomp("Enc - Before DC");
                DcLevelShiftEncode();
                //DumpTilcomp("Enc - After DC");

                if (!MctEncode())
                {
                    data_written = 0;
                    return false;
                }

                //DumpTilcomp("Enc - Before DWT");

                DwtEncode();

                //DumpTilcomp("Enc - Before T1");

                T1Encode();

                //DumpEncCblks("");

                RateAllocateEncode(dest, max_length);
            }

            ////------------------TIER2-----------------

            return T2Encode(dest, out data_written, (uint) max_length, marker_info);

            ////------------------CLEAN-----------------
        }

        //2.5 - opj_tcd_dc_level_shift_encode
        void DcLevelShiftEncode()
        {
            var tile = _tcd_image.tiles[0];
            TcdTilecomp[] tile_comps = tile.comps;
            var tccps = _tcp.tccps;

            for (int compno = 0; compno < tile.numcomps; compno++)
            {
                var tile_comp = tile_comps[compno];
                OPJ_UINT32 n_elem = (OPJ_UINT32) ((tile_comp.x1 - tile_comp.x0) * (tile_comp.y1 - tile_comp.y0));
                var tccp = tccps[compno];

                int current_pointer = 0;
                int[] data = tile_comp.data;

                if (tccp.qmfbid == 1)
                {
                    for (int i = 0; i < n_elem; i++)
                    {
                        data[current_pointer] -= tccp.dc_level_shift;
                        current_pointer++;
                    }
                }
                else
                {
                    var iof = new IntOrFloat();
                    for (int i = 0; i < n_elem; i++)
                    {
                        iof.F = (data[current_pointer] - tccp.dc_level_shift);
                        data[current_pointer] = iof.I;
                        current_pointer++;
                    }
                }
            }
        }

        //2.5 - opj_tcd_mct_encode
        bool MctEncode()
        {
            if (_tcp.mct == 0)
                return true;

            var tile = _tcd_image.tiles[0];
            var tile_comp = tile.comps[0];
            OPJ_UINT32 samples = (OPJ_UINT32)((tile_comp.x1 - tile_comp.x0) * (tile_comp.y1 - tile_comp.y0));

            if (_tcp.mct == 2)
            {
                Debug.Assert(false, "untested code");
                if (_tcp.mct_coding_matrix == null)
                    return true;

                int[][] data = new int[tile.numcomps][];
                for(int i=0; i < tile.numcomps; i++)
                {
                    data[i] = tile.comps[i].data;
                }

                MCT.EncodeCustom(_tcp.mct_coding_matrix, (int) samples, data, _image.comps[0].sgnd);
            }
            else if (_tcp.tccps[0].qmfbid == 0)
            {
                MCT.EncodeReal(tile.comps[0].data, tile.comps[1].data, tile.comps[2].data, (int) samples);
            }
            else
            {
                MCT.Encode(tile.comps[0].data, tile.comps[1].data, tile.comps[2].data, (int) samples);
            }


            return true;
        }

        //2.5 - opj_tcd_dwt_encode
        void DwtEncode()
        {
            var tile = _tcd_image.tiles[0];

            for (int compno = 0; compno < tile.numcomps; compno++)
            {
                TcdTilecomp tilec = tile.comps[compno];
                if (_tcp.tccps[compno].qmfbid == 1)
                    DWT.Encode(this, tilec);
                else if (_tcp.tccps[compno].qmfbid == 0)
                    DWT.EncodeReal(this, tilec);
            }
        }

        //2.5 - opj_tcd_t1_encode
        void T1Encode()
        {
            double[] mct_norms;
            uint mct_numcomps;

            if (_tcp.mct == 1)
            {
                mct_numcomps = 3;

                // Irreversible encoding
                if (_tcp.tccps[0].qmfbid == 0)
                    mct_norms = MCT.GetMctNormsReal();
                else
                    mct_norms = MCT.GetMctNorms();
            }
            else
            {
                mct_numcomps = _image.numcomps;
                mct_norms = _tcp.mct_norms;
            }

            EncodeCblks(_tcd_image.tiles[0], _tcp, mct_norms, mct_numcomps);
        }

        /// <summary>
        /// Encode the code-blocks of a tile
        /// </summary>
        /// <param name="tcd">TCD handle</param>
        /// <param name="tile">The tile to encode</param>
        /// <param name="tcp">Tile coding parameters</param>
        /// <param name="mct_norms"></param>
        /// <param name="mct_numcomps">Number of components used for MCT</param>
        /// <remarks>
        /// 2.5 - opj_t1_encode_cblks
        /// </remarks>
        internal bool EncodeCblks(
            TcdTile tile,
            TileCodingParams tcp,
            double[] mct_norms,
            uint mct_numcomps)
        {
            return RunThreads((mt, set_ret) =>
            {
                return Tier1Coding.EncodeCblks(tile, tcp, mct_norms, mct_numcomps, set_ret);
            }, (obj) =>
            {
                Tier1Coding.ThreadWorker((Tier1Coding.T1CBLKEncodeProcessingJob)obj);
            });
        }

        //2.5 - opj_tcd_t2_encode
        bool T2Encode(BufferCIO dest, out uint data_written, OPJ_UINT32 max_dest_size, TcdMarkerInfo marker_info)
        {
            var t2 = new Tier2Coding(_cinfo, _image, _cp);

            return t2.EncodePackets(_tcd_tileno, 
                                    _tcd_image.tiles[0], 
                                    _tcp.numlayers, 
                                    dest, 
                                    out data_written, 
                                    max_dest_size, 
                                    marker_info,
                                    _tp_num, 
                                    _tp_pos, 
                                    _cur_pino, 
                                    T2_MODE.FINAL_PASS);
        }

        //2.5 - opj_tcd_rate_allocate_encode
        void RateAllocateEncode(BufferCIO dest, int max_dest_size)
        {
            if (_cp.specific_param.enc.quality_layer_alloc_strategy == J2K_QUALITY_LAYER_ALLOCATION_STRATEGY.RATE_DISTORTION_RATIO || 
                _cp.specific_param.enc.quality_layer_alloc_strategy == J2K_QUALITY_LAYER_ALLOCATION_STRATEGY.FIXED_DISTORTION_RATIO)
            {
                uint dw = 0;
                Rateallocate(dest, ref dw, max_dest_size);
            }
            else
            {
                Debug.Assert(false, "Untested code");
                RateallocateFixed();
            }
        }

        //2.5.1 - opj_tcd_rateallocate_fixed
        void RateallocateFixed()
        {
            for (int layno = 0; layno < _tcp.numlayers; layno++)
                MakeLayerFixed(layno, true);
        }

        /// <summary>
        /// Rate allocation for the following methods:
        ///  - allocation by rate/distortio (quality_layer_alloc_strategy == RATE_DISTORTION_RATIO)
        ///  - allocation by fixed quality  (quality_layer_alloc_strategy == FIXED_DISTORTION_RATIO)
        /// </summary>
        /// <remarks>2.5 - opj_tcd_rateallocate</remarks>
        bool Rateallocate(BufferCIO dest, ref uint data_written, int len)
        {
            double[] cumdisto = new double[100];
            const double K = 1;
            double maxSE = 0;

            double min = double.MaxValue;
            double max = 0;

            var tcd_tile = _tcd_image.tiles[0];
            tcd_tile.numpix = 0;

            for (int compno = 0; compno < tcd_tile.numcomps; compno++)
            {
                var tilec = tcd_tile.comps[compno];
                tilec.numpix = 0;

                for (int resno = 0; resno < tilec.numresolutions; resno++)
                {
                    TcdResolution res = tilec.resolutions[resno];
                    for (int bandno = 0; bandno < res.numbands; bandno++)
                    {
                        TcdBand band = res.bands[bandno];
                        if (band.IsEmpty) continue;

                        for (int precno = 0; precno < res.pw * res.ph; precno++)
                        {
                            TcdPrecinct prc = band.precincts[precno];
                            for (int cblkno = 0; cblkno < prc.cw * prc.ch; cblkno++)
                            {
                                TcdCblkEnc cblk = prc.enc[cblkno];
                                for (int passno = 0; passno < cblk.totalpasses; passno++)
                                {
                                    TcdPass pass = cblk.passes[passno];
                                    int dr;
                                    double dd;

                                    if (passno == 0)
                                    {
                                        dr = (int)pass.rate;
                                        dd = pass.distortiondec;
                                    }
                                    else
                                    {
                                        dr = (int)(pass.rate - cblk.passes[passno - 1].rate);
                                        dd = pass.distortiondec - cblk.passes[passno - 1].distortiondec;
                                    }

                                    if (dr == 0) 
                                        continue;

                                    double rdslope = dd / dr;

                                    if (rdslope < min) 
                                        min = rdslope;
                                    if (rdslope > max) 
                                        max = rdslope;
                                }

                                {
                                    uint cblk_pix_count = (uint)((cblk.x1 - cblk.x0) *
                                                                 (cblk.y1 - cblk.y0));
                                    tcd_tile.numpix += cblk_pix_count;
                                    tilec.numpix += cblk_pix_count;
                                }
                            }
                        }
                    }
                }
                maxSE += (((double)(1 << (int)_image.comps[compno].prec) - 1.0)
                    * ((double)(1 << (int)_image.comps[compno].prec) - 1.0))
                    * ((double)(tilec.numpix));
            }

            //C# snip cstr_info

            for (uint layno = 0; layno < _tcp.numlayers; layno++)
            {
                double lo = min;
                double hi = max;
                int maxlen = (_tcp.rates[layno] != 0) ? Math.Min(((int)Math.Ceiling(_tcp.rates[layno])), len) : len;
                double goodthresh;
                double stable_thresh = 0;
                double distotarget = tcd_tile.distotile - ((K * maxSE) / Math.Pow((float)10, _tcp.distoratio[layno] / 10));

                /* Don't try to find an optimal threshold but rather take everything not included yet, if
                  -r xx,yy,zz,0   (quality_layer_alloc_strategy == RATE_DISTORTION_RATIO and rates == NULL)
                  -q xx,yy,zz,0	  (quality_layer_alloc_strategy == FIXED_DISTORTION_RATIO and distoratio == NULL)
                  ==> possible to have some lossy layers and the last layer for sure lossless */
                if ((_cp.specific_param.enc.quality_layer_alloc_strategy == J2K_QUALITY_LAYER_ALLOCATION_STRATEGY.RATE_DISTORTION_RATIO && 
                     _tcp.rates[layno] > 0) || 
                     _cp.specific_param.enc.quality_layer_alloc_strategy == J2K_QUALITY_LAYER_ALLOCATION_STRATEGY.FIXED_DISTORTION_RATIO && 
                     _tcp.distoratio[layno] > 0)
                {
                    var t2 = new Tier2Coding(_cinfo, _image, _cp);
                    double thresh = 0;
                    bool last_layer_allocation_ok = false;
                    var hold = dest.BufferPos;

                    for (int i = 0; i < 128; i++)
                    {
                        double new_thresh = (lo + hi) / 2;

                        // Stop iterating when the threshold has stabilized enough
                        // 0.5 * 1e-5 is somewhat arbitrary, but has been selected
                        // so that this doesn't change the results of the regression
                        // test suite
                        if (Math.Abs(new_thresh - thresh) <= 0.5 * 1e-5 * thresh)
                            break;
                        thresh = new_thresh;

                        bool layer_allocation_is_same = MakeLayer(layno, thresh, false) && i != 0;

                        if (_cp.specific_param.enc.quality_layer_alloc_strategy == J2K_QUALITY_LAYER_ALLOCATION_STRATEGY.FIXED_DISTORTION_RATIO)
                        {
                            double distoachieved;
                            if (_cp.IsCinema || _cp.IsIMF)
                            {
                                if (!t2.EncodePackets(_tcd_tileno, tcd_tile, layno + 1, dest, out data_written, (uint)maxlen, null, _cur_tp_num, _tp_pos, _cur_pino, T2_MODE.THRESH_CALC))
                                {
                                    dest.BufferPos = hold;
                                    lo = thresh;
                                    continue;
                                }
                                else
                                {
                                    dest.BufferPos = hold;
                                    distoachieved = layno == 0 ?
                                        tcd_tile.distolayer[0] : cumdisto[layno - 1] + tcd_tile.distolayer[layno];
                                    if (distoachieved < distotarget)
                                    {
                                        hi = thresh;
                                        stable_thresh = thresh;
                                        continue;
                                    }
                                    else
                                        lo = thresh;
                                }
                            }
                            else
                            {
                                distoachieved = layno == 0 ?
                                        tcd_tile.distolayer[0] : cumdisto[layno - 1] + tcd_tile.distolayer[layno];

                                if (distoachieved < distotarget)
                                {
                                    hi = thresh;
                                    stable_thresh = thresh;
                                    continue;
                                }
                                
                                lo = thresh;
                            }
                        }
                        else
                        {
                            // Disto/rate based optimization
                            //
                            // Check if the layer allocation done by opj_tcd_makelayer()
                            // is compatible of the maximum rate allocation. If not,
                            // retry with a higher threshold.
                            // If OK, try with a lower threshold.
                            // Call opj_t2_encode_packets() only if opj_tcd_makelayer()
                            // has resulted in different truncation points since its last
                            // call.

                            if ((layer_allocation_is_same && !last_layer_allocation_ok) ||
                                (!layer_allocation_is_same &&
                                 !t2.EncodePackets(_tcd_tileno, tcd_tile, layno + 1, dest, 
                                                   out data_written, (uint)maxlen, null, _cur_tp_num, _tp_pos, 
                                                   _cur_pino, 
                                                   T2_MODE.THRESH_CALC)))
                            {
                                last_layer_allocation_ok = false;
                                dest.BufferPos = hold;
                                lo = thresh;
                                continue;
                            }

                            last_layer_allocation_ok = true;
                            dest.BufferPos = hold;
                            hi = thresh;
                            stable_thresh = thresh;
                        }
                    }
                    goodthresh = stable_thresh == 0 ? thresh : stable_thresh;
                    //Snip code to clean up t2. Handled by gc
                }
                else
                {
                    // Special value to indicate to use all passes
                    goodthresh = -1;
                }

                //Snip cstr_info code

                MakeLayer(layno, goodthresh, true);

                //Used for fixed quality
                cumdisto[layno] = (layno == 0) ?
                    tcd_tile.distolayer[0] : (cumdisto[layno - 1] + tcd_tile.distolayer[layno]);
            }

            return true;
        }

        /// <remarks>
        /// 2.5.1 - opj_tcd_makelayer_fixed
        /// 
        /// This code seems to be working, but has not been tested against the original impl.
        /// </remarks>
        void MakeLayerFixed(int layno, bool final)
        {
            Debug.Assert(false, "Untested code");
            var tcd_tile = _tcd_image.tiles[0];
            tcd_tile.distolayer[layno] = 0;
            var matrice = new int[Constants.J2K_TCD_MATRIX_MAX_LAYER_COUNT, Constants.J2K_TCD_MATRIX_MAX_RESOLUTION_COUNT, 3];
            int value;

            for (int compno = 0; compno < tcd_tile.numcomps; compno++)
            {
                TcdTilecomp tilec = tcd_tile.comps[compno];
                for (int i = 0; i < _tcp.numlayers; i++)
                {
                    for (int j = 0; j < tilec.numresolutions; j++)
                    {
                        for (int k = 0; k < 3; k++)
                            matrice[i, j, k] = (int)(_cp.specific_param.enc.matrice[i * tilec.numresolutions * 3 + j * 3 + k]
                                * _image.comps[compno].prec / 16f);
                    }
                }

                for (int resno = 0; resno < tilec.numresolutions; resno++)
                {
                    TcdResolution res = tilec.resolutions[resno];
                    for (int bandno = 0; bandno < res.numbands; bandno++)
                    {
                        TcdBand band = res.bands[bandno];
                        if (band.IsEmpty)
                            continue;

                        for (int precno = 0; precno < res.pw * res.ph; precno++)
                        {
                            TcdPrecinct prc = band.precincts[precno];
                            for (int cblkno = 0; cblkno < prc.cw * prc.ch; cblkno++)
                            {
                                TcdCblkEnc cblk = prc.enc[cblkno];
                                TcdLayer layer = cblk.layers[layno];
                                if (layer == null)
                                {
                                    layer = new TcdLayer();
                                    cblk.layers[layno] = layer;
                                }

                                //number of bit-plan equal to zero
                                int imsb = (int)_image.comps[compno].prec - (int)cblk.numbps;

                                // Correction of the matrix of coefficient to include the IMSB information
                                if (layno == 0)
                                {
                                    value = matrice[layno, resno, bandno];
                                    if (imsb >= value)
                                        value = 0;
                                    else
                                        value -= imsb;
                                }
                                else
                                {
                                    value = matrice[layno, resno, bandno] - matrice[layno - 1, resno, bandno];
                                    if (imsb >= matrice[layno - 1, resno, bandno])
                                    {
                                        value -= imsb - matrice[layno - 1, resno, bandno];
                                        if (value < 0) value = 0;
                                    }
                                }

                                if (layno == 0)
                                    cblk.numpassesinlayers = 0;

                                uint n = cblk.numpassesinlayers;
                                if (n == 0)
                                {
                                    if (value != 0)
                                        n += 3 * (uint)value - 2;
                                }
                                else
                                    n += 3 * (uint)value;

                                layer.numpasses = n - cblk.numpassesinlayers;

                                if (layer.numpasses == 0)
                                    continue;

                                if (cblk.numpassesinlayers == 0)
                                {
                                    if (cblk.passes[n - 1] == null)
                                        cblk.passes[n - 1] = new TcdPass();
                                    layer.len = cblk.passes[n - 1].rate;
                                    layer.data = cblk.data;
                                }
                                else
                                {
                                    if (cblk.passes[n - 1] == null)
                                        cblk.passes[n - 1] = new TcdPass();
                                    if (cblk.passes[cblk.numpassesinlayers - 1] == null)
                                        cblk.passes[cblk.numpassesinlayers - 1] = new TcdPass();
                                    layer.len = cblk.passes[n - 1].rate - cblk.passes[cblk.numpassesinlayers - 1].rate;
                                    layer.data = cblk.data;
                                    layer.data_pos = (int)cblk.passes[cblk.numpassesinlayers - 1].rate;
                                }

                                if (final)
                                    cblk.numpassesinlayers = n;
                            }
                        }
                    }
                }
            }
        }

        /// <param name="layno">Layer number</param>
        /// <param name="thresh">Treshold</param>
        /// <param name="final">If this is the final layer</param>
        /// <returns>True if the layer allocation is unchanged</returns>
        /// <remarks>2.5.1 - opj_tcd_makelayer</remarks>
        bool MakeLayer(uint layno, double thresh, bool final)
        {
            var tcd_tile = _tcd_image.tiles[0];
            bool layer_allocation_is_same = true;

            tcd_tile.distolayer[layno] = 0;

            for (uint compno = 0; compno < tcd_tile.numcomps; compno++)
            {
                TcdTilecomp tilec = tcd_tile.comps[compno];
                for (uint resno = 0; resno < tilec.numresolutions; resno++)
                {
                    TcdResolution res = tilec.resolutions[resno];
                    for (uint bandno = 0; bandno < res.numbands; bandno++)
                    {
                        TcdBand band = res.bands[bandno];
                        if (band.IsEmpty) continue;

                        for (uint precno = 0; precno < res.pw * res.ph; precno++)
                        {
                            TcdPrecinct prc = band.precincts[precno];
                            for (uint cblkno = 0; cblkno < prc.cw * prc.ch; cblkno++)
                            {
                                //C# impl. We grab the cblk and create the layer object, since we
                                //use a class instead of a struct.
                                TcdCblkEnc cblk = prc.enc[cblkno];
                                TcdLayer layer = cblk.layers[layno];
                                if (layer == null)
                                {// Not sure if this is needed, as layer objects are created.
                                 // in CodeBlockEncAllocate
                                    Debug.Assert(false, "Null layer. No harm, just remove this assert.");
                                    layer = new TcdLayer();
                                    cblk.layers[layno] = layer;
                                }

                                uint n;
                                if (layno == 0)
                                    cblk.numpassesinlayers = 0;
                                n = cblk.numpassesinlayers;


                                if (thresh < 0)
                                {
                                    // Special value to indicate to use all passes
                                    n = cblk.totalpasses;
                                }
                                else
                                {
                                    for (uint passno = cblk.numpassesinlayers; passno < cblk.totalpasses; passno++)
                                    {
                                        uint dr;
                                        double dd;
                                        TcdPass pass = cblk.passes[passno];

                                        if (n == 0)
                                        {
                                            dr = pass.rate;
                                            dd = pass.distortiondec;
                                        }
                                        else
                                        {
                                            dr = pass.rate - cblk.passes[n - 1].rate;
                                            dd = pass.distortiondec - cblk.passes[n - 1].distortiondec;
                                        }

                                        if (dr == 0)
                                        {
                                            if (dd != 0)
                                                n = passno + 1;
                                            continue;
                                        }
                                        if (thresh - (dd / dr) < MyMath.DBL_EPSILON)
                                            n = passno + 1;
                                    }
                                }

                                if (layer.numpasses != n - cblk.numpassesinlayers)
                                {
                                    layer_allocation_is_same = false;
                                    layer.numpasses = n - cblk.numpassesinlayers;
                                }

                                if (layer.numpasses == 0)
                                {
                                    layer.disto = 0;
                                    continue;
                                }
                                if (cblk.numpassesinlayers == 0)
                                {
                                    layer.len = cblk.passes[n - 1].rate;
                                    layer.data = cblk.data;
                                    layer.data_pos = 0;
                                    layer.disto = cblk.passes[n - 1].distortiondec;
                                }
                                else
                                {
                                    layer.len = cblk.passes[n - 1].rate - cblk.passes[cblk.numpassesinlayers - 1].rate;
                                    layer.data = cblk.data;
                                    layer.data_pos = (int)cblk.passes[cblk.numpassesinlayers - 1].rate;
                                    layer.disto = cblk.passes[n - 1].distortiondec - cblk.passes[cblk.numpassesinlayers - 1].distortiondec;
                                }

                                tcd_tile.distolayer[layno] += layer.disto;

                                if (final)
                                    cblk.numpassesinlayers = n;
                            }
                        }
                    }
                }
            }

            return layer_allocation_is_same;
        }

        /// <summary>
        /// Initialize the tile coder and may reuse some meory
        /// </summary>
        /// <remarks>2.5 - opj_tcd_init_encode_tile</remarks>
        internal bool InitEncodeTile(uint tile_no)
        {
            return InitTile(tile_no, true);
        }

        //2.5
        internal bool InitDecodeTile(uint tile_no)
        {
            return InitTile(tile_no, false);
        }

        /// <param name="tile_no">Tile number</param>
        /// <remarks>
        /// 2.5 - opj_tcd_init_tile
        /// 
        /// C# note
        /// Org. impl used a macro to construct two similar functions, one
        /// for encode and one for decode. Later this was changed to a parameter.
        /// 
        /// For 2.5 we stick to the org. impl, using a parameter.
        /// </remarks>
        private bool InitTile(uint p_tile_no, bool is_encoder)
        {
            DWT.GetGainFunc gain_ptr;
            uint pdx, pdy;
            uint gain;
            int x0b, y0b;
            // extent of precincts , top left, bottom right
            int tl_prc_x_start, tl_prc_y_start, br_prc_x_end, br_prc_y_end;
            // number of precinct for a resolution 
            uint n_precincts;
            // room needed to store l_nb_precinct precinct for a resolution 
            uint n_precinct_size;
            // number of code blocks for a precinct
            uint n_code_blocks;
            // room needed to store l_nb_code_blocks code blocks for a precinct
            uint n_code_blocks_size;
            // size of data for a tile 
            int data_size;

            CodingParameters cp = _cp;
            TileCodingParams tcp = cp.tcps[p_tile_no];
            TcdTile[] tiles = _tcd_image.tiles;
            TcdTile tile = tiles[0];
            TileCompParams[] tccps = tcp.tccps;
            TileCompParams tccp = tccps[0];
            TcdTilecomp[] tilecs = tile.comps;
            JPXImage image = _image;
            ImageComp[] image_comps = _image.comps;

            uint p = p_tile_no % cp.tw;       // tile coordinates 
            uint q = p_tile_no / cp.tw;

            // 4 borders of the tile rescale on the image if necessary
            {
                var tx0 = cp.tx0 + p * cp.tdx; // can't be greater than image->x1 so won't overflow
                tile.x0 = (int)Math.Max(tx0, image.x0);
                tile.x1 = (int)Math.Min(MyMath.uint_adds(tx0, cp.tdx), image.x1);
            }

            // All those OPJ_UINT32 are casted to OPJ_INT32, let's do a sanity check
            if (tile.x0 < 0 || tile.x1 <= tile.x0)
            {
                _cinfo.Error("Tile X coordinates are not supported");
                return false;
            }

            {
                var ty0 = cp.ty0 + q * cp.tdy; //can't be greater than image->y1 so won't overflow
                tile.y0 = (int)Math.Max(ty0, image.y0);
                tile.y1 = (int)Math.Min(MyMath.uint_adds(ty0, cp.tdy), image.y1);
            }

            // All those OPJ_UINT32 are casted to OPJ_INT32, let's do a sanity check
            if (tile.y0 < 0 || tile.y1 <= tile.y0)
            {
                _cinfo.Error("Tile Y coordinates are not supported");
                return false;
            }

            // testcase 1888.pdf.asan.35.988 
            if (tccp.numresolutions == 0)
            {
                _cinfo.Error("tiles require at least one resolution\n");
                return false;
            }

            /*tile.numcomps = image.numcomps; */
            for (uint compno = 0; compno < tile.comps.Length; compno++)
            {
                //C# impl, we don't have pointers, so these three are
                //         fetched every cycle.
                tccp = tccps[compno];
                TcdTilecomp tilec = tilecs[compno];
                ImageComp image_comp = image_comps[compno];

                image_comp.resno_decoded = 0;
                // Border of each tile component (global) 
                tilec.x0 = MyMath.int_ceildiv(tile.x0, (int)image_comp.dx);
                tilec.y0 = MyMath.int_ceildiv(tile.y0, (int)image_comp.dy);
                tilec.x1 = MyMath.int_ceildiv(tile.x1, (int)image_comp.dx);
                tilec.y1 = MyMath.int_ceildiv(tile.y1, (int)image_comp.dy);
                tilec.compno = compno;

                tilec.numresolutions = tccp.numresolutions;
                if (tccp.numresolutions < (is_encoder ? cp.specific_param.enc.max_comp_size : cp.specific_param.dec.reduce))
                {
                    tilec.minimum_num_resolutions = 1;
                }
                else
                {
                    tilec.minimum_num_resolutions = tccp.numresolutions
                        - cp.specific_param.dec.reduce;
                }

                if (is_encoder)
                {
                    long tile_data_size;

                    // Compute data_size with overflow check
                    long w = tilec.x1 - tilec.x0;
                    long h = tilec.y1 - tile.y0;

                    // issue 733, l_data_size == 0U, probably something wrong should be checked before getting here
                    if (h > 0 && w > Constants.SIZE_MAX / h)
                    {
                        _cinfo.Error("Size of tile data exceeds system limits");
                        return false;
                    }
                    tile_data_size = w * h;

                    if (Constants.SIZE_MAX / 4 < tile_data_size)
                    {
                        _cinfo.Error("Size of tile data exceeds system limits");
                        return false;
                    }
                    //C# Snip muling this value with sizeof(int)

                    tilec.data_size_needed = tile_data_size;
                }

                //C# tilec.resolutions is an array of pointers instead of a
                //   struct[], so datasize is simply the number of resolutions
                data_size = (int) tilec.numresolutions;

                tilec.data_win = null;
                tilec.win_x0 = 0;
                tilec.win_y0 = 0;
                tilec.win_x1 = 0;
                tilec.win_y1 = 0;

                if (tilec.resolutions == null)
                {
                    tilec.resolutions = new TcdResolution[data_size];
                    //tilec.resolutions_size = data_size;

                    for (int c = 0; c < tilec.resolutions.Length; c++)
                        tilec.resolutions[c] = new TcdResolution();
                }
                else if (data_size > tilec.resolutions.Length)
                {
                    var old = tilec.resolutions.Length;
                    Array.Resize<TcdResolution>(ref tilec.resolutions, data_size);
                    for (int c = old; c < tilec.resolutions.Length; c++)
                        tilec.resolutions[c] = new TcdResolution();
                }

                uint level_no = tilec.numresolutions;
                //TcdResolution[] reso = tilec.resolutions;
                StepSize[] step_sizes = tccp.stepsizes;

                //C# C moves the step_size pointer, so we use this instead.
                int step_size_ptr = 0;

                for (int resno = 0; resno < tilec.numresolutions; resno++)
                {
                    int tlcbgxstart, tlcbgystart /*, brcbgxend, brcbgyend*/;
                    uint cbgwidthexpn, cbgheightexpn;
                    uint cblkwidthexpn, cblkheightexpn;

                    //C# impl, advances the res pointer.
                    TcdResolution res = tilec.resolutions[resno];

                    --level_no;

                    // Border for each resolution level (global)
                    res.x0 = MyMath.int_ceildivpow2(tilec.x0, (int)level_no);
                    res.y0 = MyMath.int_ceildivpow2(tilec.y0, (int)level_no);
                    res.x1 = MyMath.int_ceildivpow2(tilec.x1, (int)level_no);
                    res.y1 = MyMath.int_ceildivpow2(tilec.y1, (int)level_no);

                    // p. 35, table A-23, ISO/IEC FDIS154444-1 : 2000 (18 august 2000) 
                    pdx = tccp.prcw[resno];
                    pdy = tccp.prch[resno];

                    // p. 64, B.6, ISO/IEC FDIS15444-1 : 2000 (18 august 2000)  
                    tl_prc_x_start = MyMath.int_floordivpow2(res.x0, (int)pdx) << (int)pdx;
                    tl_prc_y_start = MyMath.int_floordivpow2(res.y0, (int)pdy) << (int)pdy;
                    {
                        uint tmp = (uint) MyMath.int_ceildivpow2(res.x1, (int)pdx) << (int)pdx;
                        if (tmp > int.MaxValue)
                        {
                            _cinfo.Error("Integer overflow");
                            return false;
                        }
                        br_prc_x_end = (int)tmp;
                    }
                    {
                        uint tmp = (uint) MyMath.int_ceildivpow2(res.y1, (int)pdy) << (int)pdy;
                        if (tmp > int.MaxValue)
                        {
                            _cinfo.Error("Integer overflow");
                            return false;
                        }
                        br_prc_y_end = (int)tmp;
                    }

                    res.pw = (res.x0 == res.x1) ? 0u : 
                        (uint)((br_prc_x_end - tl_prc_x_start) >> (int)pdx);
                    res.ph = (res.y0 == res.y1) ? 0u : 
                        (uint)((br_prc_y_end - tl_prc_y_start) >> (int)pdy);

                    if (res.pw != 0 && (int.MaxValue / res.pw) < res.ph)
                    {//C# impl. We use int.MaxValue instead of uint.MaxValue
                        _cinfo.Error("Size of tile data exceeds system limits");
                        return false;
                    }

                    n_precincts = res.pw * res.ph;
                    //C# impl note.
                    //Snip code that tests if the n_precincts * sizeof(opj_tcd_precinct_t)
                    //is bigger than uint.MaxValue (they get this value by casting -1 to uint)
                    //Anyway, in this implementation TcdPrecinct is not a struct. Meaning the
                    //check dosn't quite make sense. But there is probably a practial limit to
                    //how big we should accept n_precincts to be.
                    n_precinct_size = n_precincts;

                    if (resno == 0)
                    {
                        tlcbgxstart = tl_prc_x_start;
                        tlcbgystart = tl_prc_y_start;
                        /*brcbgxend = l_br_prc_x_end;*/
                        /* brcbgyend = l_br_prc_y_end;*/
                        cbgwidthexpn = pdx;
                        cbgheightexpn = pdy;
                        res.numbands = 1;
                    }
                    else
                    {
                        tlcbgxstart = MyMath.int_ceildivpow2(tl_prc_x_start, 1);
                        tlcbgystart = MyMath.int_ceildivpow2(tl_prc_y_start, 1);
                        /*brcbgxend = opj_int_ceildivpow2(l_br_prc_x_end, 1);*/
                        /*brcbgyend = opj_int_ceildivpow2(l_br_prc_y_end, 1);*/
                        cbgwidthexpn = pdx - 1;
                        cbgheightexpn = pdy - 1;
                        res.numbands = 3;
                    }

                    cblkwidthexpn = Math.Min(tccp.cblkw, cbgwidthexpn);
                    cblkheightexpn = Math.Min(tccp.cblkh, cbgheightexpn);
                    //C# impl: We set band inside the for loop.

                    for (uint bandno = 0; bandno < res.numbands; bandno++, step_size_ptr++)
                    {
                        TcdBand band = res.bands[bandno];

                        if (resno == 0)
                        {
                            band.bandno = 0;
                            band.x0 = MyMath.int_ceildivpow2(tilec.x0, (int)level_no);
                            band.y0 = MyMath.int_ceildivpow2(tilec.y0, (int)level_no);
                            band.x1 = MyMath.int_ceildivpow2(tilec.x1, (int)level_no);
                            band.y1 = MyMath.int_ceildivpow2(tilec.y1, (int)level_no);
                        }
                        else
                        {
                            band.bandno = bandno + 1;
                            // x0b = 1 if bandno = 1 or 3 
                            x0b = (int) (band.bandno & 1u);
                            // y0b = 1 if bandno = 2 or 3 
                            y0b = (int) (band.bandno >> 1);
                            // l_band border (global) 
                            band.x0 = MyMath.int64_ceildivpow2(tilec.x0 - (1L << (int)level_no) * x0b, (int)(level_no + 1));
                            band.y0 = MyMath.int64_ceildivpow2(tilec.y0 - (1L << (int)level_no) * y0b, (int)(level_no + 1));
                            band.x1 = MyMath.int64_ceildivpow2(tilec.x1 - (1L << (int)level_no) * x0b, (int)(level_no + 1));
                            band.y1 = MyMath.int64_ceildivpow2(tilec.y1 - (1L << (int)level_no) * y0b, (int)(level_no + 1));
                        }

                        if (is_encoder)
                        {
                            // Skip empty bands
                            if (band.IsEmpty)
                            {
                                // Do not zero l_band->precints to avoid leaks
                                // but make sure we don't use it later, since
                                // it will point to precincts of previous bands...
                                continue;
                            }
                        }

                        //C# *_step_size is kept in synch with *l_band, see ++l_band, ++l_step_size in 2.5 source.
                        StepSize step_size = step_sizes[step_size_ptr];
                        {
                            // Table E-1 - Sub-band gains
                            // BUG_WEIRD_TWO_INVK (look for this identifier in dwt.c):
                            // the test (!isEncoder && l_tccp->qmfbid == 0) is strongly
                            // linked to the use of two_invK instead of invK
                            int log2_gain = !is_encoder && tccp.qmfbid == 0 ? 0 : 
                                            (band.bandno == 0) ? 0 :
                                            (band.bandno == 3) ? 2 : 1;

                            // Nominal dynamic range. Equation E-4
                            int Rb = (int)image_comp.prec + log2_gain;

                            // Delta_b value of Equation E-3 in "E.1 Inverse quantization
                            // procedure" of the standard
                            band.stepsize = (1f + step_size.mant / 2048f) * (float)Math.Pow(2f, (int)(Rb - step_size.expn));
                        }

                        // Mb value of Equation E-2 in "E.1 Inverse quantization
                        // procedure" of the standard
                        band.numbps = step_size.expn + (int)tccp.numgbits - 1;

                        if (band.precincts == null)
                        {
                            if (n_precincts > 0)
                            {
                                band.precincts = new TcdPrecinct[n_precinct_size];

                                //Nulling
                                for (int c = 0; c < band.precincts.Length; c++)
                                    band.precincts[c] = new TcdPrecinct();

                                //same as l_band.precincts.Length
                                //l_band.precincts_data_size = l_nb_precinct_size;
                            }
                        }
                        else if (band.precincts.Length < n_precinct_size)
                        {
                            int old_size = band.precincts.Length;
                            Array.Resize<TcdPrecinct>(ref band.precincts, (int)n_precinct_size);
                            for (int c = old_size; c < band.precincts.Length; c++)
                                band.precincts[c] = new TcdPrecinct();
                        }

                        //C# impl: current_precinct is set within the loop
                        for (int precno = 0; precno < n_precincts; precno++)
                        {
                            int tlcblkxstart, tlcblkystart, brcblkxend, brcblkyend;
                            int cbgxstart = tlcbgxstart + (int)(precno % res.pw) * (1 << (int)cbgwidthexpn);
                            int cbgystart = tlcbgystart + (int)(precno / res.pw) * (1 << (int)cbgheightexpn);
                            int cbgxend = cbgxstart + (1 << (int)cbgwidthexpn);
                            int cbgyend = cbgystart + (1 << (int)cbgheightexpn);

                            // C# since we don't have pointers, we set instead of increment.
                            TcdPrecinct current_precinct = band.precincts[precno];

                            current_precinct.x0 = Math.Max(cbgxstart, band.x0);
                            current_precinct.y0 = Math.Max(cbgystart, band.y0);
                            current_precinct.x1 = Math.Min(cbgxend, band.x1);
                            current_precinct.y1 = Math.Min(cbgyend, band.y1);

                            tlcblkxstart = MyMath.int_floordivpow2(current_precinct.x0, (int)cblkwidthexpn) << (int)cblkwidthexpn;
                            tlcblkystart = MyMath.int_floordivpow2(current_precinct.y0, (int)cblkheightexpn) << (int)cblkheightexpn;
                            brcblkxend = MyMath.int_ceildivpow2(current_precinct.x1, (int)cblkwidthexpn) << (int)cblkwidthexpn;
                            brcblkyend = MyMath.int_ceildivpow2(current_precinct.y1, (int)cblkheightexpn) << (int)cblkheightexpn;
                            current_precinct.cw = (uint)((brcblkxend - tlcblkxstart) >> (int)cblkwidthexpn);
                            current_precinct.ch = (uint)((brcblkyend - tlcblkystart) >> (int)cblkheightexpn);

                            n_code_blocks = current_precinct.cw * current_precinct.ch;
                            //C# impl note.
                            //Snip code that tests if the n_code_blocks * sizeof(sizeof_block)
                            //is bigger than uint.MaxValue. Again we're not using a struct[]
                            n_code_blocks_size = n_code_blocks;

                            if (is_encoder)
                            {
                                //C# Impl note:
                                //   enc and dec is sepperate in C#, while unified on the C impl. 

                                if (current_precinct.enc == null)
                                {
                                    current_precinct.enc = new TcdCblkEnc[n_code_blocks_size];

                                    //Nulling
                                    for (int c = 0; c < current_precinct.enc.Length; c++)
                                        current_precinct.enc[c] = new TcdCblkEnc();

                                    //Same as l_current_precinct.enc.Length
                                    //l_current_precinct.block_size = l_nb_code_blocks_size;
                                }
                                else if (n_code_blocks_size > current_precinct.enc.Length)
                                {
                                    int old_size = current_precinct.enc.Length;
                                    Array.Resize(ref current_precinct.enc, (int)n_code_blocks_size);
                                    for (int c = old_size; c < current_precinct.enc.Length; c++)
                                        current_precinct.enc[c] = new TcdCblkEnc();
                                }
                            }
                            else
                            {
                                if (current_precinct.dec == null)
                                {
                                    current_precinct.dec = new TcdCblkDec[n_code_blocks_size];

                                    //Nulling
                                    for (int c = 0; c < current_precinct.dec.Length; c++)
                                        current_precinct.dec[c] = new TcdCblkDec();

                                    //Same as l_current_precinct.enc.Length
                                    //l_current_precinct.block_size = l_nb_code_blocks_size;
                                }
                                else if (n_code_blocks_size > current_precinct.dec.Length)
                                {
                                    int old_size = current_precinct.dec.Length;
                                    Array.Resize<TcdCblkDec>(ref current_precinct.dec, (int)n_code_blocks_size);
                                    for (int c = old_size; c < current_precinct.dec.Length; c++)
                                        current_precinct.dec[c] = new TcdCblkDec();
                                }
                            }

                            if (current_precinct.incltree == null)
                            {
                                current_precinct.incltree = TagTree.Create(current_precinct.cw, current_precinct.ch, _cinfo);
                            }
                            else
                            {
                                current_precinct.incltree.Init(current_precinct.cw, current_precinct.ch);
                            }

                            if (current_precinct.imsbtree == null)
                            {
                                current_precinct.imsbtree = TagTree.Create(current_precinct.cw, current_precinct.ch, _cinfo);
                            }
                            else
                            {
                                current_precinct.imsbtree.Init(current_precinct.cw, current_precinct.ch);
                            }

                            for (int cblkno = 0; cblkno < n_code_blocks; cblkno++)
                            {
                                int cblkxstart = tlcblkxstart + (int)(cblkno % current_precinct.cw) * (1 << (int)cblkwidthexpn);
                                int cblkystart = tlcblkystart + (int)(cblkno / current_precinct.cw) * (1 << (int)cblkheightexpn);
                                int cblkxend = cblkxstart + (1 << (int)cblkwidthexpn);
                                int cblkyend = cblkystart + (1 << (int)cblkheightexpn);

                                if (is_encoder)
                                {
                                    var code_block = current_precinct.enc[cblkno];

                                    CodeBlockEncAllocate(code_block);

                                    code_block.x0 = Math.Max(cblkxstart, current_precinct.x0);
                                    code_block.y0 = Math.Max(cblkystart, current_precinct.y0);
                                    code_block.x1 = Math.Min(cblkxend, current_precinct.x1);
                                    code_block.y1 = Math.Min(cblkyend, current_precinct.y1);

                                    CodeBlockEncAllocateData(code_block);
                                }
                                else
                                {
                                    var code_block = current_precinct.dec[cblkno];

                                    CodeBlockDecAllocate(code_block);

                                    // code-block size (global) 
                                    code_block.x0 = Math.Max(cblkxstart, current_precinct.x0);
                                    code_block.y0 = Math.Max(cblkystart, current_precinct.y0);
                                    code_block.x1 = Math.Min(cblkxend, current_precinct.x1);
                                    code_block.y1 = Math.Min(cblkyend, current_precinct.y1);
                                }
                            }
                            //C# impl, current_precinct set in the for loop
                        } /* precno */
                    } /* bandno */
                    //C# impl, res set in the for loop
                } /* resno */
                // C# impl, tccp, tilec and image_comp set in the for loop
            } /* compno */
            return true;
        }

        /// <summary>
        /// Allocates memory for an encoding block
        /// </summary>
        /// <param name="code_block">Coding block to allocate memory to</param>
        /// <remarks>2.5 - opj_tcd_code_block_enc_allocate</remarks>
        void CodeBlockEncAllocate (TcdCblkEnc code_block)
        {
            if (code_block.layers == null)
            {
                code_block.layers = new TcdLayer[100];

                for (int c = 0; c < code_block.layers.Length; c++)
                    code_block.layers[c] = new TcdLayer();
            }

            if (code_block.passes == null)
            {
                code_block.passes = new TcdPass[100];

                for (int c = 0; c < code_block.passes.Length; c++)
                    code_block.passes[c] = new TcdPass();
            }
        }

        /// <summary>
        /// Allocates data memory for an encoding code block.
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_tcd_code_block_enc_allocate_data
        /// </remarks>
        private void CodeBlockEncAllocateData(TcdCblkEnc code_block)
        {
            uint l_data_size;

            // +1 is needed for https://github.com/uclouvain/openjpeg/issues/835
            // and actually +2 required for https://github.com/uclouvain/openjpeg/issues/982
            // and +7 for https://github.com/uclouvain/openjpeg/issues/1283 (-M 3)
            // and +26 for https://github.com/uclouvain/openjpeg/issues/1283 (-M 7)
            // and +28 for https://github.com/uclouvain/openjpeg/issues/1283 (-M 44)
            // and +33 for https://github.com/uclouvain/openjpeg/issues/1283 (-M 4)
            // and +63 for https://github.com/uclouvain/openjpeg/issues/1283 (-M 4 -IMF 2K)
            // and +74 for https://github.com/uclouvain/openjpeg/issues/1283 (-M 4 -n 8 -s 7,7 -I)
            // TODO: is there a theoretical upper-bound for the compressed code
            // block size ?
            l_data_size = 74u + (uint)((code_block.x1 - code_block.x0) *
                                       (code_block.y1 - code_block.y0) * (int)sizeof(uint));

            if (l_data_size > code_block.data_size)
            {
                code_block.data = new byte[l_data_size /*+ 1*/];

                ///C# impl note. opj_mqc_init_enc sets the byte pointer to "-1", which is
                //worked around in the org. impl by ensuring that there's space ahead.
                //
                //This can be mimmicied in the C# impl, but instead we
                //avoid the problem.
                //p_code_block.data[0] = 0;
                //p_code_block->data += 1;
            }
        }

        /// <summary>
        /// Allocates memory for an decoding block
        /// </summary>
        /// <param name="code_block">Coding block to allocate memory to</param>
        /// <remarks>
        /// 2.5 - opj_tcd_code_block_dec_allocate
        /// </remarks>
        void CodeBlockDecAllocate(TcdCblkDec code_block)
        {
            if (code_block.segs == null)
            {
                code_block.segs = new TcdSeg[Constants.J2K_DEFAULT_NB_SEGS];
                //for (int c = 0; c < p_code_block.segs.Length; c++)
                //    p_code_block.segs[c] = new TcdSeg();

                code_block.current_max_segs = Constants.J2K_DEFAULT_NB_SEGS;
            }
            else
            {
                TcdSeg[] segs = code_block.segs;
                uint current_max_segs = code_block.current_max_segs;
                TcdSegDataChunk[] chunks = code_block.chunks;
                uint numchunksalloc = code_block.numchunksalloc;

                code_block.decoded_data = null;

                //C# impl, alternative to memset 0
                code_block.Reset();
                code_block.segs = segs;
                code_block.current_max_segs = current_max_segs;
                for(uint i = 0; i < current_max_segs; i++)
                {
                    //Reinits segment
                    segs[i] = new TcdSeg();
                }
                code_block.chunks = chunks;
                code_block.numchunksalloc = numchunksalloc;
            }
        }

        #endregion

        //2.5
        bool T2Decode(byte[] src, int src_len, CodestreamIndex cstr_index)
        {
            Tier2Coding t2 = new Tier2Coding(_cinfo, _image, _cp);

            return t2.DecodePackets(this, _tcd_tileno, _tcd_image.tiles[0], src, src_len, cstr_index);
        }

        /// <remarks>
        /// //2.5 - opj_tcd_t1_decode
        /// 
        /// Implements multithreading with thread limiter. This function is
        /// basically all new, with the "real" T1Decode beeing made into a
        /// generator. See comment of that function for why.
        /// </remarks>
        bool T1Decode()
        {
            return RunThreads((mt, set_ret) =>
            {
                return T1Decode(mt, set_ret);
            }, (obj) =>
            {
                Tier1Coding.ThreadWorker((Tier1Coding.T1CBLKDecodeProcessingJob)obj);
            });
        }

        /// <summary>
        /// To share code between encoding and decoding, the MT code has been
        /// funneled into this function, with the differences handled by a couple
        /// of delegates
        /// </summary>
        /// <remarks>
        /// Org impl depends on its threadpool impl for limiting the number of threads,
        /// it also takes advantage of threadlocal store.
        /// 
        /// Since this impl. depends on .net's threadpool, we have to manualy limit the
        /// number of executable threads, and local storage isn't reccomended with these
        /// threads, as we don't own them, so we don't use it.
        /// </remarks>
        /// <param name="create_jobs">Used to generate the jobs that will be scheduled</param>
        /// <param name="run_thread">Command to actually run the job</param>
        /// <returns>True of true</returns>
        private bool RunThreads(CreateJobs create_jobs, ThreadWorker run_thread)
        {
            bool ret = true;

            //How many threads are currently working
            int running_jobs = 0;

            int max_threads;
            ThreadPool.GetAvailableThreads(out max_threads, out _);
            max_threads = _cinfo.DisableMultiThreading ? 1 : Math.Min(Environment.ProcessorCount, max_threads);

            //Generator for work
            var work = create_jobs(max_threads > 1, (val) =>
            {
                //Since we only allow setting this value false,
                //we don't need to worry about syncing the threads.
                if (val != null && val.Value == false)
                    ret = val.Value;
                return ret;
            });

            var reset = new ManualResetEvent(true);
            var done = new ManualResetEvent(true); //<-- true in case there's no jobs
            try
            {
                foreach (var job in work)
                {
                    //Before we enqueue the job, we must wait for a thread to become
                    //avalible.
                    int jobs = Interlocked.Increment(ref running_jobs);
                    if (jobs == max_threads)
                    {
                        //When max_threads == 1, we'll always enter this if,
                        //including on the first itteration.

                        reset.WaitOne();
                        reset.Reset();
                    }

                    //max_threads may be == 1, which is why this isn't an "else if"
                    if (jobs == 1)
                    {
                        //Can only be 1 after incrementing from 0, as this thread is
                        //the only thread incrementing running_jobs.
                        //
                        //This means we know that "done" is set, and thus needs
                        //to be reset.
                        //
                        //We lock before we reset so that we're sure that the thread
                        //which decremented to zero is actually done, as it may be
                        //sleeping right before "done.Set()"
                        lock (reset) { done.Reset(); }
                    }

                    ThreadPool.QueueUserWorkItem((x) =>
                    {
                        try { run_thread(x); }
                        finally
                        {
                            // Locking on reset is safe as it's a method local object.
                            //
                            //The reason we grab a lock is because we this thread
                            //may fall asleep right before done.Set().
                            lock (reset)
                            {
                                //Only worker threads decrement running jobs. 
                                int rj = Interlocked.Decrement(ref running_jobs);

                                //In theory we don't always have to set the reset flag,
                                //we only need to do so when (running_jobs == max_threads)
                                //as that means the main thread may be asleep.
                                //
                                //However, setting reset doesn't hurt, as we know a slot
                                //for new work has become avalible. (This thread is finished
                                //after all), and I'm not 100% sure I'm right in my thinking.
                                //
                                //if (running_jobs == max_threads)
                                {

                                    //Signal the main thread that it can schedule more
                                    //work.
                                    reset.Set();
                                    //The lock( ) isn't needed for reset.Set(), it can be
                                    //placed after the lock. But again, I'm not 100%
                                    //sure.
                                }

                                //rj being zero means this thread decremented running_jobs to
                                //zero. Since done will only be reset when running_jobs is
                                //zero, we know that at worst the main thread is waiting to
                                //call done.reset() right after this thread sets it. 
                                //
                                //
                                //running_jobs being zero means the main thread may be
                                //waiting on the done signal*.
                                //
                                //* It can also be sleeping before the increement, in which
                                //  case it will reset the done signal after waking up.
                                //* If it's sleeping after the increement, running_jobs can't
                                //  be zero.
                                if (rj == 0 && running_jobs == 0)
                                {
                                    done.Set();
                                }
                            }
                        }
                    }, job);

                    if (jobs == max_threads)
                    {
                        //We don't want jobs to become bigger than max_threads, as my logic
                        //don't like that.
                        reset.WaitOne();
                    }

                    //For when we break out of the loop early.
                    if (!ret)
                        break;
                }

                //We have to wait until all threads terminate.
                done.WaitOne();
            }
            finally
            {
                done.Dispose();
                reset.Dispose();
            }

            return ret;
        }

        /// <summary>
        /// Creates decoding jobs
        /// </summary>
        /// <param name="must_copy">If multithreading, this must be set true</param>
        /// <remarks>
        /// 2.5 - opj_tcd_t1_decode
        /// 
        /// To simplify the MT impl, this function and the function it calls have been changed to iterators,
        /// which lets me put all the MT stuff in a single parent function.
        /// 
        /// The basic problem is that the org impl assumes you can control the amount of threads in the
        /// threadpool. That's of course possible, but have side effects on C#, unless, of course, you
        /// make your own threadpool implementation.
        /// 
        /// So since I have to make changes anyway, I've decided to make it easier for myself.
        /// </remarks>
        private IEnumerable<Tier1Coding.T1CBLKDecodeProcessingJob> T1Decode(bool must_copy, Tier1Coding.SetRet set_ret)
        {
            TcdTile tile = _tcd_image.tiles[0];
            var tccps = _tcp.tccps;
            bool check_pterm = false;


            if (_tcp.num_layers_to_decode == _tcp.numlayers &&
                (tccps[0].cblksty & CCP_CBLKSTY.PTERM) != 0)
            {
                check_pterm = true;
            }

            for (int compno = 0; compno < tile.numcomps; ++compno)
            {
                if (_used_component != null && !_used_component[compno])
                    continue;

                var tilec = tile.comps[compno];
                foreach (var job in Tier1Coding.DecodeCblks(this, tilec, tccps[compno], check_pterm,
                    _cinfo, set_ret, must_copy))
                {
                    yield return job;

                    if (!set_ret(null))
                        break;
                }

                if (!set_ret(null))
                    yield break;
            }
        }

        //2.5 - opj_tcd_dwt_decode
        bool DWTDecode()
        {
            TcdTile tile = _tcd_image.tiles[0];
            var tccps = _tcp.tccps;
            var comps = _image.comps;

            for (int compno = 0; compno < tile.numcomps; compno++)
            {
                if (_used_component != null && !_used_component[compno])
                {
                    continue;
                }

                TcdTilecomp tilec = tile.comps[compno];

                if (tccps[compno].qmfbid == 1)
                {
                    if (!DWT.Decode(this, tilec, comps[compno].resno_decoded + 1))
                        return false;
                }
                else
                {
                    if (!DWT.DecodeReal(this, tilec, comps[compno].resno_decoded + 1))
                        return false;
                }
            }

            return true;
        }

        //2.5 - opj_tcd_mct_decode
        bool MCTDecode()
        {
            TcdTile tile = _tcd_image.tiles[0];
            TcdTilecomp[] tile_comps = tile.comps;
            TcdTilecomp tile_comp = tile_comps[0];
            int samples;

            if (_tcp.mct == 0 || _used_component != null)
                return true;

            if (WholeTileDecoding)
            {
                TcdResolution res_comp0 = tile.comps[0].resolutions[tile_comp.minimum_num_resolutions - 1];

                // A bit inefficient: we process more data than needed if
                // resno_decoded < l_tile_comp->minimum_num_resolutions-1,
                // but we would need to take into account a stride then
                samples = (int)((res_comp0.x1 - res_comp0.x0) * (res_comp0.y1 - res_comp0.y0));
                if (tile.numcomps >= 3)
                {
                    if (tile_comp.minimum_num_resolutions != tile.comps[1].minimum_num_resolutions ||
                        tile_comp.minimum_num_resolutions != tile.comps[2].minimum_num_resolutions)
                    {
                        _cinfo.Error("Tiles don't all have the same dimension. Skip the MCT step.");
                        return false;
                    }

                    TcdResolution res_comp1 = tile.comps[1].resolutions[tile_comp.minimum_num_resolutions - 1];
                    TcdResolution res_comp2 = tile.comps[2].resolutions[tile_comp.minimum_num_resolutions - 1];
                    // testcase 1336.pdf.asan.47.376
                    if (_image.comps[0].resno_decoded != _image.comps[1].resno_decoded ||
                        _image.comps[0].resno_decoded != _image.comps[2].resno_decoded ||
                        (res_comp1.x1 - res_comp1.x0) * (res_comp1.y1 - res_comp1.y0) != samples ||
                        (res_comp2.x1 - res_comp2.x0) * (res_comp2.y1 - res_comp2.y0) != samples)
                    {
                        _cinfo.Error("Tiles don't all have the same dimension. Skip the MCT step.");
                        return false;
                    }
                }
            }
            else
            {
                TcdResolution res_comp0 = tile.comps[0].resolutions[tile_comp.minimum_num_resolutions - 1];
                samples = (int)((res_comp0.win_x1 - res_comp0.win_x0) * (res_comp0.win_y1 - res_comp0.win_y0));

                if (tile.numcomps >= 3)
                {
                    TcdResolution res_comp1 = tile.comps[1].resolutions[tile_comp.minimum_num_resolutions - 1];
                    TcdResolution res_comp2 = tile.comps[2].resolutions[tile_comp.minimum_num_resolutions - 1];
                    // testcase 1336.pdf.asan.47.376
                    if (_image.comps[0].resno_decoded != _image.comps[1].resno_decoded ||
                        _image.comps[0].resno_decoded != _image.comps[2].resno_decoded ||
                        (res_comp1.win_x1 - res_comp1.win_x0) * (res_comp1.win_y1 - res_comp1.win_y0) != samples ||
                        (res_comp2.win_x1 - res_comp2.win_x0) * (res_comp2.win_y1 - res_comp2.win_y0) != samples)
                    {
                        _cinfo.Error("Tiles don't all have the same dimension. Skip the MCT step.");
                        return false;
                    }
                }
            }

            if (tile.numcomps >= 3)
            {
                if (_tcp.mct == 2)
                {
                    if (_tcp.mct_decoding_matrix == null)
                        return true;

                    var data = new int[tile.numcomps][];
                    for (int i = 0; i < data.Length; i++)
                        data[i] = tile_comps[i].data;

                    MCT.DecodeCustom(_tcp.mct_decoding_matrix, samples, data, tile.numcomps, _image.comps[0].sgnd);
                }
                else
                {
                    if (_tcp.tccps[0].qmfbid == 1)
                    {
                        if (WholeTileDecoding)
                        {
                            MCT.Decode(tile.comps[0].data,
                                       tile.comps[1].data,
                                       tile.comps[2].data,
                                       samples);
                        }
                        else
                        {
                            MCT.Decode(tile.comps[0].data_win,
                                       tile.comps[1].data_win,
                                       tile.comps[2].data_win,
                                       samples);
                        }
                    }
                    else
                    {
                        if (WholeTileDecoding)
                        {
                            MCT.DecodeReal(tile.comps[0].data,
                                           tile.comps[1].data,
                                           tile.comps[2].data,
                                           samples);
                        }
                        else
                        {
                            MCT.DecodeReal(tile.comps[0].data_win,
                                           tile.comps[1].data_win,
                                           tile.comps[2].data_win,
                                           samples);
                        }
                    }
                }
            }
            else
                _cinfo.Warn("Number of components ({0}) is inconsistent with a MCT. Skip the MCT step.", tile.numcomps);

            return true;
        }

        //2.5.1 - opj_tcd_dc_level_shift_decode
        void DcLevelShiftDecode()
        {
            TcdTile tile = _tcd_image.tiles[0];
            var tccps = _tcp.tccps;
            int min, max;

            for (int compno = 0; compno < tile.numcomps; ++compno)
            {
                if (_used_component != null && !_used_component[compno])
                    continue;

                TcdTilecomp tile_comp = tile.comps[compno];
                ImageComp imagec = _image.comps[compno];
                TcdResolution res = tile_comp.resolutions[imagec.resno_decoded];
                TileCompParams tccp = tccps[compno];
                int width, height, stride, data_ptr;
                int[] data;

                if (!WholeTileDecoding)
                {
                    width = (int)(res.win_x1 - res.win_x0);
                    height = (int)(res.win_y1 - res.win_y0);
                    stride = 0;
                    data = tile_comp.data_win;
                    data_ptr = 0;
                }
                else
                {
                    width = res.x1 - res.x0;
                    height = res.y1 - res.y0;
                    stride = tile_comp.resolutions[tile_comp.minimum_num_resolutions - 1].x1 -
                             tile_comp.resolutions[tile_comp.minimum_num_resolutions - 1].x0 -
                             width;
                    data = tile_comp.data;
                    data_ptr = 0;
                }

                if (imagec.sgnd)
                {
                    min = -(1 << ((int)imagec.prec - 1));
                    max = (1 << ((int)imagec.prec - 1)) - 1;
                }
                else
                {
                    min = 0;
                    max = (1 << (int)imagec.prec) - 1;
                }

                if (width == 0 || height == 0)
                    continue;

                if (_tcp.tccps[compno].qmfbid == 1)
                {
                    for (int j = 0; j < height; j++)
                    {
                        for (int i = 0; i < width; i++)
                        {
                            data[data_ptr] = MyMath.int_clamp(data[data_ptr] + tccp.dc_level_shift, min, max);
                            data_ptr++;
                        }
                        data_ptr += stride;
                    }
                }
                else
                {
                    IntOrFloat fi = new IntOrFloat();
                    for (int j = 0; j < height; j++)
                    {
                        for (int i = 0; i < width; i++)
                        {
                            fi.I = data[data_ptr];
                            if (fi.F > int.MaxValue)
                            {
                                data[data_ptr] = max;
                            }
                            else if (fi.F < int.MinValue)
                            {
                                data[data_ptr] = min;
                            }
                            else 
                            {
                                // Do addition on int64 to avoid overflows
                                //long value_int = lrintf(fi.F);
                                long value_int = (long)Math.Round(fi.F); //<-- Impl. banker's rounding (round towards even).
                                data[data_ptr] = (int)MyMath.int64_clamp(value_int + tccp.dc_level_shift, min, max);
                            }
                            data_ptr++;
                        }
                        data_ptr += stride;
                    }
                }
            }
        }

        /// <remarks>
        /// There's a SSE2 version of this, and it uses banker's rounding. I'm just using
        /// Math.Round instead.
        /// </remarks>
        //long lrintf(float f) 
        //{ 
        //    //Give different results over what the C impl does, as it uses bankers rounding.
        //    //Fortunatly, Math.Round also uses bankers rounding.
        //    return (long)(f > 0f ? (f + .5f) : (f - .5f)); 
        //}

        /// <remarks>
        /// 2.5 - opj_tcd_decode_tile
        /// 
        /// Note that variables named "tileno" is used inconsistently  
        /// in the lib.
        ///  - As the actual tileno
        ///  - As a lookup into a tilno array
        ///  - As a dual lookup in the tilno array
        /// 
        /// In this case I changed tilno to be a the same
        /// value for both prep and this (actual).
        /// </remarks>
        internal bool DecodeTile(
            uint win_x0,
            uint win_y0,
            uint win_x1,
            uint win_y1,
            int numcomps_to_decode,
            int[] comps_indices,
            byte[] src,
            int src_len,
            uint tileno,
            CodestreamIndex cstr_index)
        {
            _tcd_tileno = tileno;
            _tcp = _cp.tcps[tileno];
            _win_x0 = win_x0;
            _win_y0 = win_y0;
            _win_x1 = win_x1;
            _win_y1 = win_y1;
            WholeTileDecoding = true;

            _used_component = null;

            if (numcomps_to_decode != 0)
            {
                _used_component = new bool[_image.numcomps];
                for(var compno = 0; 0 < numcomps_to_decode; compno++)
                {
                    _used_component[compno] = true;
                }
            }

            for (uint compno = 0; compno < _image.numcomps; compno++)
            {
                if (_used_component != null && _used_component[compno])
                {
                    continue;
                }

                if (!IsWholeTilecompDecoding(compno))
                {
                    WholeTileDecoding = false;
                    break;
                }
            }

            if (WholeTileDecoding)
            {
                for (var compno = 0; compno < _image.numcomps; compno++)
                {
                    if (_used_component != null && !_used_component[compno])
                    {
                        continue;
                    }

                    var tilec = _tcd_image.tiles[0].comps[compno];
                    var l_res = tilec.resolutions[tilec.minimum_num_resolutions - 1];
                    long l_data_size;

                    // compute data_size with overflow check
                    long res_w = (long)(l_res.x1 - l_res.x0);
                    long res_h = (long)(l_res.y1 - l_res.y0);

                    // issue 733, l_data_size == 0U, probably something wrong should be checked before getting here
                    if (res_h > 0 && res_w > Constants.SIZE_MAX / res_h)
                    {
                        _cinfo.Error("Size of tile data exceeds system limits");
                        return false;
                    }
                    l_data_size = res_w * res_h;

                    if (Constants.SIZE_MAX / sizeof(OPJ_UINT32) < l_data_size)
                    {
                        _cinfo.Error("Size of tile data exceeds system limits");
                        return false;
                    }
                    //l_data_size *= sizeof(OPJ_UINT32);

                    tilec.data_size_needed = l_data_size;

                    if (!tilec.AllocTileComponentData())
                    {
                        _cinfo.Error("Size of tile data exceeds system limits");
                        return false;
                    }
                }
            }
            else
            {
                // Compute restricted tile-component and tile-resolution coordinates
                // of the window of interest, but defer the memory allocation until
                // we know the resno_decoded
                for (var compno = 0; compno < _image.numcomps; compno++)
                {
                    if (_used_component != null && !_used_component[compno])
                    {
                        continue;
                    }

                    var tilec = _tcd_image.tiles[0].comps[compno];
                    var image_comp = _image.comps[compno];

                    // Compute the intersection of the area of interest, expressed in tile coordinates
                    // with the tile coordinates
                    tilec.win_x0 = Math.Max(
                                        (OPJ_UINT32)tilec.x0,
                                        MyMath.uint_ceildiv(_win_x0, image_comp.dx));
                    tilec.win_y0 = Math.Max(
                                        (OPJ_UINT32)tilec.y0,
                                        MyMath.uint_ceildiv(_win_y0, image_comp.dy));
                    tilec.win_x1 = Math.Min(
                                        (OPJ_UINT32)tilec.x1,
                                        MyMath.uint_ceildiv(_win_x1, image_comp.dx));
                    tilec.win_y1 = Math.Min(
                                        (OPJ_UINT32)tilec.y1,
                                        MyMath.uint_ceildiv(_win_y1, image_comp.dy));
                    if (tilec.win_x1 < tilec.win_x0 ||
                            tilec.win_y1 < tilec.win_y0)
                    {
                        // We should not normally go there. The circumstance is when
                        // the tile coordinates do not intersect the area of interest
                        // Upper level logic should not even try to decode that tile
                        _cinfo.Error("Invalid tilec.win_xxx values");
                        return false;
                    }

                    for (int resno = 0; resno < tilec.numresolutions; ++resno)
                    {
                        var res = tilec.resolutions[resno];
                        res.win_x0 = MyMath.uint_ceildivpow2(tilec.win_x0,
                                                           (int)tilec.numresolutions - 1 - resno);
                        res.win_y0 = MyMath.uint_ceildivpow2(tilec.win_y0,
                                                           (int)tilec.numresolutions - 1 - resno);
                        res.win_x1 = MyMath.uint_ceildivpow2(tilec.win_x1,
                                                           (int)tilec.numresolutions - 1 - resno);
                        res.win_y1 = MyMath.uint_ceildivpow2(tilec.win_y1,
                                                           (int)tilec.numresolutions - 1 - resno);
                    }
                }
            }

            //// --------------- Tier 2 -------------
            if (!T2Decode(src, src_len, cstr_index))
                return false;

            //DumpCblks("");

            //// --------------- Tier 1 -------------
            if (!T1Decode())
                return false;

            // For subtile decoding, now we know the resno_decoded, we can allocate
            // the tile data buffer
            if (!WholeTileDecoding)
            {
                for (int compno = 0; compno < _image.numcomps; compno++)
                {
                    TcdTilecomp tilec = _tcd_image.tiles[0].comps[compno];
                    ImageComp image_comp = _image.comps[compno];
                    var res = tilec.resolutions;
                    uint res_pt = image_comp.resno_decoded;
                    ulong w = res[res_pt].win_x1 - res[res_pt].win_x0;
                    ulong h = res[res_pt].win_y1 - res[res_pt].win_y0;
                    ulong l_data_size;

                    tilec.data_win = null;

                    if (_used_component != null && !_used_component[compno])
                    {
                        continue;
                    }

                    if (w > 0 && h > 0)
                    {
                        if (w > Constants.SIZE_MAX / h)
                        {
                            _cinfo.Error("Size of tile data exceeds system limits");
                            return false;
                        }
                        l_data_size = w * h;
                        if (l_data_size > Constants.SIZE_MAX / sizeof(int))
                        {
                            _cinfo.Error("Size of tile data exceeds system limits");
                            return false;
                        }
                        //l_data_size *= sizeof(int);

                        tilec.data_win = new int[l_data_size];
                    }
                }
            }

            //DumpTilcomp("Before DWT", false);
            //DumpAreaCblks(""); //<-- Use when !WholeTileDecoding

            //// --------------- DWT ----------------
            if (!DWTDecode())
                return false;

            //if (tileno == 17)
            //DumpTilcomp("Before MCT", !WholeTileDecoding);

            //// --------------- MCT ----------------
            if (!MCTDecode())
                return false;

            //DumpTilcomp("After MCT");

            DcLevelShiftDecode();

            //DumpTilcomp("After DC");

            return true;
        }

#if DEBUG
        void DumpTilcomp(string txt, bool data_win = false)
        {
            TcdTile tile = _tcd_image.tiles[0];
            using (var file = new System.IO.StreamWriter("c:/temp/j2k_dump.txt", append: false))
            {
                for (int comp_nr = 0; comp_nr < tile.numcomps; comp_nr++)
                {
                    file.Write(txt + " int values for component " + (comp_nr + 1) + "\n");
                    var d = data_win ? tile.comps[comp_nr].data_win : tile.comps[comp_nr].data;
                    for (uint c = 0; c < d.Length; c++)
                    {
                        file.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{1}: {0}\n", d[c], c));
                    }
                }
            }
        }

        void DumpCblks(string txt)
        {
            using (var file = new System.IO.StreamWriter("c:/temp/j2k_dump.txt"))
            {
                int nr = 1;
                foreach (var job in T1Decode(false, (f) => true))
                {
                    var cb = job.cblk;
                    file.Write(txt+"Coblk " + nr++ + " (" + cb.numchunks + " chunks):\n");
                    var d = job.cblk.chunk_data;
                    for (int c = 0; c < cb.numchunks; c++)
                    {
                        file.Write(" -- Chunk " + (c + 1) + ":\n");
                        var chunk = cb.chunks[c];
                        for (int i = chunk.data_pt, b = 0, end = i + chunk.len; i < end; i++, b++)
                        {
                            file.Write("{1}: {0}\n", d[i], b);
                        }
                    }
                }
            }
        }

        void DumpCblk(int nr, TcdCblkEnc cb)
        {
            using (var file = new System.IO.StreamWriter("c:/temp/j2k_dump.txt", append: nr != 0))
            {
                file.Write("Enc cblk " + nr + " (" + cb.totalpasses + " passes):\n");
                var d = cb.data;
                var len = cb.totalpasses == 0 ? 0 : cb.passes[cb.totalpasses - 1].rate;

                for (int c = 0, dp = cb.data_pt; c < cb.data_size && c < len; c++)
                {
                    file.Write("{1}: {0}\n", d[dp + c], c);
                }
            }
        }

        void DumpEncCblks(string txt)
        {
            using (var file = new System.IO.StreamWriter("c:/temp/j2k_dump.txt"))
            {
                int nr = 1;
                foreach (var ob in Tier1Coding.EncodeCblks(_tcd_image.tiles[0], _tcp, null, 0, (f) => true))
                {
                    if (nr == 32)
                    {
                        nr = nr;
                    }

                    var job = (Tier1Coding.T1CBLKEncodeProcessingJob)ob;
                    var cb = job.cblk;
                    file.Write(txt + "Enc cblk " + nr++ + " (" + cb.totalpasses + " passes, " + cb.numbps + " bps):\n");
                    var d = cb.data;
                    var len = cb.totalpasses == 0 ? 0 : cb.passes[cb.totalpasses - 1].rate;

                    for (int c = 0, dp = cb.data_pt; c < cb.data_size && c < len; c++)
                    {
                        file.Write("{1}: {0}\n", d[dp + c], c);
                    }
                }
            }
        }

        void DumpAreaCblks(string txt)
        {
            if (WholeTileDecoding)
                throw new NotSupportedException("This function can only be used with area decoding.");
            var tile = _tcd_image.tiles[0];

            using (var file = new System.IO.StreamWriter("c:/temp/cblks.txt"))
            {
                int nr = 1;
                for (int compno = 0; compno < tile.numcomps; compno++)
                {
                    var tilec = tile.comps[compno];
                    for (int resno = 0; resno < tilec.minimum_num_resolutions; ++resno)
                    {
                        var res = tilec.resolutions[resno];
                        for (int bandno = 0; bandno < res.numbands; ++bandno)
                        {
                            var band = res.bands[bandno];
                            for (int precno = 0; precno < res.pw * res.ph; ++precno)
                            {
                                var precinct = band.precincts[precno];
                                for (int cblkno = 0; cblkno < precinct.cw * precinct.ch; ++cblkno)
                                {
                                    var cb = precinct.dec[cblkno];
                                    if (cb.decoded_data != null)
                                    {
                                        var d = cb.decoded_data;
                                        file.Write(txt + "Coblk " + nr++ + ":\n");
                                        for (int c = 0; c < d.Length; c++)
                                        {
                                            file.Write("{1}: {0}\n", d[c], c);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
#endif

        /// <summary>
        /// Get the size in bytes of the input buffer provided before encoded.
        /// This must be the size provided to the p_src_length argument of
        /// opj_tcd_copy_tile_data()
        /// </summary>
        /// <remarks>2.5 - opj_tcd_get_encoder_input_buffer_size</remarks>
        internal uint GetEncoderInputBufferSize()
        {
            uint i, data_size = 0;
            ImageComp[] img_comps;
            TcdTilecomp[] tilecs;
            uint size_comp, remaining;

            tilecs = _tcd_image.tiles[0].comps;
            img_comps = _image.comps;
            for (i = 0; i < _image.numcomps; i++)
            {
                var img_comp = img_comps[i];
                var l_tilec = tilecs[i];
                size_comp = (uint)img_comp.prec >> 3; /*(/ 8)*/
                remaining = (uint)img_comp.prec & 7u;  /* (%8) */

                if (remaining != 0)
                {
                    size_comp++;
                }

                if (size_comp == 3)
                {
                    size_comp = 4;
                }

                data_size += size_comp * (uint)((l_tilec.x1 - l_tilec.x0) * (l_tilec.y1 - l_tilec.y0));
            }

            return data_size;
        }

        //2.5
        internal uint GetDecodedTileSize(bool take_into_account_partial_decoding)
        {
            uint i, data_size = 0;
            ImageComp[] img_comps;
            TcdTilecomp[] tile_comps;
            uint size_comp, remaining;

            tile_comps = _tcd_image.tiles[0].comps;
            img_comps = _image.comps;
            for (i = 0; i < _image.numcomps; i++)
            {
                OPJ_UINT32 w, h;
                var img_comp = img_comps[i];
                var tile_comp = tile_comps[i];
                size_comp = (uint)img_comp.prec >> 3; /*(/ 8)*/
                remaining = (uint)img_comp.prec & 7u;  /* (%8) */

                if (remaining != 0)
                {
                    size_comp++;
                }

                if (size_comp == 3)
                {
                    size_comp = 4;
                }


                var res = tile_comp.resolutions[tile_comp.minimum_num_resolutions - 1];
                if (take_into_account_partial_decoding && !WholeTileDecoding)
                {
                    w = res.win_x1 - res.win_x0;
                    h = res.win_y1 - res.win_y0;
                }
                else
                {
                    w = (OPJ_UINT32)(res.x1 - res.x0);
                    h = (OPJ_UINT32)(res.y1 - res.y0);
                }
                if (h > 0 && uint.MaxValue / w < h)
                {
                    return uint.MaxValue;
                }
                var temp = w * h;
                if (size_comp != 0 && uint.MaxValue / size_comp < temp)
                {
                    return uint.MaxValue;
                }
                temp *= size_comp;

                if (temp > uint.MaxValue - data_size)
                {
                    return uint.MaxValue;
                }
                data_size += temp;
            }

            return data_size;
        }

        //2.5 - opj_get_tile_dimensions
        void GetTileDimensions(JPXImage image,
                               TcdTilecomp tilec,
                               ImageComp img_comp,
                               out uint size_comp,
                               out uint width,
                               out uint height,
                               out uint offset_x,
                               out uint offset_y,
                               out uint image_width,
                               out uint stride,
                               out uint tile_offset)
        {
            uint remaining;
            size_comp = img_comp.prec >> 3; /* (/8) */
            remaining = img_comp.prec & 7;  /* (%8) */
            if (remaining != 0)
            {
                size_comp += 1;
            }

            if (size_comp == 3)
            {
                size_comp = 4;
            }

            width = (uint)(tilec.x1 - tilec.x0);
            height = (uint)(tilec.y1 - tilec.y0);
            offset_x = (uint)MyMath.int_ceildiv((int)image.x0,
                          (int)img_comp.dx);
            offset_y = (uint)MyMath.int_ceildiv((int)image.y0,
                          (int)img_comp.dy);
            image_width = (uint)MyMath.int_ceildiv((int)image.x1 -
                             (int)image.x0, (int)img_comp.dx);
            stride = image_width - width;
            tile_offset = ((uint)tilec.x0 - offset_x) + ((
                                 uint)tilec.y0 - offset_y) * image_width;
        }

        //2.5 - opj_j2k_get_tile_data
        internal void GetTileData(byte[] dest)
        {
            int dest_ptr = 0;

            for (uint i = 0; i < _image.numcomps; i++)
            {
                var tilec = _tcd_image.tiles[0].comps[i];
                var img_comp = _image.Components[i];
                uint size_comp, width, height,
                     stride, tile_offset;

                GetTileDimensions(_image, tilec, img_comp,
                    out size_comp, out width, out height,
                    out _, out _, out _,
                    out stride, out tile_offset);

                int src_ptr = (int)tile_offset;
                var src = img_comp.data;

                switch (size_comp)
                {
                    case 1:
                    {
                        if (img_comp.sgnd)
                        {
                            for (uint j = 0; j < height; j++)
                            {
                                for(uint k = 0; k < width; k++)
                                {
                                    dest[dest_ptr++] = (byte)src[src_ptr++];
                                }
                                src_ptr += (int)stride;
                            }
                        }
                        else
                        {
                            for (uint j = 0; j < height; j++)
                            {
                                for (uint k = 0; k < width; k++)
                                {
                                    dest[dest_ptr++] = (byte)(src[src_ptr++] & 0xFF);
                                }
                                src_ptr += (int)stride;
                            }
                        }

                        //C# we increment dest_ptr directly, so no need to set it here.
                    }
                    break;

                    case 2:
                    {
                        if (img_comp.sgnd)
                        {
                            for (uint j = 0; j < height; j++)
                            {
                                for (uint k = 0; k < width; k++)
                                {
                                    var val = (short)src[src_ptr++];
                                    dest[dest_ptr++] = (byte)(val);
                                    dest[dest_ptr++] = (byte)(val >> 8);
                                }
                                src_ptr += (int)stride;
                            }
                        }
                        else
                        {
                            for (uint j = 0; j < height; j++)
                            {
                                for (uint k = 0; k < width; k++)
                                {
                                    var val = (short)(src[src_ptr++] & 0xFFFF);
                                    dest[dest_ptr++] = (byte)(val);
                                    dest[dest_ptr++] = (byte)(val >> 8);
                                }
                                src_ptr += (int)stride;
                            }
                        }
                    }
                    break;

                    case 4:
                    {
                        for (uint j = 0; j < height; j++)
                        {
                            Buffer.BlockCopy(src, src_ptr * 4, dest, dest_ptr * 4, (int) width);
                            src_ptr += (int)stride;
                            dest_ptr += (int)width;
                        }
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Copies tile data from the given memory block onto the system.
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_tcd_copy_tile_data
        /// 
        /// C# the older 2.1 implementation did these copies faster by making
        /// src an int array. Drawback is 4x the menory use on 8bpp components.
        /// </remarks>
        internal bool CopyTileData(byte[] src, uint length)
        {
            uint data_size = GetEncoderInputBufferSize();

            if (data_size != length)
                return false;

            TcdTilecomp[] tilecs = _tcd_image.tiles[0].comps;
            var img_comps = _image.comps;
            int src_ptr = 0;

            for (int i = 0; i < img_comps.Length; i++)
            {
                var img_comp = img_comps[i];
                uint size_comp = img_comp.prec >> 3;
                uint remaining = img_comp.prec & 7;

                if (remaining != 0)
                    size_comp++;

                if (size_comp == 3)
                    size_comp = 4;

                var tilec = tilecs[i];
                var dest = tilec.data;
                int nb_elem = ((tilec.x1 - tilec.x0) * (tilec.y1 - tilec.y0));

                switch (size_comp)
                {
                    case 1:
                    {
                        if (img_comp.sgnd)
                        {
                            for (int j = 0; j < nb_elem; j++)
                                dest[j] = src[src_ptr++];
                        }
                        else
                        {
                            for (int j = 0; j < nb_elem; j++)
                                dest[j] = (src[src_ptr++] & 0xff);
                        }

                        //C# Note, we increment scr_ptr directly, so no set p_src
                    }
                    break;

                    case 2:
                    {
                        if (img_comp.sgnd)
                        {
                            for (int j = 0; j < nb_elem; j++)
                            {
                                short val = (short) (src[src_ptr++] | 
                                                    (src[src_ptr++] << 8));
                                dest[j] = val;
                            }
                        }
                        else
                        {
                            for (int j = 0; j < nb_elem; j++)
                            {
                                short val = (short)((src[src_ptr++] |
                                                    (src[src_ptr++] << 8)) & 0xFFFF);
                                dest[j] = val;
                            }
                        }

                        //C# Note, we increment scr_ptr directly, so no set p_src
                    }
                    break;

                    case 4:
                    {
                        Buffer.BlockCopy(src, src_ptr, tilec.data, 0, nb_elem * 4);
                        src_ptr += nb_elem * 4;
                    }
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>
        /// 2.1 - opj_tcd_update_tile_data
        /// 
        /// C# Yes, this code haven't been update to 2.5.
        /// 
        /// Org. impl copies to data from an int[] to a byte/short/int[].
        /// 
        /// Nice, but no publically accsible method (in this impl) allows
        /// you to call DecodeTile(xxx, data != null), so this code will
        /// never execute.
        /// </remarks>
        internal bool UpdateTileData(/*byte[] dest*/ int[][] dest)
        {
            //uint data_size = GetDecodedTileSize();

            //if (data_size > dest.Length)
            //    return false;

            TcdTilecomp[] tilecs = _tcd_image.tiles[0].comps;
            var img_comps = _image.comps;
            //int dest_ptr = 0;

            //for (int i = 0; i < img_comps.Length; i++)
            //{
            //    var tilec = tilecs[i];
            //    int nb_elem = ((tilec.x1 - tilec.x0) * (tilec.y1 - tilec.y0));

            //    //C# impl. note. No need for conversion.
            //    Buffer.BlockCopy(tilec.data, 0, dest, dest_ptr * 4, nb_elem * 4);
            //    dest_ptr += nb_elem;
            //}

            for (int i = 0; i < img_comps.Length; i++)
            {
                dest[i] = tilecs[i].data;
            }

            return true;
        }

        //2.5 - opj_tcd_is_subband_area_of_interest
        internal bool IsSubbandAreaOfInterest(
            uint compno,
            uint resno,
            uint bandno,
            uint band_x0,
            uint band_y0,
            uint band_x1,
            uint band_y1)
        {
            /* Note: those values for filter_margin are in part the result of */
            /* experimentation. The value 2 for QMFBID=1 (5x3 filter) can be linked */
            /* to the maximum left/right extension given in tables F.2 and F.3 of the */
            /* standard. The value 3 for QMFBID=0 (9x7 filter) is more suspicious, */
            /* since F.2 and F.3 would lead to 4 instead, so the current 3 might be */
            /* needed to be bumped to 4, in case inconsistencies are found while */
            /* decoding parts of irreversible coded images. */
            /* See opj_dwt_decode_partial_53 and opj_dwt_decode_partial_97 as well */
            uint filter_margin = (_tcp.tccps[compno].qmfbid == 1) ? 2u : 3u;
            var tilec = _tcd_image.tiles[0].comps[compno];
            var image_comp = _image.comps[compno];
            /* Compute the intersection of the area of interest, expressed in tile coordinates */
            /* with the tile coordinates */
            OPJ_UINT32 tcx0 = Math.Max(
                                  (OPJ_UINT32)tilec.x0,
                                  MyMath.uint_ceildiv(_win_x0, image_comp.dx));
            OPJ_UINT32 tcy0 = Math.Max(
                                  (OPJ_UINT32)tilec.y0,
                                  MyMath.uint_ceildiv(_win_y0, image_comp.dy));
            OPJ_UINT32 tcx1 = Math.Min(
                                  (OPJ_UINT32)tilec.x1,
                                  MyMath.uint_ceildiv(_win_x1, image_comp.dx));
            OPJ_UINT32 tcy1 = Math.Min(
                                  (OPJ_UINT32)tilec.y1,
                                  MyMath.uint_ceildiv(_win_y1, image_comp.dy));
            /* Compute number of decomposition for this band. See table F-1 */
            OPJ_UINT32 nb = (resno == 0) ?
                            tilec.numresolutions - 1 :
                            tilec.numresolutions - resno;
            /* Map above tile-based coordinates to sub-band-based coordinates per */
            /* equation B-15 of the standard */
            OPJ_UINT32 x0b = bandno & 1;
            OPJ_UINT32 y0b = bandno >> 1;
            OPJ_UINT32 tbx0 = (nb == 0) ? tcx0 :
                              (tcx0 <= (1U << (int)(nb - 1)) * x0b) ? 0 :
                              MyMath.uint_ceildivpow2(tcx0 - (1U << (int)(nb - 1)) * x0b, (int)nb);
            OPJ_UINT32 tby0 = (nb == 0) ? tcy0 :
                              (tcy0 <= (1U << (int)(nb - 1)) * y0b) ? 0 :
                              MyMath.uint_ceildivpow2(tcy0 - (1U << (int)(nb - 1)) * y0b, (int)nb);
            OPJ_UINT32 tbx1 = (nb == 0) ? tcx1 :
                              (tcx1 <= (1U << (int)(nb - 1)) * x0b) ? 0 :
                              MyMath.uint_ceildivpow2(tcx1 - (1U << (int)(nb - 1)) * x0b, (int)nb);
            OPJ_UINT32 tby1 = (nb == 0) ? tcy1 :
                              (tcy1 <= (1U << (int)(nb - 1)) * y0b) ? 0 :
                              MyMath.uint_ceildivpow2(tcy1 - (1U << (int)(nb - 1)) * y0b, (int)nb);
            bool intersects;

            if (tbx0 < filter_margin)
            {
                tbx0 = 0;
            }
            else
            {
                tbx0 -= filter_margin;
            }
            if (tby0 < filter_margin)
            {
                tby0 = 0;
            }
            else
            {
                tby0 -= filter_margin;
            }
            tbx1 = MyMath.uint_adds(tbx1, filter_margin);
            tby1 = MyMath.uint_adds(tby1, filter_margin);

            intersects = band_x0 < tbx1 && band_y0 < tby1 && band_x1 > tbx0 &&
                         band_y1 > tby0;

            return intersects;
        }

        /// <summary>
        /// Returns whether a tile componenent is fully decoded, taking into account
        /// _win_* members.
        /// </summary>
        /// <param name="compno">Component number</param>
        /// <returns>Whether the tile componenent is fully decoded</returns>
        /// <remarks>
        /// 2.5
        /// </remarks>
        private bool IsWholeTilecompDecoding(OPJ_UINT32 compno)
        {
            var tilec = _tcd_image.tiles[0].comps[compno];
            var image_comp = _image.comps[compno];
            // Compute the intersection of the area of interest, expressed in tile coordinates
            // with the tile coordinates
            OPJ_UINT32 tcx0 = Math.Max(
                                  (OPJ_UINT32)tilec.x0,
                                  MyMath.uint_ceildiv(_win_x0, image_comp.dx));
            OPJ_UINT32 tcy0 = Math.Max(
                                  (OPJ_UINT32)tilec.y0,
                                  MyMath.uint_ceildiv(_win_y0, image_comp.dy));
            OPJ_UINT32 tcx1 = Math.Min(
                                  (OPJ_UINT32)tilec.x1,
                                  MyMath.uint_ceildiv(_win_x1, image_comp.dx));
            OPJ_UINT32 tcy1 = Math.Min(
                                  (OPJ_UINT32)tilec.y1,
                                  MyMath.uint_ceildiv(_win_y1, image_comp.dy));

            int shift = (int) (tilec.numresolutions - tilec.minimum_num_resolutions);
            // Tolerate small margin within the reduced resolution factor to consider if
            // the whole tile path must be taken
            return (tcx0 >= (OPJ_UINT32)tilec.x0 &&
                    tcy0 >= (OPJ_UINT32)tilec.y0 &&
                    tcx1 <= (OPJ_UINT32)tilec.x1 &&
                    tcy1 <= (OPJ_UINT32)tilec.y1 &&
                    (shift >= 32 ||
                     (((tcx0 - (OPJ_UINT32)tilec.x0) >> shift) == 0 &&
                      ((tcy0 - (OPJ_UINT32)tilec.y0) >> shift) == 0 &&
                      (((OPJ_UINT32)tilec.x1 - tcx1) >> shift) == 0 &&
                      (((OPJ_UINT32)tilec.y1 - tcy1) >> shift) == 0)));
        }
    }
}
