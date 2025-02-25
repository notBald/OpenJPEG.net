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

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Tier-2 coding (packetization of code-block data)
    /// 
    /// To enable the stream to be broken up into resolution layers and
    /// quality layers the bitstream must be propperly oganised. This
    /// algorithm partitions the stream into packets, each packet holding
    /// header information usefull to determine if any block of a given
    /// sub-band is included in the packet or not, and more.
    /// 
    /// The output of a tier-1 encoder is a buch of codewords, one for each
    /// block, with a indication of valid trucation points.
    /// 
    /// Tier-2 fins the optimal truncation points, given a target bitrate.
    /// </summary>
    /// <remarks>t2.c</remarks>
    internal sealed class Tier2Coding
    {
        #region Variables and properties

        /// <summary>
        /// Encoding: pointer to the src image. 
        /// Decoding: pointer to the dst image.
        /// </summary>
        JPXImage _image;

        /// <summary>
        /// Coding parameters
        /// </summary>
        CodingParameters _cp;

        /// <summary>
        /// The parent compression info obj.
        /// </summary>
        /// <remarks>Not needed for encode</remarks>
        readonly CompressionInfo _cinfo;

        #endregion

        #region Init

        //2.5 - opj_t2_create
        internal Tier2Coding(CompressionInfo cinfo, JPXImage image, CodingParameters cp)
        {
            _cp = cp;
            _image = image;
            _cinfo = cinfo;
        }

        #endregion

        /// <summary>
        /// Docodes packets for a tile
        /// </summary>
        /// <param name="src">Source data</param>
        /// <param name="tileno">Number of the tile to decode</param>
        /// <param name="tile">The tile itself</param>
        /// <returns>Number of bytes read from src</returns>
        /// <remarks>
        /// 2.5 - opj_t2_decode_packets
        /// C# The max_length parameter is not included, as it's just src.Length
        /// </remarks>
        internal bool DecodePackets(TileCoder tcd, uint tileno, TcdTile tile, byte[] src, int max_length, CodestreamIndex cstr_index)
        {
            var tcp = _cp.tcps[tileno];
            PacketIterator[] pi_ar = PacketIterator.CreateDecode(_image, _cp, tileno, _cinfo);
            if (pi_ar == null)
            {
                return false;
            }

            int n_bytes_read;

            //C#: Position in the src stream
            int src_pos = 0;

            for (int pino = 0; pino <= tcp.numpocs; pino++)
            {
                //C# Updates the pointers
                var current_pi = pi_ar[pino];

                if (current_pi.poc.prg == PROG_ORDER.PROG_UNKNOWN)
                {
                    return false;
                }

                /* if the resolution needed is too low, one dim of the tilec could be equal to zero
                 * and no packets are used to decode this resolution and
                 * l_current_pi->resno is always >= p_tile->comps[l_current_pi->compno].minimum_num_resolutions
                 * and no l_img_comp->resno_decoded are computed
                 */
                bool[] first_pass_failed = new bool[_image.numcomps];
                for (int c = 0; c < first_pass_failed.Length; c++)
                    first_pass_failed[c] = true;
#if DEBUG
                //int n_itterations = 0, n_reads = 0;
#endif
                var enumerator = current_pi.Next();
                if (enumerator == null)
                    break;

                while (enumerator.MoveNext())
                {
                    bool skip_packet;

                    // If the packet layer is greater or equal than the maximum
                    // number of layers, skip the packet
                    if (current_pi.layno >= tcp.num_layers_to_decode)
                    {
                        skip_packet = true;
                    }
                    // If the packet resolution number is greater than the minimum
                    // number of resolution allowed, skip the packet
                    else if (current_pi.resno >= tile.comps[current_pi.compno].minimum_num_resolutions)
                    {
                        skip_packet = true;
                    } 
                    else
                    {
                        // If no precincts of any band intersects the area of interest
                        // skip the packet
                        var tilec = tile.comps[current_pi.compno];
                        var res = tilec.resolutions[current_pi.resno];

                        skip_packet = true;
                        for (var bandno = 0; bandno < res.numbands; ++bandno)
                        {
                            var band = res.bands[bandno];
                            var prec = band.precincts[current_pi.precno];

                            if (tcd.IsSubbandAreaOfInterest(current_pi.compno,
                                                        current_pi.resno,
                                                        band.bandno,
                                                        (uint)prec.x0,
                                                        (uint)prec.y0,
                                                        (uint)prec.x1,
                                                        (uint)prec.y1))
                            {
                                skip_packet = false;
                                break;
                            }
                        }
                    }

#if DEBUG
                    //n_itterations++;
                    //if (n_itterations == 248)
                    //{
                    //    n_reads = n_reads;
                    //}
#endif


                    //Decodes up to the requested number of layers.
                    if (!skip_packet)
                    {
#if DEBUG
                        //n_reads++;
#endif
                        first_pass_failed[current_pi.compno] = false;

                        if (!DecodePacket(tile, tcp, current_pi, src, src_pos, out n_bytes_read, max_length))
                            return false;

                        var img_comp = _image.comps[current_pi.compno];
                        img_comp.resno_decoded = Math.Max(current_pi.resno, img_comp.resno_decoded);
                    }
                    else
                    {
                        if (!SkipPacket(tile, tcp, current_pi, src, src_pos, out n_bytes_read, max_length))
                            return false;
                    }

                    if (first_pass_failed[current_pi.compno])
                    {
                        var img_comp = _image.comps[current_pi.compno];
                        if (img_comp.resno_decoded == 0)
                            img_comp.resno_decoded = tile.comps[current_pi.compno].minimum_num_resolutions - 1u;
                    }

                    ////Moves position in the stream.
                    src_pos += n_bytes_read;
                    max_length -= n_bytes_read;
                }
            }

            return true;
        }

        /// <summary>
        /// Decodes a single packets
        /// </summary>
        /// <param name="src">Source data</param>
        /// <param name="src_pos">Position to where to start reading</param>
        /// <param name="tile">The tile to decode</param>
        /// <param name="tcp">Parameters for decoding</param>
        /// <param name="pi">Packet information</param>
        /// <returns>Number of bytes read from the src array</returns>
        /// <remarks>
        /// 2.5
        /// </remarks>
        private bool DecodePacket(TcdTile tile, TileCodingParams tcp, PacketIterator pi, byte[] src, int src_pos, out int data_read, int max_length)
        {
            bool read_data;
            if (!ReadPacketHeader(tile, tcp, pi, out read_data, src, src_pos, out data_read, max_length))
                return false;

            src_pos += data_read;
            max_length -= data_read;

            //we should read data for the packet
            if (read_data)
            {
                int total = data_read;
                if (!ReadPacketData(tile, pi, src, src_pos, out data_read, max_length))
                    return false;
                data_read += total;
            }

            return true;
        }

        /// <summary>
        /// Skips a single packets
        /// </summary>
        /// <param name="src">Source data</param>
        /// <param name="src_pos">Position to where to start reading</param>
        /// <param name="tile">The tile to decode</param>
        /// <param name="tcp">Parameters for decoding</param>
        /// <param name="pi">Packet information</param>
        /// <returns>Number of bytes read from the src array</returns>
        /// <remarks>2.5 - opj_t2_skip_packet</remarks>
        private bool SkipPacket(TcdTile tile, TileCodingParams tcp, PacketIterator pi, byte[] src, int src_pos, out int data_read, int max_length)
        {
            bool read_data;
            if (!ReadPacketHeader(tile, tcp, pi, out read_data, src, src_pos, out data_read, max_length))
                return false;

            src_pos += data_read;
            max_length -= data_read;

            //we should read data for the packet
            if (read_data)
            {
                int total = data_read;
                if (!SkipPacketData(tile, pi, src, src_pos, out data_read, max_length))
                    return false;
                data_read += total;
            }

            return true;
        }

        //2.5.3 - opj_t2_read_packet_data
        bool ReadPacketData(TcdTile tile, PacketIterator pi, byte[] src, int src_pos, out int data_read, int max_length)
        {
            TcdResolution res = tile.comps[pi.compno].resolutions[pi.resno];
            int start_pos = src_pos;
            bool partial_buffer = false;

            for (int bandno = 0; bandno < res.numbands; bandno++)
            {
                //C# Updates pointers
                var band = res.bands[bandno];
                var prc = band.precincts[pi.precno];

                if ((band.x1 - band.x0 == 0) || (band.y1 - band.y0 == 0))
                {
                    continue;
                }
                uint n_code_blocks = prc.cw * prc.ch;

                for (int cblkno = 0; cblkno < n_code_blocks; cblkno++)
                {
                    //C# Updates pointers
                    TcdCblkDec cblk = prc.dec[cblkno];
                    TcdSeg seg;

                    //C# retains a reference to the data array
                    cblk.chunk_data = src;

                    if (cblk.numnewpasses == 0)
                    {
                        //Nothing to do
                        continue;
                    }

                    if (partial_buffer || cblk.corrupted)
                    {
                        //if a previous segment in this packet couldn't be decoded,
                        //or if this code block was corrupted in a previous layer,
                        //then mark it as corrupted.
                        cblk.numchunks = 0;
                        cblk.corrupted = true;
                        continue;
                    }

                    if (cblk.numsegs == 0)
                    {
                        Debug.Assert(cblk.segs[0] != null, "Create a new segment");
                        seg = cblk.segs[0];
                        cblk.numsegs++;
                    }
                    else
                    {
                        seg = cblk.segs[cblk.numsegs - 1];
                        if (seg.numpasses == seg.maxpasses)
                        {
                            Debug.Assert(cblk.segs[cblk.numsegs] != null, "Create a new segment");
                            seg = cblk.segs[cblk.numsegs]; //C# effectivly the same as ++seg
                            cblk.numsegs++;
                        }
                    }

                    do
                    {
                        if (src_pos + seg.newlen < src_pos ||
                            src_pos + seg.newlen > start_pos + max_length ||
                            partial_buffer)
                        {
                            if (_cp.strict)
                            {
                                _cinfo.Error("read: segment too long ({0}) with max ({1}) for codeblock {2} (p={3}, b={4}, r={5}, c={6})",
                                    seg.newlen, max_length, cblkno, pi.precno, bandno, pi.resno, pi.compno);
                                data_read = 0;
                                return false;
                            }
                            else
                            {
                                _cinfo.Warn("read: segment too long ({0}) with max ({1}) for codeblock {2} (p={3}, b={4}, r={5}, c={6})",
                                    seg.newlen, max_length, cblkno, pi.precno, bandno, pi.resno, pi.compno);
                                //skip this codeblock (and following ones in this packet) since it is a partial read
                                partial_buffer = true;
                                cblk.corrupted = true;
                                cblk.numchunks = 0;

                                break;
                            }
                        }
                        if (cblk.numchunks == cblk.numchunksalloc)
                        {
                            uint numchunksalloc = cblk.numchunksalloc * 2 + 1;
                            Array.Resize(ref cblk.chunks, (int) numchunksalloc);
                            cblk.numchunksalloc = numchunksalloc;
                        }

                        cblk.chunks[cblk.numchunks].data_pt = src_pos;
                        cblk.chunks[cblk.numchunks].len = (int) seg.newlen;
                        cblk.numchunks++;

                        src_pos += (int)seg.newlen;
                        seg.len += seg.newlen;
                        seg.numpasses += seg.numnewpasses;
                        cblk.numnewpasses -= seg.numnewpasses;

                        seg.real_num_passes = seg.numpasses;

                        if (cblk.numnewpasses > 0)
                        {
                            Debug.Assert(cblk.segs[cblk.numsegs] != null, "Create a new segment");
                            seg = cblk.segs[cblk.numsegs];
                            cblk.numsegs++;
                        }
                    } while (cblk.numnewpasses > 0);

                    cblk.real_num_segs = cblk.numsegs;
                } /* next code_block */
            }

            // Returns the number of bytes read
            if (partial_buffer)
                data_read = max_length;
            else
                data_read = src_pos - start_pos;

            return true;
        }

        //2.5.3 - opj_t2_skip_packet_data
        bool SkipPacketData(TcdTile tile, PacketIterator pi, byte[] src, int src_pos, out int data_read, int max_length)
        {
            TcdResolution res = tile.comps[pi.compno].resolutions[pi.resno];
            data_read = 0;
            int start_pos = src_pos;

            for (int bandno = 0; bandno < res.numbands; bandno++)
            {
                var band = res.bands[bandno];
                var prc = band.precincts[pi.precno];

                if ((band.x1 - band.x0 == 0) || (band.y1 - band.y0 == 0)) continue;
                uint n_code_blocks = prc.cw * prc.ch;

                for (int cblkno = 0; cblkno < n_code_blocks; cblkno++)
                {
                    TcdCblkDec cblk = prc.dec[cblkno];
                    TcdSeg seg;

                    if (cblk.numnewpasses == 0)
                        continue;

                    if (cblk.numsegs == 0)
                    {
                        Debug.Assert(cblk.segs[0] != null, "Create a new segment");
                        seg = cblk.segs[0];
                        cblk.numsegs++;
                        //cblk.data_current_size = 0;
                    }
                    else
                    {
                        seg = cblk.segs[cblk.numsegs - 1];
                        if (seg.numpasses == seg.maxpasses)
                        {
                            Debug.Assert(cblk.segs[cblk.numsegs] != null, "Create a new segment");
                            seg = cblk.segs[cblk.numsegs];
                            cblk.numsegs++;
                        }
                    }

                    do
                    {
                        if (src_pos + seg.newlen > start_pos + max_length)
                        {
                            var msg = "skip: segment too long ({0}) with max ({1}) for codeblock {2} (p={3}, b={4}, r={5}, c={6})";
                            if (_cp.strict)
                            {
                                _cinfo.Error(msg, seg.newlen, max_length, cblkno, 
                                    pi.precno, bandno, pi.resno, pi.compno);
                                return false;
                            }
                            else
                            {
                                _cinfo.Warn(msg, seg.newlen, max_length, cblkno,
                                    pi.precno, bandno, pi.resno, pi.compno);

                                data_read = max_length;
                                return true;
                            }
                        }
                       
                        src_pos += (int) seg.newlen;
                        data_read += (int) seg.newlen;

                        seg.numpasses += seg.numnewpasses;
                        cblk.numnewpasses -= seg.numnewpasses;
                        if (cblk.numnewpasses > 0)
                        {
                            Debug.Assert(cblk.segs[cblk.numsegs] != null, "Create a new segment");
                            seg = cblk.segs[cblk.numsegs];
                            cblk.numsegs++;
                        }
                    } while (cblk.numnewpasses > 0);
                }
            }

            return true;
        }


        /// <param name="max_length">How much data can be read from src, can be shorter than "src.Lenght - src_pos"</param>
        /// <remarks>2.5.3 - opj_t2_read_packet_header</remarks>>
        bool ReadPacketHeader(TcdTile tile, TileCodingParams tcp, PacketIterator pi, out bool is_data_present, byte[] src, int src_pos, out int data_dread, int max_length)
        {
            int start_pos = src_pos; //<-- Used to calc num read
            int header_length;

            TcdResolution res = tile.comps[pi.compno].resolutions[pi.resno];

            if (pi.layno == 0)
            {
                // reset tagtrees
                for (int bandno = 0; bandno < res.numbands; bandno++)
                {
                    TcdBand band = res.bands[bandno];

                    if (!band.IsEmpty)
                    {
                        Debug.Assert(band.precincts != null, "Add test for null if this assumption if false");
                        if (pi.precno >= band.precincts.Length)
                        {
                            _cinfo.Error("Invalid precinct");
                            is_data_present = false;
                            data_dread = 0;
                            return false;
                        }
                        TcdPrecinct prc = band.precincts[pi.precno];

                        TagTree.Reset(prc.incltree);
                        TagTree.Reset(prc.imsbtree);
                        uint n_code_blocks = prc.cw * prc.ch;
                        for (int cblkno = 0; cblkno < n_code_blocks; cblkno++)
                        {
                            TcdCblkDec cblk = prc.dec[cblkno];
                            cblk.numsegs = 0;
                            cblk.real_num_segs = 0;
                        }
                    }
                }
            }

            //SOP markers

            if ((tcp.csty & CP_CSTY.SOP) == CP_CSTY.SOP)
            {
                // SOP markers are allowed (i.e. optional), just warn
                if (max_length < 6)
                    _cinfo.Warn("Not enough space for expected SOP marker");
                else if (src[src_pos] != 0xff || src[src_pos + 1] != 0x91)
                    _cinfo.Warn("Expected SOP marker");
                else
                    src_pos += 6;

                /** TODO : check the Nsop value */
            }

            // When the marker PPT/PPM is used the packet header are store in PPT/PPM marker
            // This part deal with this caracteristic
            // step 1: Read packet header in the saved structure
            // step 2: Return to codestream for decoding 

            byte[] header_data;
            int hd_pos;

            //C# impl. note.
            //2.1 moves the start of the data arrays when data isn't
            //present.
            int modified_length_ptr, hd_start_pos;

            if (_cp.ppm)
            {
                header_data = _cp.ppm_data;
                hd_pos = hd_start_pos = _cp.ppm_data_start;
                modified_length_ptr = header_data.Length;
            }
            else if (tcp.ppt)
            {
                header_data = tcp.ppt_data;
                hd_pos = hd_start_pos = tcp.ppt_data_start;
                modified_length_ptr = tcp.ppt_len;
            }
            else
            {
                header_data = src;
                hd_pos = hd_start_pos = src_pos;
                modified_length_ptr = start_pos + max_length - hd_pos;
            }

            BIO bio = new BIO(header_data, hd_pos, hd_pos + modified_length_ptr);

            bool present = bio.ReadBool();

            if (!present)
            {
                bio.ByteAlign();
                hd_pos += bio.Position;

                // EPH markers
                if ((tcp.csty & CP_CSTY.EPH) == CP_CSTY.EPH)
                {
                    // EPH markers are required
                    if (modified_length_ptr - (hd_pos - hd_start_pos) < 2)
                    {
                        _cinfo.Error("Not enough space for expected EPH marker");
                        data_dread = 0;
                        is_data_present = false;
                        return false;
                    }
                    else if (header_data[hd_pos] != 0xff || header_data[hd_pos + 1] != 0x92)
                    {
                        _cinfo.Error("Expected EPH marker");
                        data_dread = 0;
                        is_data_present = false;
                        return false;
                    }
                    else
                        hd_pos += 2;
                }

                //C# impl. note:
                //Original 2.1 impl modifies the array references, which is something
                //C# does not trivially allow. 
                header_length = hd_pos - hd_start_pos;
                modified_length_ptr -= header_length;
                hd_start_pos += header_length;

                //C# impl. note:
                //Effectivly the same as "l_header_data_start += l_header_length"
                if (_cp.ppm)
                {
                    _cp.ppm_data_start = hd_start_pos;
                }
                else if (tcp.ppt)
                {
                    tcp.ppt_data_start = hd_start_pos;
                    tcp.ppt_len = modified_length_ptr;
                }
                else
                {
                    src_pos = hd_start_pos;
                }

                //C# impl. note:
                //Return value is the amount read from the input byte array,
                //not the amount read from the ppm or ppt byte arrays.
                is_data_present = false;
                data_dread = src_pos - start_pos;

                return true;
            }

            for (int bandno = 0; bandno < res.numbands; bandno++)
            {
                TcdBand band = res.bands[bandno];
                TcdPrecinct prc = band.precincts[pi.precno];

                if (band.IsEmpty) continue;

                //cblkno = code block
                uint n_code_blocks = prc.cw * prc.ch;
                for (uint cblkno = 0; cblkno < n_code_blocks; cblkno++)
                {
                    bool included;
                    TcdCblkDec cblk = prc.dec[cblkno];

                    //if cblk not yet included before --> inclusion tagtree
                    if (cblk.numsegs == 0)
                        included = prc.incltree.Decode(bio, cblkno, pi.layno + 1);
                    else
                        included = bio.ReadBool();

                    // if cblk not included
                    if (!included)
                    {
                        cblk.numnewpasses = 0;
                        continue;
                    }

                    //if cblk not yet included --> zero-bitplane tagtree
                    if (cblk.numsegs == 0)
                    {
                        uint i = 0;
                        while (!prc.imsbtree.Decode(bio, cblkno, i))
                            i++;

                        cblk.Mb = (uint)band.numbps;
                        cblk.numbps = (uint)band.numbps + 1 - i;
                        cblk.numlenbits = 3;
                    }

                    //Number of coding passes
                    cblk.numnewpasses = GetNumPasses(bio);
                    uint increment = GetCommaCode(bio);

                    //length indicator increment
                    cblk.numlenbits += increment;

                    //Segment number
                    uint segno = 0;

                    if (cblk.numsegs == 0)
                        InitSegment(cblk, segno, tcp.tccps[pi.compno].cblksty, true);
                    else
                    {
                        segno = cblk.numsegs - 1;
                        if (cblk.segs[segno].numpasses == cblk.segs[segno].maxpasses)
                            InitSegment(cblk, ++segno, tcp.tccps[pi.compno].cblksty, false);
                    }
                    int n = (int)cblk.numnewpasses;

                    if ((tcp.tccps[pi.compno].cblksty & CCP_CBLKSTY.HT) != 0)
                    {
                        do
                        {
                            uint bit_number;
                            cblk.segs[segno].numnewpasses = segno == 0 ? 1u : (uint)n;
                            bit_number = cblk.numlenbits + MyMath.uint_floorlog2(
                                             cblk.segs[segno].numnewpasses);
                            if (bit_number > 32)
                            {
                                _cinfo.Error("Invalid bit number {0} in opj_t2_read_packet_header()",
                                              bit_number);
                                is_data_present = false;
                                data_dread = 0;
                                return false;
                            }
                            cblk.segs[segno].newlen = bio.ReadUInt(bit_number);

                            n -= (int)cblk.segs[segno].numnewpasses;
                            if (n > 0)
                            {
                                ++segno;
                                InitSegment(cblk, segno, tcp.tccps[pi.compno].cblksty, false);
                            }
                        } while (n > 0);
                    }
                    else
                    {
                        do
                        {
                            uint bit_number;
                            cblk.segs[segno].numnewpasses = (uint)Math.Min((int)(
                                    cblk.segs[segno].maxpasses - cblk.segs[segno].numpasses), n);
                            bit_number = cblk.numlenbits + MyMath.uint_floorlog2(
                                             cblk.segs[segno].numnewpasses);
                            if (bit_number > 32)
                            {
                                _cinfo.Error("Invalid bit number {0} in opj_t2_read_packet_header()",
                                              bit_number);
                                is_data_present = false;
                                data_dread = 0;
                                return false;
                            }
                            cblk.segs[segno].newlen = bio.ReadUInt0(bit_number);

                            n -= (int)cblk.segs[segno].numnewpasses;
                            if (n > 0)
                            {
                                ++segno;
                                InitSegment(cblk, segno, tcp.tccps[pi.compno].cblksty, false);
                            }
                        } while (n > 0);
                    }
                }
            }

            if (bio.ByteAlign())
            {
                is_data_present = false;
                data_dread = 0;
                return false;
            }
            hd_pos += bio.Position;

            //EPH markers
            if ((tcp.csty & CP_CSTY.EPH) == CP_CSTY.EPH)
            {
                //EPH markers are required
                if (modified_length_ptr - (hd_pos - hd_start_pos) < 2)
                {
                    _cinfo.Error("Not enough space for expected EPH marker");
                    is_data_present = false;
                    data_dread = 0;
                    return false;
                }
                else if (header_data[hd_pos] != 0xff || header_data[hd_pos + 1] != 0x92)
                {
                    _cinfo.Error("Expected EPH marker");
                    is_data_present = false;
                    data_dread = 0;
                    return false;
                }
                else
                    hd_pos += 2;
            }

            //C# impl. note:
            //Original 2.1 impl modifies the array references, which is something
            //C# does not trivially allow. 
            header_length = hd_pos - hd_start_pos;
            if (header_length == 0)
            {
                is_data_present = false;
                data_dread = 0;
                return false;
            }

            modified_length_ptr -= header_length;
            hd_start_pos += header_length;

            if (_cp.ppm)
            {
                //_cp.ppm_len = modified_length_ptr;
                _cp.ppm_data_start = hd_start_pos;
            }
            else if (tcp.ppt)
            {
                tcp.ppt_data_start = hd_start_pos;
                tcp.ppt_len = modified_length_ptr;
            }
            else
            {
                //Note, for the other two (ppm and ppt ^up there)
                //we've been reading out of another byte_array,
                //which is why the src_pos isn't set for them.
                src_pos = hd_start_pos;
            }

            //C# impl. note:
            //Return value is the amount read from the inputt byte array,
            //not the amount read from the ppm or ppt byte arrays.
            is_data_present = true;
            data_dread = src_pos - start_pos;

            return true;
        }

        //2.5 - opj_t2_encode_packets
        internal bool EncodePackets(uint tileno, 
                                    TcdTile tile, 
                                    uint maxlayers, 
                                    BufferCIO dest, 
                                    out uint data_written, 
                                    uint max_len,
                                    TcdMarkerInfo marker_info,
                                    uint tpnum, 
                                    int tppos, 
                                    uint pino, 
                                    T2_MODE t2_mode)
        {
            uint l_nb_bytes;
            TileCodingParams tcp = _cp.tcps[tileno];
            int pocno = _cp.rsiz == J2K_PROFILE.CINEMA_4K ? 2 : 1;
            uint maxcomp = _cp.specific_param.enc.max_comp_size > 0 ? _image.numcomps : 1;
            uint n_pocs = tcp.numpocs + 1;

            PacketIterator[] pi = PacketIterator.InitialiseEncode(_image, _cp, (uint)tileno, t2_mode, _cinfo);

            data_written = 0;

            //Threshold calculation
            if (t2_mode == T2_MODE.THRESH_CALC)
            {
                for (uint compno = 0; compno < maxcomp; compno++)
                {
                    uint comp_len = 0;
                    int pi_ptr = 0;

                    for (uint poc = 0; poc < pocno; poc++)
                    {
                        PacketIterator current_pi = pi[pi_ptr];
                        uint tpnump = compno; //Is called l_tp_num in org source.

                        PacketIterator.CreateEncode(pi, _cp, tileno, poc, tpnump, tppos, t2_mode);

                        var enumer = current_pi.Next();
                        while (enumer.MoveNext())
                        {
                            if (current_pi.layno < maxlayers)
                            {
                                uint n_bytes;
                                if (!EncodePacket(tileno, tile, tcp, current_pi, dest, 
                                                 out n_bytes, (int)max_len, t2_mode))
                                    return false;

                                comp_len += n_bytes;
                                max_len -= n_bytes;
                                data_written += n_bytes;
                            }
                        }
                        if (_cp.specific_param.enc.max_comp_size != 0 && 
                            comp_len > _cp.specific_param.enc.max_comp_size)
                        {
                            return false;
                        }

                        pi_ptr++;
                    }
                }
            }
            else //t2_mode == FINAL_PASS
            {
                PacketIterator.CreateEncode(pi, _cp, tileno, pino, tpnum, tppos, t2_mode);
                var current_pi = pi[pino];
                if (current_pi.poc.prg == PROG_ORDER.PROG_UNKNOWN)
                {
                    return false;
                }

                if (marker_info != null && marker_info.need_PLT)
                {
                    marker_info.p_packet_size = new uint[PacketIterator.GetEncodingPacketCount(_image, _cp, tileno)];
                }

                //C# we use a generator instead of goto functins, so "Next" creates
                //   the generator. Org impl. will just call next over and over again.
                var enumer = current_pi.Next();

                //C# It looks a little odd that we don't check the result, but the result
                //    is always "true".
                while (enumer.MoveNext())
                {
                    if (current_pi.layno < maxlayers)
                    {
                        uint n_bytes;
                        if (!EncodePacket(tileno, tile, tcp, current_pi, dest, 
                                          out n_bytes, (int)max_len, t2_mode))
                            return false;

                        max_len -= n_bytes;
                        data_written += n_bytes;

                        if (marker_info != null && marker_info.need_PLT)
                        {
                            marker_info.p_packet_size[marker_info.packet_count++] = n_bytes;
                        }

                        //C# Snip cstr_info

                        tile.packno++;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Encode a packet of a tile to a destination buffer
        /// </summary>
        /// <param name="tileno">Number of the tile encoded</param>
        /// <param name="tile">Tile for which to write the packets</param>
        /// <param name="tcp">Tile coding parameters</param>
        /// <param name="pi">Packet identity</param>
        /// <param name="dest">Destination buffer</param>
        /// <param name="data_written"></param>
        /// <param name="length">Length of the destination buffer</param>
        /// <param name="t2_mode">If == THRESH_CALC In Threshold calculation ,If == FINAL_PASS Final pass</param>
        /// <remarks>2.5.1 - opj_t2_encode_packet</remarks>
        internal bool EncodePacket(uint tileno, 
                                   TcdTile tile, 
                                   TileCodingParams tcp, 
                                   PacketIterator pi, 
                                   BufferCIO dest, 
                                   out uint data_written, 
                                   int length,
                                   T2_MODE t2_mode)
        {
            //Using bcio in a way that wasn't truly intended, as a buffer. Org. impl
            //writes straight into the buffer array. I like the idea of having the
            //buffer abstracted away, as that opens the posibility of replacing the
            //buffer with a stream.
            //
            //Note that we do not modify the position of the underlying stream, and
            //we assume that the buffer inside bcio is free for us to use.
            var hold = dest.BufferPos;

            uint compno = pi.compno;
            uint resno = pi.resno;
            uint precno = pi.precno;
            uint layno = pi.layno;
            data_written = 0;

            TcdTilecomp tilec = tile.comps[compno];
            TcdResolution res = tilec.resolutions[resno];

#if ENABLE_EMPTY_PACKET_OPTIMIZATION
            bool packet_empty = true;
#else
            const bool packet_empty = false;
#endif

            //Writes out SOP 0xff91
            if ((tcp.csty & CP_CSTY.SOP) != 0)
            {
                if (length < 6)
                {
                    _cinfo.Error("opj_t2_encode_packet(): only {0} bytes remaining in " +
                                 "output buffer. {1} needed", length, 6);
                    return false;
                }

                dest.WriteByte(255);
                dest.WriteByte(145);
                dest.WriteByte(0);
                dest.WriteByte(4);
                dest.WriteByte((tile.packno >> 8) & 0xff);
                dest.WriteByte((tile.packno) & 0xff);
                length -= 6;
            }

            if (layno == 0)
            {
                for (int bandno = 0; bandno < res.numbands; bandno++)
                {
                    TcdBand band = res.bands[bandno];
                    if (band.IsEmpty)
                        continue;

                    //Avoid out of bounds access, but likely not a proper fix
                    if (precno >= res.pw * res.ph)
                    {
                        _cinfo.Error("opj_t2_encode_packet(): accessing precno={0} >= {1}",
                            precno, res.pw * res.ph);
                        return false;
                    }

                    TcdPrecinct prc = band.precincts[precno];
                    TagTree.Reset(prc.incltree);
                    TagTree.Reset(prc.imsbtree);

                    uint n_blocks = prc.cw * prc.ch;
                    for (int cblkno = 0; cblkno < n_blocks; cblkno++)
                    {
                        TcdCblkEnc cblk = prc.enc[cblkno];
                        cblk.numpasses = 0;
                        prc.imsbtree.SetValue(cblkno, band.numbps - (int)cblk.numbps);
                    }
                }
            }

#if ENABLE_EMPTY_PACKET_OPTIMIZATION
            // WARNING: this code branch is disabled, since it has been reported that
            //          such packets cause decoding issues with cinema J2K hardware

            // Check if the packet is empty
            for (int bandno = 0; bandno < res.numbands; bandno++)
            {
                TcdBand band = res.bands[bandno];
                if (band.IsEmpty)
                    continue;

                TcdPrecinct prc = band.precincts[precno];
                uint n_blocks = prc.cw * prc.ch;
                for (int cblkno = 0; cblkno < n_blocks; cblkno++)
                {
                    TcdCblkEnc cblk = prc.enc[cblkno];
                    TcdLayer layer = cblk.layers[layno];

                    if (layer.numpasses == 0)
                        continue;

                    packet_empty = false;
                    break;
                }
                if (!packet_empty)
                    break;
            }
#endif

            //Sets up a bit writer.
            //C# this method is the only method that uses this bitwriter. It was
            //   created for the 1.4 conversion, and is probably doing more work
            //   than it needs to.
            var bio = new WBIO(dest, length);
            bio.WriteBit(packet_empty ? 0 : 1); // Empty header bit

            // Writes the packet header
            for (int bandno = 0; !packet_empty && bandno < res.numbands; bandno++)
            {
                TcdBand band = res.bands[bandno];
                if (band.IsEmpty)
                    continue;

                //Avoid out of bounds access, but likely not a proper fix
                if (precno >= res.pw * res.ph)
                {
                    _cinfo.Error("opj_t2_encode_packet(): accessing precno={0} >= {1}",
                        precno, res.pw * res.ph);
                    return false;
                }

                TcdPrecinct prc = band.precincts[precno];
                uint n_blocks = prc.cw * prc.ch;

                for (int cblkno = 0; cblkno < n_blocks; cblkno++)
                {
                    TcdCblkEnc cblk = prc.enc[cblkno];
                    TcdLayer layer = cblk.layers[layno];
                    if (cblk.numpasses == 0 && layer.numpasses != 0)
                        prc.incltree.SetValue(cblkno, (int)layno);
                }

                for (int cblkno = 0; cblkno < n_blocks; cblkno++)
                {
                    TcdCblkEnc cblk = prc.enc[cblkno];
                    TcdLayer layer = cblk.layers[layno];
                    int increment = 0;
                    uint nump = 0;
                    uint len = 0;

                    // cblk inclusion bits
                    if (cblk.numpasses == 0)
                        prc.incltree.Encode(bio, cblkno, layno + 1);
                    else
                        bio.WriteBit(layer.numpasses != 0);

                    //if cblk not included, go to the next cblk  */
                    if (layer.numpasses == 0)
                        continue;

                    //if first instance of cblk --> zero bit-planes information
                    if (cblk.numpasses == 0)
                    {
                        cblk.numlenbits = 3;
                        prc.imsbtree.Encode(bio, cblkno, 999);
                    }

                    //Number of coding passes included
                    PutNumPasses(bio, layer.numpasses);
                    uint n_passes = cblk.numpasses + layer.numpasses;

                    //computation of the increase of the length indicator and insertion in the header
                    for (uint passno = cblk.numpasses; passno < n_passes; passno++)
                    {
                        TcdPass pass = cblk.passes[passno];

                        //C# If this happens it's likely that the CodingParameters.matrix is messed up somehow.
                        Debug.Assert(pass != null, "Should never happen");

                        nump++;
                        len += pass.len;

                        if (pass.term != 0 || passno == (cblk.numpasses + layer.numpasses) - 1)
                        {
                            increment = Math.Max(increment, MyMath.int_floorlog2((int)len) + 1 
                                                            - ((int)cblk.numlenbits + MyMath.int_floorlog2((int)nump)));
                            len = 0;
                            nump = 0;
                        }
                    }
                    PutCommaCode(bio, increment);

                    //Computation of the new Length indicator
                    cblk.numlenbits += (uint)increment;

                    //Insertion of the codeword segment length
                    for (int passno = (int)cblk.numpasses; passno < n_passes; passno++)
                    {
                        TcdPass pass = cblk.passes[passno];
                        nump++;
                        len += pass.len;

                        if (pass.term != 0 || passno == (cblk.numpasses + layer.numpasses) - 1)
                        {
                            bio.Write(len, (int)cblk.numlenbits + MyMath.int_floorlog2((int)nump));
                            len = 0;
                            nump = 0;
                        }
                    }
                }
            }

            if (!bio.Flush())
                return false;

            ////Implementation note: "c" from org. impl is now bcio.BufferPos
            data_written = (uint)bio.Written;
            length -= (int)data_written;
            Debug.Assert(data_written + hold + ((tcp.csty & CP_CSTY.SOP) != 0 ? 6 : 0) == dest.BufferPos);

            //End Packet Header 0xff92 marker
            if ((tcp.csty & CP_CSTY.EPH) == CP_CSTY.EPH)
            {
                if (length < 2)
                {
                    if (t2_mode == T2_MODE.FINAL_PASS)
                    {
                        _cinfo.Error("opj_t2_encode_packet(): only {0} bytes remaining in " +
                                     "output buffer. {1} needed", length, 2);
                    }
                    return false;
                }
                dest.WriteByte(0xff);
                dest.WriteByte(0x92);
                length -= 2;
            }

            ////Snip cstr_info code from org impl. 

            //Writing the packet body
            for (int bandno = 0; !packet_empty && bandno < res.numbands; bandno++)
            {
                TcdBand band = res.bands[bandno];
                if (band.IsEmpty)
                    continue;

                TcdPrecinct prc = band.precincts[precno];
                uint n_blocks = prc.cw * prc.ch;

                for (int cblkno = 0; cblkno < n_blocks; cblkno++)
                {
                    TcdCblkEnc cblk = prc.enc[cblkno];
                    TcdLayer layer = cblk.layers[layno];

                    if (layer.numpasses == 0)
                        continue;

                    if (layer.len > length)
                    {
                        if (t2_mode == T2_MODE.FINAL_PASS)
                        {
                            _cinfo.Error("opj_t2_encode_packet(): only {0} bytes remaining in " +
                                         "output buffer. {1} needed", length, layer.len);
                        }
                        return false;
                    }

                    if (t2_mode == T2_MODE.FINAL_PASS)
                        dest.Write(layer.data, layer.data_pos, (int)layer.len);
                    else
                        dest.Skip((int)layer.len);
                    cblk.numpasses += (uint)layer.numpasses;
                    length -= (int)layer.len;

                    //Snip cstr_info code
                }
            }

            ////Returns the number of bytes written
            data_written = (uint)(dest.BufferPos - hold);

            return true;
        }

        //2.5
        static uint GetCommaCode(BIO bio)
        {
            uint n = 0;
            while (bio.ReadBool())
                n++;
            return n;
        }

        /// <summary>
        /// Get number of passes
        /// </summary>
        /// <returns>Number of passes</returns>
        /// <remarks>2.5</remarks>
        static uint GetNumPasses(BIO bio)
        {
            uint n;
            if (!bio.ReadBool())
                return 1;
            if (!bio.ReadBool())
                return 2;
            if ((n = (uint)bio.Read(2)) != 3)
                return (3 + n);
            if ((n = (uint)bio.Read(5)) != 31)
                return (6 + n);
            return (37 + (uint)bio.Read0(7));
        }

        //2.5.1 - opj_t2_putcommacode
        static void PutCommaCode(WBIO bio, int n)
        {
            while (--n >= 0)
                bio.WriteBit(1u);
            bio.WriteBit(0u);
        }

        /// <summary>
        /// Variable length code for signalling delta Zil (truncation point)
        /// </summary>
        /// <param name="bio">Bit Input/Output component</param>
        /// <param name="n">delta Zil</param>
        /// <remarks>2.5.1 - opj_t2_putnumpasses</remarks>
        static void PutNumPasses(WBIO bio, uint n)
        {
            if (n == 1)
                bio.WriteBit(0u);
            else if (n == 2)
                bio.Write(2u, 2);
            else if (n <= 5)
                bio.Write(0xc | (n - 3), 4);
            else if (n <= 36)
                bio.Write(0x1e0 | (n - 6), 9);
            else if (n <= 164)
                bio.Write(0xff80 | (n - 37), 16);
        }

        //2.5.1 - opj_t2_init_seg
        static void InitSegment(TcdCblkDec dec, uint index, CCP_CBLKSTY cblksty, bool first)
        {
            if (dec.segs == null)
                dec.segs = new TcdSeg[index + 1];
            else if ( index >= dec.segs.Length)
                Array.Resize<TcdSeg>(ref dec.segs, (int)(index + 1));

            var seg = new TcdSeg();
            //Debug.Assert(dec.segs[index] == null);
            //^ Even if this assert fires, I don't think there's a problem as the org
            //  impl calls opj_tcd_reinit_segment
            dec.segs[index] = seg;

            if ((cblksty & CCP_CBLKSTY.TERMALL) == CCP_CBLKSTY.TERMALL)
                seg.maxpasses = 1;
            else if ((cblksty & CCP_CBLKSTY.LAZY) == CCP_CBLKSTY.LAZY)
            {
                if (first)
                    seg.maxpasses = 10;
                else
                    seg.maxpasses = (dec.segs[index - 1].maxpasses == 1 || 
                                     dec.segs[index - 1].maxpasses == 10) ? 2 : 1;
            }
            else
            {
                seg.maxpasses = 109;
            }
        }
    }
}
