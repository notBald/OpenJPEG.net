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
//STANDARD_SLOW_VERSION Uses the OpenJpeg 2.1 codepath for DWT decoding.
//#define STANDARD_SLOW_VERSION
using System;
using System.Diagnostics;
using System.Threading;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Discrete Wavelet Transform
    /// </summary>
    /// <remarks>
    /// The DWT can be reversible, or irreversible.
    /// 
    /// Reversible uses integer math and a modified YUV colorspace,
    /// irreversible uses floating point math and the YCbCr colorspace
    /// 
    /// Irreversible is only applicable for lossy encoding.
    /// </remarks>
    internal static class DWT
    {

        #region Consts

        const float K = 1.230174105f;
        const float invK = (float)(1.0 / 1.230174105);
        const float c13318 = 1.625732422f; //<-- two_invK
        const float dwt_alpha = -1.586134342f; //  12994
        const float dwt_beta = -0.052980118f; //    434
        const float dwt_gamma = 0.882911075f; //  -7233
        const float dwt_delta = 0.443506852f; //  -3633

        /// <summary>
        /// Number of int32 values in a SSE2 register
        /// </summary>
        /// <remarks>
        /// We don't currently support SSE2, but maybe in the future
        /// </remarks>
        const uint VREG_INT_COUNT = 4;
        const uint PARALLEL_COLS_53 = 2 * VREG_INT_COUNT;
        const int NB_ELTS_V8 = 8;

        /// <summary>
        /// This table contains the norms of the 9-7 wavelets for different bands.
        /// </summary>
        static readonly double[][] dwt_norms_real = {
	       new double[] {1.000, 1.965, 4.177, 8.403, 16.90, 33.84, 67.69, 135.3, 270.6, 540.9},
	       new double[] {2.022, 3.989, 8.355, 17.04, 34.27, 68.63, 137.3, 274.6, 549.0},
	       new double[] {2.022, 3.989, 8.355, 17.04, 34.27, 68.63, 137.3, 274.6, 549.0},
	       new double[] {2.080, 3.865, 8.307, 17.18, 34.71, 69.59, 139.3, 278.6, 557.2}
        };

        /// <summary>
        /// This table contains the norms of the 5-3 wavelets for different bands.
        /// </summary>
        static readonly double[][] dwt_norms = {
	        new double[] {1.000, 1.500, 2.750, 5.375, 10.68, 21.34, 42.67, 85.33, 170.7, 341.3},
	        new double[] {1.038, 1.592, 2.919, 5.703, 11.33, 22.64, 45.25, 90.48, 180.9},
	        new double[] {1.038, 1.592, 2.919, 5.703, 11.33, 22.64, 45.25, 90.48, 180.9},
	        new double[] {.7186, .9218, 1.586, 3.043, 6.019, 12.01, 24.00, 47.97, 95.93}
        };

        #endregion

        delegate void Encodefunc(int[] a, int dn, int sn, int cas);
        delegate void EncodeAndDeinterleaveVfunc(int[] a, int a_pt, int[] tmp, uint height, bool even, uint stride_width, uint cols);
        delegate void EncodeAndDeinterleaveH_OneRowfunc(int[] row, int row_pt, int[] tmp, uint width, bool even);

        /// <summary>
        /// Forward 5-3 wavelet transform in 2-D
        /// </summary>
        /// <remarks>2.5 - opj_dwt_encode</remarks>
        internal static bool Encode(TileCoder tcd, TcdTilecomp tilec)
        {
            return EncodeProcedure(tilec, EncodeAndDeinterleaveV, EncodeAndDeinterleaveH_OneRow, tcd.DisableMultiThreading);
        }

        /// <summary>
        /// Forward 9-7 wavelet transform in 2-D
        /// </summary>
        /// <remarks>2.5 - opj_dwt_encode_real</remarks>
        internal static bool EncodeReal(TileCoder tcd, TcdTilecomp tilec)
        {
            return EncodeProcedure(tilec, EncodeAndDeinterleaveV_Real, EncodeAndDeinterleaveH_OneRowReal, tcd.DisableMultiThreading);
        }

        //2.5 - opj_dwt_encode_procedure
        static bool EncodeProcedure(TcdTilecomp tilec, 
            EncodeAndDeinterleaveVfunc encode_and_deinterleave_v, 
            EncodeAndDeinterleaveH_OneRowfunc encode_and_deinterleave_h_one_row, 
            bool disable_multi_threading)
        {
            int num_threads;
            ThreadPool.GetAvailableThreads(out num_threads, out _);
            num_threads = disable_multi_threading ? 1 : Math.Min(Environment.ProcessorCount, num_threads);

            int[] tiledp = tilec.data;

            uint w = (uint)(tilec.x1 - tilec.x0);
            int l = (int)tilec.numresolutions - 1;

            var tr = tilec.resolutions;
            int cur_res = l; //<-- pointer to tilec.resolutions
            int last_res = cur_res - 1; //<-- pointer to tilec.resolutions

            uint data_size = MaxResolution(tilec.resolutions, (int)tilec.numresolutions);
            if (data_size > (Constants.SIZE_MAX / (NB_ELTS_V8 * sizeof(int))))
                return false;
            data_size *= NB_ELTS_V8; //C# org impl is number of bytes, here it's number of ints
            int[] bj = new int[data_size];
            int i = l;

            using (var reset = new ManualResetEvent(false))
            {
                while (i-- != 0)
                {
                    //Width of the resolution level computed
                    uint rw = (uint)(tr[cur_res].x1 - tr[cur_res].x0);

                    //Height of the resolution level computed
                    uint rh = (uint)(tr[cur_res].y1 - tr[cur_res].y0);

                    //Width of the resolution level once lower than computed one
                    uint rw1 = (uint)(tr[last_res].x1 - tr[last_res].x0);

                    //Height of the resolution level once lower than computed one 
                    uint rh1 = (uint)(tr[last_res].y1 - tr[last_res].y0);

                    //0 = non inversion on vertical filtering 1 = inversion between low-pass and high-pass filtering
                    int cas_row = tr[cur_res].x0 & 1;

                    //0 = non inversion on horizontal filtering 1 = inversion between low-pass and high-pass filtering
                    int cas_col = tr[cur_res].y0 & 1;

                    int sn = (int)rh1;
                    int dn = (int)(rh - rh1);

                    // Perform vertical pass
                    if (num_threads <= 1 || rw < 2 * NB_ELTS_V8)
                    {
                        int j = 0;
                        for (; j + NB_ELTS_V8 - 1 < rw; j += NB_ELTS_V8)
                        {
                            encode_and_deinterleave_v(tiledp, j, bj, rh, cas_col == 0, w, NB_ELTS_V8);
                        }
                        if (j < rw)
                        {
                            encode_and_deinterleave_v(tiledp, j, bj, rh, cas_col == 0, w, rw - (uint)j);
                        }
                    }
                    else
                    {
                        int num_jobs = num_threads;

                        if (rw < num_jobs)
                        {
                            num_jobs = (int)rw;
                        }

                        uint step_j = ((rw / (uint)num_jobs) / NB_ELTS_V8) * NB_ELTS_V8;

                        reset.Reset();
                        //Alternativly, we can set this to num_jobs and remove the Interlocked.Increment
                        //and the Interlocked.Decrement after the for loop
                        int n_thread_workers = 1;

                        for (uint j = 0; j < num_jobs; j++)
                        {
                            var job = new encode_v_job(
                                new dwt_local() { 
                                    mem = new int[data_size],
                                    dn = dn,
                                    sn = sn,
                                    cas = cas_col
                                },
                                rh,
                                w,
                                tiledp,
                                0,
                                j * step_j,
                                (j + 1 == num_jobs) ? rw : (j + 1) * step_j,
                                encode_and_deinterleave_v
                             );

                            Interlocked.Increment(ref n_thread_workers);
                            ThreadPool.QueueUserWorkItem((x) =>
                            {
                                try { encode_v_func((encode_v_job)x); }
                                finally
                                {
                                    if (Interlocked.Decrement(ref n_thread_workers) == 0)
                                        reset.Set();
                                }
                            }, job);
                        }
                        if (Interlocked.Decrement(ref n_thread_workers) == 0)
                            reset.Set();
                        reset.WaitOne();
                    }

                    sn = (int)rw1;
                    dn = (int)(rw - rw1);

                    // Perform horizontal pass
                    if (num_threads <= 1 || rh <= 1)
                    {
                        for (int j = 0; j < rh; j++)
                        {
                            encode_and_deinterleave_h_one_row(tiledp, j * (int)w, bj, rw, cas_row == 0);
                        }
                    }
                    else
                    {
                        int num_jobs = num_threads;

                        if (rh < num_jobs)
                        {
                            num_jobs = (int)rh;
                        }

                        uint step_j = rh / (uint)num_jobs;

                        reset.Reset();
                        //Alternativly, we can set this to num_jobs and remove the Interlocked.Increment
                        //and the Interlocked.Decrement after the for loop
                        int n_thread_workers = 1;

                        for (uint j = 0; j < num_jobs; j++)
                        {
                            var max_j = (j + 1) * step_j;
                            if (j == num_jobs - 1)
                                max_j = rh;

                            var job = new encode_h_job(
                                new dwt_local()
                                {
                                    mem = new int[data_size],
                                    dn = dn,
                                    sn = sn,
                                    cas = cas_row
                                },
                                rw,
                                w,
                                tiledp,
                                0,
                                j * step_j,
                                max_j,
                                encode_and_deinterleave_h_one_row
                             );

                            Interlocked.Increment(ref n_thread_workers);
                            ThreadPool.QueueUserWorkItem((x) =>
                            {
                                try { encode_h_func((encode_h_job)x); }
                                finally
                                {
                                    if (Interlocked.Decrement(ref n_thread_workers) == 0)
                                        reset.Set();
                                }
                            }, job);
                        }
                        if (Interlocked.Decrement(ref n_thread_workers) == 0)
                            reset.Set();
                        reset.WaitOne();
                    }

                    cur_res = last_res;

                    last_res--;
                }
            }

            return true; ;
        }

        //2.5 - opj_dwt_encode_h_func
        static void encode_h_func(encode_h_job job)
        {
            for (uint j = job.min_j; j < job.max_j; j++)
            {
                job.fn(job.tiled, job.tiledp + (int)j * (int)job.w, job.h.mem, job.rw, job.h.cas == 0);
            }
        }

        //2.5 - opj_dwt_encode_v_func
        static void encode_v_func(encode_v_job job)
        {
            uint j;
            for (j = job.min_j; j + NB_ELTS_V8 - 1 < job.max_j; j += NB_ELTS_V8)
            {
                job.encode_and_deinterleave_v(job.tiled, job.tiledp + (int)j, job.v.mem, job.rh, job.v.cas == 0, job.w, NB_ELTS_V8);
            }
            if (j < job.max_j)
            {
                job.encode_and_deinterleave_v(job.tiled, job.tiledp + (int)j, job.v.mem, job.rh, job.v.cas == 0, job.w, job.max_j - j);
            }
        }

        /// <summary>
        /// Determine maximum computed resolution level for inverse wavelet transform
        /// </summary>
        /// <param name="r">Resolutions</param>
        /// <param name="i">Number of resolutions that will be decoded</param>
        /// <remarks>2.5 - opj_dwt_max_resolution</remarks>
        static uint MaxResolution(TcdResolution[] rs, int numres)
        {
            uint mr = 0;
            uint w;
            for (int c = 1; c < numres; c++)
            {
                var r = rs[c];
                if (mr < (w = (uint)(r.x1 - r.x0)))
                    mr = w;
                if (mr < (w = (uint)(r.y1 - r.y0)))
                    mr = w;
            }
            return mr;
        }

        //2.5 - opj_dwt_calc_explicit_stepsizes
        internal static void CalcExplicitStepsizes(TileCompParams tccp, uint prec)
        {
            uint numbands = 3 * tccp.numresolutions - 2;
            for (uint bandno = 0; bandno < numbands; bandno++)
            {
                double stepsize;
                uint resno, level, orient, gain;

                resno = (bandno == 0) ? 0 : ((bandno - 1) / 3 + 1);
                orient = (bandno == 0) ? 0 : ((bandno - 1) % 3 + 1);
                level = tccp.numresolutions - 1 - resno;
                gain = (tccp.qmfbid == 0) ? 0U : ((orient == 0) ? 0U : (((orient == 1) || 
                                                  (orient == 2)) ? 1U : 2U));
                if (tccp.qntsty == CCP_QNTSTY.NOQNT)
                    stepsize = 1.0;
                else
                {
                    double norm = GetNormReal(level, orient);
                    stepsize = (1 << ((int)gain)) / norm;
                }
                EncodeStepsize((int)Math.Floor(stepsize * 8192.0), (int)(prec + gain), out tccp.stepsizes[bandno]);
            }
        }

        //2.5 - opj_dwt_getnorm_real
        static double GetNormReal(uint level, uint orient)
        {
            //This is just a band-aid to avoid a buffer overflow
            if (orient == 0 && level >= 10)
                level = 9;
            else if (orient > 0 && level >= 9)
                level = 8;
            return dwt_norms_real[orient][level];
        }

        /// <remarks>2.5 - opj_dwt_encode_stepsize</remarks>
        static void EncodeStepsize(int stepsize, int numbps, out StepSize bandno_stepsize)
        {
            int p, n;
            p = MyMath.int_floorlog2(stepsize) - 13;
            n = 11 - MyMath.int_floorlog2(stepsize);
            bandno_stepsize = new StepSize(
                numbps - p, 
                (n < 0 ? stepsize >> -n : stepsize << n) & 0x7ff);
        }

        //2.5 - opj_dwt_decode_real
        internal static bool DecodeReal(TileCoder tcd, TcdTilecomp tilec, uint numres)
        {
            if (tcd.WholeTileDecoding)
                return decode_tile_97(tilec, numres, tcd.DisableMultiThreading);
            else
                return decode_partial_97(tilec, numres);
        }

        //2.5 - opj_dwt_decode_partial_97
        static bool decode_partial_97(TcdTilecomp tilec, uint numres)
        {
            SparseArrayInt32 sa;
            v4dwt_local h = new v4dwt_local();
            v4dwt_local v = new v4dwt_local();
            // This value matches the maximum left/right extension given in tables
            // F.2 and F.3 of the standard. Note: in opj_tcd_is_subband_area_of_interest()
            // we currently use 3.
            const uint filter_width = 4U;

            TcdResolution[] tr_ar = tilec.resolutions;
            int tr_pos = 0;
            TcdResolution tr = tr_ar[tr_pos], tr_max = tr_ar[numres - 1];

            //Width of the resolution level computed
            uint rw = (uint)(tr.x1 - tr.x0);

            //Height of the resolution level computed
            uint rh = (uint)(tr.y1 - tr.y0);

            ulong data_size;

            // Compute the intersection of the area of interest, expressed in tile coordinates
            // with the tile coordinates
            uint win_tcx0 = tilec.win_x0;
            uint win_tcy0 = tilec.win_y0;
            uint win_tcx1 = tilec.win_x1;
            uint win_tcy1 = tilec.win_y1;

            if (tr_max.x0 == tr_max.x1 || tr_max.y0 == tr_max.y1)
            {
                return true;
            }

            sa = SparseArrayInt32.Init(tilec, numres);
            if (sa == null)
                return false;

            if (numres == 1U)
            {
                bool ret = sa.read(tr_max.win_x0 - (uint)tr_max.x0,
                                   tr_max.win_y0 - (uint)tr_max.y0,
                                   tr_max.win_x1 - (uint)tr_max.x0,
                                   tr_max.win_y1 - (uint)tr_max.y0,
                                   tilec.data_win, 0,
                                   1, tr_max.win_x1 - tr_max.win_x0,
                                   true);
                Debug.Assert(ret);
                return true;
            }
            data_size = MaxResolution(tr_ar, (int)numres);
            // overflow check
            // C# 
            if (data_size > (Constants.SIZE_MAX / NB_ELTS_V8 * (sizeof(float))))
            {
                return false;
            }
            h.wavelet = new float[data_size * NB_ELTS_V8];
            v.wavelet = h.wavelet;

            for (uint resno = 1; resno < numres; resno++)
            {
                uint i, j;
                /* Window of interest subband-based coordinates */
                uint win_ll_x0, win_ll_y0, win_ll_x1, win_ll_y1;
                uint win_hl_x0, win_hl_x1;
                uint win_lh_y0, win_lh_y1;
                /* Window of interest tile-resolution-based coordinates */
                uint win_tr_x0, win_tr_x1, win_tr_y0, win_tr_y1;
                /* Tile-resolution subband-based coordinates */
                uint tr_ll_x0, tr_ll_y0, tr_hl_x0, tr_lh_y0;

                tr = tr_ar[++tr_pos];

                h.sn = (int)rw;
                v.sn = (int)rh;

                rw = (uint)(tr.x1 - tr.x0);
                rh = (uint)(tr.y1 - tr.y0);

                h.dn = (int)(rw - (uint)h.sn);
                h.cas = tr.x0 % 2;

                v.dn = (int)(rh - (uint)v.sn);
                v.cas = tr.y0 % 2;

                // Get the subband coordinates for the window of interest
                // LL band
                GetBandCoordinates(tilec, resno, 0,
                                   win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                                   out win_ll_x0, out win_ll_y0,
                                   out win_ll_x1, out win_ll_y1);

                // HL band
                GetBandCoordinates(tilec, resno, 1,
                                   win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                                   out win_hl_x0, out _, out win_hl_x1, out _);

                /* LH band */
                GetBandCoordinates(tilec, resno, 2,
                                   win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                                   out _, out win_lh_y0, out _, out win_lh_y1);

                /* Beware: band index for non-LL0 resolution are 0=HL, 1=LH and 2=HH */
                tr_ll_x0 = (uint)tr.bands[1].x0;
                tr_ll_y0 = (uint)tr.bands[0].y0;
                tr_hl_x0 = (uint)tr.bands[0].x0;
                tr_lh_y0 = (uint)tr.bands[1].y0;

                /* Subtract the origin of the bands for this tile, to the subwindow */
                /* of interest band coordinates, so as to get them relative to the */
                /* tile */
                win_ll_x0 = MyMath.uint_subs(win_ll_x0, tr_ll_x0);
                win_ll_y0 = MyMath.uint_subs(win_ll_y0, tr_ll_y0);
                win_ll_x1 = MyMath.uint_subs(win_ll_x1, tr_ll_x0);
                win_ll_y1 = MyMath.uint_subs(win_ll_y1, tr_ll_y0);
                win_hl_x0 = MyMath.uint_subs(win_hl_x0, tr_hl_x0);
                win_hl_x1 = MyMath.uint_subs(win_hl_x1, tr_hl_x0);
                win_lh_y0 = MyMath.uint_subs(win_lh_y0, tr_lh_y0);
                win_lh_y1 = MyMath.uint_subs(win_lh_y1, tr_lh_y0);

                SegmentGrow(filter_width, (uint)h.sn, ref win_ll_x0, ref win_ll_x1);
                SegmentGrow(filter_width, (uint)h.dn, ref win_hl_x0, ref win_hl_x1);

                SegmentGrow(filter_width, (uint)v.sn, ref win_ll_y0, ref win_ll_y1);
                SegmentGrow(filter_width, (uint)v.dn, ref win_lh_y0, ref win_lh_y1);

                /* Compute the tile-resolution-based coordinates for the window of interest */
                if (h.cas == 0)
                {
                    win_tr_x0 = Math.Min(2 * win_ll_x0, 2 * win_hl_x0 + 1);
                    win_tr_x1 = Math.Min(Math.Max(2 * win_ll_x1, 2 * win_hl_x1 + 1), rw);
                }
                else
                {
                    win_tr_x0 = Math.Min(2 * win_hl_x0, 2 * win_ll_x0 + 1);
                    win_tr_x1 = Math.Min(Math.Max(2 * win_hl_x1, 2 * win_ll_x1 + 1), rw);
                }

                if (v.cas == 0)
                {
                    win_tr_y0 = Math.Min(2 * win_ll_y0, 2 * win_lh_y0 + 1);
                    win_tr_y1 = Math.Min(Math.Max(2 * win_ll_y1, 2 * win_lh_y1 + 1), rh);
                }
                else
                {
                    win_tr_y0 = Math.Min(2 * win_lh_y0, 2 * win_ll_y0 + 1);
                    win_tr_y1 = Math.Min(Math.Max(2 * win_lh_y1, 2 * win_ll_y1 + 1), rh);
                }

                h.win_l_x0 = win_ll_x0;
                h.win_l_x1 = win_ll_x1;
                h.win_h_x0 = win_hl_x0;
                h.win_h_x1 = win_hl_x1;
                for (j = 0; j + (NB_ELTS_V8 - 1) < rh; j += NB_ELTS_V8)
                {
                    if ((j + (NB_ELTS_V8 - 1) >= win_ll_y0 && j < win_ll_y1) ||
                            (j + (NB_ELTS_V8 - 1) >= win_lh_y0 + (uint)v.sn &&
                            j < win_lh_y1 + (uint)v.sn))
                    {
                        v8dwt_interleave_partial_h(h, sa, j, Math.Min(NB_ELTS_V8, rh - j));
                        v8dwt_decode(h);
                        if (!sa.write(win_tr_x0, j,
                                      win_tr_x1, j + NB_ELTS_V8,
                                      h.wavelet, (int)win_tr_x0 * NB_ELTS_V8, //C# Wavlet indexing, therefore mul with NB_ELTS_V8
                                      NB_ELTS_V8, 1, true))
                        {
                            return false;
                        }
                    }
                }

                if (j < rh &&
                    ((j + (NB_ELTS_V8 - 1) >= win_ll_y0 && j < win_ll_y1) ||
                     (j + (NB_ELTS_V8 - 1) >= win_lh_y0 + (uint)v.sn &&
                      j < win_lh_y1 + (uint)v.sn)))
                {
                    v8dwt_interleave_partial_h(h, sa, j, rh - j);
                    v8dwt_decode(h);
                    if (!sa.write(win_tr_x0, j,
                                  win_tr_x1, rh,
                                  h.wavelet, (int)win_tr_x0 * NB_ELTS_V8,
                                  NB_ELTS_V8, 1, true))
                    {
                        return false;
                    }
                }

                v.win_l_x0 = win_ll_y0;
                v.win_l_x1 = win_ll_y1;
                v.win_h_x0 = win_lh_y0;
                v.win_h_x1 = win_lh_y1;
                for (j = win_tr_x0; j < win_tr_x1; j += NB_ELTS_V8)
                {
                    uint nb_elts = Math.Min(NB_ELTS_V8, win_tr_x1 - j);

                    v8dwt_interleave_partial_v(v, sa, j, nb_elts);
                    v8dwt_decode(v);
                    if (!sa.write(j, win_tr_y0,
                                  j + nb_elts, win_tr_y1,
                                  v.wavelet, (int)win_tr_y0 * NB_ELTS_V8,
                                  1, NB_ELTS_V8, true))
                    {
                        return false;
                    }
                }
            }

            {
                bool ret = sa.read(
                               tr_max.win_x0 - (uint)tr_max.x0,
                               tr_max.win_y0 - (uint)tr_max.y0,
                               tr_max.win_x1 - (uint)tr_max.x0,
                               tr_max.win_y1 - (uint)tr_max.y0,
                               tilec.data_win, 0,
                               1, tr_max.win_x1 - tr_max.win_x0,
                               true);
                Debug.Assert(ret);
            }

            return true;
        }

        /// <summary>
        /// Inverse 9-7 wavelet transform in 2-D.
        /// </summary>
        /// <remarks>2.5.1 - opj_dwt_decode_tile</remarks>
        internal static bool decode_tile_97(TcdTilecomp tilec, uint numres, bool disable_multi_threading)
        {
            v4dwt_local h = new v4dwt_local();
            v4dwt_local v = new v4dwt_local();

            TcdResolution[] res_ar = tilec.resolutions;
            int res_ar_pos = 0;
            TcdResolution res = res_ar[res_ar_pos];

            //Width of the resolution level computed
            uint rw = (uint)(res.x1 - res.x0);

            //Height of the resolution level computed
            uint rh = (uint)(res.y1 - res.y0);

            int w = tilec.resolutions[tilec.minimum_num_resolutions - 1].x1 
                  - tilec.resolutions[tilec.minimum_num_resolutions - 1].x0;

            ulong data_size;
            int num_threads;

            // Not entirely sure for the return code of w == 0 which is triggered per
            // https://github.com/uclouvain/openjpeg/issues/1505
            if (numres == 1U || w == 0)
            {
                return true;
            }
            ThreadPool.GetAvailableThreads(out num_threads, out _);
            num_threads = Math.Min(Environment.ProcessorCount, num_threads);
            if (disable_multi_threading)
                num_threads = 0;

            data_size = MaxResolution(res_ar, (int)numres);
            /* overflow check */
            if (data_size > (Constants.SIZE_MAX / NB_ELTS_V8 * sizeof(float)))
            {
                return false;
            }

            h.wavelet = new float[data_size * NB_ELTS_V8];
            v.wavelet = h.wavelet;

            using (var reset = new ManualResetEvent(false))
            {
                while (--numres != 0)
                {
                    int[] aj_ar = tilec.data;
                    int aj = 0, j;

                    h.sn = (int)rw;
                    v.sn = (int)rh;

                    res = res_ar[++res_ar_pos];

                    rw = (uint)(res.x1 - res.x0);
                    rh = (uint)(res.y1 - res.y0);

                    h.dn = (int)(rw - (uint)h.sn);
                    h.cas = res.x0 % 2;

                    h.win_l_x0 = 0;
                    h.win_l_x1 = (uint)h.sn;
                    h.win_h_x0 = 0;
                    h.win_h_x1 = (uint)h.dn;

                    //C# impl. note
                    //Used to convert from float to the "int value" of the
                    //float. This is since the org impl. use a int array
                    //to store both float and int values
                    var fi = new IntOrFloat();

                    if (num_threads <= 1 || rh < 2 * NB_ELTS_V8)
                    {
                        for (j = 0; j + (NB_ELTS_V8 - 1) < rh; j += NB_ELTS_V8)
                        {
                            v8dwt_interleave_h(h, aj_ar, aj, w, NB_ELTS_V8);
                            v8dwt_decode(h);

                            //Copies that back into the aj array.
                            // C# I'm unsure why it's split into two loops, that is probably an
                            // optimalization.
                            for (int k = 0; k < rw; k++)
                            {
                                //C# note: Org. impl stores the wavlet as a struct with eight
                                //floating points. Here it's stored as a continuous array, this
                                //is why we have to multiply k with 8. 
                                int k_wavelet = k * NB_ELTS_V8;
                                fi.F = h.wavelet[k_wavelet + 0];
                                aj_ar[aj + k] = fi.I;
                                fi.F = h.wavelet[k_wavelet + 1];
                                aj_ar[aj + k + w] = fi.I;
                                fi.F = h.wavelet[k_wavelet + 2];
                                aj_ar[aj + k + w * 2] = fi.I;
                                fi.F = h.wavelet[k_wavelet + 3];
                                aj_ar[aj + k + w * 3] = fi.I;
                            }
                            for (int k = 0; k < rw; k++)
                            {
                                int k_wavelet = k * NB_ELTS_V8;
                                fi.F = h.wavelet[k_wavelet + 4];
                                aj_ar[aj + k + w * 4] = fi.I;
                                fi.F = h.wavelet[k_wavelet + 5];
                                aj_ar[aj + k + w * 5] = fi.I;
                                fi.F = h.wavelet[k_wavelet + 6];
                                aj_ar[aj + k + w * 6] = fi.I;
                                fi.F = h.wavelet[k_wavelet + 7];
                                aj_ar[aj + k + w * 7] = fi.I;
                            }

                            aj += w * NB_ELTS_V8;
                        }
                    }
                    else
                    {
                        int num_jobs = num_threads;

                        if ((rh / NB_ELTS_V8) < num_jobs)
                        {
                            num_jobs = (int)rh / NB_ELTS_V8;
                        }

                        uint step_j = ((rh / (uint)num_jobs) / NB_ELTS_V8) * NB_ELTS_V8;

                        reset.Reset();
                        //Alternativly, we can set this to num_jobs and remove the Interlocked.Increment
                        //and the Interlocked.Decrement after the for loop
                        int n_thread_workers = 1;

                        for (j = 0; j < num_jobs; j++)
                        {
                            var job = new dwt97_decode_h_job(
                                h.Clone(), rw, (uint)w, aj_ar, aj,
                                (j + 1 == num_jobs) ? (rh & unchecked((uint)~(NB_ELTS_V8 - 1))) - (uint)j * step_j : step_j
                            );
                            job.h.wavelet = new float[h.wavelet.Length];

                            aj += (int)(w * job.nb_rows);

                            Interlocked.Increment(ref n_thread_workers);
                            ThreadPool.QueueUserWorkItem((x) =>
                            {
                                try { dwt97_decode_h_func((dwt97_decode_h_job)x); }
                                finally
                                {
                                    if (Interlocked.Decrement(ref n_thread_workers) == 0)
                                        reset.Set();
                                }
                            }, job);
                        }
                        if (Interlocked.Decrement(ref n_thread_workers) == 0)
                            reset.Set();
                        reset.WaitOne();
                        j = (int)(rh & unchecked((uint)~(NB_ELTS_V8 - 1)));
                    }

                    if (j < rh)
                    {
                        
                        v8dwt_interleave_h(h, aj_ar, aj, w, rh - (uint)j);
                        v8dwt_decode(h);

                        for (int k = 0; k < rw; k++)
                        {
                            int k_wavelet = k * NB_ELTS_V8, ajk = aj + k;
                            for (uint l = 0; l < rh - j; l++)
                            {
                                fi.F = h.wavelet[k_wavelet + l];
                                aj_ar[ajk + w * l] = fi.I;
                            }
                        }
                    }

                    v.dn = (int)(rh - (uint)v.sn);
                    v.cas = res.y0 % 2;
                    v.win_l_x0 = 0;
                    v.win_l_x1 = (uint)v.sn;
                    v.win_h_x0 = 0;
                    v.win_h_x1 = (uint)v.dn;

                    aj = 0;
                    if (num_threads <= 1 || rw < 2 * NB_ELTS_V8)
                    {
                        for (j = (int)rw; j > (NB_ELTS_V8 - 1); j -= NB_ELTS_V8)
                        {
                            IntOrFloat faa;
                            faa.I = 0;

                            v8dwt_interleave_v(v, aj_ar, aj, w, NB_ELTS_V8);
                            v8dwt_decode(v);
                            for (int k = 0; k < rh; ++k)
                            {
                                Buffer.BlockCopy(v.wavelet, k * NB_ELTS_V8 * sizeof(float), aj_ar, (aj + (k * w)) * sizeof(float), NB_ELTS_V8 * sizeof(float));
                            }

                            aj += NB_ELTS_V8;
                        }
                    }
                    else
                    {
                        /* "bench_dwt -I" shows that scaling is poor, likely due to RAM
                            transfer being the limiting factor. So limit the number of
                            threads.
                            C# note: I've not run this benchmark
                         */
                        int num_jobs = Math.Max(num_threads / 2, 2);
                        //num_jobs = 1;

                        if ((rw / NB_ELTS_V8) < num_jobs)
                        {
                            num_jobs = (int)rw / NB_ELTS_V8;
                        }

                        uint step_j = ((rw / (uint)num_jobs) / NB_ELTS_V8) * NB_ELTS_V8;

                        reset.Reset();
                        //Alternativly, we can set this to num_jobs and remove the Interlocked.Increment
                        //and the Interlocked.Decrement after the for loop
                        int n_thread_workers = 1;

                        for (j = 0; j < num_jobs; j++)
                        {
                            var job = new dwt97_decode_v_job(
                                v.Clone(), rh, (uint)w, aj_ar, aj,
                                (j + 1 == num_jobs) ? (rw & unchecked((uint)~(NB_ELTS_V8 - 1))) - (uint)j * step_j : step_j
                            );
                            job.v.wavelet = new float[v.wavelet.Length];

                            aj += (int)(job.nb_columns);

                            Interlocked.Increment(ref n_thread_workers);
                            ThreadPool.QueueUserWorkItem((x) =>
                            {
                                try { dwt97_decode_v_func((dwt97_decode_v_job)x); }
                                finally
                                {
                                    if (Interlocked.Decrement(ref n_thread_workers) == 0)
                                        reset.Set();
                                }
                            }, job);
                        }
                        if (Interlocked.Decrement(ref n_thread_workers) == 0)
                            reset.Set();
                        reset.WaitOne();
                    }

                    //Makes sure not not overflow the array by copying "less than 4 floats".
                    if ((rw & (NB_ELTS_V8 - 1)) != 0)
                    {
                        j = (int)(rw & (NB_ELTS_V8 - 1));
                        v8dwt_interleave_v(v, aj_ar, aj, w, j);
                        v8dwt_decode(v);

                        for (int k = 0; k < rh; ++k)
                            Buffer.BlockCopy(v.wavelet, k * NB_ELTS_V8 * sizeof(float), aj_ar, (aj + (k * w)) * sizeof(float), j * sizeof(float));
                    }
                }
            }

            //{
            //    //C# Debug code to dump out the wavelets. Reason for doing this was to get the same floating point
            //    //   precision as the original impl. 
            //    IntOrFloat faa;
            //    faa.I = 0;

            //    using (var file = new System.IO.StreamWriter("c:/temp/cs_wavelet.txt", append: true))
            //    {
            //        file.Write("Raw float values for {0} wavelets\n", h.win_h_x1 - h.win_h_x0);
            //        for (uint wave_nr = h.win_h_x0; wave_nr < h.win_h_x1; wave_nr++)
            //        {
            //            file.Write("  --- wave {0}\n", wave_nr);
            //            for (int wave_comp = 0; wave_comp < 8; wave_comp++)
            //            {
            //                faa.F = h.wavelet[wave_nr * 8 + wave_comp];
            //                //FP is rounded differently, {1:0.000000}
            //                file.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{1}: {0}\n", faa.I, wave_comp + 1));
            //            }
            //        }
            //    }
            //}

            return true;
        }

        /// <summary>
        /// Inverse 9-7 wavelet transform in 1-D.
        /// </summary>
        /// <remarks>
        /// 2.5
        /// </remarks>
        private static void v8dwt_decode(v4dwt_local dwt)
        {
            /* BUG_WEIRD_TWO_INVK (look for this identifier in tcd.c) */
            /* Historic value for 2 / opj_invK */
            /* Normally, we should use invK, but if we do so, we have failures in the */
            /* conformance test, due to MSE and peak errors significantly higher than */
            /* accepted value */
            /* Due to using two_invK instead of invK, we have to compensate in tcd.c */
            /* the computation of the stepsize for the non LL subbands */
            const float two_invK = 1.625732422f;

            int a, b;
            if (dwt.cas == 0)
            {
                if (!((dwt.dn > 0) || (dwt.sn > 1)))
                    return;
                a = 0;
                b = 1;
            }
            else
            {
                if (!((dwt.sn > 0) || (dwt.dn > 1)))
                    return;
                a = 1;
                b = 0;
            }

            //C# Snip SSE code

            //C# Both a and b index dwt.wavelet, hench why we have to mul with NB_ELTS_V8
            v8dwt_decode_step1(dwt.wavelet, a * NB_ELTS_V8, (int)dwt.win_l_x0, (int)dwt.win_l_x1, K);
            v8dwt_decode_step1(dwt.wavelet, b * NB_ELTS_V8, (int)dwt.win_h_x0, (int)dwt.win_h_x1, two_invK);
            v8dwt_decode_step2(dwt.wavelet, b * NB_ELTS_V8, (a + 1) * NB_ELTS_V8, (int)dwt.win_l_x0, (int)dwt.win_l_x1,
                Math.Min(dwt.sn, dwt.dn - a), -dwt_delta);
            v8dwt_decode_step2(dwt.wavelet, a * NB_ELTS_V8, (b + 1) * NB_ELTS_V8, (int)dwt.win_h_x0, (int)dwt.win_h_x1,
                Math.Min(dwt.dn, dwt.sn - b), -dwt_gamma);
            v8dwt_decode_step2(dwt.wavelet, b * NB_ELTS_V8, (a + 1) * NB_ELTS_V8, (int)dwt.win_l_x0, (int)dwt.win_l_x1,
                Math.Min(dwt.sn, dwt.dn - a), -dwt_beta);
            v8dwt_decode_step2(dwt.wavelet, a * NB_ELTS_V8, (b + 1) * NB_ELTS_V8, (int)dwt.win_h_x0, (int)dwt.win_h_x1,
                Math.Min(dwt.dn, dwt.sn - b), -dwt_alpha);
        }

        /// <summary>
        /// Wavelet decode step 1
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_v8dwt_decode_step1
        /// </remarks>
        /// <param name="wavelet">The array with wavelet</param>
        /// <param name="start">Position of the wavelet in the array</param>
        /// <param name="end">Wavelet count</param>
        /// <param name="c">Some constant</param>
        private static void v8dwt_decode_step1(float[] f, int fw, int start, int end, float c)
        {
            // To be adapted if NB_ELTS_V8 changes
            for (int i = start; i < end; ++i)
            {
                f[fw + i * 2 * 8 + 0] = f[fw + i * 2 * 8 + 0] * c;
                f[fw + i * 2 * 8 + 1] = f[fw + i * 2 * 8 + 1] * c;
                f[fw + i * 2 * 8 + 2] = f[fw + i * 2 * 8 + 2] * c;
                f[fw + i * 2 * 8 + 3] = f[fw + i * 2 * 8 + 3] * c;
                f[fw + i * 2 * 8 + 4] = f[fw + i * 2 * 8 + 4] * c;
                f[fw + i * 2 * 8 + 5] = f[fw + i * 2 * 8 + 5] * c;
                f[fw + i * 2 * 8 + 6] = f[fw + i * 2 * 8 + 6] * c;
                f[fw + i * 2 * 8 + 7] = f[fw + i * 2 * 8 + 7] * c;
            }
        }

        //2.5 - opj_v8dwt_encode_step1
        private static void v8dwt_encode_step1(int[] f, int fw, uint end, float cst)
        {
            var fwi = new IntOrFloat();
            for (int i = 0; i < end; ++i)
            {
                for (int c = 0; c < NB_ELTS_V8; c++)
                {
                    fwi.I = f[fw + i * 2 * NB_ELTS_V8 + c];
                    fwi.F *= cst;
                    f[fw + i * 2 * NB_ELTS_V8 + c] = fwi.I;
                }
            }
        }

        //2.5 - opj_v8dwt_encode_step2
        private static void v8dwt_encode_step2(int[] f, int fl, int fw, uint end, uint m, float cst)
        {
            uint imax = Math.Min(end, m);
            var fli = new IntOrFloat();
            var fwi = new IntOrFloat();
            //Snip SSE code

            if (imax > 0)
            {
                for(int c = 0; c < NB_ELTS_V8; c++)
                {
                    fli.I = f[fl +  0 * NB_ELTS_V8 + c];
                    fwi.I = f[fw +  0 * NB_ELTS_V8 + c];
#if TEST_MATH_MODE
                    fli.F = (float)(fli.F + fwi.F) * cst;
#else
                    fli.F = (fli.F + fwi.F) * cst;
#endif
                    fwi.I = f[fw + -1 * NB_ELTS_V8 + c];
                    fwi.F += fli.F;
                    f[fw + -1 * NB_ELTS_V8 + c] = fwi.I;
                }
                fw += 2 * NB_ELTS_V8;
                for(int i = 1; i < imax; i++)
                {
                    for (int c = 0; c < NB_ELTS_V8; c++)
                    {
                        fli.I = f[fw + -2 * NB_ELTS_V8 + c];
                        fwi.I = f[fw +  0 * NB_ELTS_V8 + c];
#if TEST_MATH_MODE
                        fli.F = (float)(fli.F + fwi.F) * cst;
#else
                        fli.F = (fli.F + fwi.F) * cst;
#endif
                        fwi.I = f[fw + -1 * NB_ELTS_V8 + c];
                        fwi.F += fli.F;
                        f[fw + -1 * NB_ELTS_V8 + c] = fwi.I;
                    }
                    fw += 2 * NB_ELTS_V8;
                }
            }
            if (m < end)
            {
                Debug.Assert(m + 1 == end);
                for (int c = 0; c < NB_ELTS_V8; c++)
                {
                    fwi.I = f[fw + -2 * NB_ELTS_V8 + c];
#if TEST_MATH_MODE
                    fli.F = (float)(2 * fwi.F) * cst;
#else
                    fli.F = (2 * fwi.F) * cst;
#endif
                    fwi.I = f[fw + -1 * NB_ELTS_V8 + c];
                    fwi.F += fli.F;
                    f[fw + -1 * NB_ELTS_V8 + c] = fwi.I;
                }
            }
        }

        //2.5 - opj_v8dwt_decode_step2
        private static void v8dwt_decode_step2(float[] f, int fl, int fw, int start, int end, int m, float c)
        {
            int imax = Math.Min(end, m);
            if (start > 0)
            {
                fw += 2 * NB_ELTS_V8 * start;
                fl = fw - 2 * NB_ELTS_V8;
            }
            /* To be adapted if NB_ELTS_V8 changes */
            for (int i = start; i < imax; ++i)
            {
                //if (i == 5)
                //{
                //    IntOrFloat faa;
                //    faa.I = 0;

                //    faa.F = f[fw - 7];
                //    faa.F = f[fl + 1];
                //    faa.F = f[fw + 1];
                //    faa.F = c;

                //    float prob_f = (float)(f[fw - 7] + (float)((float)(f[fl + 1] + f[fw + 1]) * (float)c));
                //    faa.F = prob_f;

                //    i = i;
                //}

#if TEST_MATH_MODE
                //C# We have a problem. AFAICT, C# likes to do double precision math
                //   Liberal use of (float) seems to fix the issue. 
                f[fw - 8] = f[fw - 8] + (float)((float)(f[fl + 0] + f[fw + 0]) * (float)c);
                f[fw - 7] = f[fw - 7] + (float)((float)(f[fl + 1] + f[fw + 1]) * (float)c);
                f[fw - 6] = f[fw - 6] + (float)((float)(f[fl + 2] + f[fw + 2]) * (float)c);
                f[fw - 5] = f[fw - 5] + (float)((float)(f[fl + 3] + f[fw + 3]) * (float)c);
                f[fw - 4] = f[fw - 4] + (float)((float)(f[fl + 4] + f[fw + 4]) * (float)c);
                f[fw - 3] = f[fw - 3] + (float)((float)(f[fl + 5] + f[fw + 5]) * (float)c);
                f[fw - 2] = f[fw - 2] + (float)((float)(f[fl + 6] + f[fw + 6]) * (float)c);
                f[fw - 1] = f[fw - 1] + (float)((float)(f[fl + 7] + f[fw + 7]) * (float)c); 
#else
                //It's not a bug to have greater precision, it's a problem for verifying
                //the result against original libary.
                f[fw - 8] = f[fw - 8] + ((f[fl + 0] + f[fw + 0]) * c);
                f[fw - 7] = f[fw - 7] + ((f[fl + 1] + f[fw + 1]) * c);
                f[fw - 6] = f[fw - 6] + ((f[fl + 2] + f[fw + 2]) * c);
                f[fw - 5] = f[fw - 5] + ((f[fl + 3] + f[fw + 3]) * c);
                f[fw - 4] = f[fw - 4] + ((f[fl + 4] + f[fw + 4]) * c);
                f[fw - 3] = f[fw - 3] + ((f[fl + 5] + f[fw + 5]) * c);
                f[fw - 2] = f[fw - 2] + ((f[fl + 6] + f[fw + 6]) * c);
                f[fw - 1] = f[fw - 1] + ((f[fl + 7] + f[fw + 7]) * c);
#endif
                fl = fw;
                fw += 2 * NB_ELTS_V8;
            }
            if (m < end)
            {
                Debug.Assert(m + 1 == end);
                c += c;
#if TEST_MATH_MODE
                f[fw - 8] = f[fw - 8] + (float)(f[fl + 0] * (float)c);
                f[fw - 7] = f[fw - 7] + (float)(f[fl + 1] * (float)c);
                f[fw - 6] = f[fw - 6] + (float)(f[fl + 2] * (float)c);
                f[fw - 5] = f[fw - 5] + (float)(f[fl + 3] * (float)c);
                f[fw - 4] = f[fw - 4] + (float)(f[fl + 4] * (float)c);
                f[fw - 3] = f[fw - 3] + (float)(f[fl + 5] * (float)c);
                f[fw - 2] = f[fw - 2] + (float)(f[fl + 6] * (float)c);
                f[fw - 1] = f[fw - 1] + (float)(f[fl + 7] * (float)c);
#else
                f[fw - 8] = f[fw - 8] + (f[fl + 0] * c);
                f[fw - 7] = f[fw - 7] + (f[fl + 1] * c);
                f[fw - 6] = f[fw - 6] + (f[fl + 2] * c);
                f[fw - 5] = f[fw - 5] + (f[fl + 3] * c);
                f[fw - 4] = f[fw - 4] + (f[fl + 4] * c);
                f[fw - 3] = f[fw - 3] + (f[fl + 5] * c);
                f[fw - 2] = f[fw - 2] + (f[fl + 6] * c);
                f[fw - 1] = f[fw - 1] + (f[fl + 7] * c);
#endif
            }
        }

        //2.5
        static void EncodeStep1Combined(int[] f, int fw, uint iters_c1, uint iters_c2, float c1, float c2)
        {
            IntOrFloat fw1 = new IntOrFloat();
            uint i = 0;
            uint iters_common = Math.Min(iters_c1, iters_c2);
            Debug.Assert(Math.Abs((int)iters_c1 - (int)iters_c2) <= 1);
            for(; i + 3 < iters_common; i += 4)
            {
                fw1.I = f[fw + 0];
                fw1.F *= c1;
                f[fw + 0] = fw1.I;

                fw1.I = f[fw + 1];
                fw1.F *= c2;
                f[fw + 1] = fw1.I;

                fw1.I = f[fw + 2];
                fw1.F *= c1;
                f[fw + 2] = fw1.I;

                fw1.I = f[fw + 3];
                fw1.F *= c2;
                f[fw + 3] = fw1.I;

                fw1.I = f[fw + 4];
                fw1.F *= c1;
                f[fw + 4] = fw1.I;

                fw1.I = f[fw + 5];
                fw1.F *= c2;
                f[fw + 5] = fw1.I;

                fw1.I = f[fw + 6];
                fw1.F *= c1;
                f[fw + 6] = fw1.I;

                fw1.I = f[fw + 7];
                fw1.F *= c2;
                f[fw + 7] = fw1.I;

                fw += 8;
            }
            for(; i < iters_common; i++)
            {
                fw1.I = f[fw + 0];
                fw1.F *= c1;
                f[fw + 0] = fw1.I;

                fw1.I = f[fw + 1];
                fw1.F *= c2;
                f[fw + 1] = fw1.I;

                fw += 2;
            }
            if (i < iters_c1)
            {
                fw1.I = f[fw + 0];
                fw1.F *= c1;
                f[fw + 0] = fw1.I;
            }
            else if (i < iters_c2)
            {
                fw1.I = f[fw + 1];
                fw1.F *= c2;
                f[fw + 1] = fw1.I;
            }
        }

        //2.5
        static void EncodeStep2(int[] f, int fl, int fw, uint end, uint m, float c)
        {
            IntOrFloat fw1 = new IntOrFloat(), fw2 = new IntOrFloat();
            uint imax = Math.Min(end, m);
            if (imax > 0)
            {
                //fw[-1] += (fl[0] + fw[0]) * c;
                fw1.I = f[fl + 0];
                fw2.I = f[fw + 0];
#if TEST_MATH_MODE
                fw2.F = (float)(fw1.F + fw2.F) * c;
#else
                fw2.F = (fw1.F + fw2.F) * c;
#endif
                fw1.I = f[fw - 1];
                fw1.F += fw2.F;
                f[fw - 1] = fw1.I;
                fw += 2;
                int i = 1;
                for(; i + 3 < imax; i += 4)
                {
                    //fw[-1] += (fw[-2] + fw[0]) * c;
                    fw1.I = f[fw - 2];
                    fw2.I = f[fw + 0];
#if TEST_MATH_MODE
                    fw2.F = (float)(fw1.F + fw2.F) * c;
#else
                    fw2.F = (fw1.F + fw2.F) * c;
#endif
                    fw1.I = f[fw - 1];
                    fw1.F += fw2.F;
                    f[fw - 1] = fw1.I;

                    //fw[1] += (fw[0] + fw[2]) * c;
                    fw1.I = f[fw + 0];
                    fw2.I = f[fw + 2];
#if TEST_MATH_MODE
                    fw2.F = (float)(fw1.F + fw2.F) * c;
#else
                    fw2.F = (fw1.F + fw2.F) * c;
#endif
                    fw1.I = f[fw + 1];
                    fw1.F += fw2.F;
                    f[fw + 1] = fw1.I;

                    //fw[3] += (fw[2] + fw[4]) * c;
                    fw1.I = f[fw + 2];
                    fw2.I = f[fw + 4];
#if TEST_MATH_MODE
                    fw2.F = (float)(fw1.F + fw2.F) * c;
#else
                    fw2.F = (fw1.F + fw2.F) * c;
#endif
                    fw1.I = f[fw + 3];
                    fw1.F += fw2.F;
                    f[fw + 3] = fw1.I;

                    //fw[5] += (fw[4] + fw[6]) * c;
                    fw1.I = f[fw + 4];
                    fw2.I = f[fw + 6];
#if TEST_MATH_MODE
                    fw2.F = (float)(fw1.F + fw2.F) * c;
#else
                    fw2.F = (fw1.F + fw2.F) * c;
#endif
                    fw1.I = f[fw + 5];
                    fw1.F += fw2.F;
                    f[fw + 5] = fw1.I;

                    fw += 8;
                }
                for(; i < imax; i++)
                {
                    //fw[-1] += (fw[-2] + fw[0]) * c
                    fw1.I = f[fw - 2];
                    fw2.I = f[fw + 0];
#if TEST_MATH_MODE
                    fw2.F = (float)(fw1.F + fw2.F) * c;
#else
                    fw2.F = (fw1.F + fw2.F) * c;
#endif
                    fw1.I = f[fw - 1];
                    fw1.F += fw2.F;
                    f[fw - 1] = fw1.I;

                    fw += 2;
                }
            }
            if (m < end)
            {
                Debug.Assert(m + 1 == end);
                //fw[-1] += (2 * fw[-2]) * c;
                fw2.I = f[fw - 2];
#if TEST_MATH_MODE
                fw2.F = (float)(2 * fw2.F) * c;
#else
                fw2.F = (2 * fw2.F) * c;
#endif
                fw1.I = f[fw - 1];
                fw1.F += fw2.F;
                f[fw - 1] = fw1.I;
            }
        }

        //2.5
        private static bool decode_tile(TcdTilecomp tilec, uint numres, bool disable_multi_threading)
        {
            dwt_local h = new dwt_local();
            dwt_local v = new dwt_local();

            TcdResolution[] tr_ar = tilec.resolutions;
            int tr_pos = 0;
            TcdResolution tr = tr_ar[tr_pos], tr_max = tr_ar[numres - 1];

            //Width of the resolution level computed
            uint rw = (uint)(tr.x1 - tr.x0);

            //Height of the resolution level computed
            uint rh = (uint)(tr.y1 - tr.y0);

            uint w = (uint)(tilec.resolutions[tilec.minimum_num_resolutions -
                                                               1].x1 -
                            tilec.resolutions[tilec.minimum_num_resolutions - 1].x0);

            ulong h_mem_size;
            int num_threads;

            if (numres == 1U)
            {
                return true;
            }
            ThreadPool.GetAvailableThreads(out num_threads, out _);
            num_threads = disable_multi_threading ? 1 : Math.Min(Environment.ProcessorCount, num_threads);

            h_mem_size = MaxResolution(tr_ar, (int)numres);
            /* overflow check */
            if (h_mem_size > (Constants.SIZE_MAX / PARALLEL_COLS_53 / sizeof(int)))
            {
                return false;
            }
            // We need PARALLEL_COLS_53 times the height of the array,
            // since for the vertical pass
            // we process PARALLEL_COLS_53 columns at a time
            h_mem_size *= PARALLEL_COLS_53;
            h.mem = new int[h_mem_size];
            v.mem = h.mem;

            using (var reset = new ManualResetEvent(false))
            {
                while (--numres != 0)
                {
                    int tiledp = 0;
                    uint j;

                    tr = tr_ar[++tr_pos];
                    h.sn = (int)rw;
                    v.sn = (int)rh;

                    rw = (uint)(tr.x1 - tr.x0);
                    rh = (uint)(tr.y1 - tr.y0);

                    h.dn = (int)(rw - (uint)h.sn);
                    h.cas = tr.x0 % 2;

                    if (num_threads <= 1 || rh <= 1)
                    {
                        for (j = 0; j < rh; ++j)
                        {
                            idwt53_h(h, tilec.data, tiledp + (int)(j * w));
                        }
                    }
                    else
                    {
                        int num_jobs = num_threads;

                        if (rh < num_jobs)
                        {
                            num_jobs = (int)rh;
                        }

                        uint step_j = (rh / (uint)num_jobs);
                        
                        reset.Reset();
                        //Alternativly, we can set this to num_jobs and remove the Interlocked.Increment
                        //and the Interlocked.Decrement after the for loop
                        int n_thread_workers = 1;

                        for (j = 0; j < num_jobs; j++)
                        {
                            var max_j = (j + 1U) * step_j; // this will overflow
                            if (j == (num_jobs - 1)) //So we clamp max_j
                                max_j = rh;
                            var job = new decode_h_job(
                                h.Clone(), rw, w, tilec.data, tiledp,
                                j * step_j, max_j
                            );

                            job.h.mem = new int[h_mem_size];

                            Interlocked.Increment(ref n_thread_workers);
                            ThreadPool.QueueUserWorkItem((x) =>
                            {
                                try { decode_h_func((decode_h_job)x); }
                                finally
                                {
                                    if (Interlocked.Decrement(ref n_thread_workers) == 0)
                                        reset.Set();
                                }
                            }, job);
                        }
                        if (Interlocked.Decrement(ref n_thread_workers) == 0)
                            reset.Set();
                        reset.WaitOne();
                    }

                    v.dn = (int)(rh - (uint)v.sn);
                    v.cas = tr.y0 % 2;
 
                    if (num_threads <= 1 || rw <= 1)
                    {
                        for (j = 0; j + PARALLEL_COLS_53 <= rw; j += PARALLEL_COLS_53)
                        {
                            idwt53_v(v, tilec.data, tiledp + (int)j, (int)w, (int)PARALLEL_COLS_53);
                        }
                        if (j < rw)
                            idwt53_v(v, tilec.data, tiledp + (int)j, (int)w, (int)(rw - j));
                    }
                    else
                    {
                        int num_jobs = num_threads;

                        if (rw < num_jobs)
                        {
                            num_jobs = (int)rw;
                        }

                        uint step_j = (rw / (uint)num_jobs);

                        reset.Reset();
                        int n_thread_workers = 1;

                        for (j = 0; j < num_jobs; j++)
                        {
                            var max_j = (j + 1U) * step_j; // this can overflow
                            if (j == (num_jobs - 1))
                                max_j = rw;
                            var job = new decode_v_job(
                                v.Clone(), rh, w, tilec.data, tiledp,
                                j * step_j, max_j
                            );
                            job.v.mem = new int[h_mem_size];

                            Interlocked.Increment(ref n_thread_workers);
                            ThreadPool.QueueUserWorkItem((x) =>
                            {
                                try { decode_v_func((decode_v_job)x); }
                                finally
                                {
                                    if (Interlocked.Decrement(ref n_thread_workers) == 0)
                                        reset.Set();
                                }
                            }, job);
                        }
                        if (Interlocked.Decrement(ref n_thread_workers) == 0)
                            reset.Set();
                        reset.WaitOne();
                    }
                }
            }
            return true;
        }

        //2.5
        static bool DecodePartialTile(TcdTilecomp tilec, uint numres)
        {
            SparseArrayInt32 sa;
            dwt_local h = new dwt_local();
            dwt_local v = new dwt_local();
            // This value matches the maximum left/right extension given in tables
            // F.2 and F.3 of the standard.
            const uint filter_width = 2U;

            TcdResolution[] tr_ar = tilec.resolutions;
            int tr_pos = 0;
            TcdResolution tr = tr_ar[tr_pos], tr_max = tr_ar[numres - 1];

            //Width of the resolution level computed
            uint rw = (uint)(tr.x1 - tr.x0);

            //Height of the resolution level computed
            uint rh = (uint)(tr.y1 - tr.y0);

            ulong h_mem_size;

            // Compute the intersection of the area of interest, expressed in tile coordinates
            // with the tile coordinates
            uint win_tcx0 = tilec.win_x0;
            uint win_tcy0 = tilec.win_y0;
            uint win_tcx1 = tilec.win_x1;
            uint win_tcy1 = tilec.win_y1;

            if (tr_max.x0 == tr_max.x1 || tr_max.y0 == tr_max.y1)
            {
                return true;
            }

            sa = SparseArrayInt32.Init(tilec, numres);
            if (sa == null)
                return false;

            if (numres == 1U)
            {
                bool ret = sa.read(tr_max.win_x0 - (uint)tr_max.x0,
                                   tr_max.win_y0 - (uint)tr_max.y0,
                                   tr_max.win_x1 - (uint)tr_max.x0,
                                   tr_max.win_y1 - (uint)tr_max.y0,
                                   tilec.data_win, 0,
                                   1, tr_max.win_x1 - tr_max.win_x0,
                                   true);
                Debug.Assert(ret);
                return true;
            }
            h_mem_size = MaxResolution(tr_ar, (int) numres);
            // overflow check
            // in vertical pass, we process 4 columns at a time
            if (h_mem_size > (Constants.SIZE_MAX / (4 * sizeof(int))))
            {
                return false;
            }
            h_mem_size *= 4;
            h.mem = new int[h_mem_size];
            v.mem = h.mem;

            for (uint resno = 1; resno < numres; resno++)
            {
                uint i, j;
                /* Window of interest subband-based coordinates */
                uint win_ll_x0, win_ll_y0, win_ll_x1, win_ll_y1;
                uint win_hl_x0, win_hl_x1;
                uint win_lh_y0, win_lh_y1;
                /* Window of interest tile-resolution-based coordinates */
                uint win_tr_x0, win_tr_x1, win_tr_y0, win_tr_y1;
                /* Tile-resolution subband-based coordinates */
                uint tr_ll_x0, tr_ll_y0, tr_hl_x0, tr_lh_y0;

                tr = tr_ar[++tr_pos];

                h.sn = (int)rw;
                v.sn = (int)rh;

                rw = (uint)(tr.x1 - tr.x0);
                rh = (uint)(tr.y1 - tr.y0);

                h.dn = (int)(rw - (uint)h.sn);
                h.cas = tr.x0 % 2;

                v.dn = (int)(rh - (uint)v.sn);
                v.cas = tr.y0 % 2;

                // Get the subband coordinates for the window of interest
                // LL band
                GetBandCoordinates(tilec, resno, 0,
                                   win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                                   out win_ll_x0, out win_ll_y0,
                                   out win_ll_x1, out win_ll_y1);

                // HL band
                GetBandCoordinates(tilec, resno, 1,
                                   win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                                   out win_hl_x0, out _, out win_hl_x1, out _);

                /* LH band */
                GetBandCoordinates(tilec, resno, 2,
                                   win_tcx0, win_tcy0, win_tcx1, win_tcy1,
                                   out _, out win_lh_y0, out _, out win_lh_y1);

                /* Beware: band index for non-LL0 resolution are 0=HL, 1=LH and 2=HH */
                tr_ll_x0 = (uint)tr.bands[1].x0;
                tr_ll_y0 = (uint)tr.bands[0].y0;
                tr_hl_x0 = (uint)tr.bands[0].x0;
                tr_lh_y0 = (uint)tr.bands[1].y0;

                /* Subtract the origin of the bands for this tile, to the subwindow */
                /* of interest band coordinates, so as to get them relative to the */
                /* tile */
                win_ll_x0 = MyMath.uint_subs(win_ll_x0, tr_ll_x0);
                win_ll_y0 = MyMath.uint_subs(win_ll_y0, tr_ll_y0);
                win_ll_x1 = MyMath.uint_subs(win_ll_x1, tr_ll_x0);
                win_ll_y1 = MyMath.uint_subs(win_ll_y1, tr_ll_y0);
                win_hl_x0 = MyMath.uint_subs(win_hl_x0, tr_hl_x0);
                win_hl_x1 = MyMath.uint_subs(win_hl_x1, tr_hl_x0);
                win_lh_y0 = MyMath.uint_subs(win_lh_y0, tr_lh_y0);
                win_lh_y1 = MyMath.uint_subs(win_lh_y1, tr_lh_y0);

                SegmentGrow(filter_width, (uint)h.sn, ref win_ll_x0, ref win_ll_x1);
                SegmentGrow(filter_width, (uint)h.dn, ref win_hl_x0, ref win_hl_x1);

                SegmentGrow(filter_width, (uint)v.sn, ref win_ll_y0, ref win_ll_y1);
                SegmentGrow(filter_width, (uint)v.dn, ref win_lh_y0, ref win_lh_y1);

                /* Compute the tile-resolution-based coordinates for the window of interest */
                if (h.cas == 0)
                {
                    win_tr_x0 = Math.Min(2 * win_ll_x0, 2 * win_hl_x0 + 1);
                    win_tr_x1 = Math.Min(Math.Max(2 * win_ll_x1, 2 * win_hl_x1 + 1), rw);
                }
                else
                {
                    win_tr_x0 = Math.Min(2 * win_hl_x0, 2 * win_ll_x0 + 1);
                    win_tr_x1 = Math.Min(Math.Max(2 * win_hl_x1, 2 * win_ll_x1 + 1), rw);
                }

                if (v.cas == 0)
                {
                    win_tr_y0 = Math.Min(2 * win_ll_y0, 2 * win_lh_y0 + 1);
                    win_tr_y1 = Math.Min(Math.Max(2 * win_ll_y1, 2 * win_lh_y1 + 1), rh);
                }
                else
                {
                    win_tr_y0 = Math.Min(2 * win_lh_y0, 2 * win_ll_y0 + 1);
                    win_tr_y1 = Math.Min(Math.Max(2 * win_lh_y1, 2 * win_ll_y1 + 1), rh);
                }

                for (j = 0; j < rh; ++j)
                {
                    if ((j >= win_ll_y0 && j < win_ll_y1) ||
                            (j >= win_lh_y0 + (uint)v.sn && j < win_lh_y1 + (uint)v.sn))
                    {

                        // Avoids dwt.c:1584:44 (in opj_dwt_decode_partial_1): runtime error:
                        // signed integer overflow: -1094795586 + -1094795586 cannot be represented in type 'int'
                        // on opj_decompress -i  ../../openjpeg/MAPA.jp2 -o out.tif -d 0,0,256,256
                        // This is less extreme than memsetting the whole buffer to 0
                        // although we could potentially do better with better handling of edge conditions
                        if (win_tr_x1 >= 1 && win_tr_x1 < rw)
                        {
                            h.mem[win_tr_x1 - 1] = 0;
                        }
                        if (win_tr_x1 < rw)
                        {
                            h.mem[win_tr_x1] = 0;
                        }

                        interleave_partial_h(h.mem,
                                           h.cas,
                                           sa,
                                           j,
                                           (uint)h.sn,
                                           win_ll_x0,
                                           win_ll_x1,
                                           win_hl_x0,
                                           win_hl_x1);
                        decode_partial_1(h.mem, h.dn, h.sn, h.cas,
                                         (int)win_ll_x0,
                                         (int)win_ll_x1,
                                         (int)win_hl_x0,
                                         (int)win_hl_x1);
                        if (!sa.write(win_tr_x0, j,
                                      win_tr_x1, j + 1,
                                      h.mem, (int)win_tr_x0,
                                      1, 0, true))
                        {
                            return false;
                        }
                    }
                }

                for (i = win_tr_x0; i < win_tr_x1;)
                {
                    uint nb_cols = Math.Min(4U, win_tr_x1 - i);
                    interleave_partial_v(v.mem,
                                         v.cas,
                                         sa,
                                         i,
                                         nb_cols,
                                         (uint)v.sn,
                                         win_ll_y0,
                                         win_ll_y1,
                                         win_lh_y0,
                                         win_lh_y1);
                    decode_partial_1_parallel(v.mem, nb_cols, v.dn, v.sn, v.cas,
                                              (int)win_ll_y0,
                                              (int)win_ll_y1,
                                              (int)win_lh_y0,
                                              (int)win_lh_y1);
                    if (!sa.write(i, win_tr_y0,
                                  i + nb_cols, win_tr_y1,
                                  v.mem, 4 * (int)win_tr_y0,
                                  1, 4, true))
                    {
                        return false;
                    }

                    i += nb_cols;
                }
            }

            {
                bool ret = sa.read(
                               tr_max.win_x0 - (uint)tr_max.x0,
                               tr_max.win_y0 - (uint)tr_max.y0,
                               tr_max.win_x1 - (uint)tr_max.x0,
                               tr_max.win_y1 - (uint)tr_max.y0,
                               tilec.data_win, 0,
                               1, tr_max.win_x1 - tr_max.win_x0,
                               true);
                Debug.Assert(ret);
            }

            return true;
        }

        //2.5 - opj_dwt_interleave_partial_h
        private static void interleave_partial_h(
            int[] dest,
            int cas,
            SparseArrayInt32 sa,
            uint sa_line,
            uint sn,
            uint win_l_x0,
            uint win_l_x1,
            uint win_h_x0,
            uint win_h_x1)
        {
            bool ret;
            ret = sa.read(win_l_x0, sa_line,
                          win_l_x1, sa_line + 1,
                          dest, cas + 2 * (int)win_l_x0,
                          2, 0, true);
            Debug.Assert(ret);
            ret = sa.read(sn + win_h_x0, sa_line,
                          sn + win_h_x1, sa_line + 1,
                          dest, 1 - cas + 2 * (int)win_h_x0,
                          2, 0, true);
            Debug.Assert(ret);
        }

        //2.5 - opj_dwt_interleave_partial_v
        private static void interleave_partial_v(
            int[] dest,
            int cas,
            SparseArrayInt32 sa,
            uint sa_col,
            uint nb_cols,
            uint sn,
            uint win_l_y0,
            uint win_l_y1,
            uint win_h_y0,
            uint win_h_y1)
        {
            bool ret;
            ret = sa.read(sa_col, win_l_y0,
                          sa_col + nb_cols, win_l_y1,
                          dest, cas * 4 + 2 * 4 * (int)win_l_y0,
                          1, 2 * 4, true);
            Debug.Assert(ret);
            ret = sa.read(sa_col, sn + win_h_y0,
                          sa_col + nb_cols, sn + win_h_y1,
                          dest, (1 - cas) * 4 + 2 * 4 * (int)win_h_y0,
                          1, 2 * 4, true);
            Debug.Assert(ret);
        }

        /// <remarks>
        /// 2.5 - opj_dwt_decode_partial_1
        /// 
        /// OPJ_S(i) => a[(i)*2]
        /// OPJ_D(i) => a[(1+(i)*2)]
        /// OPJ_S_(i) => ((i)<0?OPJ_S(0):((i)>=sn?OPJ_S(sn-1):OPJ_S(i)))
        /// OPJ_D_(i) => ((i)<0?OPJ_D(0):((i)>=dn?OPJ_D(dn-1):OPJ_D(i)))
        /// OPJ_SS_(i) => ((i)<0?OPJ_S(0):((i)>=dn?OPJ_S(dn-1):OPJ_S(i)))
        /// OPJ_DD_(i) => ((i)<0?OPJ_D(0):((i)>=sn?OPJ_D(sn-1):OPJ_D(i)))
        /// 
        /// Substituted:
        /// OPJ_D_(i) => ((i) < 0 ? a[1] : ((i) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i) * 2)]))
        /// OPJ_S_(i) => ((i)<0?a[0]:((i)>=sn?a[(sn-1)*2]:a[(i)*2]))
        /// OPJ_SS_(i) => ((i)<0?a[0]:((i)>=dn?a[(dn-1)*2]:a[(i)*2]))
        /// OPJ_DD_(i) => ((i)<0?a[1]:((i)>=sn?a[(1+(sn-1)*2)]:a[(1+(i)*2)]))
        /// </remarks>
        private static void decode_partial_1(int[] a, int dn, int sn,
                                             int cas,
                                             int win_l_x0,
                                             int win_l_x1,
                                             int win_h_x0,
                                             int win_h_x1)
        {
            int i;

            if (cas == 0)
            {
                if ((dn > 0) || (sn > 1))
                { 
                    i = win_l_x0;
                    if (i < win_l_x1)
                    {
                        int i_max;

                        /* Left-most case */
                        a[i * 2] -= (((i - 1) < 0 ? a[(1 + (0) * 2)] : ((i - 1) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i - 1) * 2)]))
                                   + ((i) < 0 ? a[(1 + (0) * 2)] : ((i) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i) * 2)]))
                                   + 2) >> 2;
                        i++;

                        i_max = win_l_x1;
                        if (i_max > dn)
                        {
                            i_max = dn;
                        }
                        for (; i < i_max; i++)
                        {
                            /* No bound checking */
                            a[i * 2] -= (a[1 + (i - 1) * 2] + a[1 + i * 2] + 2) >> 2;
                        }
                        for (; i < win_l_x1; i++)
                        {
                            /* Right-most case */
                            a[i * 2] -= (((i - 1) < 0 ? a[(1 + (0) * 2)] : ((i - 1) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i - 1) * 2)]))
                                       + ((i) < 0 ? a[(1 + (0) * 2)] : ((i) >= dn ? a[(1 + (dn - 1) * 2)] : a[(1 + (i) * 2)]))
                                       + 2) >> 2;
                        }
                    }

                    i = win_h_x0;
                    if (i < win_h_x1)
                    {
                        int i_max = win_h_x1;
                        if (i_max >= sn)
                        {
                            i_max = sn - 1;
                        }
                        for (; i < i_max; i++)
                        {
                            /* No bound checking */
                            a[1 + i * 2] += (a[(i) * 2] + a[(i + 1) * 2]) >> 1;
                        }
                        for (; i < win_h_x1; i++)
                        {
                            /* Right-most case */
                            a[1 + i * 2] += (((i) < 0 ? a[0] : ((i) >= sn ? a[(sn - 1) * 2] : a[(i) * 2]))
                                           + ((i + 1) < 0 ? a[0] : ((i + 1) >= sn ? a[(sn - 1) * 2] : a[(i + 1) * 2]))
                                           ) >> 1;
                        }
                    }
                }
            }
            else
            {
                if (sn == 0 && dn == 1)
                {        /* NEW :  CASE ONE ELEMENT */
                    a[0] /= 2;
                }
                else
                {
                    for (i = win_l_x0; i < win_l_x1; i++)
                    {
                        a[1 + i * 2] = MyMath.int_sub_no_overflow(
                                              a[1 + i * 2],
                                              MyMath.int_add_no_overflow(
                                                  MyMath.int_add_no_overflow(
                                                      ((i) < 0 ? a[0] : ((i) >= dn ? a[(dn - 1) * 2] : a[(i) * 2])),
                                                      ((i + 1) < 0 ? a[0] : ((i + 1) >= dn ? a[(dn - 1) * 2] : a[(i + 1) * 2]))),
                                                  2) >> 2);
                    }
                    for (i = win_h_x0; i < win_h_x1; i++)
                    {
                        a[i * 2] = MyMath.int_add_no_overflow(
                                          a[i * 2],
                                          MyMath.int_add_no_overflow(
                                              ((i) < 0 ? a[1] : ((i) >= sn ? a[(1 + (sn - 1) * 2)] : a[(1 + (i) * 2)])),
                                              ((i - 1) < 0 ? a[1] : ((i - 1) >= sn ? a[(1 + (sn - 1) * 2)] : a[(1 + (i - 1) * 2)])))
                                          >> 1);
                    }
                }
            }
        }

        /// <remarks>
        /// 2.5
        /// 
        /// Todo: Make this more readable. Look at the OpenJpeg 2.1 C# impl. 
        ///       where the code is much more readable (Decode_1)
        /// 
        /// OPJ_S_off(i,off) => a[(uint)(i)*2*4+off]
        /// OPJ_D_off(i,off) => a[(1+(uint)(i)*2)*4+off]
        /// OPJ_S__off(i,off) => ((i)<0?OPJ_S_off(0,off):((i)>=sn?OPJ_S_off(sn-1,off):OPJ_S_off(i,off)))
        /// OPJ_D__off(i,off) => ((i)<0?OPJ_D_off(0,off):((i)>=dn?OPJ_D_off(dn-1,off):OPJ_D_off(i,off)))
        /// OPJ_SS__off(i,off) => ((i)<0?OPJ_S_off(0,off):((i)>=dn?OPJ_S_off(dn-1,off):OPJ_S_off(i,off)))
        /// OPJ_DD__off(i,off) => ((i)<0?OPJ_D_off(0,off):((i)>=sn?OPJ_D_off(sn-1,off):OPJ_D_off(i,off)))
        /// 
        /// Substituted:
        /// OPJ_S__off(i,off) => ((i)<0?a[off]:((i)>=sn?a[(uint)(sn-1)*2*4+off]:a[(uint)(i)*2*4+off]))
        /// OPJ_D__off(i,off) => ((i)<0?a[1+off]:((i)>=dn?a[(1+(uint)(dn-1)*2)*4+off]:a[(1+(uint)(i)*2)*4+off]))
        /// OPJ_SS__off(i,off) => ((i)<0?a[off]:((i)>=dn?a[(uint)(dn-1)*2*4+off]:a[(uint)(i)*2*4+off]))
        /// OPJ_DD__off(i,off) => ((i)<0?a[1+off]:((i)>=sn?a[(1+(uint)(sn-1)*2)*4+off]:a[(1+(uint)(i)*2)*4+off])
        /// </remarks>
        private static void decode_partial_1_parallel(
                                                    int[] a,
                                                    uint nb_cols,
                                                    int dn, int sn,
                                                    int cas,
                                                    int win_l_x0,
                                                    int win_l_x1,
                                                    int win_h_x0,
                                                    int win_h_x1)
        {
            int i;
            uint off;

            if (cas == 0)
            {
                if ((dn > 0) || (sn > 1))
                {
                    i = win_l_x0;
                    if (i < win_l_x1)
                    {
                        int i_max;

                        /* Left-most case */
                        for (off = 0; off < 4; off++)
                        {
                            a[(uint)(i) * 2 * 4 + off] -= (
                                ((i - 1) < 0 ? a[(1) * 4 + off] : ((i - 1) >= dn ? a[(1 + (uint)(dn - 1) * 2) * 4 + off] : a[(1 + (uint)(i - 1) * 2) * 4 + off])) 
                              + ((i) < 0 ? a[(1) * 4 + off] : ((i) >= dn ? a[(1 + (uint)(dn - 1) * 2) * 4 + off] : a[(1 + (uint)(i) * 2) * 4 + off])) 
                              + 2
                             ) >> 2;
                        }
                        i++;

                        i_max = win_l_x1;
                        if (i_max > dn)
                        {
                            i_max = dn;
                        }

                        //Snip SSE2 code. 

                        for (; i < i_max; i++)
                        {
                            /* No bound checking */
                            for (off = 0; off < 4; off++)
                            {
                                a[(uint)(i) * 2 * 4 + off] -= (
                                    a[(1 + (uint)(i - 1) * 2) * 4 + off] 
                                  + a[(1 + (uint)(i) * 2) * 4 + off] 
                                  + 2
                                ) >> 2;
                            }
                        }
                        for (; i < win_l_x1; i++)
                        {
                            /* Right-most case */
                            for (off = 0; off < 4; off++)
                            {
                                a[(uint)(i) * 2 * 4 + off] -= (
                                    ((i - 1) < 0 ? a[(1) * 4 + off] : ((i - 1) >= dn ? a[(1 + (uint)(dn - 1) * 2) * 4 + off] : a[(1 + (uint)(i - 1) * 2) * 4 + off])) 
                                  + ((i) < 0 ? a[(1) * 4 + off] : ((i) >= dn ? a[(1 + (uint)(dn - 1) * 2) * 4 + off] : a[(1 + (uint)(i) * 2) * 4 + off])) 
                                  + 2
                                ) >> 2;
                            }
                        }
                    }

                    i = win_h_x0;
                    if (i < win_h_x1)
                    {
                        int i_max = win_h_x1;
                        if (i_max >= sn)
                        {
                            i_max = sn - 1;
                        }

                        //Snip SSE2 code. 

                        for (; i < i_max; i++)
                        {
                            /* No bound checking */
                            for (off = 0; off < 4; off++)
                            {
                                a[(1 + (uint)(i) * 2) * 4 + off] += (
                                    a[(uint)(i) * 2 * 4 + off]
                                  + a[(uint)(i + 1) * 2 * 4 + off]
                                ) >> 1;
                            }
                        }
                        for (; i < win_h_x1; i++)
                        {
                            /* Right-most case */
                            for (off = 0; off < 4; off++)
                            {
                                a[(1 + (uint)(i) * 2) * 4 + off] += (
                                    ((i) < 0 ? a[off] : ((i) >= sn ? a[(uint)(sn - 1) * 2 * 4 + off] : a[(uint)(i) * 2 * 4 + off])) 
                                  + ((i + 1) < 0 ? a[off] : ((i + 1) >= sn ? a[(uint)(sn - 1) * 2 * 4 + off] : a[(uint)(i + 1) * 2 * 4 + off]))
                                ) >> 1;
                            }
                        }
                    }
                }
            }
            else
            {
                if (sn == 0 && dn == 1)
                {        /* NEW :  CASE ONE ELEMENT */
                    for (off = 0; off < 4; off++)
                        a[off] /= 2;
                }
                else
                {
                    for (i = win_l_x0; i < win_l_x1; i++)
                    {
                        for (off = 0; off < 4; off++)
                           a[(1 + (uint)(i) * 2) * 4 + off] = 
                                MyMath.int_sub_no_overflow(
                                            a[(1 + (uint)(i) * 2) * 4 + off],
                                            MyMath.int_add_no_overflow(
                                                MyMath.int_add_no_overflow(
                                                    ((i) < 0 ? a[(uint)(0) * 2 * 4 + off] : ((i) >= dn ? a[(uint)(dn - 1) * 2 * 4 + off] : a[(uint)(i) * 2 * 4 + off])), 
                                                    ((i + 1) < 0 ? a[(uint)(0) * 2 * 4 + off] : ((i + 1) >= dn ? a[(uint)(dn - 1) * 2 * 4 + off] : a[(uint)(i + 1) * 2 * 4 + off]))), 
                                                2) 
                                            >> 2);
                    }
                    for (i = win_h_x0; i < win_h_x1; i++)
                    {
                        for (off = 0; off < 4; off++)
                            a[(uint)(i) * 2 * 4 + off] = 
                                MyMath.int_add_no_overflow(
                                          a[(uint)(i) * 2 * 4 + off],
                                          MyMath.int_add_no_overflow(
                                              ((i) < 0 ? a[(1 + (uint)(0) * 2) * 4 + off] : ((i) >= sn ? a[(1 + (uint)(sn - 1) * 2) * 4 + off] : a[(1 + (uint)(i) * 2) * 4 + off])), 
                                              ((i - 1) < 0 ? a[(1 + (uint)(0) * 2) * 4 + off] : ((i - 1) >= sn ? a[(1 + (uint)(sn - 1) * 2) * 4 + off] : a[(1 + (uint)(i - 1) * 2) * 4 + off]))) 
                                          >> 1);
                    }
                }
            }
        }

        static void v8dwt_interleave_h(v4dwt_local dwt, int[] a_ar, int a, int width, uint remaining_height)
        {
            float[] bi_ar = dwt.wavelet;
            int bi = dwt.cas * NB_ELTS_V8; //C# NB_ELTS_V8 is because we're indexing into dwt.wavelet
            int x0 = (int)dwt.win_l_x0;
            int x1 = (int)dwt.win_l_x1;

            //C# impl. note:
            //Workaround for C's ability to treat float and
            //int as raw data.
            IntOrFloat fi = new IntOrFloat();

            for (int k = 0; k < 2; ++k)
            {
                //C# impl note. (a & 0x0f) and (bi & 0x0f) checks if a "pointer" is aligned.
                //Now, on C# the base pointer is always aligned, so this test should still work
                if (remaining_height >= NB_ELTS_V8 && (a & 0x0f) == 0 && (bi & 0x0f) == 0)
                {
                    // Fast code path
                    // C# - I've not done any benchmarking.
                    for (int i = x0; i < x1; ++i)
                    {
                        int j = a + i, dst = bi + i * 2 * NB_ELTS_V8;
                        fi.I = a_ar[j];
                        bi_ar[dst + 0] = fi.F;
                        j += width; fi.I = a_ar[j];
                        bi_ar[dst + 1] = fi.F;
                        j += width; fi.I = a_ar[j];
                        bi_ar[dst + 2] = fi.F;
                        j += width; fi.I = a_ar[j];
                        bi_ar[dst + 3] = fi.F;
                        j += width; fi.I = a_ar[j];
                        bi_ar[dst + 4] = fi.F;
                        j += width; fi.I = a_ar[j];
                        bi_ar[dst + 5] = fi.F;
                        j += width; fi.I = a_ar[j];
                        bi_ar[dst + 6] = fi.F;
                        j += width; fi.I = a_ar[j];
                        bi_ar[dst + 7] = fi.F;
                    }
                }
                else
                {
                    // Slow code path
                    for (int i = x0; i < x1; ++i)
                    {
                        int j = a + i, dst = bi + i * 2 * NB_ELTS_V8;
                        fi.I = a_ar[j];
                        bi_ar[dst + 0] = fi.F;
                        j += width;
                        if (remaining_height == 1) continue;
                        fi.I = a_ar[j];
                        bi_ar[dst + 1] = fi.F;
                        j += width;
                        if (remaining_height == 2) continue;
                        fi.I = a_ar[j];
                        bi_ar[dst + 2] = fi.F;
                        j += width;
                        if (remaining_height == 3) continue;
                        fi.I = a_ar[j];
                        bi_ar[dst + 3] = fi.F;
                        j += width;
                        if (remaining_height == 4) continue;
                        fi.I = a_ar[j];
                        bi_ar[dst + 4] = fi.F;
                        j += width;
                        if (remaining_height == 5) continue;
                        fi.I = a_ar[j];
                        bi_ar[dst + 5] = fi.F;
                        j += width;
                        if (remaining_height == 6) continue;
                        fi.I = a_ar[j];
                        bi_ar[dst + 6] = fi.F;
                        j += width;
                        if (remaining_height == 7) continue;
                        fi.I = a_ar[j];
                        bi_ar[dst + 7] = fi.F;
                    }
                }

                bi = (1 - dwt.cas) * NB_ELTS_V8;
                a += dwt.sn;
                x0 = (int) dwt.win_h_x0;
                x1 = (int) dwt.win_h_x1;
            }
        }

        //2.5
        static void v8dwt_interleave_v(v4dwt_local dwt, int[] a_ar, int a, int width, int n_elts_read)
        {
            //C# Impl. Note that bi_ar is not a float[], but a wavelet array where each entery has 8 floating points.
            //         This while the a array is a plain float[]. I.e how they are to be indexed differers
            float[] bi_ar = dwt.wavelet;
            int bi = dwt.cas;

            for (int i = (int)dwt.win_l_x0; i < dwt.win_l_x1; ++i)
                Buffer.BlockCopy(a_ar, (a + i * width) * sizeof(float), bi_ar, (bi + i * 2) * NB_ELTS_V8 * sizeof(float), n_elts_read * sizeof(float));
            a += dwt.sn * width;
            bi = (1 - dwt.cas);
            for (int i = (int)dwt.win_h_x0; i < dwt.win_h_x1; ++i)
                Buffer.BlockCopy(a_ar, (a + i * width) * sizeof(float), bi_ar, (bi + i * 2) * NB_ELTS_V8 * sizeof(float), n_elts_read * sizeof(float));
        }

        //2.5
        static void v8dwt_interleave_partial_h(v4dwt_local dwt, SparseArrayInt32 sa, uint sa_line, uint remaining_height)
        {
            for (uint i = 0; i < remaining_height; i++)
            {
                bool ret;
                ret = sa.read(dwt.win_l_x0, sa_line + i,
                              dwt.win_l_x1, sa_line + i + 1,
                              /* Nasty cast from float* to int32* */
                              dwt.wavelet, (dwt.cas + 2 * (int)dwt.win_l_x0) * NB_ELTS_V8 + (int)i, //C# dwt.wavelet index must be multiplied with NB_ELTS_V8
                              2 * NB_ELTS_V8, 0, true);
                Debug.Assert(ret);
                ret = sa.read((uint)dwt.sn + dwt.win_h_x0, sa_line + i,
                              (uint)dwt.sn + dwt.win_h_x1, sa_line + i + 1,
                              /* Nasty cast from float* to int32* */
                              dwt.wavelet, (1 - dwt.cas + 2 * (int)dwt.win_h_x0) * NB_ELTS_V8 + (int)i,
                              2 * NB_ELTS_V8, 0, true);
                Debug.Assert(ret);
            }
        }

        //2.5
        static void v8dwt_interleave_partial_v(v4dwt_local dwt, SparseArrayInt32 sa, uint sa_col, uint nb_elts_read)
        {
            bool ret;
            ret = sa.read(sa_col, dwt.win_l_x0,
                          sa_col + nb_elts_read, dwt.win_l_x1,
                          /* Nasty cast from float* to int32* */
                          dwt.wavelet, (dwt.cas + 2 * (int)dwt.win_l_x0) * NB_ELTS_V8,
                          1, 2 * NB_ELTS_V8, true);
            Debug.Assert(ret);
            ret = sa.read(sa_col, (uint)dwt.sn + dwt.win_h_x0,
                          sa_col + nb_elts_read, (uint)dwt.sn + dwt.win_h_x1,
                          /* Nasty cast from float* to int32* */
                          dwt.wavelet, (1 - dwt.cas + 2 * (int)dwt.win_h_x0) * NB_ELTS_V8,
                          1, 2 * NB_ELTS_V8, true);
            Debug.Assert(ret);
        }

        /// <summary>
        /// Forward lazy transform (horizontal).
        /// </summary>
        /// <remarks>2.5 - opj_dwt_deinterleave_h</remarks>
        static void Deinterleave_h(int[] a, int[] b, int b_pt, int dn, int sn, int cas)
        {
            int dest = b_pt;
            int src = 0 + cas;

            for (int i = 0; i < sn; i++)
            {
                b[dest++] = a[src];
                src += 2;
            }

            dest = b_pt + sn;
            src = 0 + 1 - cas;

            for (int i = 0; i < dn; i++)
            {
                b[dest++] = a[src];
                src += 2;
            }
        }

        /// <summary>
        /// Inverse 5-3 wavelet transform in 2-D
        /// </summary>
        /// <param name="tilec">Tile component information (current tile)</param>
        /// <param name="numres">Number of resolution levels to decode</param>
        /// <remarks>
        /// 2.5 - opj_dwt_decode
        /// </remarks>
        internal static bool Decode(TileCoder tcd, TcdTilecomp tilec, uint numres)
        {
            if (tcd.WholeTileDecoding)
                return decode_tile(tilec, numres, tcd.DisableMultiThreading);
            else
                return DecodePartialTile(tilec, numres);
        }

        /// <summary>
        /// Forward 9-7 wavelet transform in 1-D.
        /// </summary>
        /// <remarks>2.5 - opj_dwt_encode_1_real</remarks>
        static void Encode_1_real(int[] w, int dn, int sn, int cas)
        {
            int a, b;
            if (cas == 0)
            {
                a = 0;
                b = 1;
            }
            else
            {
                a = 1;
                b = 0;
            }
            EncodeStep2(w, a, b + 1, (uint)dn, (uint)Math.Min(dn, sn - b), dwt_alpha);
            EncodeStep2(w, b, a + 1, (uint)sn, (uint)Math.Min(sn, dn - a), dwt_beta);
            EncodeStep2(w, a, b + 1, (uint)dn, (uint)Math.Min(dn, sn - b), dwt_gamma);
            EncodeStep2(w, b, a + 1, (uint)sn, (uint)Math.Min(sn, dn - a), dwt_delta);

            if (a == 0)
            {
                EncodeStep1Combined(w, 0, (uint)sn, (uint)dn, invK, K);
            }
            else
            {
                EncodeStep1Combined(w, 0, (uint)dn, (uint)sn, K, invK);
            }
        }

        //2.5 - opj_dwt_encode_and_deinterleave_v_real
        static void EncodeAndDeinterleaveV_Real(int[] arr, int a_pt, int[] tmp, uint height, bool even, uint stride_width, uint cols)
        {
            if (height == 1)
                return;

            int sn = (int)((height + (even ? 1u : 0u)) >> 1);
            int dn = (int)(height - sn);
            int a, b;

            FetchColsVerticalPass(arr, a_pt, tmp, height, (int)stride_width, cols);

            if (even)
            {
                a = 0;
                b = 1;
            }
            else
            {
                a = 1;
                b = 0;
            }
            v8dwt_encode_step2(tmp, a * NB_ELTS_V8, (b + 1) * NB_ELTS_V8, (uint)dn, (uint)Math.Min(dn, sn - b), dwt_alpha);
            v8dwt_encode_step2(tmp, b * NB_ELTS_V8, (a + 1) * NB_ELTS_V8, (uint)sn, (uint)Math.Min(sn, dn - a), dwt_beta);
            v8dwt_encode_step2(tmp, a * NB_ELTS_V8, (b + 1) * NB_ELTS_V8, (uint)dn, (uint)Math.Min(dn, sn - b), dwt_gamma);
            v8dwt_encode_step2(tmp, b * NB_ELTS_V8, (a + 1) * NB_ELTS_V8, (uint)sn, (uint)Math.Min(sn, dn - a), dwt_delta);
            v8dwt_encode_step1(tmp, b * NB_ELTS_V8, (uint)dn, K);
            v8dwt_encode_step1(tmp, a * NB_ELTS_V8, (uint)sn, invK);

            if (cols == NB_ELTS_V8)
            {
                DeinterleaveV_Cols(tmp, arr, a_pt, dn, sn, (int)stride_width, even ? 0 : 1, NB_ELTS_V8);
            }
            else
            {
                DeinterleaveV_Cols(tmp, arr, a_pt, dn, sn, (int)stride_width, even ? 0 : 1, cols);
            }
        }

        /// <summary>
        /// Forward 5-3 transform, for the vertical pass, processing cols columns
        /// where cols <= NB_ELTS_V8
        /// </summary>
        /// <remarks>2.5 - opj_dwt_encode_and_deinterleave_v</remarks>
        static void EncodeAndDeinterleaveV(int[] a, int a_pt, int[] tmp, uint height, bool even, uint stride_width, uint cols)
        {
            uint sn = (height + (even ? 1u : 0u)) >> 1;
            uint dn = height - sn;

            FetchColsVerticalPass(a, a_pt, tmp, height, (int)stride_width, cols);

            //C# Snip SSE2 code

            if (even)
            {
                uint c;
                if (height > 1)
                {
                    uint i;
                    for (i = 0; i + 1 < sn; i++)
                    {
                        for (c = 0; c < 8; c++)
                        {
                            tmp[((1 + (i) * 2)) * 8 + c] -= (tmp[(i) * 2 * 8 + c] + tmp[(i + 1) * 2 * 8 + c]) >> 1;
                        }
                    }
                    if (((height) % 2) == 0)
                    {
                        for (c = 0; c < 8; c++)
                        {
                            tmp[((1 + (i) * 2)) * 8 + c] -= tmp[(i) * 2 * 8 + c];
                        }
                    }
                    for (c = 0; c < 8; c++)
                    {
                        tmp[(0) * 2 * 8 + c] += (tmp[((1 + (0) * 2)) * 8 + c] + tmp[((1 + (0) * 2)) * 8 + c] + 2) >> 2;
                    }
                    for (i = 1; i < dn; i++)
                    {
                        for (c = 0; c < 8; c++)
                        {
                            tmp[(i) * 2 * 8 + c] += (tmp[((1 + (i - 1) * 2)) * 8 + c] + tmp[((1 + (i) * 2)) * 8 + c] + 2) >> 2;
                        }
                    }
                    if (((height) % 2) == 1)
                    {
                        for (c = 0; c < 8; c++)
                        {
                            tmp[(i) * 2 * 8 + c] += (tmp[((1 + (i - 1) * 2)) * 8 + c] + tmp[((1 + (i - 1) * 2)) * 8 + c] + 2) >> 2;
                        }
                    }
                }
            }
            else
            {
                uint c;
                if (height == 1)
                {
                    for (c = 0; c < 8; c++)
                    {
                        tmp[(0) * 2 * 8 + c] *= 2;
                    }
                }
                else
                {
                    uint i;
                    for (c = 0; c < 8; c++)
                    {
                        tmp[(0) * 2 * 8 + c] -= tmp[((1 + (0) * 2)) * 8 + c];
                    }
                    for (i = 1; i < sn; i++)
                    {
                        for (c = 0; c < 8; c++)
                        {
                            tmp[(i) * 2 * 8 + c] -= (tmp[((1 + (i) * 2)) * 8 + c] + tmp[((1 + (i - 1) * 2)) * 8 + c]) >> 1;
                        }
                    }
                    if (((height) % 2) == 1)
                    {
                        for (c = 0; c < 8; c++)
                        {
                            tmp[(i) * 2 * 8 + c] -= tmp[((1 + (i - 1) * 2)) * 8 + c];
                        }
                    }
                    for (i = 0; i + 1 < dn; i++)
                    {
                        for (c = 0; c < 8; c++)
                        {
                            tmp[((1 + (i) * 2)) * 8 + c] += (tmp[(i) * 2 * 8 + c] + tmp[(i + 1) * 2 * 8 + c] + 2) >> 2;
                        }
                    }
                    if (((height) % 2) == 0)
                    {
                        for (c = 0; c < 8; c++)
                        {
                            tmp[((1 + (i) * 2)) * 8 + c] += (tmp[(i) * 2 * 8 + c] + tmp[(i) * 2 * 8 + c] + 2) >> 2;
                        }
                    }
                }
            }

            if (cols == NB_ELTS_V8)
            {
                DeinterleaveV_Cols(tmp, a, a_pt, (int)dn, (int)sn,
                                   (int)stride_width, even ? 0 : 1, NB_ELTS_V8);
            }
            else
            {
                DeinterleaveV_Cols(tmp, a, a_pt, (int)dn, (int)sn,
                                   (int)stride_width, even ? 0 : 1, cols);
            }
        }

        /// <summary>
        /// Deinterleave result of forward transform, where cols <= NB_ELTS_V8
        /// and src contains NB_ELTS_V8 consecutive values for up to NB_ELTS_V8
        /// columns
        /// </summary>
        /// <remarks>2.5 - opj_dwt_deinterleave_v_cols</remarks>
        static void DeinterleaveV_Cols(int[] src, int[] dst, int dst_pt, int dn, int sn, int stride_width, int cas, uint cols)
        {
            int org_dest = dst_pt;
            int src_pt = cas * NB_ELTS_V8;
            int i = sn;
            for (int k = 0; k < 2; k++)
            {
                while(i-- != 0)
                {
                    if (cols == NB_ELTS_V8)
                    {
                        Buffer.BlockCopy(src, src_pt * sizeof(int), dst, dst_pt * sizeof(int), NB_ELTS_V8 * sizeof(int));
                    }
                    else
                    {
                        int c = 0;
                        switch(cols)
                        {
                            case 7:
                                dst[dst_pt + c] = src[src_pt + c];
                                c++;
                                goto case 6;
                            case 6:
                                dst[dst_pt + c] = src[src_pt + c];
                                c++;
                                goto case 5;
                            case 5:
                                dst[dst_pt + c] = src[src_pt + c];
                                c++;
                                goto case 4;
                            case 4:
                                dst[dst_pt + c] = src[src_pt + c];
                                c++;
                                goto case 3;
                            case 3:
                                dst[dst_pt + c] = src[src_pt + c];
                                c++;
                                goto case 2;
                            case 2:
                                dst[dst_pt + c] = src[src_pt + c];
                                c++;
                                goto default;
                            default:
                                dst[dst_pt + c] = src[src_pt + c];
                                break;
                        }
                    }
                    dst_pt += stride_width;
                    src_pt += 2 * NB_ELTS_V8;
                }

                dst_pt = org_dest + sn * stride_width;
                src_pt = (1 - cas) * NB_ELTS_V8;
                i = dn;
            }
        }

        /// <summary>
        /// Process one line for the horizontal pass of the 9x7 forward transform
        /// </summary>
        /// <remarks>2.5 - opj_dwt_encode_and_deinterleave_h_one_row_real</remarks>
        static void EncodeAndDeinterleaveH_OneRowReal(int[] row, int row_pt, int[] tmp, uint width, bool even)
        {
            if (width == 1)
                return;
            int sn = (int)((width + (even ? 1 : 0)) >> 1);
            int dn = (int)(width - (uint)sn);
            Buffer.BlockCopy(row, row_pt * sizeof(int), tmp, 0, (int)(width * sizeof(int)));
            Encode_1_real(tmp, dn, sn, even ? 0 : 1);
            Deinterleave_h(tmp, row, row_pt, dn, sn, even ? 0 : 1);
        }

        /// <summary>
        /// Process one line for the horizontal pass of the 5x3 forward transform
        /// </summary>
        /// <remarks>2.5 - opj_dwt_encode_and_deinterleave_h_one_row</remarks>
        static void EncodeAndDeinterleaveH_OneRow(int[] row, int row_pt, int[] tmp, uint width, bool even)
        {
            int sn = (int)((width + (even ? 1 : 0)) >> 1);
            int dn = (int)(width - (uint)sn);

            if (even)
            {
                if (width > 1)
                {
                    int i;
                    for (i = 0; i < sn - 1; i++)
                    {
                        tmp[sn + i] = row[row_pt + 2 * i + 1] - ((row[row_pt + (i) * 2] + row[row_pt + (i + 1) * 2]) >> 1);
                    }
                    if ((width % 2) == 0)
                    {
                        tmp[sn + i] = row[row_pt + 2 * i + 1] - row[row_pt + (i) * 2];
                    }
                    row[row_pt + 0] += (tmp[sn] + tmp[sn] + 2) >> 2;
                    for (i = 1; i < dn; i++)
                    {
                        row[row_pt + i] = row[row_pt + 2 * i] + ((tmp[sn + (i - 1)] + tmp[sn + i] + 2) >> 2);
                    }
                    if ((width % 2) == 1)
                    {
                        row[row_pt + i] = row[row_pt + 2 * i] + ((tmp[sn + (i - 1)] + tmp[sn + (i - 1)] + 2) >> 2);
                    }
                    Buffer.BlockCopy(tmp, sn * sizeof(int), row, (row_pt + sn) * sizeof(int), dn * sizeof(int));
                }
            }
            else
            {
                if (width == 1)
                {
                    row[row_pt] *= 2;
                }
                else
                {
                    int i;
                    tmp[sn + 0] = row[row_pt + 0] - row[row_pt + 1];
                    for (i = 1; i < sn; i++)
                    {
                        tmp[sn + i] = row[row_pt + 2 * i] - ((row[row_pt + 2 * i + 1] + row[row_pt + 2 * (i - 1) + 1]) >> 1);
                    }
                    if ((width % 2) == 1)
                    {
                        tmp[sn + i] = row[row_pt + 2 * i] - row[row_pt + 2 * (i - 1) + 1];
                    }

                    for (i = 0; i < dn - 1; i++)
                    {
                        row[row_pt + i] = row[row_pt + 2 * i + 1] + ((tmp[sn + i] + tmp[sn + i + 1] + 2) >> 2);
                    }
                    if ((width % 2) == 0)
                    {
                        row[row_pt + i] = row[row_pt + 2 * i + 1] + ((tmp[sn + i] + tmp[sn + i] + 2) >> 2);
                    }
                    Buffer.BlockCopy(tmp, sn * sizeof(int), row, (row_pt + sn) * sizeof(int), dn * sizeof(int));
                }
            }
        }

        /** Fetch up to cols <= NB_ELTS_V8 for each line, and put them in tmpOut */
        /* that has a NB_ELTS_V8 interleave factor. */
        //2.5
        static void FetchColsVerticalPass(int[] a, int array, int[] tmp, uint height, int stride_width, uint cols)
        {
            if (cols == NB_ELTS_V8) {
                for (int k = 0; k < height; ++k) {
                    Buffer.BlockCopy(a, (array + k * stride_width) * sizeof(int), tmp, (NB_ELTS_V8 * k) * sizeof(int), NB_ELTS_V8 * sizeof(int));
                }
            } else {
                for (int k = 0; k < height; ++k) {
                    int c = 0;
                    for (; c < cols; c++) {
                        tmp[NB_ELTS_V8 * k + c] = a[array + c + k * stride_width];
                    }
                    for (; c < NB_ELTS_V8; c++) {
                        tmp[NB_ELTS_V8 * k + c] = 0;
                    }
                }
            }
        }

        //2.5
        private static void decode_h_func(decode_h_job job)
        {
            for(int j = (int)job.min_j; j < job.max_j; j++)
            {
                idwt53_h(job.h, job.tiled, job.tiledp + j * (int)job.w);
            }
        }

        //2.5
        private static void dwt97_decode_h_func(dwt97_decode_h_job job)
        {
            var w = (int)job.w;
            var aj_ar = job.aj;
            var aj = job.ajp;
            IntOrFloat fi = new IntOrFloat();

            for (int j = 0; j + NB_ELTS_V8 <= job.nb_rows; j += NB_ELTS_V8)
            {
                v8dwt_interleave_h(job.h, aj_ar, aj, w, NB_ELTS_V8);
                v8dwt_decode(job.h);

                // To be adapted if NB_ELTS_V8 changes
                for (int k = 0; k < job.rw; k++)
                {
                    //C# note: Org. impl stores the wavlet as a struct with four
                    //floating points. Here it's stored as a continious array. 
                    int k_wavelet = k * NB_ELTS_V8;

                    fi.F = job.h.wavelet[k_wavelet + 0];
                    aj_ar[aj + k] = fi.I;
                    fi.F = job.h.wavelet[k_wavelet + 1];
                    aj_ar[aj + k + w] = fi.I;
                    fi.F = job.h.wavelet[k_wavelet + 2];
                    aj_ar[aj + k + w * 2] = fi.I;
                    fi.F = job.h.wavelet[k_wavelet + 3];
                    aj_ar[aj + k + w * 3] = fi.I;
                }
                for (int k = 0; k < job.rw; k++)
                {
                    int k_wavelet = k * NB_ELTS_V8;

                    fi.F = job.h.wavelet[k_wavelet + 4];
                    aj_ar[aj + k + w * 4] = fi.I;
                    fi.F = job.h.wavelet[k_wavelet + 5];
                    aj_ar[aj + k + w * 5] = fi.I;
                    fi.F = job.h.wavelet[k_wavelet + 6];
                    aj_ar[aj + k + w * 6] = fi.I;
                    fi.F = job.h.wavelet[k_wavelet + 7];
                    aj_ar[aj + k + w * 7] = fi.I;
                }

                aj += w * NB_ELTS_V8;
            }
        }

        //2.5
        private static void dwt97_decode_v_func(dwt97_decode_v_job job)
        {
            var aj_ar = job.aj;
            var aj = job.ajp;

            for (uint j = 0; j + NB_ELTS_V8 <= job.nb_columns; j += NB_ELTS_V8)
            {
                v8dwt_interleave_v(job.v, aj_ar, aj, (int)job.w, NB_ELTS_V8);
                v8dwt_decode(job.v);
                for (int k = 0; k < job.rh; ++k)
                    Buffer.BlockCopy(job.v.wavelet, k * NB_ELTS_V8 * sizeof(float), aj_ar, (aj + (k * (int)job.w)) * sizeof(float), NB_ELTS_V8 * sizeof(float));

                aj += NB_ELTS_V8;
            }
        }

        /// <summary>
        /// Inverse 5-3 wavelet transform in 1-D for one row.
        /// </summary>
        /// <remarks>
        /// 2.5
        /// Performs interleave, inverse wavelet transform and copy back to buffer
        /// </remarks>
        private static void idwt53_h(dwt_local dwt, int[] tiled, int tiledp)
        {
#if STANDARD_SLOW_VERSION
            Interleave_h(dwt, tiledp, tiled);
            Decode_1(dwt);
            Buffer.BlockCopy(dwt.mem, 0, tiled, tiledp * sizeof(int), (dwt.sn + dwt.dn) * sizeof(int));
#else
            int sn = dwt.sn;
            int len = sn + dwt.dn;
            if (dwt.cas == 0)
            { /* Left-most sample is on even coordinate */
                if (len > 1)
                {
                    idwt53_h_cas0(dwt.mem, sn, len, tiled, tiledp);
                }
                else
                {
                    /* Unmodified value */
                }
            }
            else
            { /* Left-most sample is on odd coordinate */
                if (len == 1)
                {
                    tiled[tiledp] /= 2;
                }
                else if (len == 2)
                {
                    var o = dwt.mem;
                    int in_even = tiledp + sn;
                    int in_odd = tiledp;
                    o[1] = tiled[in_odd] - ((tiled[in_even] + 1) >> 1);
                    o[0] = tiled[in_even] + o[1];
                    Buffer.BlockCopy(dwt.mem, 0, tiled, tiledp * sizeof(int), len * sizeof(int));
                }
                else if (len > 2)
                {
                    opj_idwt53_h_cas1(dwt.mem, sn, len, tiled, tiledp);
                }
            }
#endif
        }

