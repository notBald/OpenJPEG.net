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
using OPJ_UINT32 = System.UInt32;
#endregion

namespace OpenJpeg.Internal
{
    /// <summary>
    /// A packet iterator that permits to get the next
    /// packet following the progression order and change of it.
    /// </summary>
    internal sealed class PacketIterator
    {
        #region Variables and properties

        /// <summary>
        /// The parent compression info obj.
        /// </summary>
        /// <remarks>Not needed for encode</remarks>
        readonly CompressionInfo _cinfo;

        /// <summary>
        /// Enabling Tile part generation
        /// </summary>
        bool tp_on;

        /// <summary>
        /// Precise if the packet has been already used 
        /// (usefull for progression order change)
        /// </summary>
        bool[] include;

        /// <summary>
        /// Layer step used to localize the packet in the include vector
        /// </summary>
        uint step_l;

        /// <summary>
        /// Resolution step used to localize the packet in the include vector
        /// </summary>
        uint step_r;

        /// <summary>
        /// Component step used to localize the packet in the include vector
        /// </summary>
        uint step_c;

        /// <summary>
        /// Precinct step used to localize the packet in the include vector
        /// </summary>
        uint step_p;

        /// <summary>
        /// component that identify the packet
        /// </summary>
        internal uint compno;

        /// <summary>
        /// Resolution that identify the packet
        /// </summary>
        internal uint resno;

        /// <summary>
        /// Precinct that identify the packet
        /// </summary>
        internal uint precno;

        /// <summary>
        /// Quality layer that identify the packet
        /// </summary>
        internal uint layno;

        /// <summary>
        /// false if the first packet
        /// </summary>
        bool first;

        /// <summary>
        /// Progression order change information
        /// </summary>
        internal ProgOrdChang poc = new ProgOrdChang();

        /// <summary>
        /// Number of components in the image
        /// </summary>
        /// <remarks>This variable can probably be dropped for comps.Length</remarks>
        uint numcomps;

        /// <summary>
        /// Components
        /// </summary>
        PIComp[] comps;

        uint tx0, ty0, tx1, ty1;
        uint x, y, dx, dy;

        #endregion

        #region Init

        private PacketIterator(CompressionInfo cinfo)
        {
            _cinfo = cinfo;
        }

        #endregion

        //2.5 - opj_pi_update_encoding_parameters
        internal static void UpdateEncodingParameters(JPXImage image, CodingParameters cp, uint tile_no)
        {
            /* encoding parameters to set */
            uint l_max_res;
            uint l_max_prec;
            uint l_tx0, l_tx1, l_ty0, l_ty1;
            uint l_dx_min, l_dy_min;

            /* preconditions */
            Debug.Assert(tile_no < (int) (cp.tw * cp.th));

            /* get encoding parameters */
            GetEncodingParameters(image, cp, tile_no, out l_tx0, out l_tx1, out l_ty0, out l_ty1, out l_dx_min, out l_dy_min, out l_max_prec, out l_max_res);

            var tcp = cp.tcps[tile_no];

            if (tcp.POC)
            {
                UpdateEncodePOCandFinal(cp, tile_no, l_tx0, l_tx1, l_ty0, l_ty1, l_max_prec, l_max_res, l_dx_min, l_dy_min);
            }
            else
            {
                UpdateEncodeNotPOC(cp, image.numcomps, tile_no, l_tx0, l_tx1, l_ty0, l_ty1, l_max_prec, l_max_res, l_dx_min, l_dy_min);
            }
        }

        //2.5 - opj_pi_update_encode_not_poc
        static void UpdateEncodeNotPOC(CodingParameters cp,
                                       uint p_num_comps,
                                       uint p_tileno,
                                       uint p_tx0,
                                       uint p_tx1,
                                       uint p_ty0,
                                       uint p_ty1,
                                       uint p_max_prec,
                                       uint p_max_res,
                                       uint p_dx_min,
                                       uint p_dy_min)
        {
            /* loop*/
            uint pino;

            /* number of pocs*/
            uint l_poc_bound;

            /* initializations*/
            var l_tcp = cp.tcps;
            var tcp = l_tcp[p_tileno];

            /* number of iterations in the loop */
            l_poc_bound = tcp.numpocs + 1;

            for (pino = 0; pino < l_poc_bound; ++pino)
            {
                var current_poc = tcp.pocs[pino];
                current_poc.compS = 0;
                current_poc.compE = p_num_comps;/*p_image.numcomps;*/
                current_poc.resS = 0;
                current_poc.resE = p_max_res;
                current_poc.layS = 0;
                current_poc.layE = tcp.numlayers;
                current_poc.prg = tcp.prg;
                current_poc.prcS = 0;
                current_poc.prcE = p_max_prec;
                current_poc.txS = p_tx0;
                current_poc.txE = p_tx1;
                current_poc.tyS = p_ty0;
                current_poc.tyE = p_ty1;
                current_poc.dx = p_dx_min;
                current_poc.dy = p_dy_min;
            }
        }

        //2.5 - opj_pi_update_encode_poc_and_final
        static void UpdateEncodePOCandFinal(CodingParameters cp,
                                            uint p_tileno,
                                            uint p_tx0,
                                            uint p_tx1,
                                            uint p_ty0,
                                            uint p_ty1,
                                            uint p_max_prec,
                                            uint p_max_res,
                                            uint p_dx_min,
                                            uint p_dy_min)
        {
            /* loop*/
            uint pino;

            /* number of pocs*/
            uint l_poc_bound;

            //Avoids compiler warning
            //OPJ_ARG_NOT_USED(p_max_res);

            /* initializations*/
            var l_tcp = cp.tcps;
            var tcp = l_tcp[p_tileno];
            /* number of iterations in the loop */
            l_poc_bound = (uint) (tcp.numpocs + 1);

            /* start at first element, and to make sure the compiler will not make a calculation each time in the loop
               store a pointer to the current element to modify rather than l_tcp->pocs[i]*/
            var current_poc = tcp.pocs[0];

            current_poc.compS = current_poc.compno0;
            current_poc.compE = current_poc.compno1;
            current_poc.resS = current_poc.resno0;
            current_poc.resE = current_poc.resno1;
            current_poc.layE = current_poc.layno1;

            /* special treatment for the first element*/
            current_poc.layS = 0;
            current_poc.prg = current_poc.prg1;
            current_poc.prcS = 0;

            current_poc.prcE = p_max_prec;
            current_poc.txS = p_tx0;
            current_poc.txE = p_tx1;
            current_poc.tyS = p_ty0;
            current_poc.tyE = p_ty1;
            current_poc.dx = p_dx_min;
            current_poc.dy = p_dy_min;

            for (pino = 1; pino < l_poc_bound; ++pino)
            {
                current_poc = tcp.pocs[pino];
                current_poc.compS = current_poc.compno0;
                current_poc.compE = current_poc.compno1;
                current_poc.resS = current_poc.resno0;
                current_poc.resE = current_poc.resno1;
                current_poc.layE = current_poc.layno1;
                current_poc.prg = current_poc.prg1;
                current_poc.prcS = 0;
                /* special treatment here different from the first element*/
                current_poc.layS = (current_poc.layE > (tcp.pocs[pino - 1]).layE) ? current_poc.layE : 0;

                current_poc.prcE = p_max_prec;
                current_poc.txS = p_tx0;
                current_poc.txE = p_tx1;
                current_poc.tyS = p_ty0;
                current_poc.tyE = p_ty1;
                current_poc.dx = p_dx_min;
                current_poc.dy = p_dy_min;
            }
        }

        //2.5 - opj_get_encoding_packet_count
        internal static uint GetEncodingPacketCount(JPXImage image, CodingParameters cp, uint tile_no)
        {
            OPJ_UINT32 max_res;
            OPJ_UINT32 max_prec;

            // get encoding parameters
            GetAllEncodingParameters(image, cp, tile_no, out _, out _,
                                  out _, out _, out _, out _, 
                                  out max_prec, out max_res, null);

            return cp.tcps[tile_no].numlayers * max_prec * image.numcomps * max_res;
        }

