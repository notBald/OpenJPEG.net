using System;

namespace OpenJpeg.Internal
{
    internal sealed class SparseArrayInt32
    {
        public readonly uint width;
        public readonly uint height;
        public readonly uint block_width;
        public readonly uint block_height;
        public readonly uint block_count_hor;
        public readonly uint block_count_ver;
        public readonly int[][] data_blocks;

        private SparseArrayInt32(uint w, uint h, uint bw, uint bh, uint bch, uint bcv,
            int[][] db)
        {
            width = w;
            height = h;
            block_width = bw;
            block_height = bh;
            block_count_hor = bch;
            block_count_ver = bcv;
            data_blocks= db;
        }

        private bool is_region_valid(uint x0,
                                     uint y0,
                                     uint x1,
                                     uint y1)
        {
            return !(x0 >= width || x1 <= x0 || x1 > width ||
             y0 >= height || y1 <= y0 || y1 > height);
        }

        /// <remarks>
        /// 2.5 - opj_sparse_array_int32_read_or_write
        /// 
        /// C# Uses a float for a buffer, but treats it like an int[]
        /// </remarks>
        private bool read_or_write(uint x0,
                           uint y0,
                           uint x1,
                           uint y1,
                           float[] buf,
                           int buf_pos,
                           uint buf_col_stride,
                           uint buf_line_stride,
                           bool forgiving,
                           bool is_read_op)
        {
            uint y, block_y;
            uint y_incr = 0;
            IntOrFloat iof = new IntOrFloat();

            if (!is_region_valid(x0, y0, x1, y1))
            {
                return forgiving;
            }

            block_y = y0 / block_height;
            for (y = y0; y < y1; block_y++, y += y_incr)
            {
                uint x, block_x;
                uint x_incr = 0;
                uint block_y_offset;
                y_incr = (y == y0) ? block_height - (y0 % block_height) :
                         block_height;
                block_y_offset = block_height - y_incr;
                y_incr = Math.Min(y_incr, y1 - y);
                block_x = x0 / block_width;
                for (x = x0; x < x1; block_x++, x += x_incr)
                {
                    uint j;
                    uint block_x_offset;
                    int[] src_block;
                    x_incr = (x == x0) ? block_width - (x0 % block_width) : block_width;
                    block_x_offset = block_width - x_incr;
                    x_incr = Math.Min(x_incr, x1 - x);
                    src_block = data_blocks[block_y * block_count_hor + block_x];
                    if (is_read_op)
                    {
                        if (src_block == null)
                        {
                            if (buf_col_stride == 1)
                            {
                                int dest_ptr = (int)(buf_pos + (y - y0) * buf_line_stride +
                                                      (x - x0) * buf_col_stride);
                                for (j = 0; j < y_incr; j++)
                                {
                                    Array.Clear(buf, dest_ptr, (int)x_incr);
                                    dest_ptr += (int)buf_line_stride;
                                }
                            }
                            else
                            {
                                int dest_ptr = (int)(buf_pos + (y - y0) * buf_line_stride +
                                                      (x - x0) * buf_col_stride);
                                for (j = 0; j < y_incr; j++)
                                {
                                    uint k;
                                    for (k = 0; k < x_incr; k++)
                                    {
                                        buf[dest_ptr + k * buf_col_stride] = 0;
                                    }
                                    dest_ptr += (int)buf_line_stride;
                                }
                            }
                        }
                        else
                        {
                            uint src_ptr = (block_y_offset * block_width + block_x_offset);
                            if (buf_col_stride == 1)
                            {
                                int dest_ptr = (int)(buf_pos + (y - y0) * buf_line_stride
                                                             +
                                                               (x - x0) * buf_col_stride);
                                //C# Commented out since it dosn't make sense for .net
                                //if (x_incr == 4)
                                //{
                                //    /* Same code as general branch, but the compiler */
                                //    /* can have an efficient memcpy() */
                                //    (void)(x_incr); /* trick to silent cppcheck duplicateBranch warning */
                                //    for (j = 0; j < y_incr; j++)
                                //    {
                                //        memcpy(dest_ptr, src_ptr, sizeof(OPJ_INT32) * x_incr);
                                //        dest_ptr += buf_line_stride;
                                //        src_ptr += block_width;
                                //    }
                                //}
                                //else
                                {
                                    for (j = 0; j < y_incr; j++)
                                    {
                                        Buffer.BlockCopy(src_block, (int)src_ptr * sizeof(int), buf, dest_ptr * sizeof(int), sizeof(int) * (int)x_incr);
                                        dest_ptr += (int)buf_line_stride;
                                        src_ptr += block_width;
                                    }
                                }
                            }
                            else
                            {
                                uint dest_ptr = ((uint)buf_pos + (y - y0) * buf_line_stride
                                                               +
                                                                 (x - x0) * buf_col_stride);
                                if (x_incr == 1)
                                {
                                    for (j = 0; j < y_incr; j++)
                                    {
                                        buf[dest_ptr] = src_block[src_ptr];
                                        dest_ptr += buf_line_stride;
                                        src_ptr += block_width;
                                    }
                                }
                                else if (y_incr == 1 && buf_col_stride == 2)
                                {
                                    uint k;
                                    for (k = 0; k < (x_incr & ~3U); k += 4)
                                    {
                                        buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                        buf[dest_ptr + (k + 1) * buf_col_stride] = src_block[src_ptr + k + 1];
                                        buf[dest_ptr + (k + 2) * buf_col_stride] = src_block[src_ptr + k + 2];
                                        buf[dest_ptr + (k + 3) * buf_col_stride] = src_block[src_ptr + k + 3];
                                    }
                                    for (; k < x_incr; k++)
                                    {
                                        buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                    }
                                }
                                else if (x_incr >= 8 && buf_col_stride == 8)
                                {
                                    for (j = 0; j < y_incr; j++)
                                    {
                                        uint k;
                                        for (k = 0; k < (x_incr & ~3U); k += 4)
                                        {
                                            buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                            buf[dest_ptr + (k + 1) * buf_col_stride] = src_block[src_ptr + k + 1];
                                            buf[dest_ptr + (k + 2) * buf_col_stride] = src_block[src_ptr + k + 2];
                                            buf[dest_ptr + (k + 3) * buf_col_stride] = src_block[src_ptr + k + 3];
                                        }
                                        for (; k < x_incr; k++)
                                        {
                                            buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                        }
                                        dest_ptr += buf_line_stride;
                                        src_ptr += block_width;
                                    }
                                }
                                else
                                {
                                    /* General case */
                                    for (j = 0; j < y_incr; j++)
                                    {
                                        uint k;
                                        for (k = 0; k < x_incr; k++)
                                        {
                                            buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                        }
                                        dest_ptr += buf_line_stride;
                                        src_ptr += block_width;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        if (src_block == null)
                        {
                            src_block = new int[block_width * block_height];
                            data_blocks[block_y * block_count_hor + block_x] = src_block;
                        }

                        if (buf_col_stride == 1)
                        {
                            int dest_ptr = (int)(0 + block_y_offset *
                                           block_width + block_x_offset);
                            int src_ptr = (int)(buf_pos + (y - y0) *
                                                buf_line_stride + (x - x0) * buf_col_stride);
                            //if (x_incr == 4)
                            //{
                            //    /* Same code as general branch, but the compiler */
                            //    /* can have an efficient memcpy() */
                            //    (void)(x_incr); /* trick to silent cppcheck duplicateBranch warning */
                            //    for (j = 0; j < y_incr; j++)
                            //    {
                            //        memcpy(dest_ptr, src_ptr, sizeof(OPJ_INT32) * x_incr);
                            //        dest_ptr += block_width;
                            //        src_ptr += buf_line_stride;
                            //    }
                            //}
                            //else
                            {
                                for (j = 0; j < y_incr; j++)
                                {
                                    Buffer.BlockCopy(buf, src_ptr * sizeof(int), src_block, dest_ptr * sizeof(int), (int)x_incr * sizeof(int));
                                    dest_ptr += (int)block_width;
                                    src_ptr += (int)buf_line_stride;
                                }
                            }
                        }
                        else
                        {
                            uint dest_ptr = (0 + block_y_offset *
                                                 block_width + block_x_offset);
                            uint src_ptr = ((uint)buf_pos + (y - y0) *
                                                buf_line_stride + (x - x0) * buf_col_stride);
                            if (x_incr == 1)
                            {
                                for (j = 0; j < y_incr; j++)
                                {
                                    iof.F = buf[src_ptr];
                                    src_block[dest_ptr] = iof.I;
                                    src_ptr += buf_line_stride;
                                    dest_ptr += block_width;
                                }
                            }
                            else if (x_incr >= 8 && buf_col_stride == 8)
                            {
                                for (j = 0; j < y_incr; j++)
                                {
                                    uint k;
                                    for (k = 0; k < (x_incr & ~3U); k += 4)
                                    {
                                        iof.F = buf[src_ptr + k * buf_col_stride];
                                        src_block[dest_ptr + k] = iof.I;
                                        iof.F = buf[src_ptr + (k + 1) * buf_col_stride];
                                        src_block[dest_ptr + k + 1] = iof.I;
                                        iof.F = buf[src_ptr + (k + 2) * buf_col_stride];
                                        src_block[dest_ptr + k + 2] = iof.I;
                                        iof.F = buf[src_ptr + (k + 3) * buf_col_stride];
                                        src_block[dest_ptr + k + 3] = iof.I;
                                    }
                                    for (; k < x_incr; k++)
                                    {
                                        iof.F = buf[src_ptr + k * buf_col_stride];
                                        src_block[dest_ptr + k] = iof.I;
                                    }
                                    src_ptr += buf_line_stride;
                                    dest_ptr += block_width;
                                }
                            }
                            else
                            {
                                /* General case */
                                for (j = 0; j < y_incr; j++)
                                {
                                    uint k;
                                    for (k = 0; k < x_incr; k++)
                                    {
                                        iof.F = buf[src_ptr + k * buf_col_stride];
                                        src_block[dest_ptr + k] = iof.I;
                                    }
                                    src_ptr += buf_line_stride;
                                    dest_ptr += block_width;
                                }
                            }
                        }
                    }
                }
            }

            return true;
        }

        //2.5 - opj_sparse_array_int32_read_or_write
        private bool read_or_write(uint x0,
                                   uint y0,
                                   uint x1,
                                   uint y1,
                                   int[] buf,
                                   int buf_pos,
                                   uint buf_col_stride,
                                   uint buf_line_stride,
                                   bool forgiving,
                                   bool is_read_op)
        {
            uint y, block_y;
            uint y_incr;

            if (!is_region_valid(x0, y0, x1, y1))
            {
                return forgiving;
            }

            block_y = y0 / block_height;
            for (y = y0; y < y1; block_y++, y += y_incr)
            {
                uint x, block_x;
                uint x_incr;
                uint block_y_offset;
                y_incr = (y == y0) ? block_height - (y0 % block_height) :
                         block_height;
                block_y_offset = block_height - y_incr;
                y_incr = Math.Min(y_incr, y1 - y);
                block_x = x0 / block_width;
                for (x = x0; x < x1; block_x++, x += x_incr)
                {
                    uint j;
                    uint block_x_offset;
                    int[] src_block;
                    x_incr = (x == x0) ? block_width - (x0 % block_width) : block_width;
                    block_x_offset = block_width - x_incr;
                    x_incr = Math.Min(x_incr, x1 - x);
                    src_block = data_blocks[block_y * block_count_hor + block_x];
                    if (is_read_op)
                    {
                        if (src_block == null)
                        {
                            if (buf_col_stride == 1)
                            {
                                int dest_ptr = (int)(buf_pos + (y - y0) * buf_line_stride +
                                                      (x - x0) * buf_col_stride);
                                for (j = 0; j < y_incr; j++)
                                {
                                    Array.Clear(buf, dest_ptr, (int)x_incr);
                                    dest_ptr += (int)buf_line_stride;
                                }
                            }
                            else
                            {
                                int dest_ptr = (int)(buf_pos + (y - y0) * buf_line_stride +
                                                      (x - x0) * buf_col_stride);
                                for (j = 0; j < y_incr; j++)
                                {
                                    uint k;
                                    for (k = 0; k < x_incr; k++)
                                    {
                                        buf[dest_ptr + k * buf_col_stride] = 0;
                                    }
                                    dest_ptr += (int)buf_line_stride;
                                }
                            }
                        }
                        else
                        {
                            uint src_ptr = (block_y_offset * block_width + block_x_offset);
                            if (buf_col_stride == 1)
                            {
                                int dest_ptr = (int)(buf_pos + (y - y0) * buf_line_stride
                                                             +
                                                               (x - x0) * buf_col_stride);
                                //if (x_incr == 4)
                                //{
                                //    /* Same code as general branch, but the compiler */
                                //    /* can have an efficient memcpy() */
                                //    (void)(x_incr); /* trick to silent cppcheck duplicateBranch warning */
                                //    for (j = 0; j < y_incr; j++)
                                //    {
                                //        memcpy(dest_ptr, src_ptr, sizeof(OPJ_INT32) * x_incr);
                                //        dest_ptr += buf_line_stride;
                                //        src_ptr += block_width;
                                //    }
                                //}
                                //else
                                {
                                    for (j = 0; j < y_incr; j++)
                                    {
                                        Buffer.BlockCopy(src_block, (int)src_ptr * sizeof(int), buf, dest_ptr * sizeof(int), sizeof(int) * (int)x_incr);
                                        dest_ptr += (int)buf_line_stride;
                                        src_ptr += block_width;
                                    }
                                }
                            }
                            else
                            {
                                uint dest_ptr = ((uint)buf_pos + (y - y0) * buf_line_stride
                                                               +
                                                                 (x - x0) * buf_col_stride);
                                if (x_incr == 1)
                                {
                                    for (j = 0; j < y_incr; j++)
                                    {
                                        buf[dest_ptr] = src_block[src_ptr];
                                        dest_ptr += buf_line_stride;
                                        src_ptr += block_width;
                                    }
                                }
                                else if (y_incr == 1 && buf_col_stride == 2)
                                {
                                    uint k;
                                    for (k = 0; k < (x_incr & ~3U); k += 4)
                                    {
                                        buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                        buf[dest_ptr + (k + 1) * buf_col_stride] = src_block[src_ptr + k + 1];
                                        buf[dest_ptr + (k + 2) * buf_col_stride] = src_block[src_ptr + k + 2];
                                        buf[dest_ptr + (k + 3) * buf_col_stride] = src_block[src_ptr + k + 3];
                                    }
                                    for (; k < x_incr; k++)
                                    {
                                        buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                    }
                                }
                                else if (x_incr >= 8 && buf_col_stride == 8)
                                {
                                    for (j = 0; j < y_incr; j++)
                                    {
                                        uint k;
                                        for (k = 0; k < (x_incr & ~3U); k += 4)
                                        {
                                            buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                            buf[dest_ptr + (k + 1) * buf_col_stride] = src_block[src_ptr + k + 1];
                                            buf[dest_ptr + (k + 2) * buf_col_stride] = src_block[src_ptr + k + 2];
                                            buf[dest_ptr + (k + 3) * buf_col_stride] = src_block[src_ptr + k + 3];
                                        }
                                        for (; k < x_incr; k++)
                                        {
                                            buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                        }
                                        dest_ptr += buf_line_stride;
                                        src_ptr += block_width;
                                    }
                                }
                                else
                                {
                                    /* General case */
                                    for (j = 0; j < y_incr; j++)
                                    {
                                        uint k;
                                        for (k = 0; k < x_incr; k++)
                                        {
                                            buf[dest_ptr + k * buf_col_stride] = src_block[src_ptr + k];
                                        }
                                        dest_ptr += buf_line_stride;
                                        src_ptr += block_width;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        if (src_block == null)
                        {
                            src_block = new int[block_width * block_height];
                            data_blocks[block_y * block_count_hor + block_x] = src_block;
                        }

                        if (buf_col_stride == 1)
                        {
                            int dest_ptr = (int)(0 + block_y_offset *
                                           block_width + block_x_offset);
                            int src_ptr = (int)(buf_pos + (y - y0) *
                                                buf_line_stride + (x - x0) * buf_col_stride);
                            //if (x_incr == 4)
                            //{
                            //    /* Same code as general branch, but the compiler */
                            //    /* can have an efficient memcpy() */
                            //    (void)(x_incr); /* trick to silent cppcheck duplicateBranch warning */
                            //    for (j = 0; j < y_incr; j++)
                            //    {
                            //        memcpy(dest_ptr, src_ptr, sizeof(OPJ_INT32) * x_incr);
                            //        dest_ptr += block_width;
                            //        src_ptr += buf_line_stride;
                            //    }
                            //}
                            //else
                            {
                                for (j = 0; j < y_incr; j++)
                                {
                                    Buffer.BlockCopy(buf, src_ptr * sizeof(int), src_block, dest_ptr * sizeof(int), (int)x_incr * sizeof(int));
                                    dest_ptr += (int)block_width;
                                    src_ptr += (int)buf_line_stride;
                                }
                            }
                        }
                        else
                        {
                            uint dest_ptr = (0 + block_y_offset *
                                                 block_width + block_x_offset);
                            uint src_ptr = ((uint)buf_pos + (y - y0) *
                                                buf_line_stride + (x - x0) * buf_col_stride);
                            if (x_incr == 1)
                            {
                                for (j = 0; j < y_incr; j++)
                                {
                                    src_block[dest_ptr] = buf[src_ptr];
                                    src_ptr += buf_line_stride;
                                    dest_ptr += block_width;
                                }
                            }
                            else if (x_incr >= 8 && buf_col_stride == 8)
                            {
                                for (j = 0; j < y_incr; j++)
                                {
                                    uint k;
                                    for (k = 0; k < (x_incr & ~3U); k += 4)
                                    {
                                        src_block[dest_ptr + k] = buf[src_ptr + k * buf_col_stride];
                                        src_block[dest_ptr + k + 1] = buf[src_ptr + (k + 1) * buf_col_stride];
                                        src_block[dest_ptr + k + 2] = buf[src_ptr + (k + 2) * buf_col_stride];
                                        src_block[dest_ptr + k + 3] = buf[src_ptr + (k + 3) * buf_col_stride];
                                    }
                                    for (; k < x_incr; k++)
                                    {
                                        src_block[dest_ptr + k] = buf[src_ptr + k * buf_col_stride];
                                    }
                                    src_ptr += buf_line_stride;
                                    dest_ptr += block_width;
                                }
                            }
                            else
                            {
                                /* General case */
                                for (j = 0; j < y_incr; j++)
                                {
                                    uint k;
                                    for (k = 0; k < x_incr; k++)
                                    {
                                        src_block[dest_ptr + k] = buf[src_ptr + k * buf_col_stride];
                                    }
                                    src_ptr += buf_line_stride;
                                    dest_ptr += block_width;
                                }
                            }
                        }
                    }
                }
            }

            return true;
        }
        internal bool read(uint x0,
                            uint y0,
                            uint x1,
                            uint y1,
                            int[] dest,
                            int dest_pos,
                            uint dest_col_stride,
                            uint dest_line_stride,
                            bool forgiving)
        {
            return read_or_write(x0, y0, x1, y1,
                    dest,
                    dest_pos,
                    dest_col_stride,
                    dest_line_stride,
                    forgiving,
                    true);
        }

        //2.5 - opj_sparse_array_int32_read
        internal bool read(uint x0,
                            uint y0,
                            uint x1,
                            uint y1,
                            float[] dest,
                            int dest_pos,
                            uint dest_col_stride,
                            uint dest_line_stride,
                            bool forgiving)
        {
            return read_or_write(x0, y0, x1, y1,
                    dest,
                    dest_pos,
                    dest_col_stride,
                    dest_line_stride,
                    forgiving,
                    true);
        }

        internal bool write(uint x0,
                            uint y0,
                            uint x1,
                            uint y1,
                            int[] src,
                            int src_pos,
                            uint src_col_stride,
                            uint src_line_stride,
                            bool forgiving)
        {
            return read_or_write(x0, y0, x1, y1,
                    src,
                    src_pos,
                    src_col_stride,
                    src_line_stride,
                    forgiving,
                    false);
        }

        internal bool write(uint x0,
                            uint y0,
                            uint x1,
                            uint y1,
                            float[] src,
                            int src_pos,
                            uint src_col_stride,
                            uint src_line_stride,
                            bool forgiving)
        {
            return read_or_write(x0, y0, x1, y1,
                    src,
                    src_pos,
                    src_col_stride,
                    src_line_stride,
                    forgiving,
                    false);
        }

        internal static SparseArrayInt32 Create(uint width, uint height, 
            uint block_width, uint block_height)
        {
            if (width == 0 || height == 0 || block_width == 0 || block_height == 0)
            {
                return null;
            }
            if (block_width > (~0U) / block_height / sizeof(int))
            {
                return null;
            }

            uint bch = MyMath.uint_ceildiv(width, block_width);
            uint bcv = MyMath.uint_ceildiv(height, block_height);
            if (bch > (~0U) / bcv)
            {
                return null;
            }

            return new SparseArrayInt32(width, height, block_width, block_height,
                bch, bcv, new int[bch * bcv][]);
        }

        internal static SparseArrayInt32 Init(TcdTilecomp tilec, uint numres)
        {
            TcdResolution tr_max = tilec.resolutions[numres - 1];
            uint w = (uint)(tr_max.x1 - tr_max.x0);
            uint h = (uint)(tr_max.y1 - tr_max.y0);

            var sa = Create(w, h, Math.Min(w, 64u), Math.Min(h, 64u));
            if (sa == null)
                return null;

            for (uint resno = 0; resno < numres; ++resno)
            {
                TcdResolution res = tilec.resolutions[resno];

                for (uint bandno = 0; bandno < res.numbands; ++bandno)
                {
                    TcdBand band = res.bands[bandno];

                    for (uint precno = 0; precno < res.pw * res.ph; ++precno)
                    {
                        TcdPrecinct precinct = band.precincts[precno];
                        for (uint cblkno = 0; cblkno < precinct.cw * precinct.ch; ++cblkno)
                        {
                            TcdCblkDec cblk = precinct.dec[cblkno];
                            if (cblk.decoded_data != null)
                            {
                                uint x = (uint)(cblk.x0 - band.x0);
                                uint y = (uint)(cblk.y0 - band.y0);
                                uint cblk_w = (uint)(cblk.x1 - cblk.x0);
                                uint cblk_h = (uint)(cblk.y1 - cblk.y0);

                                if ((band.bandno & 1) != 0)
                                {
                                    TcdResolution pres = tilec.resolutions[resno - 1];
                                    x += (uint)(pres.x1 - pres.x0);
                                }
                                if ((band.bandno & 2) != 0)
                                {
                                    TcdResolution pres = tilec.resolutions[resno - 1];
                                    y += (uint)(pres.y1 - pres.y0);
                                }

                                if (!sa.write(x, y,
                                              x + cblk_w, y + cblk_h,
                                              cblk.decoded_data, 0,
                                              1, cblk_w, true))
                                {
                                    return null;
                                }
                            }
                        }
                    }
                }
            }

            return sa;
        }
    }
}