#if STANDARD_SLOW_VERSION
        /// <summary>
        /// Inverse 5-3 wavelet transform in 1-D.
        /// </summary>
        //2.1
        static void Decode_1(dwt_local v)
        {
            Decode_1_(v.mem, v.dn, v.sn, v.cas);
        }

        /// <summary>
        /// Inverse 5-3 wavelet transform in 1-D.
        /// </summary>
        //2.1
        static void Decode_1_(int[] a, int dn, int sn, int cas)
        {
            if (cas == 0)
            {
                if (dn > 0 || sn > 1)
                {
                    for (int i = 0; i < sn; i++)
                    {
                        //C# impl. of the macro: D_(i - 1) + D_(i)
                        int D1 = (i - 1) < 0 ? 0 : (i - 1) >= dn ? (dn - 1) : (i - 1);
                        int D2 = i < 0 ? 0 : i >= dn ? (dn - 1) : i;

                        //S(i) -= (D_(i - 1) + D_(i) + 2) >> 2;
                        a[i * 2] -= (a[1 + D1 * 2] + a[1 + D2 * 2] + 2) >> 2;
                    }
                    for (int i = 0; i < dn; i++)
                    {
                        //C# impl. of the macro: S_(i) + S_(i + 1)
                        int S1 = i < 0 ? 0 : i >= sn ? (sn - 1) : i;
                        int S2 = (i + 1) < 0 ? 0 : (i + 1) >= sn ? (sn - 1) : (i + 1);

                        //D(i) += (S_(i) + S_(i + 1)) >> 1;
                        a[1 + i * 2] += (a[S1 * 2] + a[S2 * 2]) >> 1;
                    }
                    //#define S(i) a[(i)*2]
                    //#define D(i) a[(1+(i)*2)]
                     //#define S_(i) ((i)<0?S(0):((i)>=sn?S(sn-1):S(i)))
                    //#define SS_(i) ((i)<0?S(0):((i)>=dn?S(dn-1):S(i)))
                     //#define D_(i) ((i)<0?D(0):((i)>=dn?D(dn-1):D(i)))
                    //#define DD_(i) ((i)<0?D(0):((i)>=sn?D(sn-1):D(i)))
                }
            }
            else
            {
                if (sn == 0 && dn == 1)
                    a[0] /= 2;
                else
                {
                    for (int i = 0; i < sn; i++)
                    {
                        //C# impl. of the macro: SS_(i) + SS_(i + 1)
                        int SS1 = i < 0 ? 0 : i >= dn ? (dn - 1) : i;
                        int SS2 = (i + 1) < 0 ? 0 : (i + 1) >= dn ? (dn - 1) : (i + 1);

                        //D(i) -= (SS_(i) + SS_(i + 1) + 2) >> 2;
                        a[1 + i * 2] -= (a[SS1 * 2] + a[SS2 * 2] + 2) >> 2;
                    }
                    for (int i = 0; i < dn; i++)
                    {
                        //C# impl. of the macro: DD_(i) + DD_(i - 1)
                        int DD1 = i < 0 ? 0 : i >= sn ? (sn - 1) : i;
                        int DD2 = (i - 1) < 0 ? 0 : (i - 1) >= sn ? (sn - 1) : (i - 1);

                        //S(i) += (DD_(i) + DD_(i - 1)) >> 1;
                        a[i * 2] += (a[1 + DD1 * 2] + a[1 + DD2 * 2]) >> 1;
                    }
                }
            }
        }

        
        /// <summary>
        /// Inverse lazy transform (vertical).
        /// </summary>
        //2.1
        static void Interleave_v(dwt_local v, int a, int[] a_ar, int x)
        {
            int ai = a;
            int bi = v.cas;
            int[] b_ar = v.mem;
            int i = v.sn;
            while (i-- != 0)
            {
                b_ar[bi] = a_ar[ai];
                bi += 2;
                ai += x;
            }
            ai = a + (v.sn * x);
            bi = 1 - v.cas;
            i = v.dn;
            while (i-- != 0)
            {
                b_ar[bi] = a_ar[ai];
                bi += 2;
                ai += x;
            }
        }