        //2.5.1 - opj_get_encoding_parameters
        static void GetEncodingParameters(JPXImage image,
                                          CodingParameters cp,
                                          uint tileno,
                                          out uint p_tx0,
                                          out uint p_tx1,
                                          out uint p_ty0,
                                          out uint p_ty1,
                                          out uint p_dx_min,
                                          out uint p_dy_min,
                                          out uint p_max_prec,
                                          out uint p_max_res)
        {
	        /* loop */
	        int  compno, resno;

            /* position in x and y of tile */
            uint p, q;

	        /* initializations */
	        var tcp = cp.tcps[tileno];
	        var l_img_comp = image.comps;
	        var l_tccp = tcp.tccps;
            int l_tccp_ptr = 0;

	        /* here calculation of tx0, tx1, ty0, ty1, maxprec, dx and dy */
	        p = tileno % cp.tw;
	        q = tileno / cp.tw;

	        /* find extent of tile */
	        var tx0 = cp.tx0 + p * cp.tdx;
            p_tx0 = Math.Max(tx0, image.x0);
	        p_tx1 = Math.Min(MyMath.uint_adds(tx0, cp.tdx), image.x1);
            var ty0 = cp.ty0 + q * cp.tdy;
            p_ty0 = Math.Max(ty0, image.y0);
            p_ty1 = Math.Min(MyMath.uint_adds(ty0, cp.tdy), image.y1);

	        /* max precision is 0 (can only grow) */
	        p_max_prec = 0;
	        p_max_res = 0;

	        /* take the largest value for dx_min and dy_min */
	        p_dx_min = 0x7fffffff;
	        p_dy_min  = 0x7fffffff;

	        for (compno = 0; compno < image.numcomps; ++compno) {
		        /* arithmetic variables to calculate */
		        uint l_level_no;
		        uint l_rx0, l_ry0, l_rx1, l_ry1;
		        uint l_px0, l_py0, l_px1, py1;
		        uint l_pdx, l_pdy;
		        uint l_pw, l_ph;
		        uint l_product;
		        uint l_tcx0, l_tcy0, l_tcx1, l_tcy1;
                var img_comp = l_img_comp[compno];

                l_tcx0 = MyMath.uint_ceildiv(p_tx0, img_comp.dx);
                l_tcy0 = MyMath.uint_ceildiv(p_ty0, img_comp.dy);
                l_tcx1 = MyMath.uint_ceildiv(p_tx1, img_comp.dx);
                l_tcy1 = MyMath.uint_ceildiv(p_ty1, img_comp.dy);

                var tccp = l_tccp[l_tccp_ptr];
		        if (tccp.numresolutions > p_max_res) {
                    p_max_res = tccp.numresolutions;
		        }

		        /* use custom size for precincts */
                for (resno = 0; resno < tccp.numresolutions; ++resno)
                {
			        ulong l_dx, l_dy;

			        /* precinct width and height */
			        l_pdx = tccp.prcw[resno];
                    l_pdy = tccp.prch[resno];

			        l_dx = img_comp.dx * (1ul << (int) (l_pdx + tccp.numresolutions - 1 - resno));
			        l_dy = img_comp.dy * (1ul << (int) (l_pdy + tccp.numresolutions - 1 - resno));

			        /* take the minimum size for dx for each comp and resolution */
                    if (l_dx <= uint.MaxValue)
			            p_dx_min = Math.Min(p_dx_min, (uint) l_dx);
                    if (l_dy <= uint.MaxValue)
			            p_dy_min = Math.Min(p_dy_min, (uint) l_dy);

			        /* various calculations of extents */
			        l_level_no = (uint) (tccp.numresolutions - 1 - resno);

			        l_rx0 = MyMath.uint_ceildivpow2(l_tcx0, (int)l_level_no);
                    l_ry0 = MyMath.uint_ceildivpow2(l_tcy0, (int)l_level_no);
                    l_rx1 = MyMath.uint_ceildivpow2(l_tcx1, (int)l_level_no);
                    l_ry1 = MyMath.uint_ceildivpow2(l_tcy1, (int)l_level_no);

			        l_px0 = MyMath.uint_floordivpow2(l_rx0, (int)l_pdx) << (int)l_pdx;
                    l_py0 = MyMath.uint_floordivpow2(l_ry0, (int)l_pdy) << (int)l_pdy;
                    l_px1 = MyMath.uint_ceildivpow2(l_rx1, (int)l_pdx) << (int)l_pdx;

                    py1 = MyMath.uint_ceildivpow2(l_ry1, (int)l_pdy) << (int) l_pdy;

			        l_pw = (l_rx0==l_rx1) ? 0 : ((l_px1 - l_px0) >> (int) l_pdx);
			        l_ph = (l_ry0==l_ry1) ? 0 : ((py1 - l_py0) >> (int) l_pdy);

                    l_product = l_pw * l_ph;

			        /* update precision */
			        if (l_product > p_max_prec) {
				        p_max_prec = l_product;
			        }
		        }
                ++l_tccp_ptr;
	        }
        }

        //2.5 - opj_get_all_encoding_parameters
        internal static void GetAllEncodingParameters(JPXImage image,
                                          CodingParameters cp,
                                          uint tileno,
                                          out uint p_tx0,
                                          out uint p_tx1,
                                          out uint p_ty0,
                                          out uint p_ty1,
                                          out uint p_dx_min,
                                          out uint p_dy_min,
                                          out uint p_max_prec,
                                          out uint p_max_res,
                                          uint[][] resolutions)
        {
            /* loop */
            int compno, resno;

            uint[] lResolution;
            int lResolutionPtr;

            /* position in x and y of tile */
            uint p, q;

            /* initializations */
            var tcp = cp.tcps[tileno];
            var l_img_comp = image.comps;
            var l_tccp = tcp.tccps;
            int l_tccp_ptr = 0;

            /* here calculation of tx0, tx1, ty0, ty1, maxprec, dx and dy */
            p = tileno % cp.tw;
            q = tileno / cp.tw;

            /* find extent of tile */
            var tx0 = cp.tx0 + p * cp.tdx;
            p_tx0 = Math.Max(tx0, image.x0);
            p_tx1 = Math.Min(MyMath.uint_adds(tx0, cp.tdx), image.x1);
            var ty0 = cp.ty0 + q * cp.tdy;
            p_ty0 = Math.Max(ty0, image.y0);
            p_ty1 = Math.Min(MyMath.uint_adds(ty0, cp.tdy), image.y1);

            /* max precision is 0 (can only grow) */
            p_max_prec = 0;
            p_max_res = 0;

            /* take the largest value for dx_min and dy_min */
            p_dx_min = 0x7fffffff;
            p_dy_min = 0x7fffffff;

            for (compno = 0; compno < image.numcomps; ++compno)
            {
                /* arithmetic variables to calculate */
                uint l_level_no;
                uint l_rx0, l_ry0, l_rx1, l_ry1;
                uint l_px0, l_py0, l_px1, py1;
                uint l_pdx, l_pdy;
                uint l_pw, l_ph;
                uint l_product;
                uint l_tcx0, l_tcy0, l_tcx1, l_tcy1;
                var img_comp = l_img_comp[compno];

                lResolution = resolutions != null ? resolutions[compno] : null;
                lResolutionPtr = 0;

                l_tcx0 = MyMath.uint_ceildiv(p_tx0, img_comp.dx);
                l_tcy0 = MyMath.uint_ceildiv(p_ty0, img_comp.dy);
                l_tcx1 = MyMath.uint_ceildiv(p_tx1, img_comp.dx);
                l_tcy1 = MyMath.uint_ceildiv(p_ty1, img_comp.dy);

                var tccp = l_tccp[l_tccp_ptr];
                if (tccp.numresolutions > p_max_res)
                {
                    p_max_res = tccp.numresolutions;
                }

                /* use custom size for precincts */
                l_level_no = tccp.numresolutions;
                for (resno = 0; resno < tccp.numresolutions; ++resno)
                {
                    uint l_dx, l_dy;

                    --l_level_no;

                    /* precinct width and height */
                    l_pdx = tccp.prcw[resno];
                    l_pdy = tccp.prch[resno];
                    if (lResolution != null)
                    {
                        lResolution[lResolutionPtr++] = l_pdx;
                        lResolution[lResolutionPtr++] = l_pdy;
                    }
                    if (l_pdx + l_level_no < 32 &&
                        img_comp.dx <= uint.MaxValue / (1u << (int)(l_pdx + l_level_no)))
                    {
                        l_dx = img_comp.dx * (1u << (int)(l_pdx + l_level_no));

                        /* take the minimum size for dx for each comp and resolution */
                        p_dx_min = Math.Min(p_dx_min, l_dx);
                    }
                    if (l_pdy + l_level_no < 32 &&
                        img_comp.dy <= uint.MaxValue / (1u << (int)(l_pdy + l_level_no)))
                    {
                        l_dy = img_comp.dy * (1u << (int)(l_pdy + l_level_no));

                        /* take the minimum size for dx for each comp and resolution */
                        p_dy_min = Math.Min(p_dy_min, l_dy);
                    }                    
                    
                    l_rx0 = MyMath.uint_ceildivpow2(l_tcx0, (int)l_level_no);
                    l_ry0 = MyMath.uint_ceildivpow2(l_tcy0, (int)l_level_no);
                    l_rx1 = MyMath.uint_ceildivpow2(l_tcx1, (int)l_level_no);
                    l_ry1 = MyMath.uint_ceildivpow2(l_tcy1, (int)l_level_no);

                    l_px0 = MyMath.uint_floordivpow2(l_rx0, (int)l_pdx) << (int)l_pdx;
                    l_py0 = MyMath.uint_floordivpow2(l_ry0, (int)l_pdy) << (int)l_pdy;
                    l_px1 = MyMath.uint_ceildivpow2(l_rx1, (int)l_pdx) << (int)l_pdx;

                    py1 = MyMath.uint_ceildivpow2(l_ry1, (int)l_pdy) << (int)l_pdy;

                    l_pw = (l_rx0 == l_rx1) ? 0 : ((l_px1 - l_px0) >> (int)l_pdx);
                    l_ph = (l_ry0 == l_ry1) ? 0 : ((py1 - l_py0) >> (int)l_pdy);

                    l_product = l_pw * l_ph;

                    /* update precision */
                    if (l_product > p_max_prec)
                    {
                        p_max_prec = l_product;
                    }
                }
                ++l_tccp_ptr;
            }
        }

