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
using System.Diagnostics;
using System.Collections.Generic;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Tier-1 coding (coding of code-block coefficients)
    /// 
    /// This is the first step of a EBCOT algorithm*, developed
    /// by David Taubman.
    ///  
    /// Tier 1 handles source modelling* and *entropy coding".
    /// Wavelet subbands are partitioned into smaller code blocks,
    /// whith are themselves coded by bitplanes.
    /// 
    /// Each bitplane is coded sepperatly, as if it was a two
    /// colored image, with the most significant first.
    /// 
    /// Bitplanes are transformed into three chunks. These chunks
    /// are grouped into layers*, and can be trunctuated away from
    /// it's code block (by both by the encoder and decoder) to
    /// down adjust quality.
    /// 
    /// *"Embedded Block Coding with Optimised Truncation"
    /// *Entropy coding is a lossy form of encoding, though it
    ///  does not need to be lossy
    /// *Layers can/should represent quality. I.e. dropping all
    /// the chunks in a low quality layer should reduce the image
    /// quality by less than dropping those in a hight quality
    /// layer. 
    /// </summary>
    internal sealed class Tier1Coding
    {
        #region Variables and properties

        MQCoder _mqc;

        int[] _data;
        T1[] flags;
#if DEBUG
        public uint[] DEBUG_FLAGS
        {
            get
            {
                var f = new uint[flags.Length];
                //Buffer.BlockCopy(flags, 0, f, 0, f.Length * sizeof(int));
                for (int c = 0; c < f.Length; c++)
                    f[c] = (uint)flags[c];
                return f;
            }
        }
#endif

        uint _w;
        uint _h;
        int datasize
        {
            get { return _data != null ? _data.Length : 0; }
            set
            {
                if (value != datasize)
                    throw new NotImplementedException("Datasize different from _data.Length");
            }
        }
        int flagssize
        {
            get { return flags != null ? flags.Length : 0; }
            set
            {
                if (value != flagssize)
                    throw new NotImplementedException("Flagsize different from flags.Length");
            }
        }

        //TODO v.2.5: Remove
        int flags_stride;

        static readonly T1[] MOD = {
		    T1.SIG_S, T1.SIG_S|T1.SGN_S,
		    T1.SIG_E, T1.SIG_E|T1.SGN_E,
		    T1.SIG_W, T1.SIG_W|T1.SGN_W,
		    T1.SIG_N, T1.SIG_N|T1.SGN_N
	    };

        // The 3 variables below are only used by the decoder

        /// <summary>
        /// Set to TRUE in multithreaded context
        /// </summary>
        bool mustuse_cblkdatabuffer;

        /// <summary>
        /// Temporary buffer to concatenate all chunks of a codebock
        /// </summary>
        byte[] cblkdatabuffer;

        /// <summary>
        /// Maximum size available in cblkdatabuffer
        /// </summary>
        uint cblkdatabuffersize
        {
            get { return cblkdatabuffer != null ? (uint)cblkdatabuffer.Length : 0; }
            set
            {
                if (value != cblkdatabuffersize)
                    throw new NotImplementedException("cblkdatabuffersize != cblkdatabuffer.Length");
            }
        }

        ///// <summary>
        ///// Gets a byte from the flag array
        ///// </summary>
        //private byte GetFlag(int idx)
        //{
        //    return (byte)((uint)flags[idx >> 4] >> (8 * (idx & 3)));
        //}

        ///// <summary>
        ///// Sets a byte in the flag array
        ///// </summary>
        //private void SetFlag(int idx, byte value)
        //{
        //    int upper = (idx & 3) * 8;
        //    idx >>= 4;
        //    flags[idx] = (T1)((uint)flags[idx] & ~(0x000000FF << upper) | ((uint)value << upper));
        //}

        #endregion

        #region Init

        //2.5
        internal Tier1Coding()
        {
            _mqc = new MQCoder();
        }

        #endregion

        #region Encoder

        /// <summary>
        /// Generate jobs
        /// </summary>
        /// <param name="tile">The tile to encode</param>
        /// <param name="tcp">Tile coding parameters</param>
        /// <param name="mct_norms"></param>
        /// <param name="mct_numcomps">Number of components used for MCT</param>
        /// <remarks>
        /// 2.5 - opj_t1_encode_cblks
        /// 
        /// To simplify the MT impl, this function and the function it calls have been changed to iterators,
        /// which lets me put all the MT stuff in a single parent function.
        /// </remarks>
        internal static IEnumerable<object> EncodeCblks(
            TcdTile tile,
            TileCodingParams tcp,
            double[] mct_norms,
            uint mct_numcomps,
            SetRet set_ret
            )
        {
            tile.distotile = 0;

            for (uint compno = 0; compno < tile.numcomps; ++compno)
            {
                TcdTilecomp tilec = tile.comps[compno];
                TileCompParams tccp = tcp.tccps[compno];

                for (uint resno = 0; resno < tilec.numresolutions; ++resno)
                {
                    TcdResolution res = tilec.resolutions[resno];

                    for (uint bandno = 0; bandno < res.numbands; ++bandno)
                    {
                        TcdBand band = res.bands[bandno];

                        if (band.IsEmpty)
                            continue;

                        for (uint precno = 0; precno < res.pw * res.ph; ++precno)
                        {
                            TcdPrecinct prc = band.precincts[precno];

                            for (uint cblkno = 0; cblkno < prc.cw * prc.ch; ++cblkno)
                            {
                                TcdCblkEnc cblk = prc.enc[cblkno];

                                yield return new T1CBLKEncodeProcessingJob(
                                    compno,
                                    resno,
                                    cblk,
                                    tile,
                                    band,
                                    tilec,
                                    tccp,
                                    mct_norms,
                                    mct_numcomps,
                                    set_ret
                                );
                            }
                        }
                    }
                }
            }
        }

        /// <remarks>2.5.1 - opj_t1_encode_cblk</remarks>
        internal double EncodeCblk(
            TcdCblkEnc cblk, 
            uint orient, 
            uint compno, 
            uint level, 
            uint qmfbid, 
            double stepsize, 
            CCP_CBLKSTY cblksty, 
            uint numcomps, 
            double[] mct_norms,
            uint mct_numcomps)
        {
            double cumwmsedec = 0.0;

            var mqc = this._mqc;
            
            uint passno;
            int nmsedec = 0;
            T1_TYPE type = T1_TYPE.MQ;

            mqc.lut_ctxno_zc_orient = (int)(orient << 9);

            int max = 0;
            for (uint j = 0, datap = 0; j < _h; j++)
            {
                for (int i = 0; i < _w; ++i, ++datap)
                {
                    int tmp = _data[datap];
                    if (tmp < 0)
                    {
                        if (tmp == int.MinValue)
                        {
                            //2.5.1 - avoid undefined behaviour on fuzzed input
                            //
                            //If we go here, it means we have supplied an input
                            //with more bit depth than we we can really support.
                            tmp = int.MinValue + 1;
                        }

                        max = Math.Max(max, -tmp);
                        uint tmp_unsigned = to_smr(tmp);
                        _data[datap] = (int)tmp_unsigned;
                    }
                    else
                        max = Math.Max(max, tmp);
                }
            }

            cblk.numbps = max != 0 ? (uint)((MyMath.int_floorlog2(max) + 1) - Constants.T1_NMSEDEC_FRACBITS) : 0;
            if (cblk.numbps == 0)
            {
                cblk.totalpasses = 0;
                return cumwmsedec;
            }

            int bpno = (int)(cblk.numbps - 1);
            uint passtype = 2;

            mqc.ResetStates();
            mqc.SetState(T1_CTXNO.UNI, 0, 46);
            mqc.SetState(T1_CTXNO.AGG, 0, 3);
            mqc.SetState(T1_CTXNO.ZC, 0, 4);
            mqc.InitEnc(cblk.data);

            for (passno = 0; bpno >= 0; ++passno)
            {
                TcdPass pass = cblk.passes[passno];
                type = ((bpno < ((int)cblk.numbps - 4)) && (passtype < 2) && 
                        (cblksty & CCP_CBLKSTY.LAZY) != 0) ? T1_TYPE.RAW : T1_TYPE.MQ;

                // If the previous pass was terminating, we need to reset the encoder
                if (passno > 0 && cblk.passes[passno - 1].term != 0)
                {
                    if (type == T1_TYPE.RAW)
                        mqc.BypassInitEnc();
                    else
                        mqc.RestartInitEnc();
                }

                switch (passtype)
                {
                    case 0:
                        EncSigpass(bpno, ref nmsedec, type, cblksty);
                        break;

                    case 1:
                        EncRefpass(bpno, ref nmsedec, type, cblksty);
                        break;

                    case 2:
                        EncClnpass(bpno, ref nmsedec, cblksty);
                        if ((cblksty & CCP_CBLKSTY.SEGSYM) != 0)
                            mqc.SegmarkEnc();
                        break;
                }

                double tempwmsedec = Getwmsedec(nmsedec, compno, level, orient, bpno, qmfbid, 
                                                stepsize, numcomps, mct_norms, mct_numcomps);
                cumwmsedec += tempwmsedec;
                pass.distortiondec = cumwmsedec;

                if (EncIsTermPass(cblk, cblksty, bpno, passtype))
                {
                    // If it is a terminated pass, terminate it
                    if (type == T1_TYPE.RAW)
                    {
                        mqc.FlushEnc((cblksty & CCP_CBLKSTY.PTERM) != 0);
                    }
                    else
                    {
                        if ((cblksty & CCP_CBLKSTY.PTERM) != 0)
                            mqc.ErtermEnc();
                        else
                            mqc.Flush();
                    }
                    pass.term = 1;
                    pass.rate = (uint)mqc.NumBytes;
                }
                else
                {
                    // Non terminated pass
                    uint rate_extra_bytes;
                    if (type == T1_TYPE.RAW)
                        rate_extra_bytes = mqc.BypassGetExtraBytes((cblksty & CCP_CBLKSTY.PTERM) != 0);
                    else
                        rate_extra_bytes = 3;

                    pass.term = 0;
                    pass.rate = (uint)mqc.NumBytes + rate_extra_bytes;
                }

                if (++passtype == 3)
                {
                    passtype = 0;
                    bpno--;
                }

                // Code-switch "RESET"
                if ((cblksty & CCP_CBLKSTY.RESET) != CCP_CBLKSTY.NONE)
                    mqc.ResetEnc();
            }

            cblk.totalpasses = passno;

            if (cblk.totalpasses != 0)
            {
                // Make sure that pass rates are increasing
                uint last_pass_rate = (uint)mqc.NumBytes;
                for (passno = cblk.totalpasses; passno > 0;)
                {
                    var pass = cblk.passes[--passno];
                    if (pass.rate > last_pass_rate)
                        pass.rate = last_pass_rate;
                    else
                        last_pass_rate = pass.rate;
                }
            }

            for (passno = 0; passno < cblk.totalpasses; passno++)
            {
                TcdPass pass = cblk.passes[passno];

                // Prevent generation of FF as last data byte of a pass
                // For terminating passes, the flushing procedure ensured this already
                Debug.Assert(pass.rate > 0);
                if (cblk.data[pass.rate - 1] == 0xFF)
                    pass.rate--;

                pass.len = pass.rate - ((passno == 0) ? 0 : cblk.passes[passno - 1].rate);
            }

            return cumwmsedec;
        }

        /// <summary>
        /// Returns whether the pass (bpno, passtype) is terminated
        /// </summary>
        /// <remarks>2.5 - opj_t1_enc_is_term_pass</remarks>
        static bool EncIsTermPass(TcdCblkEnc cblk, CCP_CBLKSTY cblksty, int bpno, uint passtype)
        {
            // Is it the last cleanup pass ?
            if (passtype == 2 && bpno == 0)
            {
                return true;
            }

            if ((cblksty & CCP_CBLKSTY.TERMALL) != 0)
            {
                return true;
            }

            if ((cblksty & CCP_CBLKSTY.LAZY) != 0)
            {
                /* For bypass arithmetic bypass, terminate the 4th cleanup pass */
                if ((bpno == ((int)cblk.numbps - 4)) && (passtype == 2))
                {
                    return true;
                }
                /* and beyond terminate all the magnitude refinement passes (in raw) */
                /* and cleanup passes (in MQC) */
                if ((bpno < ((int)(cblk.numbps) - 4)) && (passtype > 0))
                {
                    return true;
                }
            }

            return false;
        }

        //2.5 - opj_t1_getwmsedec
        double Getwmsedec(
                int nmsedec,
                uint compno,
                uint level,
                uint orient,
                int bpno,
                uint qmfbid,
                double stepsize,
                uint numcomps,
                double[] mct_norms,
                uint mct_numcomps)
        {
            double w1 = 1, w2, wmsedec;

            if (mct_norms != null && compno < mct_numcomps)
                w1 = mct_norms[compno];

            if (qmfbid == 1)
            {
                w2 = DWT.Getnorm(level, orient);
            }
            else
            {
                int log2_gain = (orient == 0) ? 0 :
                                (orient == 3) ? 2 : 1;
                w2 = DWT.Getnorm_real(level, orient);
                stepsize /= (1 << log2_gain);
            }
            wmsedec = w1 * w2 * stepsize * (1 << bpno);
            wmsedec *= wmsedec * nmsedec / 8192.0;

            return wmsedec;
        }

        /// <summary>
        /// Encode refinement pass
        /// </summary>
        /// <remarks>2.5 - opj_t1_enc_refpass</remarks>
        void EncRefpass(int bpno, ref int nmsedec, T1_TYPE type, CCP_CBLKSTY cblksty)
        {
            int one = 1 << (bpno + Constants.T1_NMSEDEC_FRACBITS);
            int f = T1_FLAGS(0, 0); //<-- pointer to this.flags
            const int extra = 2;
            var mqc = _mqc;
            //DOWNLOAD_MQC_VARIABLES <-- Not done by C#
            int dp = 0; //<-- pointer at this._data
            uint k = 0;

            nmsedec = 0;
            for (; k < (_h & ~3U); k += 4, f += extra)
            {
                for (uint i = 0; i < _w; ++i, f++, dp += 4)
                {
                    T1 flags = this.flags[f];
                    T1 flagsUpdated = flags;

                    if ((flags & (T1.SIGMA_4 | T1.SIGMA_7 | T1.SIGMA_10 | T1.SIGMA_13)) == 0)
                    {
                        // none significant
                        continue;
                    }
                    if ((flags & (T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3)) ==
                                 (T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3))
                    {
                        // all processed by sigpass
                        continue;
                    }

                    //opj_t1_enc_refpass_step_macro
                    //(mqc, curctx, a, c, ct, flags, flagsUpdated,     datap, bpno, one, nmsedec, type, ci)
                    // mqc, curctx, a, c, ct, flags, flagsUpdated, &datap[0], bpno, one, nmsedec, type,  0
                    {
                        const int ci = 0;
                        bool v;
                        int datap = dp + ci;
                        if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 
                            (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                        {
                            T1 shift_flags = (T1)((uint)flags >> (ci * 3));
                            var ctxt = (T1_CTXNO) T1Luts.Getctxno_mag(shift_flags);
                            uint abs_data = smr_abs(_data[datap]);
                            nmsedec += T1Luts.Getnmsedec_ref(abs_data, bpno);
                            v = ((int)abs_data & one) != 0;
                            mqc.Setcurctx(ctxt);
                            if (type == T1_TYPE.RAW)
                                mqc.BypassEnc(v ? 1u : 0u);
                            else
                                mqc.Encode(v);
                            flagsUpdated |= (T1)((uint)T1.MU_THIS << (ci * 3));
                        }
                    }

                    //opj_t1_enc_refpass_step_macro
                    //(mqc, curctx, a, c, ct, flags, flagsUpdated,     datap, bpno, one, nmsedec, type, ci)
                    // mqc, curctx, a, c, ct, flags, flagsUpdated, &datap[1], bpno, one, nmsedec, type,  1
                    {
                        const int ci = 1;
                        bool v;
                        int datap = dp + ci;
                        if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                            (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                        {
                            T1 shift_flags = (T1)((uint)flags >> (ci * 3));
                            var ctxt = (T1_CTXNO)T1Luts.Getctxno_mag(shift_flags);
                            uint abs_data = smr_abs(_data[datap]);
                            nmsedec += T1Luts.Getnmsedec_ref(abs_data, bpno);
                            v = ((int)abs_data & one) != 0;
                            mqc.Setcurctx(ctxt);
                            if (type == T1_TYPE.RAW)
                                mqc.BypassEnc(v ? 1u : 0u);
                            else
                                mqc.Encode(v);
                            flagsUpdated |= (T1)((uint)T1.MU_THIS << (ci * 3));
                        }
                    }

                    //opj_t1_enc_refpass_step_macro
                    //(mqc, curctx, a, c, ct, flags, flagsUpdated,     datap, bpno, one, nmsedec, type, ci)
                    // mqc, curctx, a, c, ct, flags, flagsUpdated, &datap[2], bpno, one, nmsedec, type,  2
                    {
                        const int ci = 2;
                        bool v;
                        int datap = dp + ci;
                        if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                            (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                        {
                            T1 shift_flags = (T1)((uint)flags >> (ci * 3));
                            var ctxt = (T1_CTXNO)T1Luts.Getctxno_mag(shift_flags);
                            uint abs_data = smr_abs(_data[datap]);
                            nmsedec += T1Luts.Getnmsedec_ref(abs_data, bpno);
                            v = ((int)abs_data & one) != 0;
                            mqc.Setcurctx(ctxt);
                            if (type == T1_TYPE.RAW)
                                mqc.BypassEnc(v ? 1u : 0u);
                            else
                                mqc.Encode(v);
                            flagsUpdated |= (T1)((uint)T1.MU_THIS << (ci * 3));
                        }
                    }

                    //opj_t1_enc_refpass_step_macro
                    //(mqc, curctx, a, c, ct, flags, flagsUpdated,     datap, bpno, one, nmsedec, type, ci)
                    // mqc, curctx, a, c, ct, flags, flagsUpdated, &datap[3], bpno, one, nmsedec, type,  3
                    {
                        const int ci = 3;
                        bool v;
                        int datap = dp + ci;
                        if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                            (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                        {
                            T1 shift_flags = (T1)((uint)flags >> (ci * 3));
                            var ctxt = (T1_CTXNO)T1Luts.Getctxno_mag(shift_flags);
                            uint abs_data = smr_abs(_data[datap]);
                            nmsedec += T1Luts.Getnmsedec_ref(abs_data, bpno);
                            v = ((int)abs_data & one) != 0;
                            mqc.Setcurctx(ctxt);
                            if (type == T1_TYPE.RAW)
                                mqc.BypassEnc(v ? 1u : 0u);
                            else
                                mqc.Encode(v);
                            flagsUpdated |= (T1)((uint)T1.MU_THIS << (ci * 3));
                        }
                    }
                    this.flags[f] = flagsUpdated;
                }
            }

            if (k < _h)
            {
                int remaining_lines = (int)(_h - k);

                for (uint i = 0; i < _w; ++i, ++f)
                {
                    if ((flags[f] & (T1.SIGMA_4 | T1.SIGMA_7 | T1.SIGMA_10 | T1.SIGMA_13)) == 0)
                    {
                        /* none significant */
                        dp += remaining_lines;
                        continue;
                    }
                    for(int j = 0; j < remaining_lines; j++, dp++)
                    {
                        //opj_t1_enc_refpass_step_macro
                        //(mqc, curctx, a, c, ct, flags, flagsUpdated,     datap, bpno, one, nmsedec, type, ci)
                        // mqc, curctx, a, c, ct,    *f,           *f, &datap[0], bpno, one, nmsedec, type,  j
                        {
                            bool v;
                            if ((flags[f] & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (j * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (j * 3)))
                            {
                                T1 shift_flags = (T1)((uint)flags[f] >> (j * 3));
                                var ctxt = (T1_CTXNO)T1Luts.Getctxno_mag(shift_flags);
                                uint abs_data = smr_abs(_data[dp]);
                                nmsedec += T1Luts.Getnmsedec_ref(abs_data, bpno);
                                v = ((int)abs_data & one) != 0;
                                mqc.Setcurctx(ctxt);
                                if (type == T1_TYPE.RAW)
                                    mqc.BypassEnc(v ? 1u : 0u);
                                else
                                    mqc.Encode(v);
                                flags[f] |= (T1)((uint)T1.MU_THIS << (j * 3));
                            }
                        }
                    }
                }
            }

            //UPLOAD_MQC_VARIABLES
        }

        /// <summary>
        /// Encode significant pass
        /// </summary>
        /// <remarks>2.5 - opj_t1_enc_sigpass</remarks>
        void EncSigpass(int bpno, ref int nmsedec, T1_TYPE type, CCP_CBLKSTY cblksty)
        {
            nmsedec = 0;
            int one = 1 << (bpno + Constants.T1_NMSEDEC_FRACBITS);
            int f = T1_FLAGS(0, 0); //Pointer to this.flags
            const int extra = 2;
            var mqc = _mqc;
            //DOWNLOAD_MQC_VARIABLES //<-- C# we don't inline mqc
            int datap = 0; //<-- pointer to _data
            uint k = 0;

            for (; k < (_h & ~3U); k += 4, f += extra)
            {
                for (int i = 0; i < _w; ++i, ++f, datap += 4)
                {
                    if (flags[f] == 0)
                    {
                        // Nothing to do for any of the 4 data points
                        continue;
                    }

                    //opj_t1_enc_sigpass_step_macro
                    //(mqc, curctx, a, c, ct, flagspIn,   datapIn, bpno, one, nmsedec, type, ciIn,                         vscIn)
                    // mqc, curctx, a, c, ct,        f, &datap[0], bpno, one, nmsedec, type,    0, cblksty & J2K_CCP_CBLKSTY_VSC
                    {
                        bool v;
                        const int ci = 0;
                        bool vsc = (cblksty & CCP_CBLKSTY.VSC) != 0;
                        int l_datap = datap; //<-- pointer to _data
                        int flagsp = f;
                        T1 flags = this.flags[flagsp];
                        if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0 &&
                            (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0)
                        {
                            var ctxt1 = (T1_CTXNO)mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                            v = (smr_abs(_data[l_datap]) & (uint)one) != 0;
                            mqc.Setcurctx(ctxt1);
                            if (type == T1_TYPE.RAW)
                                mqc.BypassEnc(v ? 1u : 0u);
                            else
                                mqc.Encode(v);

                            if (v)
                            {
                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            this.flags[flagsp],
                                            this.flags[flagsp - 1],
                                            this.flags[flagsp + 1],
                                            ci);
                                var ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                v = smr_sign(_data[l_datap]);
                                nmsedec += T1Luts.Getnmsedec_sig(smr_abs(_data[l_datap]), bpno);
                                mqc.Setcurctx(ctxt2);

                                if (type == T1_TYPE.RAW)
                                    mqc.BypassEnc(v ? 1u : 0);
                                else
                                {
                                    bool spb = T1Luts.Getspb(lu);
                                    mqc.Encode(v ^ spb);
                                }
                                UpdateFlags(flagsp, ci, v ? 1u : 0, _w + 2, vsc);
                            }
                            this.flags[flagsp] |= (T1)(((uint)T1.PI_THIS) << (ci * 3));
                        }
                    }

                    //opj_t1_enc_sigpass_step_macro
                    //(mqc, curctx, a, c, ct, flagspIn,   datapIn, bpno, one, nmsedec, type, ciIn, vscIn)
                    // mqc, curctx, a, c, ct,        f, &datap[1], bpno, one, nmsedec, type,    1,     0
                    {
                        bool v;
                        const int ci = 1;
                        const bool vsc = false;
                        int l_datap = datap + 1; //<-- pointer to _data
                        int flagsp = f;
                        T1 flags = this.flags[flagsp];
                        if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0 &&
                            (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0)
                        {
                            var ctxt1 = (T1_CTXNO)mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                            v = (smr_abs(_data[l_datap]) & (uint)one) != 0;
                            mqc.Setcurctx(ctxt1);
                            if (type == T1_TYPE.RAW)
                                mqc.BypassEnc(v ? 1u : 0u);
                            else
                                mqc.Encode(v);

                            if (v)
                            {
                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            this.flags[flagsp],
                                            this.flags[flagsp - 1],
                                            this.flags[flagsp + 1],
                                            ci);
                                var ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                v = smr_sign(_data[l_datap]);
                                nmsedec += T1Luts.Getnmsedec_sig(smr_abs(_data[l_datap]), bpno);
                                mqc.Setcurctx(ctxt2);

                                if (type == T1_TYPE.RAW)
                                    mqc.BypassEnc(v ? 1u : 0);
                                else
                                {
                                    bool spb = T1Luts.Getspb(lu);
                                    mqc.Encode(v ^ spb);
                                }
                                UpdateFlags(flagsp, ci, v ? 1u : 0, _w + 2, vsc);
                            }
                            this.flags[flagsp] |= (T1)(((uint)T1.PI_THIS) << (ci * 3));
                        }
                    }

                    //opj_t1_enc_sigpass_step_macro
                    //(mqc, curctx, a, c, ct, flagspIn,   datapIn, bpno, one, nmsedec, type, ciIn, vscIn)
                    // mqc, curctx, a, c, ct,        f, &datap[2], bpno, one, nmsedec, type,    2,     0
                    {
                        bool v;
                        const int ci = 2;
                        const bool vsc = false;
                        int l_datap = datap + 2; //<-- pointer to _data
                        int flagsp = f;
                        T1 flags = this.flags[flagsp];
                        if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0 &&
                            (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0)
                        {
                            var ctxt1 = (T1_CTXNO)mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                            v = (smr_abs(_data[l_datap]) & (uint)one) != 0;
                            mqc.Setcurctx(ctxt1);
                            if (type == T1_TYPE.RAW)
                                mqc.BypassEnc(v ? 1u : 0u);
                            else
                                mqc.Encode(v);

                            if (v)
                            {
                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            this.flags[flagsp],
                                            this.flags[flagsp - 1],
                                            this.flags[flagsp + 1],
                                            ci);
                                var ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                v = smr_sign(_data[l_datap]);
                                nmsedec += T1Luts.Getnmsedec_sig(smr_abs(_data[l_datap]), bpno);
                                mqc.Setcurctx(ctxt2);

                                if (type == T1_TYPE.RAW)
                                    mqc.BypassEnc(v ? 1u : 0);
                                else
                                {
                                    bool spb = T1Luts.Getspb(lu);
                                    mqc.Encode(v ^ spb);
                                }
                                UpdateFlags(flagsp, ci, v ? 1u : 0, _w + 2, vsc);
                            }
                            this.flags[flagsp] |= (T1)(((uint)T1.PI_THIS) << (ci * 3));
                        }
                    }

                    //opj_t1_enc_sigpass_step_macro
                    //(mqc, curctx, a, c, ct, flagspIn,   datapIn, bpno, one, nmsedec, type, ciIn, vscIn)
                    // mqc, curctx, a, c, ct,        f, &datap[3], bpno, one, nmsedec, type,    3,     0
                    {
                        bool v;
                        const int ci = 3;
                        const bool vsc = false;
                        int l_datap = datap + 3; //<-- pointer to _data
                        int flagsp = f;
                        T1 flags = this.flags[flagsp];
                        if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0 &&
                            (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0)
                        {
                            var ctxt1 = (T1_CTXNO)mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                            v = (smr_abs(_data[l_datap]) & (uint)one) != 0;
                            mqc.Setcurctx(ctxt1);
                            if (type == T1_TYPE.RAW)
                                mqc.BypassEnc(v ? 1u : 0u);
                            else
                                mqc.Encode(v);

                            if (v)
                            {
                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            this.flags[flagsp],
                                            this.flags[flagsp - 1],
                                            this.flags[flagsp + 1],
                                            ci);
                                var ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                v = smr_sign(_data[l_datap]);
                                nmsedec += T1Luts.Getnmsedec_sig(smr_abs(_data[l_datap]), bpno);
                                mqc.Setcurctx(ctxt2);

                                if (type == T1_TYPE.RAW)
                                    mqc.BypassEnc(v ? 1u : 0);
                                else
                                {
                                    bool spb = T1Luts.Getspb(lu);
                                    mqc.Encode(v ^ spb);
                                }
                                UpdateFlags(flagsp, ci, v ? 1u : 0, _w + 2, vsc);
                            }
                            this.flags[flagsp] |= (T1)(((uint)T1.PI_THIS) << (ci * 3));
                        }
                    }
                }
            }

            if (k < _h)
            {
                for (uint i = 0; i < _w; ++i, ++f)
                {
                    if (flags[f] == 0)
                    {
                        // Nothing to do for any of the 4 data points
                        datap += (int)(_h - k);
                        continue;
                    }
                    for(uint j = k; j < _h; j++, datap++)
                    {
                        //opj_t1_enc_sigpass_step_macro
                        //(mqc, curctx, a, c, ct, flagspIn,   datapIn, bpno, one, nmsedec, type, ciIn,                         vscIn)
                        // mqc, curctx, a, c, ct,        f, &datap[0], bpno, one, nmsedec, type, j - k, (J == k && (cblksty & J2K_CCP_CBLKSTY_VSC) != 0
                        {
                            bool v;
                            int ci = (int)(j - k);
                            bool vsc = j == k && (cblksty & CCP_CBLKSTY.VSC) != 0;
                            int l_datap = datap; //<-- pointer to _data
                            int flagsp = f;
                            T1 flags = this.flags[flagsp];
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0 &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0)
                            {
                                var ctxt1 = (T1_CTXNO)mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                v = (smr_abs(_data[l_datap]) & (uint)one) != 0;
                                mqc.Setcurctx(ctxt1);
                                if (type == T1_TYPE.RAW)
                                    mqc.BypassEnc(v ? 1u : 0u);
                                else
                                    mqc.Encode(v);

                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                this.flags[flagsp],
                                                this.flags[flagsp - 1],
                                                this.flags[flagsp + 1],
                                                ci);
                                    var ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    v = smr_sign(_data[l_datap]);
                                    nmsedec += T1Luts.Getnmsedec_sig(smr_abs(_data[l_datap]), bpno);
                                    mqc.Setcurctx(ctxt2);

                                    if (type == T1_TYPE.RAW)
                                        mqc.BypassEnc(v ? 1u : 0);
                                    else
                                    {
                                        bool spb = T1Luts.Getspb(lu);
                                        mqc.Encode(v ^ spb);
                                    }
                                    UpdateFlags(flagsp, ci, v ? 1u : 0, _w + 2, vsc);
                                }
                                this.flags[flagsp] |= (T1)(((uint)T1.PI_THIS) << (ci * 3));
                            }
                        }
                    }
                }
            }

            //UPLOAD_MQC_VARIABLES
        }

        /// <summary>
        /// Encode clean-up pass
        /// </summary>
        /// <remarks>2.5 - opj_t1_enc_clnpass</remarks>
        void EncClnpass(int bpno, ref int nmsedec, CCP_CBLKSTY cblksty)
        {
            bool v;
            int one = 1 << (bpno + Constants.T1_NMSEDEC_FRACBITS);
            var mqc = _mqc;
            //DOWNLOAD_MQC_VARIABLES
            int datap = 0; //<-- pointer to _data
            int f = T1_FLAGS(0, 0); //<-- Pointer to this.flags
            const int extra = 2;


            nmsedec = 0;
            uint k = 0;
            for (; k < (_h & ~3U); k += 4, f += extra)
            {
                for (uint i = 0; i < _w; ++i, f++)
                {
                    uint runlen;
                    bool agg = flags[f] == 0;

                    if (agg)
                    {
                        for (runlen = 0; runlen < 4; ++runlen, ++datap)
                        {
                            if ((smr_abs(_data[datap]) & one) != 0)
                                break;
                        }
                        mqc.Setcurctx(T1_CTXNO.AGG);
                        mqc.Encode(runlen != 4);
                        if (runlen == 4) continue;
                        mqc.Setcurctx(T1_CTXNO.UNI);
                        mqc.Encode((runlen >> 1) == 1);
                        mqc.Encode((runlen & 1) == 1);
                    }
                    else
                    {
                        runlen = 0;
                    }
                    //opj_t1_enc_clnpass_step_macro
                    //(mqc, curctx, a, c, ct, flagspIn, datapIn, bpno, one, nmsedec, agg, runlen, lim, cblksty)
                    // mqc, curctx, a, c, ct,        f,   datap, bpno, one, nmsedec, agg, runlen,  4U, cblksty
                    {
                        const uint lim = 4U;
                        T1 flagsp = flags[f];
                        const T1 check = (T1.SIGMA_4 | T1.SIGMA_7 | T1.SIGMA_10 | T1.SIGMA_13 | T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                        int l_datap = datap;

                        if ((flagsp & check) == check)
                        {
                            if (runlen == 0)
                                flagsp &= ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                            else if (runlen == 1)
                                flagsp &= ~(T1.PI_1 | T1.PI_2 | T1.PI_3);
                            else if (runlen == 2)
                                flagsp &= ~(T1.PI_2 | T1.PI_3);
                            else if (runlen == 3)
                                flagsp &= ~(T1.PI_3);

                            //C# org impl is writing to a pointer, so we have to write the final
                            //into the array.
                            flags[f] = flagsp;
                        }
                        else
                        {
                            for(int ci = (int)runlen; ci < lim; ci++)
                            {
                                bool goto_PARTIAL = false;
                                if (agg && ci == runlen)
                                    goto_PARTIAL = true;
                                else if ((flagsp & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0)
                                {
                                    var ctxt1 = (T1_CTXNO) mqc.Getctxno_zc((T1)((uint)flagsp >> (ci * 3)));
                                    mqc.Setcurctx(ctxt1);
                                    v = (smr_abs(_data[l_datap]) & (uint)one) != 0;
                                    mqc.Encode(v);
                                    if (v)
                                    {
                                        goto_PARTIAL = true;
                                    }
                                }
                                if (goto_PARTIAL)
                                {
                                    bool vsc;
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(flagsp, flags[f - 1], flags[f + 1], ci);
                                    nmsedec += T1Luts.Getnmsedec_sig(smr_abs(_data[l_datap]), bpno);
                                    var ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    mqc.Setcurctx(ctxt2);
                                    v = smr_sign(_data[l_datap]);
                                    bool spb = T1Luts.Getspb(lu);
                                    mqc.Encode(v ^ spb);
                                    vsc = ((cblksty & CCP_CBLKSTY.VSC) != 0 && ci == 0);

                                    UpdateFlags(f, ci, v ? 1u : 0, _w + 2, vsc);

                                    //C# UpdateFlags changes the flag value, so we have to
                                    //   update our local "flagsp"
                                    flagsp = flags[f];
                                }

                                flagsp &= ~(T1)((uint)T1.PI_THIS << (3 * ci));

                                //C# org impl is writing to a pointer, so we have to write the final
                                //into the array.
                                flags[f] = flagsp;
                                l_datap++;
                            }
                        }
                    }
                    datap += 4 - (int)runlen;
                }
            }

            if (k < _h)
            {
                const bool agg = false;
                const uint runlen = 0;

                for (uint i = 0; i <_w; i++, f++)
                {
                    //opj_t1_enc_clnpass_step_macro
                    //(mqc, curctx, a, c, ct, flagspIn, datapIn, bpno, one, nmsedec, agg, runlen, lim,   cblksty)
                    // mqc, curctx, a, c, ct,        f,   datap, bpno, one, nmsedec, agg, runlen, _h - k, cblksty
                    {
                        uint lim = _h - k;
                        T1 flagsp = flags[f];
                        const T1 check = (T1.SIGMA_4 | T1.SIGMA_7 | T1.SIGMA_10 | T1.SIGMA_13 | T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                        int l_datap = datap;

                        if ((flagsp & check) == check)
                        {
                            if (runlen == 0)
                                flagsp &= ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);

                            //C# org impl is writing to a pointer, so we have to write the final
                            //into the array.
                            flags[f] = flagsp;
                        }
                        else
                        {
                            for (int ci = (int)runlen; ci < lim; ci++)
                            {
                                bool goto_PARTIAL = false;
                                if (agg && ci == runlen)
                                    goto_PARTIAL = true;
                                else if ((flagsp & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0)
                                {
                                    var ctxt1 = (T1_CTXNO)mqc.Getctxno_zc((T1)((uint)flagsp >> (ci * 3)));
                                    mqc.Setcurctx(ctxt1);
                                    v = (smr_abs(_data[l_datap]) & (uint)one) != 0;
                                    mqc.Encode(v);
                                    if (v)
                                    {
                                        goto_PARTIAL = true;
                                    }
                                }
                                if (goto_PARTIAL)
                                {
                                    bool vsc;
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(flagsp, flags[f - 1], flags[f + 1], ci);
                                    nmsedec += T1Luts.Getnmsedec_sig(smr_abs(_data[l_datap]), bpno);
                                    var ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    mqc.Setcurctx(ctxt2);
                                    v = smr_sign(_data[l_datap]);
                                    bool spb = T1Luts.Getspb(lu);
                                    mqc.Encode(v ^ spb);
                                    vsc = ((cblksty & CCP_CBLKSTY.VSC) != 0 && ci == 0);

                                    UpdateFlags(f, ci, v ? 1u : 0, _w + 2, vsc);

                                    //C# UpdateFlags changes the flag value, so we have to
                                    //   update our local "flagsp"
                                    flagsp = flags[f];
                                }

                                flagsp &= ~(T1)((uint)T1.PI_THIS << (3 * ci));

                                //C# org impl is writing to a pointer, so we have to write the final
                                //into the array.
                                flags[f] = flagsp;
                                l_datap++;
                            }
                        }
                    }
                    datap += (int)(_h - k);
                }
            }

            //UPLOAD_MQC_VARIABLES
        }

        #endregion

        //2.5 - opj_t1_decode_cblks
        internal static IEnumerable<T1CBLKDecodeProcessingJob> DecodeCblks(TileCoder tcd,
            TcdTilecomp tilec, TileCompParams tccp, bool check_pterm,
            CompressionInfo cinfo, SetRet set_ret, bool must_copy)
        {
            for (uint resno = 0; resno < tilec.minimum_num_resolutions; ++resno)
            {
                TcdResolution res = tilec.resolutions[resno];

                for (int bandno = 0; bandno < res.numbands; ++bandno)
                {
                    TcdBand band = res.bands[bandno];

                    for (int precno = 0; precno < res.pw * res.ph; ++precno)
                    {
                        TcdPrecinct precinct = band.precincts[precno];

                        if (!tcd.IsSubbandAreaOfInterest(tilec.compno,
                                                            resno,
                                                            band.bandno,
                                                            (uint)precinct.x0,
                                                            (uint)precinct.y0,
                                                            (uint)precinct.x1,
                                                            (uint)precinct.y1))
                        {
                            for (uint cblkno = 0; cblkno < precinct.cw * precinct.ch; ++cblkno)
                            {
                                precinct.dec[cblkno].decoded_data = null;
                            }

                            continue;
                        }

                        for (int cblkno = 0; cblkno < precinct.cw * precinct.ch; ++cblkno)
                        {
                            TcdCblkDec cblk = precinct.dec[cblkno];

                            if (!tcd.IsSubbandAreaOfInterest(tilec.compno,
                                                            resno,
                                                            band.bandno,
                                                            (uint)cblk.x0,
                                                            (uint)cblk.y0,
                                                            (uint)cblk.x1,
                                                            (uint)cblk.y1))
                            {
                                cblk.decoded_data = null;
                                continue;
                            }

                            if (!tcd.WholeTileDecoding)
                            {
                                if (cblk.decoded_data != null)
                                    continue;

                                uint cblk_w = (uint)(cblk.x1 - cblk.x0);
                                uint cblk_h = (uint)(cblk.y1 - cblk.y0);

                                if (cblk_w == 0 || cblk_h == 0)
                                    continue;
                            }

                            yield return new T1CBLKDecodeProcessingJob(
                                tcd.WholeTileDecoding,
                                resno,
                                cblk,
                                band,
                                tilec,
                                tccp,
                                cinfo,
                                check_pterm,
                                must_copy,
                                set_ret
                            );

                            if (!set_ret(null))
                                yield break;
                        }
                    }
                }
            }
        }

#if DEBUG
        static int cblk_count = 0, n_dumps = 0;
        void DumpData(string txt)
        {
            using (var file = new System.IO.StreamWriter("c:/temp/t1_dump.txt", append: n_dumps++ > 0))
            {
                file.Write(txt+"dumping code block: " + ++cblk_count+"\n");
                file.Write(" -- " + flagssize + " flags\n");
                for (int c=0; c < flagssize; c++)
                {                                        
                    file.Write("{1}: {0}\n", ((uint)flags[c]), c);
                }
                file.Write(" -- " + datasize + " ints\n");
                for (int c = 0; c < datasize; c++)
                {
                    file.Write("{1}: {0}\n", _data[c], c);
                }
            }
        }
#endif

        /// <remarks>
        /// 2.5 - opj_t1_cblk_encode_processor
        /// </remarks>
        internal static void ThreadWorker(T1CBLKEncodeProcessingJob job)
        {
            var cblk = job.cblk;
            var band = job.band;
            var tilec = job.tilec;
            var tccp = job.tccp;
            var resno = job.resno;
            uint tile_w = (uint)(tilec.x1 - tilec.x0);

            int x = cblk.x0 - band.x0;
            int y = cblk.y0 - band.y0;

            if (!job.pret)
                return;

            //C#
            //It's not recommended to use thread local storage on the
            //generic threadpool, so we always create this object.
            var t1 = new Tier1Coding();

            if ((band.bandno & 1) != 0)
            {
                TcdResolution pres = tilec.resolutions[resno - 1];
                x += pres.x1 - pres.x0;
            }
            if ((band.bandno & 2) != 0)
            {
                TcdResolution pres = tilec.resolutions[resno - 1];
                y += pres.y1 - pres.y0;
            }

            t1.allocate_buffers(cblk.x1 - cblk.x0, cblk.y1 - cblk.y0);

            //These are set by "allocate buffers"
            uint cblk_w = t1._w;
            uint cblk_h = t1._h;

            int tiledp = (y * (int)tile_w) + x; //Tile data pointer
            int[] tiledp_ar = tilec.data;

            if (tccp.qmfbid == 1)
            {
                var d1 = t1._data;
                int t1data = 0;
                uint j = 0;
                for (; j < (cblk_h & ~3U); j += 4)
                {
                    for (uint i = 0; i < cblk_w; i++)
                    {
                        d1[t1data++] = tiledp_ar[tiledp + (j + 0) * tile_w + i] << Constants.T1_NMSEDEC_FRACBITS;
                        d1[t1data++] = tiledp_ar[tiledp + (j + 1) * tile_w + i] << Constants.T1_NMSEDEC_FRACBITS;
                        d1[t1data++] = tiledp_ar[tiledp + (j + 2) * tile_w + i] << Constants.T1_NMSEDEC_FRACBITS;
                        d1[t1data++] = tiledp_ar[tiledp + (j + 3) * tile_w + i] << Constants.T1_NMSEDEC_FRACBITS;
                    }
                }
                if (j < cblk_h)
                {
                    for (uint i = 0; i < cblk_w; i++)
                    {
                        for(uint k = j; k < cblk_h; k++)
                        {
                            d1[t1data++] = tiledp_ar[tiledp + k * tile_w + i] << Constants.T1_NMSEDEC_FRACBITS;
                        }
                    }
                }
            }
            else
            {
                var d1 = t1._data;
                int t1data = 0;
                uint j = 0;
                IntOrFloat tmp = new IntOrFloat();

                for (; j < (cblk_h & ~3U); j += 4)
                {
                    for (uint i = 0; i < cblk_w; i++)
                    {
#if TEST_MATH_MODE
                        tmp.I = tiledp_ar[tiledp + (j + 0) * tile_w + i];
                        //lrintf == bankers rounding
                        d1[t1data++] = (int)(float)Math.Round((float)((float)(tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS)));
                        tmp.I = tiledp_ar[tiledp + (j + 1) * tile_w + i];
                        d1[t1data++] = (int)(float)Math.Round((float)((float)(tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS)));
                        tmp.I = tiledp_ar[tiledp + (j + 2) * tile_w + i];
                        d1[t1data++] = (int)(float)Math.Round((float)((float)(tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS)));
                        tmp.I = tiledp_ar[tiledp + (j + 3) * tile_w + i];
                        d1[t1data++] = (int)(float)Math.Round((float)((float)(tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS)));
#else
                        tmp.I = tiledp_ar[tiledp + (j + 0) * tile_w + i];
                        //lrintf == bankers rounding
                        d1[t1data++] = (int)Math.Round((tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS));
                        tmp.I = tiledp_ar[tiledp + (j + 1) * tile_w + i];
                        d1[t1data++] = (int)Math.Round((tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS));
                        tmp.I = tiledp_ar[tiledp + (j + 2) * tile_w + i];
                        d1[t1data++] = (int)Math.Round((tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS));
                        tmp.I = tiledp_ar[tiledp + (j + 3) * tile_w + i];
                        d1[t1data++] = (int)Math.Round((tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS));
#endif
                    }
                }
                if (j < cblk_h)
                {
                    for (uint i = 0; i < cblk_w; i++)
                    {
                        for (uint k = j; k < cblk_h; k++)
                        {
#if TEST_MATH_MODE
                            tmp.I = tiledp_ar[tiledp + k * tile_w + i];
                            d1[t1data++] = (int)(float)Math.Round((float)((float)(tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS)));
#else
                            tmp.I = tiledp_ar[tiledp + k * tile_w + i];
                            d1[t1data++] = (int)Math.Round((tmp.F / band.stepsize) * (1 << Constants.T1_NMSEDEC_FRACBITS));
#endif
                        }
                    }
                }
            }

            {
                double cumwmsedec = t1.EncodeCblk(
                    cblk,
                    band.bandno,
                    job.compno,
                    tilec.numresolutions - 1 - resno,
                    tccp.qmfbid,
                    band.stepsize,
                    tccp.cblksty,
                    job.tile.numcomps,
                    job.mct_norms,
                    job.mct_numcomps);

                //C#. What should I lock on? For now, job.tile, but that is probably not ideal.
                lock(job.tile)
                {
                    job.tile.distotile += cumwmsedec;
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_clbl_decode_processor
        /// </remarks>
        internal static void ThreadWorker(T1CBLKDecodeProcessingJob job)
        {
            uint cblk_w, cblk_h;

            var cblk = job.cblk;

            if (!job.whole_tile_decoding)
            {
                cblk_w = (uint)(cblk.x1 - cblk.x0);
                cblk_h = (uint)(cblk.y1 - cblk.y0);

                cblk.decoded_data = new int[cblk_w * cblk_h];
            }
            else if (cblk.decoded_data != null)
            {
                // Not sure if this code path can happen, but better
                // safe than sorry
                cblk.decoded_data = null;
            }

            var resno = job.resno;
            var band = job.band;
            var tilec = job.tilec;
            var tccp = job.tccp;
            var tile_w = (uint)(tilec.resolutions[tilec.minimum_num_resolutions - 1].x1
                                -
                                tilec.resolutions[tilec.minimum_num_resolutions - 1].x0);

            if (!job.pret)
            {
                return;
            }

            //C#
            //It's not recommended to use thread local storage on the
            //generic threadpool. This because you need to remove the
            //stored objects to avoid a memory leak... this needs some
            //thinking
            var t1 = new Tier1Coding();
            //Perhaps have a syncronized linked list over threadlocals,
            //hmm. Can you call threadlocal.remove from antoher thread?
            //But, I belive there's a low chance I'll see a threadpool
            //thread again, so this optimalization is probably pointless

            t1.mustuse_cblkdatabuffer = job.mustuse_cblkdatabuffer;

            if ((tccp.cblksty & CCP_CBLKSTY.HT) != 0)
            {
                if (!t1.DecodeHTCblk(cblk, 
                                     band.bandno, 
                                     (uint) tccp.roishift, 
                                     tccp.cblksty,
                                     job.cinfo,
                                     job.check_pterm))
                {
                    job.pret = false;
                    return;
                }
            }
            else
            {
                if (!t1.DecodeCblk(cblk,
                                     band.bandno,
                                     (uint)tccp.roishift,
                                     tccp.cblksty,
                                     job.cinfo,
                                     job.check_pterm))
                {
                    job.pret = false;
                    return;
                }
            }

            //Debug
            //if (cblk_count > 320)
            //    t1.DumpData("");
            //else
            //    cblk_count++;

            int x = cblk.x0 - band.x0;
            int y = cblk.y0 - band.y0;
            if ((band.bandno & 1) != 0)
            {
                TcdResolution pres = tilec.resolutions[resno - 1];
                x += pres.x1 - pres.x0;
            }
            if ((band.bandno & 2) != 0)
            {
                TcdResolution pres = tilec.resolutions[resno - 1];
                y += pres.y1 - pres.y0;
            }

            var datap = cblk.decoded_data != null ? cblk.decoded_data : t1._data;
            cblk_w = t1._w;
            cblk_h = t1._h;

            if ((tccp.roishift) != 0)
            {
                if (tccp.roishift >= 31)
                {
                    for (int j = 0; j < cblk_h; ++j)
                    {
                        for (int i = 0; i < cblk_w; ++i)
                        {
                            datap[(j * cblk_w) + i] = 0;
                        }
                    }
                }
                else
                {
                    uint thresh = 1u << tccp.roishift;
                    for (int j = 0; j < cblk_h; ++j)
                    {
                        for (int i = 0; i < cblk_w; ++i)
                        {
                            int val = datap[(j * cblk_w) + i];
                            int mag = Math.Abs(val);
                            if (mag >= thresh)
                            {
                                mag >>= tccp.roishift;
                                datap[(j * cblk_w) + i] = val < 0 ? -mag : mag;
                            }
                        }
                    }
                }
            }

            // Both can be non NULL if for example decoding a full tile and then
            // partially a tile. In which case partial decoding should be the
            // priority
            Debug.Assert(cblk.decoded_data != null | tilec.data != null);

            if (cblk.decoded_data != null)
            {
                uint cblk_size = cblk_w * cblk_h;
                if (tccp.qmfbid == 1)
                {
                    for (int i = 0; i < cblk_size; ++i)
                    {
                        datap[i] /= 2;
                    }
                }
                else
                {
                    float stepsize = 0.5f * band.stepsize;
                    // SSE2 is possible on .net 5
                    // https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.x86.sse2.converttovector128single?view=net-7.0
                    // but for now we're sticking with the old
                    int i = 0;
                    #region SSE2
                    //# ifdef __SSE2__
                    //                    {
                    //                        const __m128 xmm_stepsize = _mm_set1_ps(stepsize);
                    //                        for (; i < (cblk_size & ~15U); i += 16)
                    //                        {
                    //                            __m128 xmm0_data = _mm_cvtepi32_ps(_mm_load_si128((__m128i * const)(
                    //                                                                   datap + 0)));
                    //                        __m128 xmm1_data = _mm_cvtepi32_ps(_mm_load_si128((__m128i * const)(
                    //                                                               datap + 4)));
                    //                        __m128 xmm2_data = _mm_cvtepi32_ps(_mm_load_si128((__m128i * const)(
                    //                                                               datap + 8)));
                    //                        __m128 xmm3_data = _mm_cvtepi32_ps(_mm_load_si128((__m128i * const)(
                    //                                                               datap + 12)));
                    //                        _mm_store_ps((float*)(datap + 0), _mm_mul_ps(xmm0_data, xmm_stepsize));
                    //                        _mm_store_ps((float*)(datap + 4), _mm_mul_ps(xmm1_data, xmm_stepsize));
                    //                        _mm_store_ps((float*)(datap + 8), _mm_mul_ps(xmm2_data, xmm_stepsize));
                    //                        _mm_store_ps((float*)(datap + 12), _mm_mul_ps(xmm3_data, xmm_stepsize));
                    //                        datap += 16;
                    //                    }
                    //                }
                    //#endif
                    #endregion
                    IntOrFloat tmp = new IntOrFloat();
                    for (; i < cblk_size; ++i)
                    {
                        tmp.F = datap[i] * stepsize;

                        datap[i] = tmp.I;
                    }
                }
            }
            else if (tccp.qmfbid == 1)
            {
                var tiled = tilec.data;
                int tiledp = y * (int)tile_w + x;

                for (uint j = 0; j < cblk_h; j++)
                {
                    uint i = 0, end = cblk_w & ~3U;
                    for (; i < end; i += 4U)
                    {
                        int tmp0 = datap[(j * cblk_w) + i + 0U];
                        int tmp1 = datap[(j * cblk_w) + i + 1U];
                        int tmp2 = datap[(j * cblk_w) + i + 2U];
                        int tmp3 = datap[(j * cblk_w) + i + 3U];
                        tiled[tiledp + j * tile_w + i + 0U] = tmp0 / 2;
                        tiled[tiledp + j * tile_w + i + 1U] = tmp1 / 2;
                        tiled[tiledp + j * tile_w + i + 2U] = tmp2 / 2;
                        tiled[tiledp + j * tile_w + i + 3U] = tmp3 / 2;
                    }
                    for (; i < cblk_w; ++i)
                    {
                        int tmp = datap[(j * cblk_w) + i];
                        tiled[tiledp + (j * tile_w) + i] = tmp / 2;
                    }
                }
            }
            else
            {
                float stepsize = 0.5f * band.stepsize;
                var tiled = tilec.data;
                int tiledp = y * (int)tile_w + x;
                IntOrFloat tmp = new IntOrFloat();
                for (uint j = 0, pp = 0; j < cblk_h; j++)
                {
                    int tiledp2 = tiledp;
                    for (int i=0; i < cblk_w; i++)
                    {
                        tmp.F = (float)datap[pp] * stepsize;
                        tiled[tiledp2] = tmp.I;
                        pp++;
                        tiledp2++;
                    }
                    tiledp += (int)tile_w;
                }
            }
        }

        private T1 get_byte_flag(int pt)
        {
            //Benchmarks show this to be a lot faster than using a struct
            return (T1)(((uint)flags[pt / 4] & (0x000000FFU << 8 * (pt & 0x03))) >> 8 * (pt & 0x03));
        }

        private void set_byte_flag(int pt, byte f)
        {
            //Benchmarks show this to be faster than using a struct
            flags[pt / 4] = (T1)(((uint)flags[pt / 4] & ~(0x000000FFU << 8 * (pt & 0x03))) | (((uint)f) << 8 * (pt & 0x03)));
        }

        /// <remarks>
        /// 2.5.1 - opj_t1_ht_decode_cblk
        /// 
        /// This function sits in the ht_dec source file, however to do that
        /// various functions have to be set internal. Instead this main function
        /// will sit here, and all helper functions and classes will be in the
        /// ht_dec.cs source file
        /// </remarks>
        private bool DecodeHTCblk(TcdCblkDec cblk,
                                  uint orient,
                                  uint roishift,
                                  CCP_CBLKSTY cblksty,
                                  CompressionInfo cinfo,
                                  bool check_pterm)
        {
            uint vlc_val;              // fetched data from VLC bitstream
            byte[] cblkdata;
            int cblkdata_pt;
            uint[] qinf = new uint[2];
            bool stripe_causal = (cblksty & CCP_CBLKSTY.VSC) != 0;
            int y;

            if (roishift != 0)
            {
                cinfo.ErrorMT("We do not support decoding HT codeblocks with ROI");
                return false;
            }

            ht_dec_allocate_buffers(cblk.x1 - cblk.x0, cblk.y1 - cblk.y0);

            if (cblk.Mb == 0)
                return true;

            // numbps = Mb + 1 - zero_bplanes, Mb = Kmax, zero_bplanes = missing_msbs
            uint zero_bplanes = (cblk.Mb + 1) - cblk.numbps;

            //Compute whole codeblock length from chunk lengths
            uint cblk_len = 0;
            {
                for (int i = 0; i < cblk.numchunks; i++)
                    cblk_len += (uint)cblk.chunks[i].len;
            }

            if (cblk.numchunks > 1 || mustuse_cblkdatabuffer)
            {
                /* Allocate temporary memory if needed */
                if (cblk_len > cblkdatabuffersize)
                {
                    //Array.Resize is probably slower, since it retains the old data.
                    //if (cblkdatabuffer == null)
                    cblkdatabuffer = new byte[cblk_len];
                    //else
                    //    Array.Resize(ref cblkdatabuffer, (int)cblk_len);
                }

                // Concatenate all chunks
                cblkdata = cblkdatabuffer;
                if (cblkdata == null)
                    return false;
                cblk_len = 0;
                for (int i = 0; i < cblk.numchunks; i++)
                {
                    Buffer.BlockCopy(cblk.chunk_data, cblk.chunks[i].data_pt, cblkdata, (int)cblk_len, cblk.chunks[i].len);
                    cblk_len += (uint)cblk.chunks[i].len;
                }

                cblkdata_pt = 0;
            }
            else if (cblk.numchunks == 1)
            {
                cblkdata = cblk.chunk_data;
                cblkdata_pt = cblk.chunks[0].data_pt;
            }
            else
            {
                // Not sure if that can happen in practice, but avoid Coverity to
                // think we will dereference a null cblkdta pointer
                Debug.Assert(false, "Zero chunks? Check for bugs :)");
                return true;
            }

            // Pointer to cblkdata. Is is a pointer to bitstream
            int coded_data = cblkdata_pt;
            // Pointer to _data. Is a pointer to decoded codeblock data buf.
            int decoded_data = 0;
            // num_passes is the number of passes: 1 if CUP only, 2 for
            // CUP+SPP, and 3 for CUP+SPP+MRP
            uint num_passes = cblk.numsegs > 0 ? cblk.segs[0].real_num_passes : 0;
            num_passes += cblk.numsegs > 1 ? cblk.segs[1].real_num_passes : 0;
            // lengths1 is the length of cleanup pass
            uint lengths1 = num_passes > 0 ? cblk.segs[0].len : 0;
            // lengths2 is the length of refinement passes (either SPP only or SPP+MRP)
            uint lengths2 = num_passes > 1 ? cblk.segs[1].len : 0;
            // width is the decoded codeblock width
            int width = cblk.x1 - cblk.x0;
            // height is the decoded codeblock height
            int height = cblk.y1 - cblk.y0;
            // stride is the decoded codeblock buffer stride
            int stride = width;

            /*  sigma1 and sigma2 contains significant (i.e., non-zero) pixel
             *  locations.  The buffers are used interchangeably, because we need
             *  more than 4 rows of significance information at a given time.
             *  Each 32 bits contain significance information for 4 rows of 8
             *  columns each.  If we denote 32 bits by 0xaaaaaaaa, the each "a" is
             *  called a nibble and has significance information for 4 rows.
             *  The least significant nibble has information for the first column,
             *  and so on. The nibble's LSB is for the first row, and so on.
             *  Since, at most, we can have 1024 columns in a quad, we need 128
             *  entries; we added 1 for convenience when propagation of signifcance
             *  goes outside the structure
             *  To work in OpenJPEG these buffers has been expanded to 132.
             */
            int pflags = 0; //C# pointer to flags
            int sigma1 = pflags;
            int sigma2 = sigma1 + 132;
            // mbr arrangement is similar to sigma; mbr contains locations
            // that become significant during significance propagation pass
            int mbr1 = sigma2 + 132;
            int mbr2 = mbr1 + 132;
            //a pointer to sigma
            int sip = sigma1;  //pointers to arrays to be used interchangeably
            int sip_shift = 0; //the amount of shift needed for sigma

            if (num_passes > 1 && lengths2 == 0)
            {
                cinfo.WarnMT("A malformed codeblock that has " +
                    "more than one coding pass, but zero length for " +
                    "2nd and potentially the 3rd pass in an HT codeblock.");

                num_passes = 1;
            }
            else if (num_passes > 3)
            {
                cinfo.ErrorMT("We do not support more than 3 " +
                    "coding passes in an HT codeblock; This codeblocks has " +
                    "{0} passes.", num_passes);

                return false;
            }

            if (cblk.Mb > 30)
            {
                /* This check is better moved to opj_t2_read_packet_header() in t2.c
                   We do not have enough precision to decode any passes
                   The design of openjpeg assumes that the bits of a 32-bit integer are
                   assigned as follows:
                   bit 31 is for sign
                   bits 30-1 are for magnitude
                   bit 0 is for the center of the quantization bin
                   Therefore we can only do values of cblk->Mb <= 30
                 */
                cinfo.ErrorMT("32 bits are not enough to " +
                    "decode this codeblock, since the number of " +
                    "bitplane, {0}, is larger than 30.", cblk.Mb);

                return false;
            }
            if (zero_bplanes > cblk.Mb)
            {
                /* This check is better moved to opj_t2_read_packet_header() in t2.c,
                   in the line "l_cblk->numbps = (OPJ_UINT32)l_band->numbps + 1 - i;"
                   where i is the zero bitplanes, and should be no larger than cblk->Mb
                   We cannot have more zero bitplanes than there are planes. */
                cinfo.ErrorMT("Malformed HT codeblock. " +
                    "Decoding this codeblock is stopped. There are " +
                    "{0} zero bitplanes in {1} bitplanes.", zero_bplanes, cblk.Mb);

                return false;
            }
            if (zero_bplanes == cblk.Mb && num_passes > 1)
            {
                //Todo:
                //Locking on cinfo is not ideal, as this object is publically visible.
                lock (cinfo)
                {
                    /* When the number of zero bitplanes is equal to the number of bitplanes,
                       only the cleanup pass makes sense*/
                    if (ht_dec.only_cleanup_pass_is_decoded == false)
                    {
                        ht_dec.only_cleanup_pass_is_decoded = true;
                        cinfo.WarnMT("Malformed HT codeblock. " +
                            "When the number of zero planes bitplanes is " +
                            "equal to the number of bitplanes, only the cleanup " +
                            "pass makes sense, but we have {0} passes in this " +
                            "codeblock. Therefore, only the cleanup pass will be " +
                            "decoded. This message will not be displayed again.",
                            num_passes);
                    }
                }
                num_passes = 1;
            }

            uint p = cblk.numbps;

            // zero planes plus 1
            uint zero_bplanes_p1 = zero_bplanes + 1;

            if (lengths1 < 2 || (uint)lengths1 > cblk_len ||
                (uint)(lengths1 + lengths2) > cblk_len)
            {
                cinfo.ErrorMT("Malformed HT codeblock. " +
                              "Invalid codeblock length values.");
                return false;
            }
            // read scup and fix the bytes there
            int lcup = (int)lengths1;  // length of CUP
            //scup is the length of MEL + VLC
            int scup = (((int)cblkdata[coded_data + lcup - 1]) << 4) + (cblkdata[coded_data + lcup - 2] & 0xF);
            if (scup < 2 || scup > lcup || scup > 4079)
            { //something is wrong
                // The standard stipulates 2 <= Scup <= min(Lcup, 4079)
                cinfo.ErrorMT("Malformed HT codeblock. " +
                              "One of the following condition is not met: " +
                              "2 <= Scup <= min(Lcup, 4079)");
                return false;
            }

            // init structures
            ht_dec.frwd_struct sigprop = null; //C# if converting to a struct, simply drop this null
            ht_dec.rev_struct magref = null; //C# if converting to a struct, simply drop this null
            bool fail;
            var mel = new ht_dec.dec_mel(cblkdata, coded_data, lcup, scup, out fail);
            if (fail) 
            {
                cinfo.ErrorMT("Malformed HT codeblock. " +
                              "Incorrect MEL segment sequence.\n");
                return false;
            }
            var vlc = new ht_dec.rev_struct(cblkdata, coded_data, lcup, scup);
            var magsgn = new ht_dec.frwd_struct(cblkdata, coded_data, lcup - scup, 0xFF);
            if (num_passes > 1) {
                sigprop = new ht_dec.frwd_struct(cblkdata, coded_data + (int)lengths1, (int)lengths2, 0);
                if (num_passes > 2)
                    magref = new ht_dec.rev_struct(coded_data, cblkdata, (int)lengths1, (int)lengths2);
            }

            /** State storage
              *  One byte per quad; for 1024 columns, or 512 quads, we need
              *  512 bytes. We are using 2 extra bytes one on the left and one on
              *  the right for convenience.
              *
              *  The MSB bit in each byte is (\sigma^nw | \sigma^n), and the 7 LSBs
              *  contain max(E^nw | E^n)
              */

            // 514 is enough for a block width of 1024, +2 extra
            // here expanded to 528
            int line_state = (mbr2 + 132) * 4; // C# byte pointer to uint flags, that's wy we mul with 4

            //initial 2 lines
            /////////////////
            int lsp = line_state;   // uint pointer to "uint" flags
            set_byte_flag(lsp, 0);  // for initial row of quad, we set to 0
            int run = mel.get_run();// decode runs of events from MEL bitstrm
            // data represented as runs of 0 events
            // See mel_decode description
            qinf[0] = qinf[1] = 0;  // quad info decoded from VLC bitstream
            uint c_q = 0;           // context for quad q
            int sp = decoded_data;  // uint pointer to the int _data array
            // vlc_val;             // fetched data from VLC bitstream

            for (int x = 0; x < width; x += 4)
            { // one iteration per quad pair
                uint[] U_q = new uint[2]; // u values for the quad pair
                uint uvlc_mode;
                uint consumed_bits;
                uint m_n, v_n;
                uint ms_val;
                uint locs;

                // decode VLC
                /////////////

                //first quad
                // Get the head of the VLC bitstream. One fetch is enough for two
                // quads, since the largest VLC code is 7 bits, and maximum number of
                // bits used for u is 8.  Therefore for two quads we need 30 bits
                // (if we include unstuffing, then 32 bits are enough, since we have
                // a maximum of one stuffing per two bytes)
                vlc_val = vlc.fetch();

                //decode VLC using the context c_q and the head of the VLC bitstream
                qinf[0] = T1HTLuts.vlc_tbl0[(c_q << 7) | (vlc_val & 0x7F)];

                if (c_q == 0)
                { // if zero context, we need to use one MEL event
                    run -= 2; //the number of 0 events is multiplied by 2, so subtract 2

                    // Is the run terminated in 1? if so, use decoded VLC code,
                    // otherwise, discard decoded data, since we will decoded again
                    // using a different context
                    qinf[0] = (run == -1) ? qinf[0] : 0;

                    // is run -1 or -2? this means a run has been consumed
                    if (run < 0)
                    {
                        run = mel.get_run();    // get another run
                    }
                }

                // prepare context for the next quad; eqn. 1 in ITU T.814
                c_q = ((qinf[0] & 0x10) >> 4) | ((qinf[0] & 0xE0) >> 5);

                //remove data from vlc stream (0 bits are removed if qinf is not used)
                vlc_val = vlc.advance((int)(qinf[0] & 0x7));

                //update sigma
                // The update depends on the value of x; consider one OPJ_UINT32
                // if x is 0, 8, 16 and so on, then this line update c locations
                //      nibble (4 bits) number   0 1 2 3 4 5 6 7
                //                         LSB   c c 0 0 0 0 0 0
                //                               c c 0 0 0 0 0 0
                //                               0 0 0 0 0 0 0 0
                //                               0 0 0 0 0 0 0 0
                // if x is 4, 12, 20, then this line update locations c
                //      nibble (4 bits) number   0 1 2 3 4 5 6 7
                //                         LSB   0 0 0 0 c c 0 0
                //                               0 0 0 0 c c 0 0
                //                               0 0 0 0 0 0 0 0
                //                               0 0 0 0 0 0 0 0
                flags[sip] |= (T1)((((qinf[0] & 0x30) >> 4) | ((qinf[0] & 0xC0) >> 2)) << sip_shift);

                //second quad
                qinf[1] = 0;
                if (x + 2 < width)
                { // do not run if codeblock is narrower
                  //decode VLC using the context c_q and the head of the VLC bitstream
                    qinf[1] = T1HTLuts.vlc_tbl0[(c_q << 7) | (vlc_val & 0x7F)];

                    // if context is zero, use one MEL event
                    if (c_q == 0)
                    { //zero context
                        run -= 2; //subtract 2, since events number if multiplied by 2

                        // if event is 0, discard decoded qinf
                        qinf[1] = (run == -1) ? qinf[1] : 0;

                        if (run < 0)
                        { // have we consumed all events in a run
                            run = mel.get_run();    // if yes, then get another run
                        }
                    }

                    //prepare context for the next quad, eqn. 1 in ITU T.814
                    c_q = ((qinf[1] & 0x10) >> 4) | ((qinf[1] & 0xE0) >> 5);

                    //remove data from vlc stream, if qinf is not used, cwdlen is 0
                    vlc_val = vlc.advance((int) qinf[1] & 0x7);
                }

                //update sigma
                // The update depends on the value of x; consider one OPJ_UINT32
                // if x is 0, 8, 16 and so on, then this line update c locations
                //      nibble (4 bits) number   0 1 2 3 4 5 6 7
                //                         LSB   0 0 c c 0 0 0 0
                //                               0 0 c c 0 0 0 0
                //                               0 0 0 0 0 0 0 0
                //                               0 0 0 0 0 0 0 0
                // if x is 4, 12, 20, then this line update locations c
                //      nibble (4 bits) number   0 1 2 3 4 5 6 7
                //                         LSB   0 0 0 0 0 0 c c
                //                               0 0 0 0 0 0 c c
                //                               0 0 0 0 0 0 0 0
                //                               0 0 0 0 0 0 0 0
                flags[sip] |= (T1)((((qinf[1] & 0x30) | ((qinf[1] & 0xC0) << 2))) << (4 + sip_shift));

                sip += (x & 0x7) != 0 ? 1 : 0; // move sigma pointer to next entry
                sip_shift ^= 0x10;        // increment/decrement sip_shift by 16

                // retrieve u
                /////////////

                // uvlc_mode is made up of u_offset bits from the quad pair
                uvlc_mode = ((qinf[0] & 0x8) >> 3) | ((qinf[1] & 0x8) >> 2);
                if (uvlc_mode == 3)
                { // if both u_offset are set, get an event from
                  // the MEL run of events
                    run -= 2; //subtract 2, since events number if multiplied by 2
                    uvlc_mode += (run == -1) ? 1u : 0u; //increment uvlc_mode if event is 1
                    if (run < 0)
                    { // if run is consumed (run is -1 or -2), get another run
                        run = mel.get_run();
                    }
                }
                //decode uvlc_mode to get u for both quads
                consumed_bits = ht_dec.decode_init_uvlc(vlc_val, uvlc_mode, U_q);
                if (U_q[0] > zero_bplanes_p1 || U_q[1] > zero_bplanes_p1)
                {

                    cinfo.ErrorMT("Malformed HT codeblock. Decoding "+
                                  "this codeblock is stopped. U_q is larger than zero "+
                                  "bitplanes + 1");
                    return false;
                }

                //consume u bits in the VLC code
                vlc_val = vlc.advance((int)consumed_bits);

                //decode magsgn and update line_state
                /////////////////////////////////////

                //We obtain a mask for the samples locations that needs evaluation
                locs = 0xFF;
                if (x + 4 > width)
                {
                    locs >>= (x + 4 - width) << 1;    // limits width
                }
                locs = height > 1 ? locs : (locs & 0x55);         // limits height

                if (((((qinf[0] & 0xF0) >> 4) | (qinf[1] & 0xF0)) & ~locs) != 0)
                {

                    cinfo.ErrorMT("Malformed HT codeblock. "+
                                  "VLC code produces significant samples outside "+
                                  "the codeblock area.");

                    return false;
                }

                //first quad, starting at first sample in quad and moving on
                if ((qinf[0] & 0x10) != 0)
                { //is it significant? (sigma_n)
                    uint val;

                    ms_val = magsgn.fetch();                    //get 32 bits of magsgn data
                    m_n = U_q[0] - ((qinf[0] >> 12) & 1);       //evaluate m_n (number of bits
                                                                // to read from bitstream), using EMB e_k
                    magsgn.advance((int) m_n);                  //consume m_n
                    val = ms_val << 31;                         //get sign bit
                    v_n = ms_val & ((1U << (int)m_n) - 1);      //keep only m_n bits
                    v_n |= ((qinf[0] & 0x100) >> 8) << (int)m_n;//add EMB e_1 as MSB
                    v_n |= 1;                                   //add center of bin
                    //v_n now has 2 * (\mu - 1) + 0.5 with correct sign bit
                    //add 2 to make it 2*\mu+0.5, shift it up to missing MSBs
                    _data[sp] = (int) (val | ((v_n + 2) << (int)(p - 1)));
                }
                else if ((locs & 0x1) != 0)
                { // if this is inside the codeblock, set the
                    _data[sp] = 0;           // sample to zero
                }

                if ((qinf[0] & 0x20) != 0)
                { //sigma_n
                    uint val, t;

                    ms_val = magsgn.fetch();                     //get 32 bits
                    m_n = U_q[0] - ((qinf[0] >> 13) & 1);        //m_n, uses EMB e_k
                    magsgn.advance((int)m_n);                    //consume m_n
                    val = ms_val << 31;                          //get sign bit
                    v_n = ms_val & ((1U << (int)m_n) - 1);       //keep only m_n bits
                    v_n |= ((qinf[0] & 0x200) >> 9) << (int)m_n; //add EMB e_1
                    v_n |= 1;                                    //bin center
                    //v_n now has 2 * (\mu - 1) + 0.5 with correct sign bit
                    //add 2 to make it 2*\mu+0.5, shift it up to missing MSBs
                    _data[sp + stride] = (int)(val | ((v_n + 2) << (int) (p - 1)));

                    //update line_state: bit 7 (\sigma^N), and E^N
                    t = (uint)get_byte_flag(lsp) & 0x7F;       // keep E^NW
                    v_n = 32 - ht_dec.count_leading_zeros(v_n);
                    set_byte_flag(lsp, (byte)(0x80 | (t > v_n ? t : v_n))); //max(E^NW, E^N) | s
                }
                else if ((locs & 0x2) != 0)
                { // if this is inside the codeblock, set the
                    _data[sp + stride] = 0;      // sample to zero
                }

                ++lsp; // move to next quad information
                ++sp;  // move to next column of samples

                //this is similar to the above two samples
                if ((qinf[0] & 0x40) != 0)
                {
                    uint val;

                    ms_val = magsgn.fetch();
                    m_n = U_q[0] - ((qinf[0] >> 14) & 1);
                    magsgn.advance((int)m_n);
                    val = ms_val << 31;
                    v_n = ms_val & ((1U << (int)m_n) - 1);
                    v_n |= (((qinf[0] & 0x400) >> 10) << (int)m_n);
                    v_n |= 1;
                    _data[sp] = (int) (val | ((v_n + 2) << (int) (p - 1)));
                }
                else if ((locs & 0x4) != 0)
                {
                    _data[sp] = 0;
                }

                set_byte_flag(lsp, 0);
                if ((qinf[0] & 0x80) != 0)
                {
                    uint val;
                    ms_val = magsgn.fetch();
                    m_n = U_q[0] - ((qinf[0] >> 15) & 1); //m_n
                    magsgn.advance((int)m_n);
                    val = ms_val << 31;
                    v_n = ms_val & ((1U << (int)m_n) - 1);
                    v_n |= ((qinf[0] & 0x800) >> 11) << (int)m_n;
                    v_n |= 1; //center of bin
                    _data[sp + stride] = (int) (val | ((v_n + 2) << (int)(p - 1)));

                    //line_state: bit 7 (\sigma^NW), and E^NW for next quad
                    set_byte_flag(lsp, (byte)(0x80 | (32 - ht_dec.count_leading_zeros(v_n))));
                }
                else if ((locs & 0x8) != 0)
                { //if outside set to 0
                    _data[sp + stride] = 0;
                }

                ++sp; //move to next column

                //second quad
                if ((qinf[1] & 0x10) != 0)
                {
                    uint val;

                    ms_val = magsgn.fetch();
                    m_n = U_q[1] - ((qinf[1] >> 12) & 1); //m_n
                    magsgn.advance((int)m_n);
                    val = ms_val << 31;
                    v_n = ms_val & ((1U << (int)m_n) - 1);
                    v_n |= (((qinf[1] & 0x100) >> 8) << (int)m_n);
                    v_n |= 1;
                    _data[sp] = (int)(val | ((v_n + 2) << (int)(p - 1)));
                }
                else if ((locs & 0x10) != 0)
                {
                    _data[sp] = 0;
                }

                if ((qinf[1] & 0x20) != 0)
                {
                    uint val, t;

                    ms_val = magsgn.fetch();
                    m_n = U_q[1] - ((qinf[1] >> 13) & 1); //m_n
                    magsgn.advance((int)m_n);
                    val = ms_val << 31;
                    v_n = ms_val & ((1U << (int)m_n) - 1);
                    v_n |= (((qinf[1] & 0x200) >> 9) << (int)m_n);
                    v_n |= 1;
                    _data[sp + stride] = (int)(val | ((v_n + 2) << (int)(p - 1)));

                    //update line_state: bit 7 (\sigma^N), and E^N
                    t = (uint)get_byte_flag(lsp) & 0x7F;            //E^NW
                    v_n = 32 - ht_dec.count_leading_zeros(v_n);     //E^N
                    set_byte_flag(lsp, (byte)(0x80 | (t > v_n ? t : v_n))); //max(E^NW, E^N) | s
                }
                else if ((locs & 0x20) != 0)
                {
                    _data[sp + stride] = 0;    //no need to update line_state
                }

                ++lsp; //move line state to next quad
                ++sp;  //move to next sample

                if ((qinf[1] & 0x40) != 0)
                {
                    uint val;

                    ms_val = magsgn.fetch();
                    m_n = U_q[1] - ((qinf[1] >> 14) & 1); //m_n
                    magsgn.advance((int)m_n);
                    val = ms_val << 31;
                    v_n = ms_val & ((1U << (int)m_n) - 1);
                    v_n |= (((qinf[1] & 0x400) >> 10) << (int)m_n);
                    v_n |= 1;
                    _data[sp] = (int)(val | ((v_n + 2) << (int)(p - 1)));
                }
                else if ((locs & 0x40) != 0)
                {
                    _data[sp] = 0;
                }

                set_byte_flag(lsp, 0);
                if ((qinf[1] & 0x80) != 0)
                {
                    uint val;

                    ms_val = magsgn.fetch();
                    m_n = U_q[1] - ((qinf[1] >> 15) & 1); //m_n
                    magsgn.advance((int)m_n);
                    val = ms_val << 31;
                    v_n = ms_val & ((1U << (int)m_n) - 1);
                    v_n |= (((qinf[1] & 0x800) >> 11) << (int)m_n);
                    v_n |= 1; //center of bin
                    _data[sp + stride] = (int)(val | ((v_n + 2) << (int)(p - 1)));

                    //line_state: bit 7 (\sigma^NW), and E^NW for next quad
                    set_byte_flag(lsp, (byte)(0x80 | (32 - ht_dec.count_leading_zeros(v_n))));
                }
                else if ((locs & 0x80) != 0)
                {
                    _data[sp + stride] = 0;
                }

                ++sp;
            }

            //non-initial lines
            //////////////////////////
            for (y = 2; y < height; /*done at the end of loop*/)
            {
                byte ls0;

                sip_shift ^= 0x2;  // shift sigma to the upper half od the nibble
                sip_shift &= unchecked((int)0xFFFFFFEF); //move back to 0 (it might have been at 0x10)
                sip = (y & 0x4) != 0 ? sigma2 : sigma1; //choose sigma array

                lsp = line_state;
                ls0 = (byte)get_byte_flag(lsp);        // read the line state value
                set_byte_flag(lsp, 0);                 // and set it to zero
                sp = decoded_data + y * stride; // generated samples
                c_q = 0;                        // context
                for (int x = 0; x < width; x += 4)
                {
                    uint[] U_q = new uint[2];
                    uint uvlc_mode, consumed_bits;
                    uint m_n, v_n;
                    uint ms_val;
                    uint locs;

                    // decode vlc
                    /////////////

                    //first quad
                    // get context, eqn. 2 ITU T.814
                    // c_q has \sigma^W | \sigma^SW
                    c_q |= (uint)(ls0 >> 7);          //\sigma^NW | \sigma^N
                    c_q |= ((uint)get_byte_flag(lsp + 1) >> 5) & 0x4; //\sigma^NE | \sigma^NF

                    //the following is very similar to previous code, so please refer to
                    // that
                    vlc_val = vlc.fetch();
                    qinf[0] = T1HTLuts.vlc_tbl1[(c_q << 7) | (vlc_val & 0x7F)];
                    if (c_q == 0)
                    { //zero context
                        run -= 2;
                        qinf[0] = (run == -1) ? qinf[0] : 0;
                        if (run < 0)
                        {
                            run = mel.get_run();
                        }
                    }
                    //prepare context for the next quad, \sigma^W | \sigma^SW
                    c_q = ((qinf[0] & 0x40) >> 5) | ((qinf[0] & 0x80) >> 6);

                    //remove data from vlc stream
                    vlc_val = vlc.advance((int)qinf[0] & 0x7);

                    //update sigma
                    // The update depends on the value of x and y; consider one OPJ_UINT32
                    // if x is 0, 8, 16 and so on, and y is 2, 6, etc., then this
                    // line update c locations
                    //      nibble (4 bits) number   0 1 2 3 4 5 6 7
                    //                         LSB   0 0 0 0 0 0 0 0
                    //                               0 0 0 0 0 0 0 0
                    //                               c c 0 0 0 0 0 0
                    //                               c c 0 0 0 0 0 0
                    flags[sip] |= (T1) ((((qinf[0] & 0x30) >> 4) | ((qinf[0] & 0xC0) >> 2)) << sip_shift);

                    //second quad
                    qinf[1] = 0;
                    if (x + 2 < width)
                    {
                        c_q |= (uint)get_byte_flag(lsp + 1) >> 7;
                        c_q |= ((uint)get_byte_flag(lsp + 2) >> 5) & 0x4;
                        qinf[1] = T1HTLuts.vlc_tbl1[(c_q << 7) | (vlc_val & 0x7F)];
                        if (c_q == 0)
                        { //zero context
                            run -= 2;
                            qinf[1] = (run == -1) ? qinf[1] : 0;
                            if (run < 0)
                            {
                                run = mel.get_run();
                            }
                        }
                        //prepare context for the next quad
                        c_q = ((qinf[1] & 0x40) >> 5) | ((qinf[1] & 0x80) >> 6);
                        //remove data from vlc stream
                        vlc_val = vlc.advance((int)qinf[1] & 0x7);
                    }

                    //update sigma
                    flags[sip] |= (T1) (((qinf[1] & 0x30) | ((qinf[1] & 0xC0) << 2)) << (4 + sip_shift));

                    sip += (x & 0x7) != 0 ? 1 : 0;
                    sip_shift ^= 0x10;

                    //retrieve u
                    ////////////
                    uvlc_mode = ((qinf[0] & 0x8) >> 3) | ((qinf[1] & 0x8) >> 2);
                    consumed_bits = ht_dec.decode_noninit_uvlc(vlc_val, uvlc_mode, U_q);
                    vlc_val = vlc.advance((int)consumed_bits);

                    //calculate E^max and add it to U_q, eqns 5 and 6 in ITU T.814
                    if (((qinf[0] & 0xF0) & ((qinf[0] & 0xF0) - 1)) != 0)
                    { // is \gamma_q 1?
                        uint E = (ls0 & 0x7Fu);
                        E = E > ((uint)get_byte_flag(lsp + 1) & 0x7Fu) ? E : ((uint)get_byte_flag(lsp + 1) & 0x7Fu); //max(E, E^NE, E^NF)
                                                                         //since U_q already has u_q + 1, we subtract 2 instead of 1
                        U_q[0] += E > 2 ? E - 2 : 0;
                    }

                    if (((qinf[1] & 0xF0) & ((qinf[1] & 0xF0) - 1)) != 0)
                    { //is \gamma_q 1?
                        uint E = ((uint)get_byte_flag(lsp + 1) & 0x7Fu);
                        E = E > ((uint)get_byte_flag(lsp + 2) & 0x7Fu) ? E : ((uint)get_byte_flag(lsp + 2) & 0x7Fu); //max(E, E^NE, E^NF)
                                                                         //since U_q already has u_q + 1, we subtract 2 instead of 1
                        U_q[1] += E > 2 ? E - 2 : 0;
                    }

                    if (U_q[0] > zero_bplanes_p1 || U_q[1] > zero_bplanes_p1)
                    {
                        cinfo.ErrorMT("Malformed HT codeblock. "+
                                      "Decoding this codeblock is stopped. U_q is"+        
                                      "larger than bitplanes + 1");

                        return false;
                    }

                    ls0 = (byte)get_byte_flag(lsp + 2); //for next double quad
                    set_byte_flag(lsp + 1, 0);
                    set_byte_flag(lsp + 2, 0);

                    //decode magsgn and update line_state
                    /////////////////////////////////////

                    //locations where samples need update
                    locs = 0xFF;
                    if (x + 4 > width)
                    {
                        locs >>= (x + 4 - width) << 1;
                    }
                    locs = y + 2 <= height ? locs : (locs & 0x55);

                    if (((((qinf[0] & 0xF0) >> 4) | (qinf[1] & 0xF0)) & ~locs) != 0)
                    {
                        cinfo.ErrorMT("Malformed HT codeblock. "+
                                      "VLC code produces significant samples outside "+
                                      "the codeblock area.");

                        return false;
                    }

                    if ((qinf[0] & 0x10) != 0)
                    { //sigma_n
                        uint val;

                        ms_val = magsgn.fetch();
                        m_n = U_q[0] - ((qinf[0] >> 12) & 1); //m_n
                        magsgn.advance((int)m_n);
                        val = ms_val << 31;
                        v_n = ms_val & ((1U << (int)m_n) - 1);
                        v_n |= ((qinf[0] & 0x100) >> 8) << (int)m_n;
                        v_n |= 1; //center of bin
                        _data[sp] = (int) (val | ((v_n + 2) << (int)(p - 1)));
                    }
                    else if ((locs & 0x1) != 0)
                    {
                        _data[sp] = 0;
                    }

                    if ((qinf[0] & 0x20) != 0)
                    { //sigma_n
                        uint val, t;

                        ms_val = magsgn.fetch();
                        m_n = U_q[0] - ((qinf[0] >> 13) & 1); //m_n
                        magsgn.advance((int)m_n);
                        val = ms_val << 31;
                        v_n = ms_val & ((1U << (int)m_n) - 1);
                        v_n |= ((qinf[0] & 0x200) >> 9) << (int)m_n;
                        v_n |= 1; //center of bin
                        _data[sp + stride] = (int)(val | ((v_n + 2) << (int)(p - 1)));

                        //update line_state: bit 7 (\sigma^N), and E^N
                        t = (uint)get_byte_flag(lsp) & 0x7F;          //E^NW
                        v_n = 32 - ht_dec.count_leading_zeros(v_n);
                        set_byte_flag(lsp, (byte)(0x80 | (t > v_n ? t : v_n)));
                    }
                    else if ((locs & 0x2) != 0)
                    {
                        _data[sp + stride] = 0;    //no need to update line_state
                    }

                    ++lsp;
                    ++sp;

                    if ((qinf[0] & 0x40) != 0)
                    { //sigma_n
                        uint val;

                        ms_val = magsgn.fetch();
                        m_n = U_q[0] - ((qinf[0] >> 14) & 1); //m_n
                        magsgn.advance((int)m_n);
                        val = ms_val << 31;
                        v_n = ms_val & ((1U << (int)m_n) - 1);
                        v_n |= (((qinf[0] & 0x400) >> 10) << (int)m_n);
                        v_n |= 1;                            //center of bin
                        _data[sp] = (int)(val | ((v_n + 2) << (int)(p - 1)));
                    }
                    else if ((locs & 0x4) != 0)
                    {
                        _data[sp] = 0;
                    }

                    if ((qinf[0] & 0x80) != 0)
                    { //sigma_n
                        uint val;

                        ms_val = magsgn.fetch();
                        m_n = U_q[0] - ((qinf[0] >> 15) & 1); //m_n
                        magsgn.advance((int)m_n);
                        val = ms_val << 31;
                        v_n = ms_val & ((1U << (int)m_n) - 1);
                        v_n |= ((qinf[0] & 0x800) >> 11) << (int)m_n;
                        v_n |= 1; //center of bin
                        _data[sp + stride] = (int)(val | ((v_n + 2) << (int)(p - 1)));

                        //update line_state: bit 7 (\sigma^NW), and E^NW for next quad
                        set_byte_flag(lsp, (byte)(0x80 | (32 - ht_dec.count_leading_zeros(v_n))));
                    }
                    else if ((locs & 0x8) != 0)
                    {
                        _data[sp + stride] = 0;
                    }

                    ++sp;

                    if ((qinf[1] & 0x10) != 0)
                    { //sigma_n
                        uint val;

                        ms_val = magsgn.fetch();
                        m_n = U_q[1] - ((qinf[1] >> 12) & 1); //m_n
                        magsgn.advance((int)m_n);
                        val = ms_val << 31;
                        v_n = ms_val & ((1U << (int)m_n) - 1);
                        v_n |= (((qinf[1] & 0x100) >> 8) << (int)m_n);
                        v_n |= 1;                            //center of bin
                        _data[sp] = (int)(val | ((v_n + 2) << (int)(p - 1)));
                    }
                    else if ((locs & 0x10) != 0)
                    {
                        _data[sp] = 0;
                    }

                    if ((qinf[1] & 0x20) != 0)
                    { //sigma_n
                        uint val, t;

                        ms_val = magsgn.fetch();
                        m_n = U_q[1] - ((qinf[1] >> 13) & 1); //m_n
                        magsgn.advance((int)m_n);
                        val = ms_val << 31;
                        v_n = ms_val & ((1U << (int)m_n) - 1);
                        v_n |= (((qinf[1] & 0x200) >> 9) << (int)m_n);
                        v_n |= 1; //center of bin
                        _data[sp + stride] = (int)(val | ((v_n + 2) << (int)(p - 1)));

                        //update line_state: bit 7 (\sigma^N), and E^N
                        t = (uint)get_byte_flag(lsp) & 0x7F;          //E^NW
                        v_n = 32 - ht_dec.count_leading_zeros(v_n);
                        set_byte_flag(lsp, (byte)(0x80 | (t > v_n ? t : v_n)));
                    }
                    else if ((locs & 0x20) != 0)
                    {
                        _data[sp + stride] = 0;    //no need to update line_state
                    }

                    ++lsp;
                    ++sp;

                    if ((qinf[1] & 0x40) != 0)
                    { //sigma_n
                        uint val;

                        ms_val = magsgn.fetch();
                        m_n = U_q[1] - ((qinf[1] >> 14) & 1); //m_n
                        magsgn.advance((int)m_n);
                        val = ms_val << 31;
                        v_n = ms_val & ((1U << (int)m_n) - 1);
                        v_n |= (((qinf[1] & 0x400) >> 10) << (int)m_n);
                        v_n |= 1;                            //center of bin
                        _data[sp] = (int)(val | ((v_n + 2) << (int)(p - 1)));
                    }
                    else if ((locs & 0x40) != 0)
                    {
                        _data[sp] = 0;
                    }

                    if ((qinf[1] & 0x80) != 0)
                    { //sigma_n
                        uint val;

                        ms_val = magsgn.fetch();
                        m_n = U_q[1] - ((qinf[1] >> 15) & 1); //m_n
                        magsgn.advance((int)m_n);
                        val = ms_val << 31;
                        v_n = ms_val & ((1U << (int)m_n) - 1);
                        v_n |= (((qinf[1] & 0x800) >> 11) << (int)m_n);
                        v_n |= 1; //center of bin
                        _data[sp + stride] = (int)(val | ((v_n + 2) << (int)(p - 1)));

                        //update line_state: bit 7 (\sigma^NW), and E^NW for next quad
                        set_byte_flag(lsp, (byte)(0x80 | (32 - ht_dec.count_leading_zeros(v_n))));
                    }
                    else if ((locs & 0x80) != 0)
                    {
                        _data[sp + stride] = 0;
                    }

                    ++sp;
                }

                y += 2;
                if (num_passes > 1 && (y & 3) == 0)
                { //executed at multiples of 4
                  // This is for SPP and potentially MRP

                    if (num_passes > 2)
                    { //do MRP
                      // select the current stripe
                        int cur_sig = (y & 0x4) != 0 ? sigma1 : sigma2;
                        // the address of the data that needs updating
                        int dpp = decoded_data + (y - 4) * stride;
                        uint half = 1u << (int)(p - 2); // half the center of the bin
                        int i;
                        for (i = 0; i < width; i += 8)
                        {
                            //Process one entry from sigma array at a time
                            // Each nibble (4 bits) in the sigma array represents 4 rows,
                            // and the 32 bits contain 8 columns
                            uint cwd = magref.fetch_mrp(); // get 32 bit data
                            uint sig = (uint)flags[cur_sig++]; // 32 bit that will be processed now
                            uint col_mask = 0xFu;  // a mask for a column in sig
                            int dp = dpp + i;    // next column in decode samples
                            if (sig != 0)
                            { // if any of the 32 bits are set
                                int j;
                                for (j = 0; j < 8; ++j, dp++)
                                { //one column at a time
                                    if ((sig & col_mask) != 0)
                                    { // lowest nibble
                                        uint sample_mask = 0x11111111u & col_mask; //LSB

                                        if ((sig & sample_mask) != 0)
                                        { //if LSB is set
                                            uint sym;

                                            Debug.Assert(_data[dp] != 0); // decoded value cannot be zero
                                            sym = cwd & 1; // get it value
                                                           // remove center of bin if sym is 0
                                            _data[dp] ^= (int)((1 - sym) << (int)(p - 1));
                                            _data[dp] |= (int)half;      // put half the center of bin
                                            cwd >>= 1;          //consume word
                                        }
                                        sample_mask += sample_mask; //next row

                                        if ((sig & sample_mask) != 0)
                                        {
                                            uint sym;

                                            Debug.Assert(_data[dp + stride] != 0);
                                            sym = cwd & 1;
                                            _data[dp + stride] ^= (int)((1 - sym) << (int)(p - 1));
                                            _data[dp + stride] |= (int)half;
                                            cwd >>= 1;
                                        }
                                        sample_mask += sample_mask;

                                        if ((sig & sample_mask) != 0)
                                        {
                                            uint sym;

                                            Debug.Assert(_data[dp + 2 * stride] != 0);
                                            sym = cwd & 1;
                                            _data[dp + 2 * stride] ^= (int)((1 - sym) << (int)(p - 1));
                                            _data[dp + 2 * stride] |= (int)half;
                                            cwd >>= 1;
                                        }
                                        sample_mask += sample_mask;

                                        if ((sig & sample_mask) != 0)
                                        {
                                            uint sym;

                                            Debug.Assert(_data[dp + 3 * stride] != 0);
                                            sym = cwd & 1;
                                            _data[dp + 3 * stride] ^= (int)((1 - sym) << (int)(p - 1));
                                            _data[dp + 3 * stride] |= (int)half;
                                            cwd >>= 1;
                                        }
                                        sample_mask += sample_mask;
                                    }
                                    col_mask <<= 4; //next column
                                }
                            }
                            // consume data according to the number of bits set
                            magref.advance_mrp((int) ht_dec.population_count(sig));
                        }
                    }

                    if (y >= 4)
                    { // update mbr array at the end of each stripe
                      //generate mbr corresponding to a stripe
                        int sig = (y & 0x4) != 0 ? sigma1 : sigma2;
                        int mbr = (y & 0x4) != 0 ? mbr1 : mbr2;

                        //data is processed in patches of 8 columns, each
                        // each 32 bits in sigma1 or mbr1 represent 4 rows

                        //integrate horizontally
                        uint prev = 0; // previous columns
                        int i;
                        for (i = 0; i < width; i += 8, mbr++, sig++)
                        {
                            uint t, z;

                            flags[mbr] = flags[sig];         //start with significant samples
                            flags[mbr] |= (T1) (prev >> 28);    //for first column, left neighbors
                            flags[mbr] |= (T1)((uint)flags[sig] << 4);   //left neighbors
                            flags[mbr] |= (T1)((uint)flags[sig] >> 4);   //right neighbors
                            flags[mbr] |= (T1)((uint)flags[sig + 1] << 28);  //for last column, right neighbors
                            prev = (uint)flags[sig];           // for next group of columns

                            //integrate vertically
                            t = (uint)flags[mbr];
                            z = (uint)flags[mbr];
                            z |= (t & 0x77777777) << 1; //above neighbors
                            z |= (t & 0xEEEEEEEE) >> 1; //below neighbors
                            flags[mbr] = (T1)(z & ~(uint)flags[sig]); //remove already significance samples
                        }
                    }

                    if (y >= 8)
                    { //wait until 8 rows has been processed
                        int cur_sig, cur_mbr, nxt_sig, nxt_mbr;
                        uint prev;
                        uint val;
                        int i;

                        // add membership from the next stripe, obtained above
                        cur_sig = (y & 0x4) != 0 ? sigma2 : sigma1;
                        cur_mbr = (y & 0x4) != 0 ? mbr2 : mbr1;
                        nxt_sig = (y & 0x4) != 0 ? sigma1 : sigma2;  //future samples
                        prev = 0; // the columns before these group of 8 columns
                        for (i = 0; i < width; i += 8, cur_mbr++, cur_sig++, nxt_sig++)
                        {
                            uint t = (uint)flags[nxt_sig];
                            t |= prev >> 28;        //for first column, left neighbors
                            t |= (uint)flags[nxt_sig] << 4;   //left neighbors
                            t |= (uint)flags[nxt_sig] >> 4;   //right neighbors
                            t |= (uint)flags[nxt_sig + 1] << 28;  //for last column, right neighbors
                            prev = (uint)flags[nxt_sig];      // for next group of columns

                            if (!stripe_causal)
                            {
                                flags[cur_mbr] |= (T1)((t & 0x11111111u) << 3); //propagate up to cur_mbr
                            }
                            flags[cur_mbr] &= (T1) ~(uint)flags[cur_sig]; //remove already significance samples
                        }

                        //find new locations and get signs
                        cur_sig = (y & 0x4) != 0 ? sigma2 : sigma1;
                        cur_mbr = (y & 0x4) != 0 ? mbr2 : mbr1;
                        nxt_sig = (y & 0x4) != 0 ? sigma1 : sigma2; //future samples
                        nxt_mbr = (y & 0x4) != 0 ? mbr1 : mbr2;     //future samples
                        val = 3u << (int)(p - 2); // sample values for newly discovered
                                             // significant samples including the bin center
                        for (i = 0; i < width;
                                i += 8, cur_sig++, cur_mbr++, nxt_sig++, nxt_mbr++)
                        {
                            uint ux, tx;
                            uint mbr = (uint)flags[cur_mbr];
                            uint new_sig = 0;
                            if (mbr != 0)
                            { //are there any samples that might be significant
                                int n;
                                for (n = 0; n < 8; n += 4)
                                {
                                    uint col_mask;
                                    uint inv_sig;
                                    int end;
                                    int j;

                                    uint cwd = sigprop.fetch(); //get 32 bits
                                    uint cnt = 0;

                                    int dp = decoded_data + (y - 8) * stride;
                                    dp += i + n; //address for decoded samples

                                    col_mask = 0xFu << (4 * n); //a mask to select a column

                                    inv_sig = ~(uint)flags[cur_sig]; // insignificant samples

                                    //find the last sample we operate on
                                    end = n + 4 + i < width ? n + 4 : width - i;

                                    for (j = n; j < end; ++j, ++dp, col_mask <<= 4)
                                    {
                                        uint sample_mask;

                                        if ((col_mask & mbr) == 0)
                                        { //no samples need checking
                                            continue;
                                        }

                                        //scan mbr to find a new significant sample
                                        sample_mask = 0x11111111u & col_mask; // LSB
                                        if ((mbr & sample_mask) != 0)
                                        {
                                            Debug.Assert(_data[dp] == 0); // the sample must have been 0
                                            if ((cwd & 1) != 0)
                                            { //if this sample has become significant
                                              // must propagate it to nearby samples
                                                uint t;
                                                new_sig |= sample_mask;  // new significant samples
                                                t = 0x32u << (j * 4);// propagation to neighbors
                                                mbr |= t & inv_sig; //remove already significant samples
                                            }
                                            cwd >>= 1;
                                            ++cnt; //consume bit and increment number of
                                                   //consumed bits
                                        }

                                        sample_mask += sample_mask;  // next row
                                        if ((mbr & sample_mask) != 0)
                                        {
                                            Debug.Assert(_data[dp + stride] == 0);
                                            if ((cwd & 1) != 0)
                                            {
                                                uint t;
                                                new_sig |= sample_mask;
                                                t = 0x74u << (j * 4);
                                                mbr |= t & inv_sig;
                                            }
                                            cwd >>= 1;
                                            ++cnt;
                                        }

                                        sample_mask += sample_mask;
                                        if ((mbr & sample_mask) != 0)
                                        {
                                            Debug.Assert(_data[dp + 2 * stride] == 0);
                                            if ((cwd & 1) != 0)
                                            {
                                                uint t;
                                                new_sig |= sample_mask;
                                                t = 0xE8u << (j * 4);
                                                mbr |= t & inv_sig;
                                            }
                                            cwd >>= 1;
                                            ++cnt;
                                        }

                                        sample_mask += sample_mask;
                                        if ((mbr & sample_mask) != 0)
                                        {
                                            Debug.Assert(_data[dp + 3 * stride] == 0);
                                            if ((cwd & 1) != 0)
                                            {
                                                uint t;
                                                new_sig |= sample_mask;
                                                t = 0xC0u << (j * 4);
                                                mbr |= t & inv_sig;
                                            }
                                            cwd >>= 1;
                                            ++cnt;
                                        }
                                    }

                                    //obtain signs here
                                    if ((new_sig & (0xFFFFu << (4 * n))) != 0)
                                    { //if any
                                        dp = decoded_data + (y - 8) * stride;
                                        dp += i + n; // decoded samples address
                                        col_mask = 0xFu << (4 * n); //mask to select a column

                                        for (j = n; j < end; ++j, ++dp, col_mask <<= 4)
                                        {
                                            uint sample_mask;

                                            if ((col_mask & new_sig) == 0)
                                            { //if non is significant
                                                continue;
                                            }

                                            //scan 4 signs
                                            sample_mask = 0x11111111u & col_mask;
                                            if ((new_sig & sample_mask) != 0)
                                            {
                                                Debug.Assert(_data[dp] == 0);
                                                _data[dp] |= (int)(((cwd & 1) << 31) | val); //put value and sign
                                                cwd >>= 1;
                                                ++cnt; //consume bit and increment number
                                                       //of consumed bits
                                            }

                                            sample_mask += sample_mask;
                                            if ((new_sig & sample_mask) != 0)
                                            {
                                                Debug.Assert(_data[dp + stride] == 0);
                                                _data[dp + stride] |= (int)(((cwd & 1) << 31) | val);
                                                cwd >>= 1;
                                                ++cnt;
                                            }

                                            sample_mask += sample_mask;
                                            if ((new_sig & sample_mask) != 0)
                                            {
                                                Debug.Assert(_data[dp + 2 * stride] == 0);
                                                _data[dp + 2 * stride] |= (int)(((cwd & 1) << 31) | val);
                                                cwd >>= 1;
                                                ++cnt;
                                            }

                                            sample_mask += sample_mask;
                                            if ((new_sig & sample_mask) != 0)
                                            {
                                                Debug.Assert(_data[dp + 3 * stride] == 0);
                                                _data[dp + 3 * stride] |= (int)(((cwd & 1) << 31) | val);
                                                cwd >>= 1;
                                                ++cnt;
                                            }
                                        }

                                    }
                                    sigprop.advance((int)cnt); //consume the bits from bitstrm
                                    cnt = 0;

                                    //update the next 8 columns
                                    if (n == 4)
                                    {
                                        //horizontally
                                        uint t = new_sig >> 28;
                                        t |= ((t & 0xE) >> 1) | ((t & 7) << 1);
                                        flags[cur_mbr + 1] |= (T1) (t & ~(uint)flags[cur_sig + 1]);
                                    }
                                }
                            }
                            //update the next stripe (vertically propagation)
                            new_sig |= (uint)flags[cur_sig];
                            ux = (new_sig & 0x88888888) >> 3;
                            tx = ux | (ux << 4) | (ux >> 4); //left and right neighbors
                            if (i > 0)
                            {
                                flags[nxt_mbr - 1] |= (T1) ((ux << 28) & ~(uint)flags[nxt_sig -1]);
                            }
                            flags[nxt_mbr] |= (T1) (tx & ~(uint)flags[nxt_sig]);
                            flags[nxt_mbr + 1] |= (T1) ((ux >> 28) & ~(uint)flags[nxt_sig + 1]);
                        }

                        //clear current sigma
                        //mbr need not be cleared because it is overwritten
                        cur_sig = (y & 0x4) != 0 ? sigma2 : sigma1;
                        Array.Clear(flags, cur_sig, (int)((((uint)width + 7u) >> 3) + 1u) /*<< 2*/); //C# we remove "<< 2", since Array.Clear counts ints, not bytes. 
                    }
                }
            }

            //terminating
            if (num_passes > 1)
            {
                int st;

                if (num_passes > 2 && ((height & 3) == 1 || (height & 3) == 2))
                {
                    //do magref
                    int cur_sig = (height & 0x4) != 0 ? sigma2 : sigma1; //reversed
                    int dpp = decoded_data + (height & 0xFFFFFC) * stride;
                    uint half = 1u << (int)(p - 2);
                    int i;
                    for (i = 0; i < width; i += 8)
                    {
                        uint cwd = magref.fetch_mrp();
                        uint sig = (uint)flags[cur_sig++];
                        uint col_mask = 0xF;
                        int dp = dpp + i;
                        if (sig != 0)
                        {
                            int j;
                            for (j = 0; j < 8; ++j, dp++)
                            {
                                if ((sig & col_mask) != 0)
                                {
                                    uint sample_mask = 0x11111111 & col_mask;

                                    if ((sig & sample_mask) != 0)
                                    {
                                        uint sym;
                                        Debug.Assert(_data[dp] != 0);
                                        sym = cwd & 1;
                                        _data[dp] ^= (int)((1 - sym) << (int)(p - 1));
                                        _data[dp] |= (int)half;
                                        cwd >>= 1;
                                    }
                                    sample_mask += sample_mask;

                                    if ((sig & sample_mask) != 0)
                                    {
                                        uint sym;
                                        Debug.Assert(_data[dp + stride] != 0);
                                        sym = cwd & 1;
                                        _data[dp + stride] ^= (int)((1 - sym) << (int)(p - 1));
                                        _data[dp + stride] |= (int)half;
                                        cwd >>= 1;
                                    }
                                    sample_mask += sample_mask;

                                    if ((sig & sample_mask) != 0)
                                    {
                                        uint sym;
                                        Debug.Assert(_data[dp + 2 * stride] != 0);
                                        sym = cwd & 1;
                                        _data[dp + 2 * stride] ^= (int)((1 - sym) << (int)(p - 1));
                                        _data[dp + 2 * stride] |= (int)half;
                                        cwd >>= 1;
                                    }
                                    sample_mask += sample_mask;

                                    if ((sig & sample_mask) != 0)
                                    {
                                        uint sym;
                                        Debug.Assert(_data[dp + 3 * stride] != 0);
                                        sym = cwd & 1;
                                        _data[dp + 3 * stride] ^= (int)((1 - sym) << (int)(p - 1));
                                        _data[dp + 3 * stride] |= (int)half;
                                        cwd >>= 1;
                                    }
                                    sample_mask += sample_mask;
                                }
                                col_mask <<= 4;
                            }
                        }
                        magref.advance_mrp((int)ht_dec.population_count(sig));
                    }
                }

                //do the last incomplete stripe
                // for cases of (height & 3) == 0 and 3
                // the should have been processed previously
                if ((height & 3) == 1 || (height & 3) == 2)
                {
                    //generate mbr of first stripe
                    int sig = (height & 0x4) != 0 ? sigma2 : sigma1;
                    int mbr = (height & 0x4) != 0 ? mbr2 : mbr1;
                    //integrate horizontally
                    uint prev = 0;
                    int i;
                    for (i = 0; i < width; i += 8, mbr++, sig++)
                    {
                        uint t, z;

                        flags[mbr] = flags[sig];
                        flags[mbr] |= (T1)(prev >> 28);    //for first column, left neighbors
                        flags[mbr] |= (T1)((uint)flags[sig] << 4);   //left neighbors
                        flags[mbr] |= (T1)((uint)flags[sig] >> 4);   //left neighbors
                        flags[mbr] |= (T1)((uint)flags[sig + 1] << 28);  //for last column, right neighbors
                        prev = (uint)flags[sig];

                        //integrate vertically
                        t = (uint)flags[mbr];
                        z = (uint)flags[mbr];
                        z |= (t & 0x77777777) << 1; //above neighbors
                        z |= (t & 0xEEEEEEEE) >> 1; //below neighbors
                        flags[mbr] = (T1)(z & ~(uint)flags[sig]); //remove already significance samples
                    }
                }

                st = height;
                st -= height > 6 ? (((height + 1) & 3) + 3) : height;
                for (y = st; y < height; y += 4)
                {
                    int cur_sig, cur_mbr, nxt_sig, nxt_mbr;
                    uint val;

                    uint pattern = 0xFFFFFFFFu; // a pattern needed samples
                    if (height - y == 3)
                    {
                        pattern = 0x77777777u;
                    }
                    else if (height - y == 2)
                    {
                        pattern = 0x33333333u;
                    }
                    else if (height - y == 1)
                    {
                        pattern = 0x11111111u;
                    }

                    //add membership from the next stripe, obtained above
                    if (height - y > 4)
                    {
                        uint prev = 0;
                        
                        cur_sig = (y & 0x4) != 0 ? sigma2 : sigma1;
                        cur_mbr = (y & 0x4) != 0 ? mbr2 : mbr1;
                        nxt_sig = (y & 0x4) != 0 ? sigma1 : sigma2;
                        for (int i = 0; i < width; i += 8, cur_mbr++, cur_sig++, nxt_sig++)
                        {
                            uint t = (uint)flags[nxt_sig];
                            t |= prev >> 28;     //for first column, left neighbors
                            t |= (uint)flags[nxt_sig] << 4;   //left neighbors
                            t |= (uint)flags[nxt_sig] >> 4;   //left neighbors
                            t |= (uint)flags[nxt_sig + 1] << 28;  //for last column, right neighbors
                            prev = (uint)flags[nxt_sig];

                            if (!stripe_causal)
                            {
                                flags[cur_mbr] |= (T1) ((t & 0x11111111u) << 3);
                            }
                            //remove already significance samples
                            flags[cur_mbr] &= (T1) ~(uint)flags[cur_sig];
                        }
                    }

                    //find new locations and get signs
                    cur_sig = (y & 0x4) != 0 ? sigma2 : sigma1;
                    cur_mbr = (y & 0x4) != 0 ? mbr2 : mbr1;
                    nxt_sig = (y & 0x4) != 0 ? sigma1 : sigma2;
                    nxt_mbr = (y & 0x4) != 0 ? mbr1 : mbr2;
                    val = 3u << (int)(p - 2);
                    for (int i = 0; i < width; i += 8,
                            cur_sig++, cur_mbr++, nxt_sig++, nxt_mbr++)
                    {
                        uint mbr = (uint)flags[cur_mbr] & pattern; //skip unneeded samples
                        uint new_sig = 0;
                        uint ux, tx;
                        if (mbr != 0)
                        {
                            int n;
                            for (n = 0; n < 8; n += 4)
                            {
                                uint col_mask;
                                uint inv_sig;
                                int end;
                                int j;

                                uint cwd = sigprop.fetch();
                                uint cnt = 0;

                                int dp = decoded_data + y * stride;
                                dp += i + n;

                                col_mask = 0xFu << (4 * n);

                                inv_sig = ~(uint)flags[cur_sig] & pattern;

                                end = n + 4 + i < width ? n + 4 : width - i;
                                for (j = n; j < end; ++j, ++dp, col_mask <<= 4)
                                {
                                    uint sample_mask;

                                    if ((col_mask & mbr) == 0)
                                    {
                                        continue;
                                    }

                                    //scan 4 mbr
                                    sample_mask = 0x11111111u & col_mask;
                                    if ((mbr & sample_mask) != 0)
                                    {
                                        Debug.Assert(_data[dp] == 0);
                                        if ((cwd & 1) != 0)
                                        {
                                            uint t;
                                            new_sig |= sample_mask;
                                            t = 0x32u << (j * 4);
                                            mbr |= t & inv_sig;
                                        }
                                        cwd >>= 1;
                                        ++cnt;
                                    }

                                    sample_mask += sample_mask;
                                    if ((mbr & sample_mask) != 0)
                                    {
                                        Debug.Assert(_data[dp + stride] == 0);
                                        if ((cwd & 1) != 0)
                                        {
                                            uint t;
                                            new_sig |= sample_mask;
                                            t = 0x74u << (j * 4);
                                            mbr |= t & inv_sig;
                                        }
                                        cwd >>= 1;
                                        ++cnt;
                                    }

                                    sample_mask += sample_mask;
                                    if ((mbr & sample_mask) != 0)
                                    {
                                        Debug.Assert(_data[dp + 2 * stride] == 0);
                                        if ((cwd & 1) != 0)
                                        {
                                            uint t;
                                            new_sig |= sample_mask;
                                            t = 0xE8u << (j * 4);
                                            mbr |= t & inv_sig;
                                        }
                                        cwd >>= 1;
                                        ++cnt;
                                    }

                                    sample_mask += sample_mask;
                                    if ((mbr & sample_mask) != 0)
                                    {
                                        Debug.Assert(_data[dp + 3 * stride] == 0);
                                        if ((cwd & 1) != 0)
                                        {
                                            uint t;
                                            new_sig |= sample_mask;
                                            t = 0xC0u << (j * 4);
                                            mbr |= t & inv_sig;
                                        }
                                        cwd >>= 1;
                                        ++cnt;
                                    }
                                }

                                //signs here
                                if ((new_sig & (0xFFFFu << (4 * n))) != 0)
                                {
                                    dp = decoded_data + y * stride;
                                    dp += i + n;
                                    col_mask = 0xFu << (4 * n);

                                    for (j = n; j < end; ++j, ++dp, col_mask <<= 4)
                                    {
                                        uint sample_mask;
                                        if ((col_mask & new_sig) == 0)
                                        {
                                            continue;
                                        }

                                        //scan 4 signs
                                        sample_mask = 0x11111111u & col_mask;
                                        if ((new_sig & sample_mask) != 0)
                                        {
                                            Debug.Assert(_data[dp] == 0);
                                            _data[dp] |= (int)(((cwd & 1) << 31) | val);
                                            cwd >>= 1;
                                            ++cnt;
                                        }

                                        sample_mask += sample_mask;
                                        if ((new_sig & sample_mask) != 0)
                                        {
                                            Debug.Assert(_data[dp + stride] == 0);
                                            _data[dp + stride] |= (int)(((cwd & 1) << 31) | val);
                                            cwd >>= 1;
                                            ++cnt;
                                        }

                                        sample_mask += sample_mask;
                                        if ((new_sig & sample_mask) != 0)
                                        {
                                            Debug.Assert(_data[dp + 2 * stride] == 0);
                                            _data[dp + 2 * stride] |= (int)(((cwd & 1) << 31) | val);
                                            cwd >>= 1;
                                            ++cnt;
                                        }

                                        sample_mask += sample_mask;
                                        if ((new_sig & sample_mask) != 0)
                                        {
                                            Debug.Assert(_data[dp + 3 * stride] == 0);
                                            _data[dp + 3 * stride] |= (int)(((cwd & 1) << 31) | val);
                                            cwd >>= 1;
                                            ++cnt;
                                        }
                                    }

                                }
                                sigprop.advance((int)cnt);
                                cnt = 0;

                                //update next columns
                                if (n == 4)
                                {
                                    //horizontally
                                    uint t = new_sig >> 28;
                                    t |= ((t & 0xE) >> 1) | ((t & 7) << 1);
                                    flags[cur_mbr + 1] |= (T1)(t & ~(uint)flags[cur_sig + 1]);
                                }
                            }
                        }
                        //propagate down (vertically propagation)
                        new_sig |= (uint)flags[cur_sig];
                        ux = (new_sig & 0x88888888) >> 3;
                        tx = ux | (ux << 4) | (ux >> 4);
                        if (i > 0)
                        {
                            flags[nxt_mbr - 1] |= (T1)((ux << 28) & ~(uint)flags[nxt_sig - 1]);
                        }
                        flags[nxt_mbr] |= (T1)(tx & ~(uint)flags[nxt_sig]);
                        flags[nxt_mbr + 1] |= (T1)((ux >> 28) & ~(uint)flags[nxt_sig + 1]);
                    }
                }
            }

            {
                for (y = 0; y < height; ++y)
                {
                    sp = decoded_data + y * stride;
                    for (int x = 0; x < width; ++x, ++sp)
                    {
                        int val = _data[sp] & 0x7FFFFFFF;
                        _data[sp] = ((uint)_data[sp] & 0x80000000) != 0 ? -val : val;
                    }
                }
            }

            return true;
        }

        //2.5.3 - opj_t1_decode_cblk
        private bool DecodeCblk(TcdCblkDec cblk,
                                  uint orient,
                                  uint roishift,
                                  CCP_CBLKSTY cblksty,
                                  CompressionInfo cinfo,
                                  bool check_pterm)
        {
            byte[] cblkdata;
            uint cblkdataindex;
            //uint cblkdata_pt; //<-- No need for both pointer and index
            T1_TYPE type;
            int[] original_t1_data = null;

            //C# Sets a pointer to the T1Luts.cs lut_ctxno_zc table
            _mqc.lut_ctxno_zc_orient = (int)orient << 9;

            allocate_buffers(cblk.x1 - cblk.x0, cblk.y1 - cblk.y0);

            int bpno_plus_one = (int)(roishift + cblk.numbps);
            if (bpno_plus_one >= 31)
            {
                cinfo.ErrorMT("opj_t1_decode_cblk(): unsupported bpno_plus_one = {0} >= 31",
                           bpno_plus_one);
                return false;
            }
            uint passtype = 2;

            _mqc.ResetStates();
            _mqc.SetState(T1_CTXNO.UNI, 0, 46);
            _mqc.SetState(T1_CTXNO.AGG, 0, 3);
            _mqc.SetState(T1_CTXNO.ZC, 0, 4);

            if (cblk.corrupted)
            {
                Debug.Assert(cblk.numchunks == 0);
                return true;
            }

            //Even if we have a single chunk, in multi-threaded decoding
            //the insertion of our synthetic marker might potentially override
            //valid codestream of other codeblocks decoded in parallel.
            if (cblk.numchunks > 1 || (mustuse_cblkdatabuffer && cblk.numchunks > 0))
            {
                //Compute whole codeblock length from chunk lengths
                uint cblk_len = 0;
                {
                    for (int i = 0; i < cblk.numchunks; i++)
                        cblk_len += (uint)cblk.chunks[i].len;
                }

                /* Allocate temporary memory if needed */
                if (cblk_len + Constants.COMMON_CBLK_DATA_EXTRA > cblkdatabuffersize)
                {
                    //Array.Resize is probably slower, since it retains the old data.
                    //if (cblkdatabuffer == null)
                    cblkdatabuffer = new byte[cblk_len + Constants.COMMON_CBLK_DATA_EXTRA];
                    //else
                    //    Array.Resize(ref cblkdatabuffer, (int)cblk_len);
                }

                // Concatenate all chunks
                cblkdata = cblkdatabuffer;
                cblk_len = 0;
                for (int i = 0; i < cblk.numchunks; i++)
                {
                    Buffer.BlockCopy(cblk.chunk_data, cblk.chunks[i].data_pt, cblkdata, (int)cblk_len, cblk.chunks[i].len);
                    cblk_len += (uint)cblk.chunks[i].len;
                }

                //cblkdata_pt = 0;
                cblkdataindex = 0;
            }
            else if (cblk.numchunks == 1)
            {
                cblkdata = cblk.chunk_data;
                //cblkdata_pt = (uint)cblk.chunks[0].data_pt;
                cblkdataindex = (uint)cblk.chunks[0].data_pt;
            }
            else
            {
                // Not sure if that can happen in practice, but avoid Coverity to
                // think we will dereference a null cblkdta pointer
                //Debug.Assert(false, "Zero chunks? Check for bugs :)");
                //^ flower_foveon.jp2 ends up on this codepath all the time
                return true;
            }

            // For subtile decoding, directly decode in the decoded_data buffer of
            // the code-block. Hack t1->data to point to it, and restore it later
            if (cblk.decoded_data != null)
            {
                original_t1_data = _data;
                _data = cblk.decoded_data;
            }

            for (int segno = 0; segno < cblk.real_num_segs; ++segno)
            {
                TcdSeg seg = cblk.segs[segno];

                // BYPASS mode. Note the (int)cblk.numbps, this is to prevent a wraparound when numbps < 4
                type = ((bpno_plus_one <= ((int)cblk.numbps - 4) && (passtype < 2) &&
                       (cblksty & CCP_CBLKSTY.LAZY) == CCP_CBLKSTY.LAZY))
                       ? T1_TYPE.RAW : T1_TYPE.MQ;

                if (type == T1_TYPE.RAW)
                    _mqc.InitRawDec(cblkdata, /*cblkdata_pt +*/ cblkdataindex, seg.len, Constants.COMMON_CBLK_DATA_EXTRA);
                else
                    _mqc.InitDec(cblkdata, /*cblkdata_pt +*/ cblkdataindex, seg.len, Constants.COMMON_CBLK_DATA_EXTRA);
                cblkdataindex += seg.len;

                for (int passno = 0; passno < seg.real_num_passes && bpno_plus_one >= 1; ++passno)
                {
                    //if (cblk_count == 18)
                    //{
                    //    cblk_count = cblk_count;
                    //}
                    switch (passtype)
                    {
                        case 0:
                            if (type == T1_TYPE.RAW)
                                DecSigpassRaw(bpno_plus_one, cblksty);
                            else
                                DecSigpassMqc(bpno_plus_one, cblksty);
                            break;
                        case 1:
                            if (type == T1_TYPE.RAW)
                                DecRefpassRaw(bpno_plus_one);
                            else
                                DecRefpassMqc(bpno_plus_one);
                            break;
                        case 2:
                            DecClnpass(bpno_plus_one, cblksty); 
                            break;
                    }
                    //if (cblk_count > 74)
                    //{
                    //    DumpData("");
                    //}
                    //else
                    //    cblk_count++;

                    if ((cblksty & CCP_CBLKSTY.RESET) != 0 && type == T1_TYPE.MQ)
                    {
                        _mqc.ResetStates();
                        _mqc.SetState(T1_CTXNO.UNI, 0, 46);
                        _mqc.SetState(T1_CTXNO.AGG, 0, 3);
                        _mqc.SetState(T1_CTXNO.ZC, 0, 4);
                    }
                    if (++passtype == 3)
                    {
                        passtype = 0;
                        bpno_plus_one--;
                    }
                }

                _mqc.FinishDec();
            }

            if (check_pterm)
            {
                _mqc.CheckPTerm(cinfo);
            }

            if (cblk.decoded_data != null)
                _data = original_t1_data;

            return true;
        }

        //2.5 - opj_t1_dec_sigpass_raw
        void DecSigpassRaw(int bpno, CCP_CBLKSTY cblksty)
        {
            int data = 0; //<- pointer to _data
            int flagsp = T1_FLAGS(0, 0); //<- pointer to flags
            int w = (int)_w;
            int one = 1 << bpno;
            int half = one >> 1;
            int oneplushalf = one | half;
            uint k;

            for (k = 0; k < (_h & ~3U); k+= 4, flagsp +=2, data += 3 * w)
            {
                for (int i = 0; i < w; i++, flagsp++, data++)
                {
                    if (this.flags[flagsp] != T1.NONE)
                    {
                        DecSigpassStepRaw(flagsp, data, oneplushalf, (cblksty & CCP_CBLKSTY.VSC) != 0, 0);
                        DecSigpassStepRaw(flagsp, data + w, oneplushalf, false, 1);
                        DecSigpassStepRaw(flagsp, data + 2 * w, oneplushalf, false, 2);
                        DecSigpassStepRaw(flagsp, data + 3 * w, oneplushalf, false, 3);
                    }
                }
            }
            if (k < _h)
            {
                for (int i = 0; i < w; i++, flagsp++, data++)
                {
                    for (int j = 0; j < _h - k; j++)
                    {
                        DecSigpassStepRaw(flagsp, data + j * w, oneplushalf, (cblksty & CCP_CBLKSTY.VSC) != 0, j);
                    }
                }
            }
        }

        //2-5 T1_FLAGS (Macro). Returns int instead of pointer
        private int T1_FLAGS(uint x, uint y)
        {
            return (int) (x + 1 + ((y / 4) + 1) * (_w + 2));
        }

        /// <summary>
        /// Decode clean pass
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_t1_dec_clnpass
        /// </remarks>
        void DecClnpass(int bpno, CCP_CBLKSTY cblksty)
        {
            if (_w == 64 && _h == 64)
            {
                if ((cblksty & CCP_CBLKSTY.VSC) != 0)
                    DecClnpass64x64_vsc(bpno);
                else
                    DecClnpass64x64_novsc(bpno);
            }
            else
            {
                if ((cblksty & CCP_CBLKSTY.VSC) != 0)
                    DecClnpassGeneric_vsc(bpno);
                else
                    DecClnpassGeneric_novsc(bpno);
            }
            DecClnpassCheckSegsym(cblksty);
        }

        //2.5 - opj_t1_dec_clnpass_check_segsym
        void DecClnpassCheckSegsym(CCP_CBLKSTY cblksty)
        {
            if ((cblksty & CCP_CBLKSTY.SEGSYM) != 0)
            {
                uint v, v2;
                _mqc.Setcurctx(T1_CTXNO.UNI);
                v = _mqc.Decode() ? 1u : 0u;
                v2 = _mqc.Decode() ? 1u : 0u;
                v = (v << 1) | v2;
                v2 = _mqc.Decode() ? 1u : 0u;
                v = (v << 1) | v2;
                v2 = _mqc.Decode() ? 1u : 0u;
                v = (v << 1) | v2;
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_clnpass_64x64_vsc
        /// 
        /// Based on macro opj_t1_dec_clnpass_internal
        /// </remarks>
        void DecClnpass64x64_vsc(int bpno)
        {
            //opj_t1_dec_clnpass_internal
            //(t1, bpno,      vsc,  w,  h, flags_stride)
            // t1, bpno, OPJ_TRUE, 64, 64, 66
            const bool vsc = true;
            const int w = 64;
            const uint h = 64;
            const int flags_stride = 66;
            int one, half, oneplushalf;
            uint runlen;
            uint k; int i, j;
            int data = 0; //Pointer to this._data
            int flagsp = flags_stride + 1; //Pointer to this.flags
            //DOWNLOAD_MQC_VARIABLES
            //^ C# we don't inline mqc.
            bool v;
            one = 1 << bpno;
            half = one >> 1;
            oneplushalf = one | half;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags == 0)
                    {
                        bool partial = true;
                        _mqc.Setcurctx(T1_CTXNO.AGG); //opj_t1_setcurctx macro
                        v = _mqc.Decode(); //opj_mqc_decode_macro
                        if (!v)
                            continue;
                        _mqc.Setcurctx(T1_CTXNO.UNI); //opj_t1_setcurctx macro
                        runlen = _mqc.Decode() ? 1u : 0u;
                        v = _mqc.Decode();
                        runlen = (runlen << 1) | (v ? 1u : 0u);
                        switch(runlen)
                        {
                            case 0:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                                {
                                    const bool check_flags = false;
                                    const int data_stride = w;
                                    const int ci = 0;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!true)
                                            {
#pragma warning disable CS0162 // Unreachable code detected
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
#pragma warning restore CS0162 // Unreachable code detected
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 1;
                            case 1:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    const int data_stride = w;
                                    const int ci = 1;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 2;
                            case 2:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    const int data_stride = w;
                                    const int ci = 2;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 3;
                            case 3:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    const int data_stride = w;
                                    const int ci = 3;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                break;
                        }
                    }
                    else
                    {
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                        {
                            const bool check_flags = true;
                            const int data_stride = w;
                            const int ci = 0;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            const int data_stride = w;
                            const int ci = 1;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            const int data_stride = w;
                            const int ci = 2;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            const int data_stride = w;
                            const int ci = 3;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                    }
                    this.flags[flagsp] = flags & ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++flagsp, ++data)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecClnpassStep(flagsp, data + j * w, oneplushalf, j, vsc);
                    }
                    flags[flagsp] &= ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_clnpass_generic_vsc
        /// 
        /// Based on macro opj_t1_dec_clnpass_internal
        /// </remarks>
        void DecClnpassGeneric_vsc(int bpno)
        {
            //opj_t1_dec_clnpass_internal
            //(t1, bpno,      vsc,  w,  h, flags_stride)
            // t1, bpno, OPJ_TRUE, _w, :h, _w + 2
            const bool vsc = true;
            int w = (int)_w;
            uint h = _h;
            int flags_stride = w + 2;
            int one, half, oneplushalf;
            uint runlen;
            uint k; int i, j;
            int data = 0; //Pointer to this._data
            int flagsp = flags_stride + 1; //Pointer to this.flags
            //DOWNLOAD_MQC_VARIABLES
            //^ C# we don't inline mqc.
            bool v;
            one = 1 << bpno;
            half = one >> 1;
            oneplushalf = one | half;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags == 0)
                    {
                        bool partial = true;
                        _mqc.Setcurctx(T1_CTXNO.AGG); //opj_t1_setcurctx macro
                        v = _mqc.Decode(); //opj_mqc_decode_macro
                        if (!v)
                            continue;
                        _mqc.Setcurctx(T1_CTXNO.UNI); //opj_t1_setcurctx macro
                        runlen = _mqc.Decode() ? 1u : 0u;
                        v = _mqc.Decode();
                        runlen = (runlen << 1) | (v ? 1u : 0u);
                        switch (runlen)
                        {
                            case 0:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                                {
                                    const bool check_flags = false;
                                    int data_stride = w;
                                    const int ci = 0;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!true)
                                            {
#pragma warning disable CS0162 // Unreachable code detected
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
#pragma warning restore CS0162 // Unreachable code detected
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, vsc);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 1;
                            case 1:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    int data_stride = w;
                                    const int ci = 1;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 2;
                            case 2:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    int data_stride = w;
                                    const int ci = 2;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 3;
                            case 3:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    int data_stride = w;
                                    const int ci = 3;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                break;
                        }
                    }
                    else
                    {
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                        {
                            const bool check_flags = true;
                            int data_stride = w;
                            const int ci = 0;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, vsc);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            int data_stride = w;
                            const int ci = 1;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            int data_stride = w;
                            const int ci = 2;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            int data_stride = w;
                            const int ci = 3;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                    }
                    this.flags[flagsp] = flags & ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++flagsp, ++data)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecClnpassStep(flagsp, data + j * w, oneplushalf, j, vsc);
                    }
                    flags[flagsp] &= ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_clnpass_64x64_novsc
        /// 
        /// Based on macro opj_t1_dec_clnpass_internal
        /// </remarks>
        void DecClnpass64x64_novsc(int bpno)
        {
            //opj_t1_dec_clnpass_internal
            //(t1, bpno,      vsc,  w,  h, flags_stride)
            // t1, bpno, OPJ_FALSE, 64, 64, 66
            const bool vsc = false;
            const int w = 64;
            const uint h = 64;
            const int flags_stride = 66;
            int one, half, oneplushalf;
            uint runlen;
            uint k; int i, j;
            int data = 0; //Pointer to this._data
            int flagsp = flags_stride + 1; //Pointer to this.flags
            //DOWNLOAD_MQC_VARIABLES
            //^ C# we don't inline mqc.
            bool v;
            one = 1 << bpno;
            half = one >> 1;
            oneplushalf = one | half;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags == 0)
                    {
                        bool partial = true;
                        _mqc.Setcurctx(T1_CTXNO.AGG); //opj_t1_setcurctx macro
                        v = _mqc.Decode(); //opj_mqc_decode_macro
                        if (!v)
                            continue;
                        _mqc.Setcurctx(T1_CTXNO.UNI); //opj_t1_setcurctx macro
                        runlen = _mqc.Decode() ? 1u : 0u;
                        v = _mqc.Decode();
                        runlen = (runlen << 1) | (v ? 1u : 0u);
                        switch (runlen)
                        {
                            case 0:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                                {
                                    const bool check_flags = false;
                                    const int data_stride = w;
                                    const int ci = 0;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!true)
                                            {
#pragma warning disable CS0162 // Unreachable code detected
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
#pragma warning restore CS0162 // Unreachable code detected
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 1;
                            case 1:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    const int data_stride = w;
                                    const int ci = 1;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 2;
                            case 2:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    const int data_stride = w;
                                    const int ci = 2;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 3;
                            case 3:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    const int data_stride = w;
                                    const int ci = 3;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                break;
                        }
                    }
                    else
                    {
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                        {
                            const bool check_flags = true;
                            const int data_stride = w;
                            const int ci = 0;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            const int data_stride = w;
                            const int ci = 1;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            const int data_stride = w;
                            const int ci = 2;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            const int data_stride = w;
                            const int ci = 3;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                    }
                    this.flags[flagsp] = flags & ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++flagsp, ++data)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecClnpassStep(flagsp, data + j * w, oneplushalf, j, vsc);
                    }
                    flags[flagsp] &= ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_clnpass_generic_novsc
        /// 
        /// Based on macro opj_t1_dec_clnpass_internal
        /// </remarks>
        void DecClnpassGeneric_novsc(int bpno)
        {
            //opj_t1_dec_clnpass_internal
            //(t1, bpno,      vsc,  w,  h, flags_stride)
            // t1, bpno, OPJ_FALSE,_w, :h, _w + 2
            const bool vsc = false;
            int w = (int)_w;
            uint h = _h;
            int flags_stride = w + 2;
            int one, half, oneplushalf;
            uint runlen;
            uint k; int i, j;
            int data = 0; //Pointer to this._data
            int flagsp = flags_stride + 1; //Pointer to this.flags
            //DOWNLOAD_MQC_VARIABLES
            //^ C# we don't inline mqc.
            bool v;
            one = 1 << bpno;
            half = one >> 1;
            oneplushalf = one | half;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags == 0)
                    {
                        bool partial = true;
                        _mqc.Setcurctx(T1_CTXNO.AGG); //opj_t1_setcurctx macro
                        v = _mqc.Decode(); //opj_mqc_decode_macro
                        if (!v)
                            continue;
                        _mqc.Setcurctx(T1_CTXNO.UNI); //opj_t1_setcurctx macro
                        runlen = _mqc.Decode() ? 1u : 0u;
                        v = _mqc.Decode();
                        runlen = (runlen << 1) | (v ? 1u : 0u);
                        switch (runlen)
                        {
                            case 0:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                                {
                                    const bool check_flags = false;
                                    int data_stride = w;
                                    const int ci = 0;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!true)
                                            {
#pragma warning disable CS0162 // Unreachable code detected
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
#pragma warning restore CS0162 // Unreachable code detected
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, vsc);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 1;
                            case 1:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    int data_stride = w;
                                    const int ci = 1;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 2;
                            case 2:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    int data_stride = w;
                                    const int ci = 2;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                partial = false;
                                goto case 3;
                            case 3:
                                //opj_t1_dec_clnpass_step_macro
                                //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                                //   OPJ_FALSE, OPJ_TRUE, flags, flagsp, flags_stride, data,         l_w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                                {
                                    const bool check_flags = false;
                                    int data_stride = w;
                                    const int ci = 3;
                                    if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                                    {
                                        do
                                        {
                                            if (!partial)
                                            {
                                                var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                                _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                if (!v)
                                                    break;
                                            }
                                            {
                                                T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                    flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                                _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                                v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                                v = v ^ T1Luts.Getspb(lu);
                                                _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                                UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                            }
                                        } while (false);
                                    }
                                }
                                break;
                        }
                    }
                    else
                    {
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                        {
                            const bool check_flags = true;
                            int data_stride = w;
                            const int ci = 0;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, vsc);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            int data_stride = w;
                            const int ci = 1;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            int data_stride = w;
                            const int ci = 2;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                        //opj_t1_dec_clnpass_step_macro
                        //(check_flags,  partial, flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        //   OPJ_TRUE, OPJ_FALSE, flags, flagsp, flags_stride, data,         l_w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const bool check_flags = true;
                            int data_stride = w;
                            const int ci = 3;
                            if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                            {
                                do
                                {
                                    if (!false)
                                    {
                                        var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                        _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        if (!v)
                                            break;
                                    }
                                    {
                                        T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                            flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                                        _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                                        v = _mqc.Decode(); //<- opj_mqc_decode_macro
                                        v = v ^ T1Luts.Getspb(lu);
                                        _data[data + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                                        UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, (uint)flags_stride, false);
                                    }
                                } while (false);
                            }
                        }
                    }
                    this.flags[flagsp] = flags & ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++flagsp, ++data)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecClnpassStep(flagsp, data + j * w, oneplushalf, j, vsc);
                    }
                    flags[flagsp] &= ~(T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }
            }
        }

        //2.5 - opj_t1_dec_refpass_raw
        void DecRefpassRaw(int bpno)
        {
            int one, poshalf;
            int i, j, k;
            int data = 0; //Pointer to this._data
            int flagsp = T1_FLAGS(0, 0); //Pointer to this.flags
            int w = (int)_w;
            one = 1 << bpno;
            poshalf = one >> 1;
            for (k = 0; k < (_h & ~3U); k += 4, flagsp += 2, data += 3 * w)
            {
                for (i = 0; i < w; ++i, ++flagsp, ++data)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags != 0)
                    {
                        DecRefpassStepRaw(flagsp, data, poshalf, 0);
                        DecRefpassStepRaw(flagsp, data + w, poshalf, 1);
                        DecRefpassStepRaw(flagsp, data + 2 * w, poshalf, 2);
                        DecRefpassStepRaw(flagsp, data + 3 * w, poshalf, 3);
                    }
                }
            }
            if (k < _h)
            {
                for (i = 0; i < w; ++i, ++flagsp, ++data)
                {
                    for (j = 0; j < _h - k; ++j)
                    {
                        DecRefpassStepRaw(flagsp, data + j * w, poshalf, j);
                    }
                }
            }
        }

        //2.5 - opj_t1_dec_refpass_mqc
        void DecRefpassMqc(int bpno)
        {
            if (_w == 64 && _h == 64)
                DecRefpassMqc64x64(bpno);
            else
                DecRefpassMqcGeneric(bpno);
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_refpass_mqc_64x64
        /// 
        /// Based on macro opj_t1_dec_refpass_mqc_internal
        /// </remarks>
        void DecRefpassMqc64x64(int bpno)
        {
            //opj_t1_dec_refpass_mqc_internal
            //(t1, bpno,  w,  h, flags_stride)
            // t1, bpno, 64, 64, 66;
            const int w = 64;
            const uint h = 64;
            const int flags_stride = 66;
            int one, poshalf;
            uint k; int i, j;
            int data = 0; //Pointer to this._data
            int flagsp = flags_stride + 1; //Pointer to this.flags
            //DOWNLOAD_MQC_VARIABLES
            //^ C# We don't inline the MQC functions.
            bool v;
            one = 1 << bpno;
            poshalf = one >> 1;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags != 0)
                    {
                        //opj_t1_dec_refpass_step_mqc_macro
                        //(flags, data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
                        // flags, data,         l_w,  0, mqc, curctx, v, a, c, ct, poshalf
                        {
                            const int ci = 0;
                            const int data_stride = w;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                            {
                                uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                                _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                _data[data + ci * data_stride] += (v ^ (_data[data + ci * data_stride] < 0)) ? poshalf : -poshalf;
                                flags |= (T1)((uint)T1.MU_THIS << (ci * 3));
                            }
                        }
                        //opj_t1_dec_refpass_step_mqc_macro
                        //(flags, data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
                        // flags, data,         l_w,  1, mqc, curctx, v, a, c, ct, poshalf
                        {
                            const int ci = 1;
                            const int data_stride = w;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                            {
                                uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                                _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                _data[data + ci * data_stride] += (v ^ (_data[data + ci * data_stride] < 0)) ? poshalf : -poshalf;
                                flags |= (T1)((uint)T1.MU_THIS << (ci * 3));
                            }
                        }
                        //opj_t1_dec_refpass_step_mqc_macro
                        //(flags, data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
                        // flags, data,         l_w,  2, mqc, curctx, v, a, c, ct, poshalf
                        {
                            const int ci = 2;
                            const int data_stride = w;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                            {
                                uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                                _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                _data[data + ci * data_stride] += (v ^ (_data[data + ci * data_stride] < 0)) ? poshalf : -poshalf;
                                flags |= (T1)((uint)T1.MU_THIS << (ci * 3));
                            }
                        }
                        //opj_t1_dec_refpass_step_mqc_macro
                        //(flags, data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
                        // flags, data,         l_w,  3, mqc, curctx, v, a, c, ct, poshalf
                        {
                            const int ci = 3;
                            const int data_stride = w;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                            {
                                uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                                _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                _data[data + ci * data_stride] += (v ^ (_data[data + ci * data_stride] < 0)) ? poshalf : -poshalf;
                                flags |= (T1)((uint)T1.MU_THIS << (ci * 3));
                            }
                        }
                        this.flags[flagsp] = flags;
                    }
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecRefpassStepMqc(flagsp, data + j * w, poshalf, j);
                    }
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_refpass_mqc_generic
        /// 
        /// Based on macro opj_t1_dec_refpass_mqc_internal
        /// </remarks>
        void DecRefpassMqcGeneric(int bpno)
        {
            //opj_t1_dec_refpass_mqc_internal
            //(t1, bpno,     w,     h, flags_stride)
            // t1, bpno, t1->w, t1->h, t1->w + 2U
            int w = (int)_w;
            uint h = _h;
            int flags_stride = w + 2;
            int one, poshalf;
            uint k; int i, j;
            int data = 0; //Pointer to this._data
            int flagsp = flags_stride + 1; //Pointer to this.flags
            //DOWNLOAD_MQC_VARIABLES
            //^ C# We don't inline the MQC functions.
            bool v;
            one = 1 << bpno;
            poshalf = one >> 1;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags != 0)
                    {
                        //opj_t1_dec_refpass_step_mqc_macro
                        //(flags, data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
                        // flags, data,         l_w,  0, mqc, curctx, v, a, c, ct, poshalf
                        {
                            const int ci = 0;
                            int data_stride = w;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                            {
                                uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                                _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                _data[data + ci * data_stride] += (v ^ (_data[data + ci * data_stride] < 0)) ? poshalf : -poshalf;
                                flags |= (T1)((uint)T1.MU_THIS << (ci * 3));
                            }
                        }
                        //opj_t1_dec_refpass_step_mqc_macro
                        //(flags, data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
                        // flags, data,         l_w,  1, mqc, curctx, v, a, c, ct, poshalf
                        {
                            const int ci = 1;
                            int data_stride = w;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                            {
                                uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                                _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                _data[data + ci * data_stride] += (v ^ (_data[data + ci * data_stride] < 0)) ? poshalf : -poshalf;
                                flags |= (T1)((uint)T1.MU_THIS << (ci * 3));
                            }
                        }
                        //opj_t1_dec_refpass_step_mqc_macro
                        //(flags, data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
                        // flags, data,         l_w,  2, mqc, curctx, v, a, c, ct, poshalf
                        {
                            const int ci = 2;
                            int data_stride = w;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                            {
                                uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                                _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                _data[data + ci * data_stride] += (v ^ (_data[data + ci * data_stride] < 0)) ? poshalf : -poshalf;
                                flags |= (T1)((uint)T1.MU_THIS << (ci * 3));
                            }
                        }
                        //opj_t1_dec_refpass_step_mqc_macro
                        //(flags, data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
                        // flags, data,         l_w,  3, mqc, curctx, v, a, c, ct, poshalf
                        {
                            const int ci = 3;
                            int data_stride = w;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                                (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                            {
                                uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                                _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                _data[data + ci * data_stride] += (v ^ (_data[data + ci * data_stride] < 0)) ? poshalf : -poshalf;
                                flags |= (T1)((uint)T1.MU_THIS << (ci * 3));
                            }
                        }
                        this.flags[flagsp] = flags;
                    }
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecRefpassStepMqc(flagsp, data + j * w, poshalf, j);
                    }
                }
            }
        }

        //2.5 - opj_t1_dec_sigpass_mqc
        void DecSigpassMqc(int bpno, CCP_CBLKSTY cblksty)
        {
            if (_w == 64 && _h == 64)
            {
                if ((cblksty & CCP_CBLKSTY.VSC) != 0)
                    DecSigpassMqc64x64_vsc(bpno);
                else
                    DecSigpassMqc64x64_novsc(bpno);
            }
            else
            {
                if ((cblksty & CCP_CBLKSTY.VSC) != 0)
                    DecSigpassMqcGeneric_vsc(bpno);
                else
                    DecSigpassMqcGeneric_novsc(bpno);
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_sigpass_mqc_64x64_vsc
        /// 
        /// Based on the opj_t1_dec_sigpass_mqc_internal macro
        /// </remarks>
        void DecSigpassMqc64x64_vsc(int bpno)
        {
            const int flags_stride = 66;
            const int width = 64;
            const uint height = 64;
            const bool vsc = true;

            //opj_t1_dec_sigpass_mqc_internal
            //(bpno,      vsc,     w,     h,  flags_stride)
            // bpno, OPJ_TRUE, width, height, flags_stride
            int one, half, oneplushalf;
            uint i, j, k;
            int data = 0; // Pointer to _data
            int flagsp = flags_stride + 1;
            const int w = width;
            const uint h = height;
            //DOWNLOAD_MQC_VARIABLES
            //C# Org impl does the MQC using inline macros. We'll instead use the
            //   OpenJpeg 2.1 functions.
            bool v;

            one = 1 << bpno;
            half = one >> 1;
            oneplushalf = one | half;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags != 0)
                    {
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                        {
                            const int ci = 0;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 1;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 2;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 3;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        this.flags[flagsp] = flags;
                    }
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecSigpassStepMqc(flagsp, data + (int)j * w, oneplushalf, (int)j, flags_stride, vsc);
                    }
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_sigpass_mqc_generic_vsc
        /// 
        /// Based on the opj_t1_dec_sigpass_mqc_internal macro
        /// </remarks>
        void DecSigpassMqcGeneric_vsc(int bpno)
        {
            uint flags_stride = _w + 2u;
            const bool vsc = true;

            //opj_t1_dec_sigpass_mqc_internal
            //(bpno,      vsc,     w,     h,  flags_stride)
            // bpno, OPJ_TRUE, width, height, flags_stride
            int one, half, oneplushalf;
            uint i, j, k;
            int data = 0; // Pointer to _data
            int flagsp = (int)flags_stride + 1;
            int w = (int)_w;
            uint h = _h;
            //DOWNLOAD_MQC_VARIABLES
            //C# Org impl does the MQC using inline macros. We'll instead use the
            //   OpenJpeg 2.1 functions.
            bool v;

            one = 1 << bpno;
            half = one >> 1;
            oneplushalf = one | half;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags != 0)
                    {
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                        {
                            const int ci = 0;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 1;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 2;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 3;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        this.flags[flagsp] = flags;
                    }
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecSigpassStepMqc(flagsp, data + (int)j * w, oneplushalf, (int)j, flags_stride, vsc);
                    }
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_sigpass_mqc_64x64_novsc
        /// 
        /// Based on the opj_t1_dec_sigpass_mqc_internal macro
        /// </remarks>
        void DecSigpassMqc64x64_novsc(int bpno)
        {
            const int flags_stride = 66;
            const int width = 64;
            const uint height = 64;
            const bool vsc = false;

            //opj_t1_dec_sigpass_mqc_internal
            //(bpno,      vsc,     w,     h,  flags_stride)
            // bpno, OPJ_TRUE, width, height, flags_stride
            int one, half, oneplushalf;
            uint i, j, k;
            int data = 0; // Pointer to _data
            int flagsp = flags_stride + 1;
            const int w = width;
            const uint h = height;
            //DOWNLOAD_MQC_VARIABLES
            //C# Org impl does the MQC using inline macros. We'll instead use the
            //   OpenJpeg 2.1 functions.
            bool v;

            one = 1 << bpno;
            half = one >> 1;
            oneplushalf = one | half;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags != 0)
                    {
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                        {
                            const int ci = 0;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 1;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 2;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 3;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        this.flags[flagsp] = flags;
                    }
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecSigpassStepMqc(flagsp, data + (int)j * w, oneplushalf, (int)j, flags_stride, vsc);
                    }
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_sigpass_mqc_generic_novsc
        /// 
        /// Based on the opj_t1_dec_sigpass_mqc_internal macro
        /// </remarks>
        void DecSigpassMqcGeneric_novsc(int bpno)
        {
            uint flags_stride = _w + 2u;
            const bool vsc = false;

            //opj_t1_dec_sigpass_mqc_internal
            //(bpno,      vsc,     w,     h,  flags_stride)
            // bpno, OPJ_TRUE, width, height, flags_stride
            int one, half, oneplushalf;
            uint i, j, k;
            int data = 0; // Pointer to _data
            int flagsp = (int)flags_stride + 1;
            int w = (int)_w;
            uint h = _h;
            //DOWNLOAD_MQC_VARIABLES
            //C# Org impl does the MQC using inline macros. We'll instead use the
            //   OpenJpeg 2.1 functions.
            bool v;

            one = 1 << bpno;
            half = one >> 1;
            oneplushalf = one | half;
            for (k = 0; k < (h & ~3u); k += 4, data += 3 * w, flagsp += 2)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    T1 flags = this.flags[flagsp];
                    if (flags != 0)
                    {
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  0, mqc, curctx, v, a, c, ct, oneplushalf, vsc
                        {
                            const int ci = 0;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  1, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 1;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  2, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 2;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        //Macro: opj_t1_dec_sigpass_step_mqc_macro
                        //(flags, flagsp, flags_stride, data, data_stride, ci, mqc, curctx, v, a, c, ct, oneplushalf, vsc)
                        // flags, flagsp, flags_stride, data,           w,  3, mqc, curctx, v, a, c, ct, oneplushalf, false
                        {
                            const int ci = 3;
                            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
                            {
                                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                                v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                if (v)
                                {
                                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                                        flags,
                                                        this.flags[flagsp - 1],
                                                        this.flags[flagsp + 1],
                                                        ci);
                                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                                    bool spb = T1Luts.Getspb(lu);
                                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                                    v = v ^ spb;
                                    _data[data + ci * w] = v ? -oneplushalf : oneplushalf;
                                    UpdateFlagsMacro(ref flags, flagsp, ci, v ? 1u : 0u, flags_stride, false);
                                }
                                flags |= (T1)((uint)T1.PI_THIS << (ci * 3));
                            }
                        }
                        this.flags[flagsp] = flags;
                    }
                }
            }
            //UPLOAD_MQC_VARIABLES
            if (k < h)
            {
                for (i = 0; i < w; ++i, ++data, ++flagsp)
                {
                    for (j = 0; j < h - k; ++j)
                    {
                        DecSigpassStepMqc(flagsp, data + (int)j * w, oneplushalf, (int)j, flags_stride, vsc);
                    }
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_sigpass_step_mqc
        /// 
        /// Based on opj_t1_dec_sigpass_step_mqc_macro
        /// </remarks>
        void DecSigpassStepMqc(int flagsp, int datap, int oneplushalf, int ci, uint flags_stride, bool vsc)
        {
            //opj_t1_dec_sigpass_step_mqc_macro
            //(flags,  flagsp, flags_stride,  data, data_stride, ci, mqc,      curctx, v,      a,      c,      ct, oneplushalf, vsc)
            //*flagsp, flagsp, flags_stride, datap,           0, ci, mqc, mqc->curctx, v, mqc->a, mqc->c, mqc->ct, oneplushalf, vsc
            const int data_stride = 0;
            T1 flags = this.flags[flagsp];
            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0U &&
                                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != 0U)
            {
                uint ctxt1 = _mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                _mqc.curctx = _mqc.ctxs[ctxt1]; //<-- opj_t1_setcurctx
                bool v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                if (v)
                {
                    T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                        flags,
                                        this.flags[flagsp - 1],
                                        this.flags[flagsp + 1],
                                        ci);
                    T1_CTXNO ctxt2 = (T1_CTXNO)T1Luts.Getctxno_sc(lu);
                    bool spb = T1Luts.Getspb(lu);
                    _mqc.Setcurctx(ctxt2); //<-- opj_t1_setcurctx
                    v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                    v = v ^ spb;
                    _data[datap + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                    UpdateFlagsMacro(ref this.flags[flagsp], flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                }
                this.flags[flagsp] |= (T1)((uint)T1.PI_THIS << (ci * 3));
            }
        }

        //2.5 - opj_t1_dec_refpass_step_mqc
        void DecRefpassStepMqc(int flagsp, int datap, int poshalf, int ci)
        {
            T1 flags = this.flags[flagsp];
            //opj_t1_dec_refpass_step_mqc_macro
            //(  flags,  data, data_stride, ci, mqc, curctx, v, a, c, ct, poshalf)
            // *flagsp, datap,           0, ci, mqc, curctx, v, a, c, ct, poshalf
            {
                const int data_stride = 0;
                if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                    (T1)((uint)T1.SIGMA_THIS << (ci * 3)))
                {
                    uint ctxt = T1Luts.Getctxno_mag((T1)((uint)flags >> (ci * 3)));
                    _mqc.Setcurctx((T1_CTXNO)ctxt); //<-- opj_t1_setcurctx macro
                    bool v = _mqc.Decode(); //<-- opj_mqc_decode_macro
                    _data[datap + ci * data_stride] += (v ^ (_data[datap + ci * data_stride] < 0)) ? poshalf : -poshalf;
                    this.flags[flagsp] |= (T1)((uint)T1.MU_THIS << (ci * 3));
                }
            }
        }

        //2.5 - opj_t1_dec_refpass_step_raw
        void DecRefpassStepRaw(int flagsp, int datap, int poshalf, int ci)
        {
            T1 flag = flags[flagsp];
            if ((flag & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) ==
                ((T1)((uint)T1.SIGMA_THIS << (ci * 3))))
            {
                bool v = _mqc.RawDecode();
                _data[datap] += (v ^ (_data[datap] < 0)) ? poshalf : -poshalf;
                flags[flagsp] |= (T1)((uint)T1.MU_THIS << (ci * 3));
            }
        }

        //2.1
        void DecRefpassStepRaw(int flag_pos, int data_pos, int poshalf, int neghalf, bool vsc)
        {
            T1 flag = vsc ? (flags[flag_pos] & (~(T1.SIG_S | T1.SIG_SE | T1.SIG_SW | T1.SGN_S))) : flags[flag_pos];
            if ((flag & (T1.SIG | T1.VISIT)) == T1.SIG)
            {
                throw new NotImplementedException();
                //bool v = raw.Decode();
                //int t = v ? poshalf : neghalf;
                //_data[data_pos] += _data[data_pos] < 0 ? -t : t;
                //flags[flag_pos] |= T1.REFINE;
            }
        }

        //2.5 - opj_t1_dec_sigpass_step_raw
        void DecSigpassStepRaw(int flagsp, int datap, int oneplushalf, bool vsc, int ci)
        {
            T1 flags = this.flags[flagsp];

            if ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == T1.NONE &&
                (flags & (T1)((uint)T1.SIGMA_NEIGHBOURS << (ci * 3))) != T1.NONE)
            {
                if (_mqc.RawDecode())
                {
                    bool v = _mqc.RawDecode();
                    _data[datap] = v ? -oneplushalf : oneplushalf;
                    UpdateFlags(flagsp, ci, v ? 1u : 0u, _w + 2, vsc);
                }
                this.flags[flagsp] |= (T1)((uint)T1.PI_THIS << (ci * 3));
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_dec_clnpass_step
        /// 
        /// Based on opj_t1_dec_clnpass_step_macro
        /// </remarks>
        void DecClnpassStep(int flagsp, int datap, int oneplushalf, int ci, bool vsc)
        {
            T1 flags = this.flags[flagsp];
            //opj_t1_dec_clnpass_step_macro
            //(check_flags,  partial,   flags, flagsp, flags_stride,  data, data_stride, ci, mqc,      curctx, ..., oneplushalf, vsc)
            //   OPJ_TRUE, OPJ_FALSE, *flagsp, flagsp,   t1->w + 2U, datap,           0, ci, mqc, mqc->curctx, ..., oneplushalf, vsc
            {
                const bool check_flags = true;
                const int data_stride = 0;
                uint flags_stride = _w + 2u;
                if (!check_flags || ((flags & (T1)((uint)(T1.SIGMA_THIS | T1.PI_THIS) << (ci * 3))) == 0))
                {
                    do
                    {
                        if (!false)
                        {
                            var ctxt1 = (T1_CTXNO)_mqc.Getctxno_zc((T1)((uint)flags >> (ci * 3)));
                            _mqc.Setcurctx(ctxt1); //<- opj_t1_setcurctx macro
                            bool v = _mqc.Decode(); //<- opj_mqc_decode_macro
                            if (!v)
                                break;
                        }
                        {
                            T1 lu = T1Luts.Getctxtno_sc_or_spb_index(
                                flags, this.flags[flagsp - 1], this.flags[flagsp + 1], ci);
                            _mqc.Setcurctx(T1Luts.Getctxno_sc(lu)); //<- opj_t1_setcurctx macro
                            bool v = _mqc.Decode(); //<- opj_mqc_decode_macro
                            v = v ^ T1Luts.Getspb(lu);
                            _data[datap + ci * data_stride] = v ? -oneplushalf : oneplushalf;
                            UpdateFlagsMacro(ref this.flags[flagsp], flagsp, ci, v ? 1u : 0u, flags_stride, vsc);
                        }
                    } while (false);
                }
            }
        }

        /// <remarks>
        /// 2.5 - opj_t1_update_flags
        /// </remarks>
        void UpdateFlags(int flagsp, int ci, uint s, uint stride, bool vsc)
        {
            UpdateFlagsMacro(ref flags[flagsp], flagsp, ci, s, stride, vsc);
        }

        /// <remarks>
        /// 2.5 - opj_t1_update_flags_macro
        /// </remarks>
        void UpdateFlagsMacro(ref T1 flags, int flagsp, int ci, uint s, uint stride, bool vsc)
        {
            // East
            this.flags[flagsp - 1] |= (T1)((uint)T1.SIGMA_5 << (3 * ci));

            // Mark target as significant
            flags |= (T1)(((s << (int)T1.CHI_1_I) | (uint)T1.SIGMA_4) << (3 * ci));

            // West
            this.flags[flagsp + 1] |= (T1)((uint)T1.SIGMA_3 << (3 * ci));

            // North-west, north, north-east
            if (ci == 0 && !vsc)
            {
                int north = flagsp - (int)stride;
                this.flags[north] |= (T1)(s << (int)T1.CHI_5_I) | T1.SIGMA_16;
                this.flags[north - 1] |= T1.SIGMA_17;
                this.flags[north + 1] |= T1.SIGMA_15;
            }

            // South-west, south, south-east
            if (ci == 3)
            {
                int south = flagsp + (int)stride;
                this.flags[south] |= (T1)(s << (int)T1.CHI_0_I) | T1.SIGMA_1;
                this.flags[south - 1] |= T1.SIGMA_2;
                this.flags[south + 1] |= T1.SIGMA_0;
            }
        }

        /// <summary>
        /// Allocates buffers
        /// </summary>
        /// <param name="w">Width, max 1024</param>
        /// <param name="h">Height, max 1024</param>    
        /// <remarks>
        /// 2.5.1 - opj_t1_allocate_buffers (in ht_dec.c)
        /// 
        /// This function should be moved to the ht_dec.cs file to align with the original impl.
        /// </remarks>
        void ht_dec_allocate_buffers(int w, int h)
        {
            //w * h can at most be 4096

            {
                int new_datasize = w * h;

                if (new_datasize > datasize || _data == null)
                {
                    _data = new int[new_datasize];
                    datasize = new_datasize;
                }
                else
                {
                    //Perhaps just make a new array?
                    Array.Clear(_data, 0, _data.Length);
                }
            }

            // We expand these buffers to multiples of 16 bytes.
            // We need 4 buffers of 129 integers each, expanded to 132 integers each
            // We also need 514 bytes of buffer, expanded to 528 bytes
            int new_flagsize = 132 * /* sizeof(uint) * */ 4;
            new_flagsize += 528 / 4; // 514 expanded to multiples of 16

            if (new_flagsize > flagssize || flags == null)
            {
                flags = new T1[new_flagsize];
            }
            else
            {
                Array.Clear(flags, 0, new_flagsize);
            }
            flagssize = new_flagsize;

            _w = (uint)w;
            _h = (uint)h;
        }

        /// <summary>
        /// Allocates buffers
        /// </summary>
        /// <param name="w">Width, max 1024</param>
        /// <param name="h">Height, max 1024</param>    
        /// <remarks>
        /// 2.5 - opj_t1_allocate_buffers
        /// </remarks>
        void allocate_buffers(int w, int h)
        {
            //w * h can at most be 4096

            {
                int new_datasize = w * h;

                if (new_datasize > datasize || _data == null)
                {
                    _data = new int[new_datasize];
                    datasize = new_datasize;
                }
                else
                {
                    //Perhaps just make a new array?
                    Array.Clear(_data, 0, _data.Length);
                }
            }

            int flags_stride = w + 2;

            int flagssize = (h + 3) / 4 + 2;

            flagssize *= flags_stride;
            {
                int flags_height = (h + 3) / 4;

                if (flagssize > this.flagssize || flags == null)
                    flags = new T1[flagssize];
                else
                    Array.Clear(flags, 0, flagssize);
                this.flagssize = flagssize;

                for (int x = 0, b = ((flags_height + 1) * flags_stride); x < flags_stride; ++x)
                {
                    //magic value to hopefully stop any passes being interested in this entry
                    flags[x] = (T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                    flags[b++] = (T1.PI_0 | T1.PI_1 | T1.PI_2 | T1.PI_3);
                }

                if ((h % 4) != 0)
                {
                    T1 v = T1.NONE;
                    if (h % 4 == 1)
                    {
                        v |= T1.PI_1 | T1.PI_2 | T1.PI_3;
                    }
                    else if (h % 4 == 2)
                    {
                        v |= T1.PI_2 | T1.PI_3;
                    }
                    else if (h % 4 == 3)
                    {
                        v |= T1.PI_3;
                    }
                    for (int b = ((flags_height) * flags_stride), x = 0; x < flags_stride; ++x)
                    {
                        flags[b++] = v;
                    }
                }
            }

            _w = (uint) w;
            _h = (uint) h;
        }

        #region Helper classes

        private static uint to_smr(int x)
        {
            return x >= 0 ? (uint)x : (uint)(-x) | 0x80000000U;
        }
        private static uint smr_abs(int x)
        {
            return ((uint)x) & 0x7FFFFFFFU;
        }
        private static bool smr_sign(int x)
        {
            return (((uint)x) >> 31) != 0;
        }

        /// <summary>
        /// Function for setting and retrieving the ret value
        /// </summary>
        /// <param name="val">What to set ret to</param>
        /// <returns>Current ret value</returns>
        internal delegate bool SetRet(bool? val);

        internal class T1CBLKDecodeProcessingJob
        {
            public readonly bool whole_tile_decoding;
            public readonly uint resno;
            public readonly TcdCblkDec cblk;
            public readonly TcdBand band;
            public readonly TcdTilecomp tilec;
            public readonly TileCompParams tccp;
            public readonly bool mustuse_cblkdatabuffer;
            public bool pret
            {
                get { return set_ret(null); }
                set { set_ret(value); }
            }
            public readonly CompressionInfo cinfo;
            public readonly bool check_pterm;
            private readonly SetRet set_ret;

            public T1CBLKDecodeProcessingJob(
                bool whole_tile_decoding,
                uint resno,
                TcdCblkDec cblk,
                TcdBand band,
                TcdTilecomp tilec,
                TileCompParams tccp,
                CompressionInfo cinfo,
                bool check_pterm,
                bool mustuse_cblkdatabuffer,
                SetRet set_ret
                )
            {
                this.whole_tile_decoding = whole_tile_decoding;
                this.resno = resno;
                this.cblk = cblk;
                this.band = band;
                this.tilec = tilec;
                this.tccp = tccp;
                this.cinfo = cinfo;
                this.check_pterm = check_pterm;
                this.mustuse_cblkdatabuffer = mustuse_cblkdatabuffer;
                this.set_ret = set_ret;
            }
        }

        internal class T1CBLKEncodeProcessingJob
        {
            public readonly uint compno;
            public readonly uint resno;
            public readonly TcdCblkEnc cblk;
            public readonly TcdTile tile;
            public readonly TcdBand band;
            public readonly TcdTilecomp tilec;
            public readonly TileCompParams tccp;
            public readonly double[] mct_norms;
            public readonly uint mct_numcomps;
            private readonly SetRet set_ret;
            public bool pret
            {
                get { return set_ret(null); }
                set { set_ret(value); }
            }

            public T1CBLKEncodeProcessingJob(
                uint compno,
                uint resno,
                TcdCblkEnc cblk,
                TcdTile tile,
                TcdBand band,
                TcdTilecomp tilec,
                TileCompParams tccp,
                double[] mct_norms,
                uint mct_numcomps,
                SetRet set_ret
                )
            {
                this.compno = compno;
                this.resno = resno;
                this.cblk = cblk;
                this.tile = tile;
                this.band = band;
                this.tilec = tilec;
                this.tccp = tccp;
                this.mct_norms = mct_norms;
                this.mct_numcomps = mct_numcomps;
                this.set_ret = set_ret;
            }
        }

        #endregion
    }
}