#else
        //2.5
        private static void idwt53_h_cas0(int[] tmp, int sn, int len, int[] tiled, int tiledp)
        {
            Debug.Assert(len > 1);

            int i, j;
            int in_even = tiledp;
            int in_odd = tiledp + sn;

#if TWO_PASS_VERSION
            /* For documentation purpose: performs lifting in two iterations, */
            /* but without explicit interleaving */

            /* Even */
            tmp[0] = tiled[in_even] - ((tiled[in_odd] + 1) >> 1);
            for (i = 2, j = 0; i <= len - 2; i += 2, j++)
            {
                tmp[i] = tiled[in_even + j + 1] - ((tiled[in_odd + j] + tiled[in_odd + j + 1] + 2) >> 2);
            }
            if ((len & 1) != 0)
            { /* if len is odd */
                tmp[len - 1] = tiled[in_even + (len - 1) / 2] - ((tiled[in_odd + (len - 2) / 2] + 1) >> 1);
            }

            /* Odd */
            for (i = 1, j = 0; i < len - 1; i += 2, j++)
            {
                tmp[i] = tiled[in_odd + j] + ((tmp[i - 1] + tmp[i + 1]) >> 1);
            }
            if ((len & 1) == 0)
            { /* if len is even */
                tmp[len - 1] = tiled[in_odd + (len - 1) / 2] + tmp[len - 2];
            }
#else
            // Improved version of the TWO_PASS_VERSION:
            // Performs lifting in one single iteration. Saves memory
            // accesses and explicit interleaving.
            int d1c, d1n, s1n, s0c, s0n;

            s1n = tiled[in_even];
            d1n = tiled[in_odd];
            s0n = s1n - ((d1n + 1) >> 1);

            for (i = 0, j = 1; i < (len - 3); i += 2, j++)
            {
                d1c = d1n;
                s0c = s0n;

                s1n = tiled[in_even + j];
                d1n = tiled[in_odd + j];

                s0n = s1n - ((d1c + d1n + 2) >> 2);

                tmp[i] = s0c;
                tmp[i + 1] = MyMath.int_add_no_overflow(d1c, MyMath.int_add_no_overflow(s0c,
                                                     s0n) >> 1);
            }

            tmp[i] = s0n;

            if ((len & 1) != 0)
            {
                tmp[len - 1] = tiled[in_even + (len - 1) / 2] - ((d1n + 1) >> 1);
                tmp[len - 2] = d1n + ((s0n + tmp[len - 1]) >> 1);
            }
            else
            {
                tmp[len - 1] = d1n + s0n;
            }
#endif
            Buffer.BlockCopy(tmp, 0, tiled, tiledp * sizeof(int), len * sizeof(int));
        }

        //2.5
        private static void opj_idwt53_h_cas1(int[] tmp, int sn, int len, int[] tiled, int tiledp)
        {
            Debug.Assert(len > 2);

            int i, j;
            int in_even = tiledp + sn;
            int in_odd = tiledp;

#if TWO_PASS_VERSION
            /* For documentation purpose: performs lifting in two iterations, */
            /* but without explicit interleaving */

            /* Odd */
            for (i = 1, j = 0; i < len - 1; i += 2, j++)
            {
                tmp[i] = tiled[in_odd + j] - ((tiled[in_even + j] + tiled[in_even + j + 1] + 2) >> 2);
            }
            if ((len & 1) == 0)
            {
                tmp[len - 1] = tiled[in_odd + len / 2 - 1] - ((tiled[in_even + len / 2 - 1] + 1) >> 1);
            }

            /* Even */
            tmp[0] = tiled[in_even] + tmp[1];
            for (i = 2, j = 1; i < len - 1; i += 2, j++)
            {
                tmp[i] = tiled[in_even + j] + ((tmp[i + 1] + tmp[i - 1]) >> 1);
            }
            if ((len & 1) != 0)
            {
                tmp[len - 1] = tiled[in_even + len / 2] + tmp[len - 2];
            }
#else
            int s1, s2, dc, dn;

            /* Improved version of the TWO_PASS_VERSION: */
            /* Performs lifting in one single iteration. Saves memory */
            /* accesses and explicit interleaving. */

            s1 = tiled[in_even + 1];
            dc = tiled[in_odd] - ((tiled[in_even] + s1 + 2) >> 2);
            tmp[0] = tiled[in_even] + dc;

            int end = len - 2 - ((len & 1) == 0 ? 1 : 0);
            for (i = 1, j = 1; i < end; i += 2, j++)
            {

                s2 = tiled[in_even + j + 1];

                dn = tiled[in_odd + j] - ((s1 + s2 + 2) >> 2);
                tmp[i] = dc;
                tmp[i + 1] = MyMath.int_add_no_overflow(s1, MyMath.int_add_no_overflow(dn, dc) >> 1);

                dc = dn;
                s1 = s2;
            }

            tmp[i] = dc;

            if ((len & 1) == 0)
            {
                dn = tiled[in_odd + len / 2 - 1] - ((s1 + 1) >> 1);
                tmp[len - 2] = s1 + ((dn + dc) >> 1);
                tmp[len - 1] = dn;
            }
            else
            {
                tmp[len - 1] = s1 + dc;
            }
#endif
            Buffer.BlockCopy(tmp, 0, tiled, tiledp * sizeof(int), len * sizeof(int));
        }

        //2.5
        private static void idwt3_v_cas0(int[] tmp, int sn, int len, int[] tiled, int tiledp_col, int stride)
        {
            int i, j;
            int d1c, d1n, s1n, s0c, s0n;

            Debug.Assert(len > 1);

            /* Performs lifting in one single iteration. Saves memory */
            /* accesses and explicit interleaving. */

            s1n = tiled[tiledp_col];
            d1n = tiled[tiledp_col + sn * stride];
            s0n = s1n - ((d1n + 1) >> 1);

            for (i = 0, j = 0; i < (len - 3); i += 2, j++)
            {
                d1c = d1n;
                s0c = s0n;

                s1n = tiled[tiledp_col + (j + 1) * stride];
                d1n = tiled[tiledp_col + (sn + j + 1) * stride];

                s0n = MyMath.int_sub_no_overflow(s1n,
                                  MyMath.int_add_no_overflow(MyMath.int_add_no_overflow(d1c, d1n), 2) >> 2);

                tmp[i] = s0c;
                tmp[i + 1] = MyMath.int_add_no_overflow(d1c, MyMath.int_add_no_overflow(s0c,
                                                     s0n) >> 1);
            }

            tmp[i] = s0n;

            if ((len & 1) != 0)
            {
                tmp[len - 1] =
                    tiled[tiledp_col + ((len - 1) / 2) * stride] -
                    ((d1n + 1) >> 1);
                tmp[len - 2] = d1n + ((s0n + tmp[len - 1]) >> 1);
            }
            else
            {
                tmp[len - 1] = d1n + s0n;
            }

            for (i = 0; i < len; ++i)
            {
                tiled[tiledp_col + i * stride] = tmp[i];
                //if (214928 == tiledp_col + i * stride)
                //{
                //    Debug.Write("Hello");
                //}
            }
        }

        //2.5
        private static void idwt3_v_cas1(int[] tmp, int sn, int len, int[] tiled, int tiledp_col, int stride)
        {
            int i, j;
            int s1, s2, dc, dn;
            int in_even = tiledp_col + sn * stride;
            int in_odd = tiledp_col;

            Debug.Assert(len > 2);

            // Performs lifting in one single iteration. Saves memory
            // accesses and explicit interleaving.

            s1 = tiled[in_even + stride];
            dc = tiled[in_odd] - ((tiled[in_even] + s1 + 2) >> 2);
            tmp[0] = tiled[in_even] + dc;
            int end = len - 2 - ((len & 1) == 0 ? 1 : 0);
            for (i = 1, j = 1; i < end; i += 2, j++)
            {

                s2 = tiled[in_even + (j + 1) * stride];

                dn = tiled[in_odd + j * stride] - ((s1 + s2 + 2) >> 2);
                tmp[i] = dc;
                tmp[i + 1] = s1 + ((dn + dc) >> 1);

                dc = dn;
                s1 = s2;
            }
            tmp[i] = dc;
            if ((len & 1) == 0)
            {
                dn = tiled[in_odd + (len / 2 - 1) * stride] - ((s1 + 1) >> 1);
                tmp[len - 2] = s1 + ((dn + dc) >> 1);
                tmp[len - 1] = dn;
            }
            else
            {
                tmp[len - 1] = s1 + dc;
            }

            for (i = 0; i < len; ++i)
            {
                tiled[tiledp_col + i * stride] = tmp[i];
            }
        }