        /// <summary>
        /// Modify the packet iterator for enabling tile part generation
        /// </summary>
        /// <param name="pi">Handle to the packet iterator generated in pi_initialise_encode</param>
        /// <param name="cp">Coding parameters</param>
        /// <param name="tileno">Number that identifies the tile for which to list the packets</param>
        /// <param name="pino"></param>
        /// <param name="tpnum">Tile part number of the current tile</param>
        /// <param name="tppos">The position of the tile part flag in the progression order</param>
        /// <param name="t2_mode"></param>
        /// <remarks>2.5 - opj_pi_create_encode</remarks>
        internal static void CreateEncode(PacketIterator[] pi, CodingParameters cp, uint tileno, uint pino, uint tpnum, int tppos, T2_MODE t2_mode)
        {
            int incr_top = 1, resetX = 0;
            TileCodingParams tcps = cp.tcps[tileno];
            ProgOrdChang tcp = tcps.pocs[pino];

            string prog = J2K.ConvertProgressionOrder(tcp.prg);

            pi[pino].first = true;
            pi[pino].poc.prg = tcp.prg;
            
            if (!(cp.specific_param.enc.tp_on && ((!cp.IsCinema && 
                !cp.IsIMF &&
                (t2_mode == T2_MODE.FINAL_PASS)) || cp.IsCinema || cp.IsIMF)))
            {
                pi[pino].poc.resno0 = tcp.resS;
                pi[pino].poc.resno1 = tcp.resE;
                pi[pino].poc.compno0 = tcp.compS;
                pi[pino].poc.compno1 = tcp.compE;
                pi[pino].poc.layno0 = tcp.layS;
                pi[pino].poc.layno1 = tcp.layE;
                pi[pino].poc.precno0 = tcp.prcS;
                pi[pino].poc.precno1 = tcp.prcE;
                pi[pino].poc.tx0 = tcp.txS;
                pi[pino].poc.ty0 = tcp.tyS;
                pi[pino].poc.tx1 = tcp.txE;
                pi[pino].poc.ty1 = tcp.tyE;
            }
            else
            {
                for (int i = tppos + 1; i < 4; i++)
                {
                    switch (prog[i])
                    {
                        case 'R':
                            pi[pino].poc.resno0 = tcp.resS;
                            pi[pino].poc.resno1 = tcp.resE;
                            break;
                        case 'C':
                            pi[pino].poc.compno0 = tcp.compS;
                            pi[pino].poc.compno1 = tcp.compE;
                            break;                       
                        case 'L':
                            pi[pino].poc.layno0 = tcp.layS;
                            pi[pino].poc.layno1 = tcp.layE;
                            break;
                        case 'P':
                            switch (tcp.prg)
                            {
                                case PROG_ORDER.LRCP:
                                case PROG_ORDER.RLCP:
                                    pi[pino].poc.precno0 = tcp.prcS;
                                    pi[pino].poc.precno1 = tcp.prcE;
                                    break;
                                default:
                                    pi[pino].poc.tx0 = tcp.txS;
                                    pi[pino].poc.ty0 = tcp.tyS;
                                    pi[pino].poc.tx1 = tcp.txE;
                                    pi[pino].poc.ty1 = tcp.tyE;
                                    break;
                            }
                            break;
                    }
                }

                if (tpnum == 0)
                {
                    for (int i = tppos; i >= 0; i--)
                    {
                        switch (prog[i])
                        {
                            case 'C':
                                tcp.comp_t = tcp.compS;
                                pi[pino].poc.compno0 = tcp.comp_t;
                                pi[pino].poc.compno1 = tcp.comp_t + 1;
                                tcp.comp_t += 1;
                                break;
                            case 'R':
                                tcp.res_t = tcp.resS;
                                pi[pino].poc.resno0 = tcp.res_t;
                                pi[pino].poc.resno1 = tcp.res_t + 1;
                                tcp.res_t += 1;
                                break;
                            case 'L':
                                tcp.lay_t = tcp.layS;
                                pi[pino].poc.layno0 = tcp.lay_t;
                                pi[pino].poc.layno1 = tcp.lay_t + 1;
                                tcp.lay_t += 1;
                                break;
                            case 'P':
                                switch (tcp.prg)
                                {
                                    case PROG_ORDER.LRCP:
                                    case PROG_ORDER.RLCP:
                                        tcp.prc_t = tcp.prcS;
                                        pi[pino].poc.precno0 = tcp.prc_t;
                                        pi[pino].poc.precno1 = tcp.prc_t + 1;
                                        tcp.prc_t += 1;
                                        break;
                                    default:
                                        tcp.tx0_t = tcp.txS;
                                        tcp.ty0_t = tcp.tyS;
                                        pi[pino].poc.tx0 = tcp.tx0_t;
                                        pi[pino].poc.tx1 = (tcp.tx0_t + tcp.dx - (tcp.tx0_t % tcp.dx));
                                        pi[pino].poc.ty0 = tcp.ty0_t;
                                        pi[pino].poc.ty1 = (tcp.ty0_t + tcp.dy - (tcp.ty0_t % tcp.dy));
                                        tcp.tx0_t = pi[pino].poc.tx1;
                                        tcp.ty0_t = pi[pino].poc.ty1;
                                        break;
                                }
                                break;
                        }
                    }
                    incr_top = 1;
                }
                else
                {
                    for (int i = tppos; i >= 0; i--)
                    {
                        switch (prog[i])
                        {
                            case 'C':
                                pi[pino].poc.compno0 = tcp.comp_t - 1;
                                pi[pino].poc.compno1 = tcp.comp_t;
                                break;
                            case 'R':
                                pi[pino].poc.resno0 = tcp.res_t - 1;
                                pi[pino].poc.resno1 = tcp.res_t;
                                break;
                            case 'L':
                                pi[pino].poc.layno0 = tcp.lay_t - 1;
                                pi[pino].poc.layno1 = tcp.lay_t;
                                break;
                            case 'P':
                                switch (tcp.prg)
                                {
                                    case PROG_ORDER.LRCP:
                                    case PROG_ORDER.RLCP:
                                        pi[pino].poc.precno0 = tcp.prc_t - 1;
                                        pi[pino].poc.precno1 = tcp.prc_t;
                                        break;
                                    default:
                                        pi[pino].poc.tx0 = (tcp.tx0_t - tcp.dx - (tcp.tx0_t % tcp.dx));
                                        pi[pino].poc.tx1 = tcp.tx0_t;
                                        pi[pino].poc.ty0 = (tcp.ty0_t - tcp.dy - (tcp.ty0_t % tcp.dy));
                                        pi[pino].poc.ty1 = tcp.ty0_t;
                                        break;
                                }
                                break;
                        }
                        if (incr_top == 1)
                        {
                            switch (prog[i])
                            {
                                case 'R':
                                    if (tcp.res_t == tcp.resE)
                                    {
                                        if (CheckNextLevel(i - 1, cp, tileno, pino, prog))
                                        {
                                            tcp.res_t = tcp.resS;
                                            pi[pino].poc.resno0 = tcp.res_t;
                                            pi[pino].poc.resno1 = tcp.res_t + 1;
                                            tcp.res_t += 1;
                                            incr_top = 1;
                                        }
                                        else
                                        {
                                            incr_top = 0;
                                        }
                                    }
                                    else
                                    {
                                        pi[pino].poc.resno0 = tcp.res_t;
                                        pi[pino].poc.resno1 = tcp.res_t + 1;
                                        tcp.res_t += 1;
                                        incr_top = 0;
                                    }
                                    break;
                                case 'C':
                                    if (tcp.comp_t == tcp.compE)
                                    {
                                        if (CheckNextLevel(i - 1, cp, tileno, pino, prog))
                                        {
                                            tcp.comp_t = tcp.compS;
                                            pi[pino].poc.compno0 = tcp.comp_t;
                                            pi[pino].poc.compno1 = tcp.comp_t + 1;
                                            tcp.comp_t += 1;
                                            incr_top = 1;
                                        }
                                        else
                                        {
                                            incr_top = 0;
                                        }
                                    }
                                    else
                                    {
                                        pi[pino].poc.compno0 = tcp.comp_t;
                                        pi[pino].poc.compno1 = tcp.comp_t + 1;
                                        tcp.comp_t += 1;
                                        incr_top = 0;
                                    }
                                    break;
                                case 'L':
                                    if (tcp.lay_t == tcp.layE)
                                    {
                                        if (CheckNextLevel(i - 1, cp, tileno, pino, prog))
                                        {
                                            tcp.lay_t = tcp.layS;
                                            pi[pino].poc.layno0 = tcp.lay_t;
                                            pi[pino].poc.layno1 = tcp.lay_t + 1;
                                            tcp.lay_t += 1;
                                            incr_top = 1;
                                        }
                                        else
                                        {
                                            incr_top = 0;
                                        }
                                    }
                                    else
                                    {
                                        pi[pino].poc.layno0 = tcp.lay_t;
                                        pi[pino].poc.layno1 = tcp.lay_t + 1;
                                        tcp.lay_t += 1;
                                        incr_top = 0;
                                    }
                                    break;
                                case 'P':
                                    switch (tcp.prg)
                                    {
                                        case PROG_ORDER.LRCP:
                                        case PROG_ORDER.RLCP:
                                            if (tcp.prc_t == tcp.prcE)
                                            {
                                                if (CheckNextLevel(i - 1, cp, tileno, pino, prog))
                                                {
                                                    tcp.prc_t = tcp.prcS;
                                                    pi[pino].poc.precno0 = tcp.prc_t;
                                                    pi[pino].poc.precno1 = tcp.prc_t + 1;
                                                    tcp.prc_t += 1;
                                                    incr_top = 1;
                                                }
                                                else
                                                {
                                                    incr_top = 0;
                                                }
                                            }
                                            else
                                            {
                                                pi[pino].poc.precno0 = tcp.prc_t;
                                                pi[pino].poc.precno1 = tcp.prc_t + 1;
                                                tcp.prc_t += 1;
                                                incr_top = 0;
                                            }
                                            break;
                                        default:
                                            if (tcp.tx0_t >= tcp.txE)
                                            {
                                                if (tcp.ty0_t >= tcp.tyE)
                                                {
                                                    if (CheckNextLevel(i - 1, cp, tileno, pino, prog))
                                                    {
                                                        tcp.ty0_t = tcp.tyS;
                                                        pi[pino].poc.ty0 = tcp.ty0_t;
                                                        pi[pino].poc.ty1 = (tcp.ty0_t + tcp.dy - (tcp.ty0_t % tcp.dy));
                                                        tcp.ty0_t = pi[pino].poc.ty1;
                                                        incr_top = 1; resetX = 1;
                                                    }
                                                    else
                                                    {
                                                        incr_top = 0; resetX = 0;
                                                    }
                                                }
                                                else
                                                {
                                                    pi[pino].poc.ty0 = tcp.ty0_t;
                                                    pi[pino].poc.ty1 = (tcp.ty0_t + tcp.dy - (tcp.ty0_t % tcp.dy));
                                                    tcp.ty0_t = pi[pino].poc.ty1;
                                                    incr_top = 0; resetX = 1;
                                                }
                                                if (resetX == 1)
                                                {
                                                    tcp.tx0_t = tcp.txS;
                                                    pi[pino].poc.tx0 = tcp.tx0_t;
                                                    pi[pino].poc.tx1 = (tcp.tx0_t + tcp.dx - (tcp.tx0_t % tcp.dx));
                                                    tcp.tx0_t = pi[pino].poc.tx1;
                                                }
                                            }
                                            else
                                            {
                                                pi[pino].poc.tx0 = tcp.tx0_t;
                                                pi[pino].poc.tx1 = (tcp.tx0_t + tcp.dx - (tcp.tx0_t % tcp.dx));
                                                tcp.tx0_t = pi[pino].poc.tx1;
                                                incr_top = 0;
                                            }
                                            break;
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
        }

        //2.1
        static bool CheckNextLevel(int pos, CodingParameters cp, uint tileno, uint pino, string prog)
        {
            Debug.Assert(false, "Unconverted code");
	        var tcps = cp.tcps[tileno];
	        var tcp = tcps.pocs[pino];

	        if(pos>=0)
            {
		        for(int i=pos;pos>=0;i--)
                {
			        switch(prog[i])
                    {
		            case 'R':
			            if(tcp.res_t==tcp.resE)
                        {
                            return CheckNextLevel(pos - 1, cp, tileno, pino, prog);  
			            }
				        return true;
		            case 'C':
			            if(tcp.comp_t==tcp.compE)
                        {
                            return CheckNextLevel(pos - 1, cp, tileno, pino, prog);
			            }
			            return true;
		            case 'L':
			            if(tcp.lay_t==tcp.layE)
                        {
                            return CheckNextLevel(pos - 1, cp, tileno, pino, prog);
			            }
				        return true;
		            case 'P':
			            switch(tcp.prg)
                        {
                            case PROG_ORDER.RLCP:
                            case PROG_ORDER.LRCP:
					            if(tcp.prc_t == tcp.prcE)
                                {
                                    return CheckNextLevel(i - 1, cp, tileno, pino, prog);
					            }
						        return true;
			                default:
				                if(tcp.tx0_t == tcp.txE)
                                {
					                /*TY*/
					                if(tcp.ty0_t == tcp.tyE)
                                    {
                                        return CheckNextLevel(i - 1, cp, tileno, pino, prog); 
					                }
				                }
					            return true;
			            }/*end case P*/
		            }/*end switch*/
		        }/*end for*/
	        }/*end if*/
	        return false;
        }

        /// <summary>
        /// Creates a packet iterator for encoding
        /// </summary>
        /// <param name="image">The image being encoded</param>
        /// <param name="cp">The coding parameters</param>
        /// <param name="tileno">Index of the tile being encoded</param>
        /// <param name="t2_mode">The type of pass for generating the packet iterator</param>
        /// <param name="cinfo">Event manager</param>
        /// <remarks>2.5 - opj_pi_initialise_encode</remarks>
        internal static PacketIterator[] InitialiseEncode(JPXImage image, 
            CodingParameters cp, uint tileno, T2_MODE t2_mode, CompressionInfo cinfo)
        {
            uint numcomps = image.numcomps;

            // Encoding prameters to set
            OPJ_UINT32 max_res;
            OPJ_UINT32 max_prec;
            OPJ_UINT32 tx0, tx1, ty0, ty1;
            OPJ_UINT32 dx_min, dy_min;
            OPJ_UINT32 step_p, step_c, step_r, step_l;

            TileCodingParams tcp = cp.tcps[tileno];
            //uint bound = tcp.numpocs + 1; //<-- using pi_ar.Length instead

            //to store w, h, dx and dy for all components and resolutions
            const int data_stride = 4 * Constants.J2K_MAXRLVLS;
            OPJ_UINT32[] tmp_data = new OPJ_UINT32[image.numcomps * data_stride];
            OPJ_UINT32[] tmp_ptr = new OPJ_UINT32[image.numcomps];

            // Memory allocation for pi
            PacketIterator[] pi_ar = Create(image, cp, tileno, cinfo);

            // Updates pointer array
            for (uint compno = 1; compno < tmp_ptr.Length; compno++)
                tmp_ptr[compno] = compno * data_stride;

            GetAllEncodingParameters(image, cp, tileno, out tx0, out tx1, out ty0, out ty1, out dx_min, out dy_min, out max_prec, out max_res, tmp_ptr, ref tmp_data);

            // Step calculations
            step_p = 1;
            step_c = max_prec * step_p;
            step_r = image.numcomps * step_c;
            step_l = max_res * step_r;

            // set values on the first packet iterator
            var current_pi = pi_ar[0];
            current_pi.tp_on = cp.specific_param.enc.tp_on;
            current_pi.include = new bool[tcp.numlayers * step_l];

            // Special treatment for the first packet iterator
            PIComp[] current_comps = current_pi.comps;
            var img_comps = image.comps;
            //var tccps = tcp.tccps; //<-- C# Not used of anything
            current_pi.tx0 = tx0;
            current_pi.ty0 = ty0;
            current_pi.tx1 = tx1;
            current_pi.ty1 = ty1;
            current_pi.dx = dx_min;
            current_pi.dy = dy_min;
            current_pi.step_p = step_p;
            current_pi.step_c = step_c;
            current_pi.step_r = step_r;
            current_pi.step_l = step_l;

            // Allocation for components and number of components has already been calculated by opj_pi_create
            for (int compno = 0; compno < numcomps; ++compno)
            {
                PIComp current_comp = current_comps[compno];
                ImageComp img_comp = img_comps[compno];

                PIResolution[] res_ar = current_comp.resolutions;
                int encoding_value_ptr = (int)tmp_ptr[compno];

                current_comp.dx = img_comp.dx;
                current_comp.dy = img_comp.dy;

                /* resolutions have already been initialized */
                for (int resno = 0; resno < current_comp.numresolutions; resno++)
                {
                    PIResolution res = res_ar[resno];
                    res.pdx = tmp_data[encoding_value_ptr++];
                    res.pdy = tmp_data[encoding_value_ptr++];
                    res.pw = tmp_data[encoding_value_ptr++];
                    res.ph = tmp_data[encoding_value_ptr++];
                }
            }

            for (int pino = 1; pino < pi_ar.Length; ++pino)
            {
                current_pi = pi_ar[pino];
                current_comps = current_pi.comps;
                img_comps = image.comps;

                current_pi.tx0 = tx0;
                current_pi.ty0 = ty0;
                current_pi.tx1 = tx1;
                current_pi.ty1 = ty1;
                current_pi.dx = dx_min;
                current_pi.dy = dy_min;
                current_pi.step_p = step_p;
                current_pi.step_c = step_c;
                current_pi.step_r = step_r;
                current_pi.step_l = step_l;

                // Allocation for components and number of components has already been calculated by opj_pi_create
                for (int compno = 0; compno < numcomps; ++compno)
                {
                    PIComp current_comp = current_comps[compno];
                    ImageComp img_comp = img_comps[compno];

                    PIResolution[] res_ar = current_comp.resolutions;
                    int encoding_value_ptr = (int)tmp_ptr[compno];

                    current_comp.dx = img_comp.dx;
                    current_comp.dy = img_comp.dy;
                    /* resolutions have already been initialized */
                    for (int resno = 0; resno < current_comp.numresolutions; resno++)
                    {
                        PIResolution l_res = res_ar[resno];
                        l_res.pdx = tmp_data[encoding_value_ptr++];
                        l_res.pdy = tmp_data[encoding_value_ptr++];
                        l_res.pw = tmp_data[encoding_value_ptr++];
                        l_res.ph = tmp_data[encoding_value_ptr++];
                    }
                }

                // special treatment
                current_pi.include = pi_ar[pino - 1].include;
            }

            if (tcp.POC && (cp.IsCinema || t2_mode == T2_MODE.FINAL_PASS))
            {
                UpdateEncodePocAndFinal(cp, tileno, tx0, tx1, ty0, ty1, max_prec, max_res, dx_min, dy_min);
            }
            else
            {
                UpdateEncodeNotPoc(cp, numcomps, (OPJ_UINT32)tileno, tx0, tx1, ty0, ty1, max_prec, max_res, dx_min, dy_min);
            }

            return pi_ar;
        }

        //2.5 - opj_pi_update_encode_poc_and_final
        static void UpdateEncodePocAndFinal(CodingParameters cp,
                                            OPJ_UINT32 tileno,
                                            OPJ_UINT32 tx0,
                                            OPJ_UINT32 tx1,
                                            OPJ_UINT32 ty0,
                                            OPJ_UINT32 ty1,
                                            OPJ_UINT32 max_prec,
                                            OPJ_UINT32 max_res,
                                            OPJ_UINT32 dx_min,
                                            OPJ_UINT32 dy_min)
        {
            // preconditions in debug
            Debug.Assert(cp != null);
            Debug.Assert(tileno < cp.tw * cp.th);

            // initializations
            TileCodingParams tcp = cp.tcps[tileno];

            /* start at first element, and to make sure the compiler will not make a calculation each time in the loop
               store a pointer to the current element to modify rather than l_tcp.pocs[i]*/
            var current_poc = tcp.pocs[0];

            current_poc.compS = current_poc.compno0;
            current_poc.compE = current_poc.compno1;
            current_poc.resS = current_poc.resno0;
            current_poc.resE = current_poc.resno1;
            current_poc.layE = current_poc.layno1;

            // special treatment for the first element
            current_poc.layS = 0;
            current_poc.prg = current_poc.prg1;
            current_poc.prcS = 0;

            current_poc.prcE = max_prec;
            current_poc.txS = (OPJ_UINT32)tx0;
            current_poc.txE = (OPJ_UINT32)tx1;
            current_poc.tyS = (OPJ_UINT32)ty0;
            current_poc.tyE = (OPJ_UINT32)ty1;
            current_poc.dx = dx_min;
            current_poc.dy = dy_min;

            for (int pino = 1; pino < tcp.pocs.Length; ++pino)
            {
                current_poc = tcp.pocs[pino];

                current_poc.compS = current_poc.compno0;
                current_poc.compE = current_poc.compno1;
                current_poc.resS = current_poc.resno0;
                current_poc.resE = current_poc.resno1;
                current_poc.layE = current_poc.layno1;
                current_poc.prg = current_poc.prg1;
                current_poc.prcS = 0;
                // special treatment here different from the first element
                current_poc.layS = (current_poc.layE > tcp.pocs[pino - 1].layE) ? current_poc.layE : 0;

                current_poc.prcE = max_prec;
                current_poc.txS = (OPJ_UINT32)tx0;
                current_poc.txE = (OPJ_UINT32)tx1;
                current_poc.tyS = (OPJ_UINT32)ty0;
                current_poc.tyE = (OPJ_UINT32)ty1;
                current_poc.dx = dx_min;
                current_poc.dy = dy_min;
            }
        }

        /// <summary>
        /// Updates the coding parameters if the encoding is not used with Progression 
        /// order changes and final (and cinema parameters are used).
        /// </summary>
        /// <param name="cp">The coding parameters to modify</param>
        /// <param name="num_comps">The number of components</param>
        /// <param name="tileno">The tile index being concerned</param>
        /// <param name="tx0">X0 parameter for the tile</param>
        /// <param name="tx1">X1 parameter for the tile</param>
        /// <param name="ty0">Y0 parameter for the tile</param>
        /// <param name="ty1">Y1 parameter for the tile</param>
        /// <param name="max_prec">The maximum precision for all the bands of the tile</param>
        /// <param name="max_res">The maximum number of resolutions for all the poc inside the tile</param>
        /// <param name="dx_min">The minimum dx of all the components of all the resolutions for the tile</param>
        /// <param name="dy_min">The minimum dy of all the components of all the resolutions for the tile</param>
        /// <remarks>2.5 - opj_pi_update_encode_not_poc</remarks>
        static void UpdateEncodeNotPoc(CodingParameters cp,
                                       OPJ_UINT32 num_comps,
                                       OPJ_UINT32 tileno,
                                       uint tx0,
                                       uint tx1,
                                       uint ty0,
                                       uint ty1,
                                       OPJ_UINT32 max_prec,
                                       OPJ_UINT32 max_res,
                                       OPJ_UINT32 dx_min,
                                       OPJ_UINT32 dy_min)
        {
            /* preconditions in debug*/
            Debug.Assert(cp != null);
            Debug.Assert(tileno < cp.tw * cp.th);

            /* initializations*/
            TileCodingParams tcp = cp.tcps[tileno];

            //poc_bound = l_tcp->numpocs + 1; //<-- useing tcp.pocs.Length instead

            for (int pino = 0; pino < tcp.pocs.Length; ++pino)
            {
                ProgOrdChang current_poc = tcp.pocs[pino];

                current_poc.compS = 0;
                current_poc.compE = num_comps;/*p_image.numcomps;*/
                current_poc.resS = 0;
                current_poc.resE = max_res;
                current_poc.layS = 0;
                current_poc.layE = tcp.numlayers;
                current_poc.prg = tcp.prg;
                current_poc.prcS = 0;
                current_poc.prcE = max_prec;
                current_poc.txS = tx0;
                current_poc.txE = tx1;
                current_poc.tyS = ty0;
                current_poc.tyE = ty1;
                current_poc.dx = dx_min;
                current_poc.dy = dy_min;
            }
        }

        /// <remarks>
        /// 2.5
        ///C# About resolution_ptrs and resolutions.
        ///In the org impl, resolution_ptrs is pointers into the resolutions
        ///array. Since we do not have pointers we instead update the data directly.
        ///
        /// An alternative solution would be to use uint?, as that could achive the
        /// same result as the org impl.
        /// </remarks>
        static void GetAllEncodingParameters(JPXImage image,
                                             CodingParameters cp,
                                             OPJ_UINT32 tileno,
                                             out uint tx0,
                                             out uint tx1,
                                             out uint ty0,
                                             out uint ty1,
                                             out OPJ_UINT32 dx_min,
                                             out OPJ_UINT32 dy_min,
                                             out OPJ_UINT32 max_prec,
                                             out OPJ_UINT32 max_res,
                                             OPJ_UINT32[] resolution_ptrs,
                                             ref OPJ_UINT32[] resolutions)
        {
            //C# I don't think there's a need for this feature, and it simplifies the impl.
            if (resolution_ptrs == null)
                throw new NotImplementedException("Null argument for resolution_ptrs");
            
            // loop
	        OPJ_UINT32 compno, resno;

	        // to store l_dx, l_dy, w and h for each resolution and component.
	        OPJ_UINT32 resolution_ptr;

	        // preconditions in debug
	        Debug.Assert(cp != null);
            Debug.Assert(image != null);
            Debug.Assert(tileno < cp.tw * cp.th);

	        // initializations
	        TileCodingParams tcp = cp.tcps[tileno];
	        TileCompParams[] tccps = tcp.tccps;
	        ImageComp[] img_comps = image.comps;

	        // position in x and y of tile
            OPJ_UINT32 p = tileno % (OPJ_UINT32) cp.tw;
            OPJ_UINT32 q = tileno / (OPJ_UINT32) cp.tw;

            // here calculation of tx0, tx1, ty0, ty1 
            {
                uint l_tx0 = cp.tx0 + p * cp.tdx;
                tx0 = Math.Max(l_tx0, image.x0);
                tx1 = Math.Min(MyMath.uint_adds(l_tx0, cp.tdx), image.x1);
                uint l_ty0 = cp.ty0 + q * cp.tdy;
                ty0 = Math.Max(l_ty0, image.y0);
                ty1 = Math.Min(MyMath.uint_adds(l_ty0, cp.tdy), image.y1);
            }

	        // max precision and resolution is 0 (can only grow)
	        max_prec = 0;
	        max_res = 0;

	        // take the largest value for dx_min and dy_min
	        dx_min = 0x7fffffff;
	        dy_min = 0x7fffffff;

	        for (compno = 0; compno < image.numcomps; ++compno) 
            {
		        // aritmetic variables to calculate
		        OPJ_UINT32 level_no;
                OPJ_UINT32 rx0, ry0, rx1, ry1;
                OPJ_UINT32 px0, py0, px1, py1;
		        OPJ_UINT32 product;
                OPJ_UINT32 tcx0, tcy0, tcx1, tcy1;
		        OPJ_UINT32 pdx, pdy, pw, ph;

                resolution_ptr = resolution_ptrs[compno];

                //C# Sets the "pointers"
                var img_comp = img_comps[compno];
                var tccp = tccps[compno];

		        tcx0 = MyMath.uint_ceildiv(tx0, img_comp.dx);
                tcy0 = MyMath.uint_ceildiv(ty0, img_comp.dy);
                tcx1 = MyMath.uint_ceildiv(tx1, img_comp.dx);
                tcy1 = MyMath.uint_ceildiv(ty1, img_comp.dy);

		        if (tccp.numresolutions > max_res)
			        max_res = tccp.numresolutions;

		        // use custom size for precincts
		        level_no = tccp.numresolutions;
		        for (resno = 0; resno < tccp.numresolutions; ++resno) 
                {
			        OPJ_UINT32 dx, dy;

                    --level_no;

                    // precinct width and height
                    pdx = tccp.prcw[resno];
			        pdy = tccp.prch[resno];
                    resolutions[resolution_ptr++] = pdx;
                    resolutions[resolution_ptr++] = pdy;
                    if (pdx + level_no < 32 &&
                        img_comp.dx <= uint.MaxValue / (1u << (int)(pdx + level_no)))
                    {
                        dx = img_comp.dx * (1u << (int)(pdx + level_no));
                        // take the minimum size for l_dx for each comp and resolution
                        dx_min = Math.Min(dx_min, dx);
                    }
                    if (pdy + level_no < 32 &&
                        img_comp.dy <= uint.MaxValue / (1u << (int)(pdy + level_no)))
                    {
                        dy = img_comp.dy * (1u << (int)(pdy + level_no));
                        dy_min = Math.Min(dy_min, dy);
                    }

			        /* various calculations of extents*/
                    rx0 = MyMath.uint_ceildivpow2(tcx0, (int)level_no);
                    ry0 = MyMath.uint_ceildivpow2(tcy0, (int)level_no);
                    rx1 = MyMath.uint_ceildivpow2(tcx1, (int)level_no);
                    ry1 = MyMath.uint_ceildivpow2(tcy1, (int)level_no);
                    px0 = MyMath.uint_floordivpow2(rx0, (int)pdx) << (int)pdx;
                    py0 = MyMath.uint_floordivpow2(ry0, (int)pdy) << (int)pdy;
                    px1 = MyMath.uint_ceildivpow2(rx1, (int)pdx) << (int)pdx;
                    py1 = MyMath.uint_ceildivpow2(ry1, (int)pdy) << (int)pdy;
			        pw = (rx0==rx1)?0:(OPJ_UINT32)((px1 - px0) >> (int)pdx);
			        ph = (ry0==ry1)?0:(OPJ_UINT32)((py1 - py0) >> (int)pdy);
                    resolutions[resolution_ptr++] = pw;
                    resolutions[resolution_ptr++] = ph;
			        product = pw * ph;
			
                    // update precision
			        if (product > max_prec) {
				        max_prec = product;
			        }
		        }
                //C# Snip pointer increments
	        }
        }

        /// <remarks>
        /// 2.5
        /// Org method: pi.c opj_pi_iterator_t *opj_pi_create(opj_image_t *image, opj_cp_t *cp, int tileno)
        /// </remarks>
        internal static PacketIterator[] Create(JPXImage image, CodingParameters cp, uint tileno, CompressionInfo cinfo)
        {
            TileCodingParams tcp = cp.tcps[tileno];
            //uint poc_bound = tcp.numpocs + 1; C# uses pi_ar.Length instead

            var pi_ar = new PacketIterator[tcp.numpocs + 1];

            //C# current_pi is set inside the loop
            for (int pino = 0; pino < pi_ar.Length; pino++)
            {
                TileCompParams tccp;
                var current_pi = pi_ar[pino] = new PacketIterator(cinfo);

                current_pi.numcomps = image.numcomps;
                current_pi.comps = new PIComp[image.numcomps];

                for (int compno = 0; compno < current_pi.comps.Length; compno++)
                {
                    var comp = current_pi.comps[compno] = new PIComp();

                    tccp = tcp.tccps[compno];

                    comp.resolutions = new PIResolution[tccp.numresolutions];
                    comp.numresolutions = tccp.numresolutions;

                    for (int resno = 0; resno < comp.resolutions.Length; resno++)
                        comp.resolutions[resno] = new PIResolution();
                }     
            }

            return pi_ar;
        }

        /// <remarks>
        /// 2.5
        /// Org method: pi.c opj_pi_iterator_t *pi_create_decode(opj_image_t *image, opj_cp_t *cp, int tileno)
        /// </remarks>
        internal static PacketIterator[] CreateDecode(JPXImage image, CodingParameters cp, uint tileno, CompressionInfo cinfo)
        {
            TileCodingParams tcp = cp.tcps[tileno];
            //uint bound = tcp.numpocs + 1; C# uses pi_ar.Length instead

            //to store w, h, dx and dy fro all components and resolutions
            const int data_stride = 4 * Constants.J2K_MAXRLVLS;
            OPJ_UINT32[] tmp_data = new OPJ_UINT32[image.numcomps * data_stride];
            OPJ_UINT32[] tmp_ptr = new OPJ_UINT32[image.numcomps];

            /* encoding prameters to set*/
            OPJ_UINT32 max_res;
            OPJ_UINT32 max_prec;
            OPJ_UINT32 tx0, tx1, ty0, ty1;
            OPJ_UINT32 dx_min, dy_min;
            OPJ_UINT32 step_p, step_c, step_r, step_l;

            var pi_ar = Create(image, cp, (uint) tileno, cinfo);

            // C# l_encoding_value_ptr is a pointer, so we don't use it. The workaround is
            //    to add tmp_data as a parameter to GetAllEncodingParameters

            //Updates pointer array. Starting from 1, as first position is always 0
            for (uint compno = 1; compno < tmp_ptr.Length; compno++)
                tmp_ptr[compno] = compno * data_stride;

            GetAllEncodingParameters(image, cp, (OPJ_UINT32)tileno, out tx0, out tx1, out ty0, out ty1, out dx_min, out dy_min, out max_prec, out max_res, tmp_ptr, ref tmp_data);

            // step calculations
            step_p = 1;
            step_c = max_prec * step_p;
            step_r = image.numcomps * step_c;
            step_l = max_res * step_r;

            // set values on the first packet iterator
            var current_pi = pi_ar[0];
            current_pi.include = null;
            if (step_l <= int.MaxValue / (tcp.numlayers + 1))
            {
                //C# impl. Instead of shorts we use bool, which takes 2 x the amount of
                //         memory, but looks cleaner.
                current_pi.include = new bool[(tcp.numlayers + 1) * step_l];
            }

            if (current_pi.include == null)
            {
                return null;
            }

            // Special treatment for the first packet iterator
            PIComp[] current_comps = current_pi.comps;
            var img_comps = image.comps;
            //var tccps = tcp.tccps;

            current_pi.tx0 = tx0;
            current_pi.ty0 = ty0;
            current_pi.tx1 = tx1;
            current_pi.ty1 = ty1;

            current_pi.step_p = step_p;
            current_pi.step_c = step_c;
            current_pi.step_r = step_r;
            current_pi.step_l = step_l;

            // allocation for components and number of components has already been calculated by opj_pi_create
            for (int compno = 0; compno < current_pi.numcomps; ++compno)
            {
                //C# Updating pointers
                PIComp current_comp = current_comps[compno];
                ImageComp img_comp = img_comps[compno];

                PIResolution[] res_ar = current_comp.resolutions;

                //C# impl. This is a pointer into tmp_data, which is why we
                //         have the tmp_data as an extra parameter to this func
                uint encoding_value_ptr = tmp_ptr[compno];

                current_comp.dx = img_comp.dx;
                current_comp.dy = img_comp.dy;

                /* resolutions have already been initialized */
                for (int resno = 0; resno < current_comp.numresolutions; resno++)
                {
                    //C# Updates pointers
                    PIResolution res = res_ar[resno];

                    res.pdx = tmp_data[encoding_value_ptr++];
                    res.pdy = tmp_data[encoding_value_ptr++];
                    res.pw = tmp_data[encoding_value_ptr++];
                    res.ph = tmp_data[encoding_value_ptr++];
                }
            }

            for (int pino = 1; pino < pi_ar.Length; ++pino)
            {
                //C# Updates the ponters
                current_pi = pi_ar[pino];

                current_comps = current_pi.comps;
                img_comps = image.comps;
                //C# tccp is not used in this loop

                current_pi.tx0 = tx0;
                current_pi.ty0 = ty0;
                current_pi.tx1 = tx1;
                current_pi.ty1 = ty1;
                //current_pi.dx = dx_min;
                //current_pi.dy = dy_min;
                current_pi.step_p = step_p;
                current_pi.step_c = step_c;
                current_pi.step_r = step_r;
                current_pi.step_l = step_l;

                // allocation for components and number of components has already been calculated by opj_pi_create
                for (int compno = 0; compno < current_pi.numcomps; ++compno)
                {
                    //C# Updates the pointers
                    PIComp current_comp = current_comps[compno];
                    ImageComp img_comp = img_comps[compno];

                    PIResolution[] res_ar = current_comp.resolutions;
                    uint encoding_value_ptr = tmp_ptr[compno];

                    current_comp.dx = img_comp.dx;
                    current_comp.dy = img_comp.dy;
                    /* resolutions have already been initialized */
                    for (int resno = 0; resno < current_comp.numresolutions; resno++)
                    {
                        //C# updates the pointers
                        PIResolution l_res = res_ar[resno];

                        l_res.pdx = tmp_data[encoding_value_ptr++];
                        l_res.pdy = tmp_data[encoding_value_ptr++];
                        l_res.pw = tmp_data[encoding_value_ptr++];
                        l_res.ph = tmp_data[encoding_value_ptr++];
                    }
                }

                // special treatment
                current_pi.include = pi_ar[pino - 1].include;
            }

            if (tcp.POC)
                UpdateDecodePoc(pi_ar, tcp, max_prec, max_res);
            else
                UpdateDecodeNotPoc(pi_ar, tcp, max_prec, max_res);

            return pi_ar;
        }

        //2.5
        static void UpdateDecodeNotPoc(PacketIterator[] pi_ar, TileCodingParams tcp, OPJ_UINT32 max_precision, OPJ_UINT32 max_res)
        {
            //C# instead of l_bound we use pi_ar.Length
            Debug.Assert(pi_ar.Length == tcp.numpocs + 1);

            for (int pino = 0; pino < pi_ar.Length; pino++)
            {
                //C# updates the pointers
                var current_pi = pi_ar[pino];

                current_pi.poc.prg = tcp.prg;
                //current_pi.first = true;
                current_pi.poc.resno0 = 0;
                current_pi.poc.compno0 = 0;
                current_pi.poc.layno0 = 0;
                current_pi.poc.precno0 = 0;
                current_pi.poc.resno1 = max_res;
                current_pi.poc.compno1 = current_pi.numcomps;
                current_pi.poc.layno1 = tcp.numlayers;
                current_pi.poc.precno1 = max_precision;
            }
        }

        //2.5 - opj_pi_update_decode_poc
        static void UpdateDecodePoc(PacketIterator[] pi_ar, TileCodingParams tcp, OPJ_UINT32 max_precision, OPJ_UINT32 p_max_res)
        {
            //C# instead of l_bound we use pi_ar.Length
            Debug.Assert(pi_ar.Length == tcp.numpocs + 1);

            for (int pino = 0; pino < pi_ar.Length; pino++)
            {
                //C# updates the pointers
                var current_poc = tcp.pocs[pino];
                var current_pi = pi_ar[pino];

                current_pi.poc.prg = current_poc.prg; /* Progression Order #0 */
                //current_pi.first = true;

                current_pi.poc.resno0 = current_poc.resno0; // Resolution Level Index #0 (Start)
                current_pi.poc.compno0 = current_poc.compno0; // Component Index #0 (Start)
                current_pi.poc.layno0 = 0;
                current_pi.poc.precno0 = 0;
                current_pi.poc.resno1 = current_poc.resno1; // Resolution Level Index #0 (End)
                current_pi.poc.compno1 = current_poc.compno1; // Component Index #0 (End)
                current_pi.poc.layno1 = Math.Min(current_poc.layno1, tcp.numlayers); // Layer Index #0 (End)
                current_pi.poc.precno1 = max_precision;
            }
        }

        //2.5 - opj_pi_next
        internal IEnumerator<bool> Next()
        {
            switch (poc.prg)
            {
                case PROG_ORDER.LRCP:
                    return Next_lrcp().GetEnumerator();
                case PROG_ORDER.RLCP:
                    return Next_rlcp().GetEnumerator();
                case PROG_ORDER.RPCL:
                    return Next_rpcl().GetEnumerator();
                case PROG_ORDER.PCRL:
                    return Next_pcrl().GetEnumerator();
                case PROG_ORDER.CPRL:
                    return Next_cprl().GetEnumerator();
                //case PROG_ORDER.PROG_UNKNOWN:
                default:
                    return null;
            }
        }

        /// <summary>
        /// Next Layer, Resolution, Component, ProgOrder
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_pi_next_lrcp
        /// 
        /// Org impl uses goto to jump into a codeblock, that is
        /// not allowed in C#.
        /// 
        /// Workaround is to rewrite this method as a generator.
        /// </remarks>
        IEnumerable<bool> Next_lrcp()
        {
            //bool skip;
            PIComp comp;
            PIResolution res;

            if (poc.compno0 >= numcomps ||
                poc.compno1 >= numcomps + 1)
            {
                _cinfo.Error("opj_pi_next_rlcp(): invalid compno0/compno1");
                yield break;
            }

            for (layno = poc.layno0; layno < poc.layno1; layno++)
            {
                for (resno = poc.resno0; resno < poc.resno1; resno++)
                {
                    for (compno = poc.compno0; compno < poc.compno1; compno++)
                    {
                        comp = comps[compno];
                        if (resno >= comp.numresolutions)
                            continue;
                        res = comp.resolutions[resno];
                        if (!tp_on)
                            poc.precno1 = res.pw * res.ph;
                        
                        for (precno = poc.precno0; precno < poc.precno1; precno++)
                        {
                            //I belive c++ long == c# int (index was long)
                            uint index = layno * step_l + resno * step_r + compno * step_c + precno * step_p;
                            if (index >= include.Length)
                            {
                                //C# impl. note:
                                //This happens with "p0_07.j2k". The org. impl (1.4) will happily
                                //read into unallocated memory. But due to coincidence, it
                                //will always return false.
                                //...but since then they've added this check too :/
                                _cinfo.Error("Invalid access to pi->include");
                                yield break;
                            }
                            if (!include[index])
                            {
                                include[index] = true;
                                yield return true;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Next Resolution, Layer, Component, ProgOrder
        /// </summary>
        /// <remarks>
        /// 2.5
        /// </remarks>
        IEnumerable<bool> Next_rlcp()
        {
            PIComp comp;
            PIResolution res;

            if (poc.compno0 >= numcomps ||
                poc.compno1 >= numcomps + 1)
            {
                _cinfo.Error("opj_pi_next_rlcp(): invalid compno0/compno1");
                yield break;
            }

            for (resno = poc.resno0; resno < poc.resno1; resno++)
            {
                for (layno = poc.layno0; layno < poc.layno1; layno++)
                {
                    for (compno = poc.compno0; compno < poc.compno1; compno++)
                    {
                        comp = comps[compno];
                        if (resno >= comp.numresolutions)
                            continue;
                        res = comp.resolutions[resno];
                        if (!tp_on)
                            poc.precno1 = res.pw * res.ph;

                        for (precno = poc.precno0; precno < poc.precno1; precno++)
                        {
                            //I belive c++ long == c# int (index was long)
                            uint index = layno * step_l + resno * step_r + compno * step_c + precno * step_p;
                            if (index >= include.Length)
                            {
                                _cinfo.Error("Invalid access to pi->include");
                                yield break; //<-- See as lrcp as to why
                            }
                            if (!include[index])
                            {
                                include[index] = true;
                                yield return true;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Next Resolution, ProgOrder(y then x), Component, Layer
        /// </summary>
        /// <remarks>
        /// 2.5.1
        /// </remarks>
        IEnumerable<bool> Next_rpcl()
        {
            PIComp comp;
            PIResolution res;

            if (poc.compno0 >= numcomps ||
                poc.compno1 >= numcomps + 1)
            {
                _cinfo.Error("opj_pi_next_rlcp(): invalid compno0/compno1");
                yield break;
            }

            { 
                OPJ_UINT32 compno, resno;
                //first = false;
                dx = 0;
                dy = 0;
                for (compno = 0; compno < numcomps; compno++)
                {
                    comp = comps[compno];
                    for (resno = 0; resno < comp.numresolutions; resno++)
                    {
                        OPJ_UINT32 dx, dy;
                        res = comp.resolutions[resno];
                        var tmp = res.pdx + comp.numresolutions - 1 - resno;
                        if (tmp < 32 &&
                            comp.dx <= int.MaxValue / (1 << (int)tmp))
                        {
                            dx = comp.dx * (1u << (int)tmp);
                            this.dx = this.dx == 0 ? dx : Math.Min(this.dx, dx);
                        }
                        tmp = res.pdy + comp.numresolutions - 1 - resno;
                        if (tmp < 32 &&
                            comp.dy <= int.MaxValue / (1 << (int)tmp))
                        {
                            dy = comp.dy * (1u << (int)tmp);
                            this.dy = this.dy == 0 ? dy : Math.Min(this.dy, dy);
                        }
                    }
                }

                if (dx == 0 || dy == 0)
                {
                    yield break;
                }
            }
                

            if (!tp_on)
            {
                poc.ty0 = ty0;
                poc.tx0 = tx0;
                poc.ty1 = ty1;
                poc.tx1 = tx1;
            }

            for (resno = poc.resno0; resno < poc.resno1; resno++)
            {
                for (y = poc.ty0; y < poc.ty1; y += dy - (y % dy))
                {
                    for (x = poc.tx0; x < poc.tx1; x += dx - (x % dx))
                    {
                        for (compno = poc.compno0; compno < poc.compno1; compno++)
                        {
                            uint levelno, trx0, try0, trx1;
                            uint try1, rpx, rpy, prci, prcj;

                            comp = comps[compno];
                            if (resno >= comp.numresolutions)
                                continue;
                            res = comp.resolutions[resno];
                            levelno = comp.numresolutions - 1u - resno;

                            if ((uint)(((ulong)comp.dx << (int) levelno) >> (int) levelno) != comp.dx ||
                            (uint)(((ulong)comp.dy << (int) levelno) >> (int) levelno) != comp.dy)
                            {
                                continue;
                            }

                            trx0 = MyMath.uint64_ceildiv_res_uint32(tx0, (ulong)comp.dx << (int)levelno);
                            try0 = MyMath.uint64_ceildiv_res_uint32(ty0, (ulong)comp.dy << (int)levelno);
                            trx1 = MyMath.uint64_ceildiv_res_uint32(tx1, (ulong)comp.dx << (int)levelno);
                            try1 = MyMath.uint64_ceildiv_res_uint32(ty1, (ulong)comp.dy << (int)levelno);
                            rpx = res.pdx + levelno;
                            rpy = res.pdy + levelno;

                            if ((uint)(((ulong)comp.dx << (int) rpx) >> (int) rpx) != comp.dx ||
                            (uint)(((ulong)comp.dy << (int) rpy) >> (int) rpy) != comp.dy)
                            {
                                continue;
                            }

                            /* See ISO-15441. B.12.1.3 Resolution level-position-component-layer progression */
                            if (!(((ulong)y % ((ulong)comp.dy << (int) rpy) == 0) || ((y == ty0) && (((ulong)try0 << (int) levelno) % ((ulong)1U << (int) rpy)) != 0)))
                                continue;
                            if (!(((ulong)x % ((ulong)comp.dx << (int) rpx) == 0) || ((x == tx0) && (((ulong)trx0 << (int)levelno) % ((ulong)1U << (int)rpx)) != 0)))
                                continue;
                            if ((res.pw == 0) || (res.ph == 0)) continue;
                            if ((trx0 == trx1) || (try0 == try1)) continue;

                            prci = MyMath.uint_floordivpow2(MyMath.uint64_ceildiv_res_uint32(x, (ulong)comp.dx << (int)levelno), (int)res.pdx)
                                    - MyMath.uint_floordivpow2(trx0, (int)res.pdx);
                            prcj = MyMath.uint_floordivpow2(MyMath.uint64_ceildiv_res_uint32(y, (ulong)comp.dy << (int)levelno), (int)res.pdy)
                                    - MyMath.uint_floordivpow2(try0, (int)res.pdy);
                            precno = prci + prcj * res.pw;
                            
                            for (layno = poc.layno0; layno < poc.layno1; layno++)
                            {
                                uint index = layno * step_l + resno * step_r + compno * step_c + precno * step_p;
                                if (index >= include.Length)
                                {
                                    _cinfo.Error("Invalid access to pi->include");
                                    yield break;
                                }
                                if (!include[index])
                                {
                                    include[index] = true;
                                    yield return true;
                                }
                                //C# Skip enters here
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Next ProgOrder(y then x), Component, Resolution, Layer
        /// </summary>
        /// <remarks>2.5.1 - opj_pi_next_pcrl</remarks>
        IEnumerable<bool> Next_pcrl()
        {
            PIComp comp;
            PIResolution res;

            if (poc.compno0 >= numcomps ||
                poc.compno1 >= numcomps + 1)
            {
                _cinfo.Error("opj_pi_next_rlcp(): invalid compno0/compno1");
                yield break;
            }

            {
                OPJ_UINT32 compno, resno;
                //first = false;
                dx = 0;
                dy = 0;
                for (compno = 0; compno < numcomps; compno++)
                {
                    comp = comps[compno];
                    for (resno = 0; resno < comp.numresolutions; resno++)
                    {
                        OPJ_UINT32 dx, dy;
                        res = comp.resolutions[resno];
                        var tmp = res.pdx + comp.numresolutions - 1 - resno;
                        if (tmp < 32 &&
                            comp.dx <= int.MaxValue / (1 << (int)tmp))
                        {
                            dx = comp.dx * (1u << (int)tmp);
                            this.dx = this.dx == 0 ? dx : Math.Min(this.dx, dx);
                        }
                        tmp = res.pdy + comp.numresolutions - 1 - resno;
                        if (tmp < 32 &&
                            comp.dy <= int.MaxValue / (1 << (int)tmp))
                        {
                            dy = comp.dy * (1u << (int)tmp);
                            this.dy = this.dy == 0 ? dy : Math.Min(this.dy, dy);
                        }
                    }
                }

                if (dx == 0 || dy == 0)
                {
                    yield break;
                }
            }

            if (!tp_on)
            {
                poc.ty0 = ty0;
                poc.tx0 = tx0;
                poc.ty1 = ty1;
                poc.tx1 = tx1;
            }

            for (y = poc.ty0; y < poc.ty1; y += dy - (y % dy))
            {
                for (x = poc.tx0; x < poc.tx1; x += dx - (x % dx))
                {
                    for (compno = poc.compno0; compno < poc.compno1; compno++)
                    {
                        comp = comps[compno];
                        for (resno = poc.resno0; resno < MyMath.uint_min(poc.resno1, comp.numresolutions); resno++)
                        {
                            uint levelno, trx0, try0, trx1;
                            uint try1, rpx, rpy, prci, prcj;

                            res = comp.resolutions[resno];
                            levelno = comp.numresolutions - 1 - resno;

                            if ((uint)(((ulong)comp.dx << (int)levelno) >> (int)levelno) != comp.dx ||
                            (uint)(((ulong)comp.dy << (int)levelno) >> (int)levelno) != comp.dy)
                            {
                                continue;
                            }

                            trx0 = MyMath.uint64_ceildiv_res_uint32(tx0, (ulong)comp.dx << (int)levelno);
                            try0 = MyMath.uint64_ceildiv_res_uint32(ty0, (ulong)comp.dy << (int)levelno);
                            trx1 = MyMath.uint64_ceildiv_res_uint32(tx1, (ulong)comp.dx << (int)levelno);
                            try1 = MyMath.uint64_ceildiv_res_uint32(ty1, (ulong)comp.dy << (int)levelno);
                            rpx = res.pdx + levelno;
                            rpy = res.pdy + levelno;

                            if ((uint)(((ulong)comp.dx << (int)rpx) >> (int)rpx) != comp.dx ||
                            (uint)(((ulong)comp.dy << (int)rpy) >> (int)rpy) != comp.dy)
                            {
                                continue;
                            }

                            /* See ISO-15441. B.12.1.3 Resolution level-position-component-layer progression */
                            if (!(((ulong)y % ((ulong)comp.dy << (int)rpy) == 0) || ((y == ty0) && (((ulong)try0 << (int)levelno) % ((ulong)1U << (int)rpy)) != 0)))
                                continue;
                            if (!(((ulong)x % ((ulong)comp.dx << (int)rpx) == 0) || ((x == tx0) && (((ulong)trx0 << (int)levelno) % ((ulong)1U << (int)rpx)) != 0)))
                                continue;
                            if ((res.pw == 0) || (res.ph == 0)) continue;
                            if ((trx0 == trx1) || (try0 == try1)) continue;

                            prci = MyMath.uint_floordivpow2(MyMath.uint64_ceildiv_res_uint32(x, (ulong)comp.dx << (int)levelno), (int)res.pdx)
                                    - MyMath.uint_floordivpow2(trx0, (int)res.pdx);
                            prcj = MyMath.uint_floordivpow2(MyMath.uint64_ceildiv_res_uint32(y, (ulong)comp.dy << (int)levelno), (int)res.pdy)
                                    - MyMath.uint_floordivpow2(try0, (int)res.pdy);
                            precno = prci + prcj * res.pw;

                            //C# Part of skip:
                            layno = poc.layno0;
                            
                            for (layno = poc.layno0; layno < poc.layno1; layno++)
                            {
                                uint index = layno * step_l + resno * step_r + compno * step_c + precno * step_p;
                                if (index >= include.Length)
                                {
                                    _cinfo.Error("Invalid access to pi->include");
                                    yield break;
                                }
                                if (!include[index])
                                {
                                    include[index] = true;
                                    yield return true;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Next Component, ProgOrder(y then x), Resolution, Layer
        /// </summary>
        /// <remarks>
        /// 2.5.1 - opj_pi_next_cprl
        /// 
        /// Org impl uses goto to jump into a codeblock, that is
        /// not allowed in C#.
        /// 
        /// Workaround is to rewrite this method as a generator.
        /// </remarks>
        IEnumerable<bool> Next_cprl()
        {
            PIComp comp;
            PIResolution res;

            if (poc.compno0 >= numcomps ||
                poc.compno1 >= numcomps + 1)
            {
                _cinfo.Error("opj_pi_next_rlcp(): invalid compno0/compno1");
                yield break;
            }

            for (compno = poc.compno0; compno < poc.compno1; compno++)
            {
                {
                    OPJ_UINT32 resno;
                    dx = 0;
                    dy = 0;
                    comp = comps[compno];
                    for (resno = 0; resno < comp.numresolutions; resno++)
                    {
                        OPJ_UINT32 dx, dy;
                        res = comp.resolutions[resno];
                        var tmp = res.pdx + comp.numresolutions - 1 - resno;
                        if (tmp < 32 &&
                            comp.dx <= int.MaxValue / (1 << (int)tmp))
                        {
                            dx = comp.dx * (1u << (int)tmp);
                            this.dx = this.dx == 0 ? dx : Math.Min(this.dx, dx);
                        }
                        tmp = res.pdy + comp.numresolutions - 1 - resno;
                        if (tmp < 32 &&
                            comp.dy <= int.MaxValue / (1 << (int)tmp))
                        {
                            dy = comp.dy * (1u << (int)tmp);
                            this.dy = this.dy == 0 ? dy : Math.Min(this.dy, dy);
                        }
                    }

                    if (dx == 0 || dy == 0)
                    {
                        yield break;
                    }
                    

                    if (!tp_on)
                    {
                        poc.ty0 = ty0;
                        poc.tx0 = tx0;
                        poc.ty1 = ty1;
                        poc.tx1 = tx1;
                    }

                    y = poc.ty0;
                }

                for (; y < poc.ty1; y += dy - (y % dy))
                {
                    for (x = poc.tx0; x < poc.tx1; x += dx - (x % dx))
                    {
                        for (resno = poc.resno0; resno < MyMath.uint_min(poc.resno1, comp.numresolutions); resno++)
                        {
                            uint levelno, trx0, try0, trx1;
                            uint try1, rpx, rpy, prci, prcj;

                            res = comp.resolutions[resno];
                            levelno = comp.numresolutions - 1 - resno;

                            if ((uint)(((ulong)comp.dx << (int)levelno) >> (int)levelno) != comp.dx ||
                                (uint)(((ulong)comp.dy << (int)levelno) >> (int)levelno) != comp.dy)
                            {
                                continue;
                            }

                            trx0 = MyMath.uint64_ceildiv_res_uint32(tx0, (ulong)comp.dx << (int)levelno);
                            try0 = MyMath.uint64_ceildiv_res_uint32(ty0, (ulong)comp.dy << (int)levelno);
                            trx1 = MyMath.uint64_ceildiv_res_uint32(tx1, (ulong)comp.dx << (int)levelno);
                            try1 = MyMath.uint64_ceildiv_res_uint32(ty1, (ulong)comp.dy << (int)levelno);
                            rpx = res.pdx + levelno;
                            rpy = res.pdy + levelno;

                            if ((uint)(((ulong)comp.dx << (int)rpx) >> (int)rpx) != comp.dx ||
                                (uint)(((ulong)comp.dy << (int)rpy) >> (int)rpy) != comp.dy)
                            {
                                continue;
                            }

                            /* See ISO-15441. B.12.1.3 Resolution level-position-component-layer progression */
                            if (!(((ulong)y % ((ulong)comp.dy << (int)rpy) == 0) || ((y == ty0) && (((ulong)try0 << (int)levelno) % ((ulong)1U << (int)rpy)) != 0)))
                                continue;
                            if (!(((ulong)x % ((ulong)comp.dx << (int)rpx) == 0) || ((x == tx0) && (((ulong)trx0 << (int)levelno) % ((ulong)1U << (int)rpx)) != 0)))
                                continue;
                            if ((res.pw == 0) || (res.ph == 0)) continue;
                            if ((trx0 == trx1) || (try0 == try1)) continue;

                            prci = MyMath.uint_floordivpow2(MyMath.uint64_ceildiv_res_uint32(x, (ulong)comp.dx << (int)levelno), (int)res.pdx)
                                    - MyMath.uint_floordivpow2(trx0, (int)res.pdx);
                            prcj = MyMath.uint_floordivpow2(MyMath.uint64_ceildiv_res_uint32(y, (ulong)comp.dy << (int)levelno), (int)res.pdy)
                                    - MyMath.uint_floordivpow2(try0, (int)res.pdy);
                            precno = prci + prcj * res.pw;
                            
                            for (layno = poc.layno0; layno < poc.layno1; layno++)
                            {
                                //I belive c++ long == c# int (index was long)
                                uint index = layno * step_l + resno * step_r + compno * step_c + precno * step_p;
                                if (index >= include.Length)
                                {
                                    _cinfo.Error("Invalid access to pi->include");
                                    yield break;
                                }
                                if (!include[index])
                                {
                                    include[index] = true;
                                    yield return true;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Packet Iterator Component
    /// </summary>
    internal class PIComp
    {
        internal uint dx, dy;

        /// <summary>
        /// Number of resolution levels
        /// </summary>
        /// <remarks>Can probably be dropped for resolutions.Length</remarks>
        internal uint numresolutions;

        internal PIResolution[] resolutions;
    }

    /// <summary>
    /// Packet Iterator Resolution
    /// </summary>
    internal class PIResolution
    {
        internal uint pdx, pdy;
        internal uint pw, ph;
    }
}