#endif

        //2.5
        private static void decode_v_func(decode_v_job job)
        {
            int j;
            for (j = (int)job.min_j; j + PARALLEL_COLS_53 <= job.max_j; j += (int)PARALLEL_COLS_53)
            {
                idwt53_v(job.v, job.tiled, job.tiledp +j, (int)job.w, (int)PARALLEL_COLS_53);
            }
            if (j < job.max_j)
                idwt53_v(job.v, job.tiled, job.tiledp + j, (int)job.w, (int)(job.max_j - j));
        }

        //2.5
        private static void idwt53_v(dwt_local dwt, int[] tiled, int tiledp_col, int stride, int nb_cols)
        {
#if STANDARD_SLOW_VERSION
            for (int c = 0; c < nb_cols; c ++) {
                Interleave_v(dwt, tiledp_col + c, tiled, stride);
                Decode_1(dwt);
                for (int k = 0; k < dwt.sn + dwt.dn; ++k) {
                    tiled[tiledp_col + c + k * stride] = dwt.mem[k];
                }
            }
#else
            //var mem_copy = new int[dwt.mem.Length];
            //Array.Copy(dwt.mem, mem_copy, dwt.mem.Length);
            //var tiled_copy = new int[tiled.Length];
            //Array.Copy(tiled, tiled_copy, tiled_copy.Length);

            //for (int c = 0; c < nb_cols; c++)
            //{
            //    Interleave_v(dwt, tiledp_col + c, tiled, stride);
            //    Decode_1(dwt);
            //    for (int k = 0; k < dwt.sn + dwt.dn; ++k)
            //    {
            //        tiled[tiledp_col + c + k * stride] = dwt.mem[k];
            //    }
            //}

            //var correct_tiled = tiled;
            //tiled = tiled_copy;
            //dwt.mem = mem_copy;

            int sn = dwt.sn;
            int len = sn + dwt.dn;
            if (dwt.cas == 0)
            {
                //C# Snip SSE2

                if (len > 1)
                {
                    for (int c = 0; c < nb_cols; c++, tiledp_col++)
                    {
                        idwt3_v_cas0(dwt.mem, sn, len, tiled, tiledp_col, stride);
                    }

                    ////Check for correctnes
                    //for (int c = 0; c < correct_tiled.Length; c++)
                    //{
                    //    if (correct_tiled[c] != tiled[c])
                    //    {
                    //        Debug.Write("Nah");
                    //    }
                    //}
                    return;
                }
            }
            else
            {
                if (len == 1)
                {
                    for (int c = 0; c < nb_cols; c++, tiledp_col++)
                    {
                        tiled[tiledp_col] /= 2;
                    }
                    return;
                }

                if (len == 2)
                {
                    int[] o = dwt.mem;
                    for (int c = 0; c < nb_cols; c++, tiledp_col++)
                    {
                        int in_even = tiledp_col + sn * stride;
                        int in_odd = tiledp_col;

                        o[1] = tiled[in_odd] - ((tiled[in_even] + 1) >> 1);
                        o[0] = tiled[in_even] + o[1];

                        for (int i = 0; i < len; ++i)
                        {
                            tiled[tiledp_col + i * stride] = o[i] ;
                        }
                    }

                    return;
                }

                //C# Snip SSE2

                if (len > 2)
                {
                    for (int c = 0; c < nb_cols; c++, tiledp_col++)
                    {
                        idwt3_v_cas1(dwt.mem, sn, len, tiled, tiledp_col, stride);
                    }
                    return;
                }
            }
#endif
        }

        /// <summary>
        /// Get norm of 9-7 wavelet.
        /// </summary>
        /// <remarks>2.5 - opj_dwt_getnorm_real</remarks>
        internal static double Getnorm_real(uint level, uint orient)
        {
            if (orient == 0 && level >= 10)
                level = 9;
            else if (orient > 0 && level >= 9)
                level = 8;
            return dwt_norms_real[orient][level];
        }

        /// <summary>
        /// Get norm of 5-3 wavelet.
        /// </summary>
        /// <remarks>2.5 - opj_dwt_getnorm</remarks>
        internal static double Getnorm(uint level, uint orient)
        {
            //FIXME ! This is just a band-aid to avoid a buffer overflow
            if (orient == 0 && level >= 10)
                level = 9;
            else if (orient > 0 && level >= 9)
                level = 8;

            return dwt_norms[orient][level];
        }

        internal delegate int GetGainFunc(int orient);

        //2.5
        private static void GetBandCoordinates(TcdTilecomp tilec,
                uint resno,
                uint bandno,
                uint tcx0,
                uint tcy0,
                uint tcx1,
                uint tcy1,
                out uint tbx0,
                out uint tby0,
                out uint tbx1,
                out uint tby1)
        {
            // Compute number of decomposition for this band. See table F-1
            int nb = (resno == 0) ?
                      (int)tilec.numresolutions - 1 :
                      (int)(tilec.numresolutions - resno);
            /* Map above tile-based coordinates to sub-band-based coordinates per */
            /* equation B-15 of the standard */
            uint x0b = bandno & 1;
            uint y0b = bandno >> 1;
            //if (tbx0)
            {
                tbx0 = (nb == 0) ? tcx0 :
                       (tcx0 <= (1U << (int)(nb - 1)) * x0b) ? 0 :
                       MyMath.uint_ceildivpow2(tcx0 - (1U << (nb - 1)) * x0b, nb);
            }
            //if (tby0)
            {
                tby0 = (nb == 0) ? tcy0 :
                       (tcy0 <= (1U << (nb - 1)) * y0b) ? 0 :
                       MyMath.uint_ceildivpow2(tcy0 - (1U << (nb - 1)) * y0b, nb);
            }
            //if (tbx1)
            {
                tbx1 = (nb == 0) ? tcx1 :
                       (tcx1 <= (1U << (nb - 1)) * x0b) ? 0 :
                       MyMath.uint_ceildivpow2(tcx1 - (1U << (nb - 1)) * x0b, nb);
            }
            //if (tby1)
            {
                tby1 = (nb == 0) ? tcy1 :
                       (tcy1 <= (1U << (nb - 1)) * y0b) ? 0 :
                       MyMath.uint_ceildivpow2(tcy1 - (1U << (nb - 1)) * y0b, nb);
            }
        }

        //2.5 - opj_dwt_segment_grow
        private static void SegmentGrow(uint filter_width, uint max_size, ref uint start, ref uint end)
        {
            start = MyMath.uint_subs(start, filter_width);
            end = MyMath.uint_adds(end, filter_width);
            end = Math.Min(end, max_size);
        }

        delegate void DWG_action(dwt_local dwt);

        class dwt_local
        {
            internal int[] mem;

            /// <summary>
            /// Number of elements in high pass band
            /// </summary>
            internal int dn;

            /// <summary>
            /// Number of elements in low pass band
            /// </summary>
            internal int sn;

            /// <summary>
            /// 0 = start on even coord, 1 = start on odd coord
            /// </summary>
            internal int cas;

            public dwt_local Clone()
            {
                return (dwt_local)MemberwiseClone();
            }
        }

        class v4dwt_local
        {
            /// <summary>
            /// Each wavelet is 4 floating points.
            /// </summary>
            /// <remarks>
            ///C# note: Org. impl stores the wavlet as a struct with eight
            ///floating points. Here it's stored as a continious array. 
            ///
            /// This so to make it possible to use Buffer.BlockCopy to
            /// copy the values. 
            ///</remarks>
            internal float[] wavelet;

            /// <summary>
            /// Number of elements in high pass band
            /// </summary>
            internal int dn;

            /// <summary>
            /// Number of elements in low pass band
            /// </summary>
            internal int sn;

            /// <summary>
            ///  0 = start on even coord, 1 = start on odd coord
            /// </summary>
            internal int cas;

            /// <summary>
            /// Start coord in low pass band
            /// </summary>
            internal uint win_l_x0;

            /// <summary>
            /// End coord in low pass band
            /// </summary>
            internal uint win_l_x1;

            /// <summary>
            /// Start coord in high pass band
            /// </summary>
            internal uint win_h_x0;

            /// <summary>
            /// End coord in high pass band
            /// </summary>
            internal uint win_h_x1;

            public v4dwt_local Clone() { return (v4dwt_local)MemberwiseClone(); }
        }

        /// <summary>
        /// Used by the MT impl
        /// </summary>
        private class decode_h_job
        {
            public readonly dwt_local h;
            public readonly uint rw;
            public readonly uint w;
            public readonly int[] tiled;
            public readonly int tiledp;
            public readonly uint min_j;
            public readonly uint max_j;

            public decode_h_job(
                dwt_local h,
                uint rw, uint w,
                int[] tiled, int tiledp,
                uint min_j, uint max_j
            )
            {
                this.h = h;
                this.rw = rw;
                this.w = w;
                this.tiled = tiled;
                this.tiledp = tiledp;
                this.min_j = min_j;
                this.max_j = max_j;
            }
        }

        /// <summary>
        /// Used by the MT impl
        /// </summary>
        private class encode_v_job
        {
            public readonly dwt_local v;
            public readonly uint rh;
            public readonly uint w;
            public readonly int[] tiled;
            public readonly int tiledp;
            public readonly uint min_j;
            public readonly uint max_j;
            public readonly EncodeAndDeinterleaveVfunc encode_and_deinterleave_v;

            public encode_v_job(
                dwt_local v,
                uint rh, uint w,
                int[] tiled, int tiledp,
                uint min_j, uint max_j,
                EncodeAndDeinterleaveVfunc encode_and_deinterleave_v
            )
            {
                this.v = v;
                this.rh = rh;
                this.w = w;
                this.tiled = tiled;
                this.tiledp = tiledp;
                this.min_j = min_j;
                this.max_j = max_j;
                this.encode_and_deinterleave_v = encode_and_deinterleave_v;
            }
        }

        /// <summary>
        /// Used by the MT impl
        /// </summary>
        private class encode_h_job
        {
            public readonly dwt_local h;
            public readonly uint rw;
            public readonly uint w;
            public readonly int[] tiled;
            public readonly int tiledp;
            public readonly uint min_j;
            public readonly uint max_j;
            public readonly EncodeAndDeinterleaveH_OneRowfunc fn;

            public encode_h_job(
                dwt_local h,
                uint rw, uint w,
                int[] tiled, int tiledp,
                uint min_j, uint max_j,
                EncodeAndDeinterleaveH_OneRowfunc fn
            )
            {
                this.h = h;
                this.rw = rw;
                this.w = w;
                this.tiled = tiled;
                this.tiledp = tiledp;
                this.min_j = min_j;
                this.max_j = max_j;
                this.fn = fn;
            }
        }

        /// <summary>
        /// Used by the MT impl
        /// </summary>
        private class decode_v_job
        {
            public readonly dwt_local v;
            public readonly uint rh;
            public readonly uint w;
            public readonly int[] tiled;
            public readonly int tiledp;
            public readonly uint min_j;
            public readonly uint max_j;

            public decode_v_job(
                dwt_local v,
                uint rh, uint w,
                int[] tiled, int tiledp,
                uint min_j, uint max_j
            )
            {
                this.v = v;
                this.rh = rh;
                this.w = w;
                this.tiled = tiled;
                this.tiledp = tiledp;
                this.min_j = min_j;
                this.max_j = max_j;
            }
        }

        /// <summary>
        /// Used by the MT impl
        /// </summary>
        private class dwt97_decode_h_job
        {
            public readonly v4dwt_local h;
            public readonly uint rw;
            public readonly uint w;
            public readonly int[] aj;
            public readonly int ajp;
            public readonly uint nb_rows;

            public dwt97_decode_h_job(
                v4dwt_local h,
                uint rw, uint w,
                int[] aj, int ajp,
                uint nb_rows
            )
            {
                this.h = h;
                this.rw = rw;
                this.w = w;
                this.aj = aj;
                this.ajp = ajp;
                this.nb_rows = nb_rows;
            }
        }

        /// <summary>
        /// Used by the MT impl
        /// </summary>
        private class dwt97_decode_v_job
        {
            public readonly v4dwt_local v;
            public readonly uint rh;
            public readonly uint w;
            public readonly int[] aj;
            public readonly int ajp;
            public readonly uint nb_columns;

            public dwt97_decode_v_job(
                v4dwt_local v,
                uint rh, uint w,
                int[] aj, int ajp,
                uint nb_columns
            )
            {
                this.v = v;
                this.rh = rh;
                this.w = w;
                this.aj = aj;
                this.ajp = ajp;
                this.nb_columns = nb_columns;
            }
        }
    }
}
