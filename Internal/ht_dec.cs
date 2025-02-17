using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace OpenJpeg.Internal
{
    internal static class ht_dec
    {
        /// <summary>
        /// Displays the error message for disabling the decoding of SPP and
        /// MRP passes
        /// </summary>
        internal static bool only_cleanup_pass_is_decoded = false;

        /// <summary>
        /// Read uint Little Endian
        /// </summary>
        /// <param name="pos">Position of first byte in the array</param>
        /// <param name="source">Array with bytes</param>
        /// <returns>uint</returns>
        private static uint ReadUIntLE(int pos, byte[] source)
        {
            //if (pos + 3 >= bytes.Length)
            //    throw new EndOfStreamException();

            return
                ((uint)source[pos + 3]) << 24 |
                ((uint)source[pos + 2]) << 16 |
                ((uint)source[pos + 1]) << 8 |
                 (uint)source[pos];
        }

        //internal static void WriteUIntLE(uint val, int pos, byte[] dest)
        //{
        //    dest[pos++] = unchecked((byte)(val >> 24));
        //    dest[pos++] = unchecked((byte)(val >> 16));
        //    dest[pos++] = unchecked((byte)(val >> 8));
        //    dest[pos] = unchecked((byte)val);
        //}

        /// <summary>
        /// Number of set bits
        /// </summary>
        /// <remarks>
        /// https://stackoverflow.com/questions/10439242/count-leading-zeroes-in-an-int32
        /// </remarks>
        internal static uint population_count(uint x)
        {
            x -= ((x >> 1) & 0x55555555);
            x = (((x >> 2) & 0x33333333) + (x & 0x33333333));
            x = (((x >> 4) + x) & 0x0f0f0f0f);
            x += (x >> 8);
            x += (x >> 16);
            return (x & 0x0000003f);
        }

        /// <summary>
        /// Counts leading zeros
        /// </summary>
        /// <remarks>
        /// https://stackoverflow.com/questions/10439242/count-leading-zeroes-in-an-int32
        /// 
        /// Alternative: System.Numerics.BitOperations.LeadingZeroCount, 
        ///              avalible from .net 5.0
        /// </remarks>
        internal static uint count_leading_zeros(uint x)
        {
            const uint numIntBits = sizeof(uint) * 8; //compile time constant
                                                      //do the smearing
            x |= x >> 1;
            x |= x >> 2;
            x |= x >> 4;
            x |= x >> 8;
            x |= x >> 16;
            //count the ones
            x -= x >> 1 & 0x55555555;
            x = (x >> 2 & 0x33333333) + (x & 0x33333333);
            x = (x >> 4) + x & 0x0f0f0f0f;
            x += x >> 8;
            x += x >> 16;
            return numIntBits - (x & 0x0000003f); //subtract # of 1s from 32
        }

        /// <summary>
        ///table stores possible decoding three bits from vlc
        /// there are 8 entries for xx1, x10, 100, 000, where x means do not care
        /// table value is made up of
        /// 2 bits in the LSB for prefix length
        /// 3 bits for suffix length
        /// 3 bits in the MSB for prefix value (u_pfx in Table 3 of ITU T.814)
        /// </summary>
        private static readonly byte[] dec = new byte[] 
        { // the index is the prefix codeword
            3 | (5 << 2) | (5 << 5),        //000 == 000, prefix codeword "000"
            1 | (0 << 2) | (1 << 5),        //001 == xx1, prefix codeword "1"
            2 | (0 << 2) | (2 << 5),        //010 == x10, prefix codeword "01"
            1 | (0 << 2) | (1 << 5),        //011 == xx1, prefix codeword "1"
            3 | (1 << 2) | (3 << 5),        //100 == 100, prefix codeword "001"
            1 | (0 << 2) | (1 << 5),        //101 == xx1, prefix codeword "1"
            2 | (0 << 2) | (2 << 5),        //110 == x10, prefix codeword "01"
            1 | (0 << 2) | (1 << 5)         //111 == xx1, prefix codeword "1"
        };

        /// <summary>
        /// Decode initial UVLC to get the u value (or u_q)
        /// </summary>
        /// <param name="vlc">vlc is the head of the VLC bitstream</param>
        /// <param name="mode">
        /// mode is 0, 1, 2, 3, or 4. Values in 0 to 3 are composed of
        /// u_off of 1st quad and 2nd quad of a quad pair.  The value
        /// 4 occurs when both bits are 1, and the event decoded
        /// from MEL bitstream is also 1.
        /// </param>
        /// <param name="u">
        /// u is the u value (or u_q) + 1.  Note: we produce u + 1
        /// this value is a partial calculation of u + kappa
        /// </param>
        internal static uint decode_init_uvlc(uint vlc, uint mode, uint[] u)
        {
            uint consumed_bits = 0;
            if (mode == 0)
            { // both u_off are 0
                u[0] = u[1] = 1; //Kappa is 1 for initial line
            }
            else if (mode <= 2)
            { // u_off are either 01 or 10
                uint d;
                uint suffix_len;

                d = dec[vlc & 0x7];   //look at the least significant 3 bits
                vlc >>= (int)(d & 0x3);                 //prefix length
                consumed_bits += d & 0x3;

                suffix_len = ((d >> 2) & 0x7);
                consumed_bits += suffix_len;

                d = (d >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                u[0] = (mode == 1) ? d + 1 : 1; // kappa is 1 for initial line
                u[1] = (mode == 1) ? 1 : d + 1; // kappa is 1 for initial line
            }
            else if (mode == 3)
            { // both u_off are 1, and MEL event is 0
                uint d1 = dec[vlc & 0x7];  // LSBs of VLC are prefix codeword
                vlc >>= (int)(d1 & 0x3);                // Consume bits
                consumed_bits += d1 & 0x3;

                if ((d1 & 0x3) > 2)
                {
                    uint suffix_len;

                    //u_{q_2} prefix
                    u[1] = (vlc & 1) + 1 + 1; //Kappa is 1 for initial line
                    ++consumed_bits;
                    vlc >>= 1;

                    suffix_len = ((d1 >> 2) & 0x7);
                    consumed_bits += suffix_len;
                    d1 = (d1 >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                    u[0] = d1 + 1; //Kappa is 1 for initial line
                }
                else
                {
                    uint d2;
                    uint suffix_len;

                    d2 = dec[vlc & 0x7];  // LSBs of VLC are prefix codeword
                    vlc >>= (int)(d2 & 0x3);                // Consume bits
                    consumed_bits += d2 & 0x3;

                    suffix_len = ((d1 >> 2) & 0x7);
                    consumed_bits += suffix_len;

                    d1 = (d1 >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                    u[0] = d1 + 1; //Kappa is 1 for initial line
                    vlc >>= (int)suffix_len;

                    suffix_len = ((d2 >> 2) & 0x7);
                    consumed_bits += suffix_len;

                    d2 = (d2 >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                    u[1] = d2 + 1; //Kappa is 1 for initial line
                }
            }
            else if (mode == 4)
            { // both u_off are 1, and MEL event is 1
                uint d1;
                uint d2;
                uint suffix_len;

                d1 = dec[vlc & 0x7];  // LSBs of VLC are prefix codeword
                vlc >>= (int)(d1 & 0x3);                // Consume bits
                consumed_bits += d1 & 0x3;

                d2 = dec[vlc & 0x7];  // LSBs of VLC are prefix codeword
                vlc >>= (int)(d2 & 0x3);                // Consume bits
                consumed_bits += d2 & 0x3;

                suffix_len = ((d1 >> 2) & 0x7);
                consumed_bits += suffix_len;

                d1 = (d1 >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                u[0] = d1 + 3; // add 2+kappa
                vlc >>= (int)suffix_len;

                suffix_len = ((d2 >> 2) & 0x7);
                consumed_bits += suffix_len;

                d2 = (d2 >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                u[1] = d2 + 3; // add 2+kappa
            }
            return consumed_bits;
        }

        /// <summary>
        /// Decode non-initial UVLC to get the u value (or u_q)
        /// </summary>
        /// <param name="vlc">vlc is the head of the VLC bitstream</param>
        /// <param name="mode">
        /// mode is 0, 1, 2, 3, or 4. Values in 0 to 3 are composed of
        /// u_off of 1st quad and 2nd quad of a quad pair.  The value
        /// 4 occurs when both bits are 1, and the event decoded
        /// from MEL bitstream is also 1.
        /// </param>
        /// <param name="u">
        /// u is the u value (or u_q) + 1.  Note: we produce u + 1
        /// this value is a partial calculation of u + kappa
        /// </param>
        internal static uint decode_noninit_uvlc(uint vlc, uint mode, uint[] u)
        {
            uint consumed_bits = 0;
            if (mode == 0)
            {
                u[0] = u[1] = 1; //for kappa
            }
            else if (mode <= 2)
            { //u_off are either 01 or 10
                uint d;
                uint suffix_len;

                d = dec[vlc & 0x7];  //look at the least significant 3 bits
                vlc >>= (int)d & 0x3;                //prefix length
                consumed_bits += d & 0x3;

                suffix_len = ((d >> 2) & 0x7);
                consumed_bits += suffix_len;

                d = (d >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                u[0] = (mode == 1) ? d + 1 : 1; //for kappa
                u[1] = (mode == 1) ? 1 : d + 1; //for kappa
            }
            else if (mode == 3)
            { // both u_off are 1
                uint d1;
                uint d2;
                uint suffix_len;

                d1 = dec[vlc & 0x7];  // LSBs of VLC are prefix codeword
                vlc >>= (int)d1 & 0x3;                // Consume bits
                consumed_bits += d1 & 0x3;

                d2 = dec[vlc & 0x7];  // LSBs of VLC are prefix codeword
                vlc >>= (int)d2 & 0x3;                // Consume bits
                consumed_bits += d2 & 0x3;

                suffix_len = ((d1 >> 2) & 0x7);
                consumed_bits += suffix_len;

                d1 = (d1 >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                u[0] = d1 + 1;  //1 for kappa
                vlc >>= (int)suffix_len;

                suffix_len = ((d2 >> 2) & 0x7);
                consumed_bits += suffix_len;

                d2 = (d2 >> 5) + (vlc & ((1U << (int)suffix_len) - 1)); // u value
                u[1] = d2 + 1;  //1 for kappa
            }
            return consumed_bits;
        }

        /// <summary>
        /// data decoding machinery
        /// </summary>
        /// <remarks>
        /// Can be either class or struct, but I'm not sure what's best.
        /// C# is optimized towards classes, so without running any benchmarks
        /// I think it's best to set it as a class.
        /// </remarks>
        internal class dec_mel
        {
            /// <summary>
            /// MEL exponents
            /// </summary>
            private static readonly int[] exp = new int[] {
                0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 5
            };

            /// <summary>
            /// The source data array
            /// </summary>
            byte[] src;

            /// <summary>
            /// Position in the src array
            /// </summary>
            int data;

            /// <summary>
            /// Temporary buffer for read data
            /// </summary>
            ulong tmp;

            /// <summary>
            /// Number of bits stored in tmp
            /// </summary>
            int bits;

            /// <summary>
            /// Number of bytes in MEL code
            /// </summary>
            int size;

            /// <summary>
            /// True if the next bit needs to be unstuffed
            /// </summary>
            bool unstuff;

            /// <summary>
            /// State of MEL decoder
            /// </summary>
            int k;

            /// <summary>
            /// Number of decoded runs left in runs (maximum 8)
            /// </summary>
            int num_runs;

            /// <summary>
            /// Queue of decoded MEL codewords (7 bits/run)
            /// </summary>
            ulong runs;

            /// <summary>
            /// Initiates for MEL decoding and reads some bytes in order
            /// to get the read address to a multiple of 4
            /// </summary>
            /// <param name="buffer">Source data</param>
            /// <param name="offset">Pointer to start of data</param>
            /// <param name="lcup">Length of MagSgn+MEL+VLC segments</param>
            /// <param name="scup">Length of MEL+VLC segments</param>
            /// <remarks>2.5.1 - mel_init</remarks>
            internal dec_mel(byte[] buffer, int offset, int lcup, int scup, out bool fail)
            {
                src = buffer;
                data = offset + lcup - scup; // move the pointer to the start of MEL
                bits = 0;                    // 0 bits in tmp
                tmp = 0;                     //
                unstuff = false;             // no unstuffing
                size = scup - 1;             // size is the length of MEL+VLC-1
                k = 0;                       // 0 for state
                num_runs = 0;                // num_runs is 0
                runs = 0;                    // 

                //This code is borrowed; original is for a different architecture
                //These few lines take care of the case where data is not at a multiple
                // of 4 boundary.  It reads 1,2,3 up to 4 bytes from the MEL segment
                int num = 4 - (data & 0x3);
                for (int i = 0; i < num; ++i)
                { // this code is similar to mel_read
                    ulong d;
                    int d_bits;

                    //Debug.Assert(unstuff == false || src[data] <= 0x8F);
                    if (unstuff && src[data] > 0x8F)
                    {
                        fail = true;
                        return;
                    }
                    d = (size > 0) ? src[data] : 0xFFu; // if buffer is consumed
                                                        // set data to 0xFF
                    if (size == 1)
                    {
                        d |= 0xF;    //if this is MEL+VLC-1, set LSBs to 0xF
                    }
                    // see the standard
                    data += (size-- > 0) ? 1 : 0; //increment if the end is not reached
                    d_bits = 8 - (unstuff ? 1 : 0); //if unstuffing is needed, reduce by 1
                    tmp = (tmp << d_bits) | d; //store bits in tmp
                    bits += d_bits;  //increment tmp by number of bits
                    unstuff = ((d & 0xFF) == 0xFF); //true of next byte needs
                    //unstuffing
                }
                tmp <<= (64 - bits); //push all the way up so the first bit
                // is the MSB

                fail = false;
            }

            /// <summary>
            /// Retrieves one run
            /// </summary>
            /// <remarks>
            /// if there are no runs stored MEL segment is decoded
            /// </remarks>
            internal int get_run()
            {
                int t;
                if (num_runs == 0)
                { //if no runs, decode more bit from MEL segment
                    decode();
                }

                t = (int) (runs & 0x7F); //retrieve one run
                runs >>= 7;  // remove the retrieved run
                num_runs--;
                return t; // return run
            }

            void decode()
            {
                if (bits < 6)
                { // if there are less than 6 bits in tmp
                    read();    // then read from the MEL bitstream
                }
                // 6 bits is the largest decodable MEL cwd

                //repeat so long that there is enough decodable bits in tmp,
                // and the runs store is not full (num_runs < 8)
                while (bits >= 6 && num_runs < 8)
                {
                    int eval = exp[k]; // number of bits associated with state
                    int run = 0;
                    if ((tmp & (1ul << 63)) != 0) { //The next bit to decode (stored in MSB)
                                                    //one is found
                        run = 1 << eval;
                        run--; // consecutive runs of 0 events - 1
                        k = k + 1 < 12 ? k + 1 : 12;//increment, max is 12
                        tmp <<= 1; // consume one bit from tmp
                        bits -= 1;
                        run = run << 1; // a stretch of zeros not terminating in one
                    } 
                    else
                    {
                        //0 is found
                        run = (int)(tmp >> (63 - eval)) & ((1 << eval) - 1);
                        k = k - 1 > 0 ? k - 1 : 0; //decrement, min is 0
                        tmp <<= eval + 1; //consume eval + 1 bits (max is 6)
                        bits -= eval + 1;
                        run = (run << 1) + 1; // a stretch of zeros terminating with one
                    }
                    eval = num_runs * 7;            // 7 bits per run
                    runs &= ~((ulong)0x3F << eval); // 6 bits are sufficient
                    runs |= ((ulong)run) << eval;   // store the value in runs
                    num_runs++;                     // increment count
                }
            }

            /// <summary>
            /// Reads and unstuffs the MEL bitstream
            /// </summary>
            /// <remarks>
            /// This design needs more bytes in the codeblock buffer than the length
            /// of the cleanup pass by up to 2 bytes.
            /// 
            /// Unstuffing removes the MSB of the byte following a byte whose
            /// value is 0xFF; this prevents sequences larger than 0xFF7F in value
            /// from appearing in the bitstream.
            /// </remarks>
            void read()
            {
                uint val;
                int bits;
                uint t;
                int unstuff;

                if (this.bits > 32)
                { //there are enough bits in the tmp variable
                    return;    // return without reading new data
                }

                val = 0xFFFFFFFF;      // feed in 0xFF if buffer is exhausted
                if (size > 4)
                {  // if there is more than 4 bytes the MEL segment
                    val = ReadUIntLE(data, src);  // read 32 bits from MEL data
                    data += 4;           // advance pointer
                    size -= 4;           // reduce counter
                }
                else if (size > 0)
                { // 4 or less
                    uint m, v;
                    int i = 0;
                    while (size > 1)
                    {
                        v = src[data++]; // read one byte at a time
                        m = ~(0xFFu << i); // mask of location
                        val = (val & m) | (v << i);   // put byte in its correct location
                        --size;
                        i += 8;
                    }
                    // size equal to 1
                    v = src[data++];  // the one before the last is different
                    v |= 0xF;                         // MEL and VLC segments can overlap
                    m = ~(0xFFu << i);
                    val = (val & m) | (v << i);
                    --size;
                }

                // next we unstuff them before adding them to the buffer
                bits = 32 - (this.unstuff ? 1 : 0); // number of bits in val, subtract 1 if
                                                    // the previously read byte requires
                                                    // unstuffing

                // data is unstuffed and accumulated in t
                // bits has the number of bits in t
                t = val & 0xFF;
                unstuff = ((val & 0xFF) == 0xFF) ? 1 : 0; // true if the byte needs unstuffing
                bits -= unstuff;                          // there is one less bit in t if unstuffing is needed
                t = t << (8 - unstuff);                   // move up to make room for the next byte

                //this is a repeat of the above
                t |= (val >> 8) & 0xFF;
                unstuff = (((val >> 8) & 0xFF) == 0xFF) ? 1 : 0;
                bits -= unstuff;
                t = t << (8 - unstuff);

                t |= (val >> 16) & 0xFF;
                unstuff = (((val >> 16) & 0xFF) == 0xFF) ? 1 : 0;
                bits -= unstuff;
                t = t << (8 - unstuff);

                t |= (val >> 24) & 0xFF;
                this.unstuff = (((val >> 24) & 0xFF) == 0xFF);

                // move t to tmp, and push the result all the way up, so we read from
                // the MSB
                tmp |= ((ulong)t) << (64 - bits - this.bits);
                this.bits += bits; //increment the number of bits in tmp
            }
        }

        /// <summary>
        /// A structure for reading and unstuffing a segment that grows
        /// backward, such as VLC and MRP
        /// </summary>
        /// <remarks>
        /// Similarly to dec_mel, this can be either class or struct
        /// </remarks>
        internal class rev_struct
        {
            /// <summary>
            /// The source data array
            /// </summary>
            byte[] src;

            /// <summary>
            /// Position in the src array
            /// </summary>
            int data;

            /// <summary>
            /// Temporary buffer for read data
            /// </summary>
            ulong tmp;

            /// <summary>
            /// Number of bits stored in tmp
            /// </summary>
            int bits;

            /// <summary>
            /// Size of data
            /// </summary>
            int size;

            /// <summary>
            /// True if the next bit needs to be unstuffed
            /// </summary>
            /// <remarks>
            /// then the current byte is unstuffed if it is 0x7F
            /// </remarks>
            bool unstuff;

            /// <summary>
            /// Initiates the rev_struct_t structure and reads a few bytes to
            /// move the read address to multiple of 4
            /// </summary>
            /// <remarks>
            /// There is another similar rev_init_mrp subroutine.  The difference is
            /// that this one, rev_init, discards the first 12 bits (they have the
            /// sum of the lengths of VLC and MEL segments), and first unstuff depends
            /// on first 4 bits.
            /// </remarks>
            /// <param name="buffer">Data buffer</param>
            /// <param name="offset">Pointer to byte at the start of the cleanup pass</param>
            /// <param name="lcup">Length of MagSgn+MEL+VLC segments</param>
            /// <param name="scup">Length of MEL+VLC segments</param>
            internal rev_struct(byte[] buffer, int offset, int lcup, int scup)
            {
                src = buffer;

                //first byte has only the upper 4 bits
                data = offset + lcup - 2;

                //size can not be larger than this, in fact it should be smaller
                size = scup - 2;

                {
                    uint d = src[data--];   // read one byte (this is a half byte)
                    tmp = d >> 4;           // both initialize and set
                    bits = 4 - (((tmp & 7) == 7) ? 1 : 0); //check standard
                    unstuff = (d | 0xF) > 0x8F; //this is useful for the next byte
                }

                //This code is designed for an architecture that read address should
                // align to the read size (address multiple of 4 if read size is 4)
                //These few lines take care of the case where data is not at a multiple
                // of 4 boundary. It reads 1,2,3 up to 4 bytes from the VLC bitstream.
                // To read 32 bits, read from (vlcp->data - 3)
                int num = 1 + (data & 0x3);
                int tnum = num < size ? num : size;
                for (int i = 0; i < tnum; ++i)
                {
                    uint d_bits;
                    ulong d = src[data--];  // read one byte and move read pointer
                    //check if the last byte was >0x8F (unstuff == true) and this is 0x7F
                    d_bits = 8u - ((unstuff && ((d & 0x7F) == 0x7F)) ? 1u : 0u);
                    tmp |= d << bits; // move data to vlcp->tmp
                    bits += (int)d_bits;
                    unstuff = d > 0x8F; // for next byte
                }
                size -= tnum;
                read();  // read another 32 bits
            }

            /// <summary>
            /// Initialized rev_struct structure for MRP segment, and reads
            /// a number of bytes such that the next 32 bits read are from
            /// an address that is a multiple of 4. Note this is designed for
            /// an architecture that read size must be compatible with the
            /// alignment of the read address
            /// </summary>
            /// <remarks>
            /// There is another similar subroutine rev_init.  This subroutine does
            /// NOT skip the first 12 bits, and starts with unstuff set to true.
            /// </remarks>
            /// <param name="offset">Source buffer</param>
            /// <param name="buffer">Pointer to byte at the start of the cleanup pass</param>
            /// <param name="lcup">Length of MagSgn+MEL+VLC segments</param>
            /// <param name="len2">Length of SPP+MRP segments</param>
            internal rev_struct(int offset, byte[] buffer, int lcup, int len2)
            {
                src = buffer;
                data = offset + lcup + len2 - 1;
                size = len2;
                unstuff = true;
                bits = 0;
                tmp = 0;

                //This code is designed for an architecture that read address should
                // align to the read size (address multiple of 4 if read size is 4)
                //These few lines take care of the case where data is not at a multiple
                // of 4 boundary.  It reads 1,2,3 up to 4 bytes from the MRP stream
                int num = 1 + (data & 0x3);
                for (int i = 0; i < num; ++i)
                {
                    //read a byte, 0 if no more data
                    ulong d = (size-- > 0) ? src[data--] : 0u;
                    //check if unstuffing is needed
                    int d_bits = (int)(8u - ((unstuff && ((d & 0x7F) == 0x7F)) ? 1u : 0u));
                    tmp |= d << bits; // move data to vlcp->tmp
                    bits += d_bits;
                    unstuff = d > 0x8F; // for next byte
                }
                read_mrp();
            }

            /// <summary>
            /// Read and unstuff data from a backwardly-growing segment
            /// </summary>
            /// <remarks>
            /// This reader can read up to 8 bytes from before the VLC segment.
            /// Care must be taken not read from unreadable memory, causing a
            /// segmentation fault.
            /// 
            /// Note that there is another subroutine rev_read_mrp that is slightly
            /// different.  The other one fills zeros when the buffer is exhausted.
            /// This one basically does not care if the bytes are consumed, because
            /// any extra data should not be used in the actual decoding.
            /// 
            /// Unstuffing is needed to prevent sequences more than 0xFF8F from
            /// appearing in the bits stream; since we are reading backward, we keep
            /// watch when a value larger than 0x8F appears in the bitstream.
            /// If the byte following this is 0x7F, we unstuff this byte (ignore the
            /// MSB of that byte, which should be 0).
            /// </remarks>
            private void read()
            {
                //process 4 bytes at a time
                if (this.bits > 32)
                { // if there are more than 32 bits in tmp, then
                    return;    // reading 32 bits can overflow vlcp->tmp
                }
                uint val = 0;
                //the next line (the if statement) needs to be tested first
                if (size > 3)
                { // if there are more than 3 bytes left in VLC
                  // (vlcp->data - 3) move pointer back to read 32 bits at once
                    val = ReadUIntLE(data - 3, src); // then read 32 bits
                    data -= 4;                // move data pointer back by 4
                    size -= 4;                // reduce available byte by 4
                }
                else if (size > 0)
                { // 4 or less
                    int i = 24;
                    while (size > 0)
                    {
                        uint v = src[data--]; // read one byte at a time
                        val |= (v << i);      // put byte in its correct location
                        --size;
                        i -= 8;
                    }
                }

                //accumulate in tmp, number of bits in tmp are stored in bits
                uint tmp = val >> 24;  //start with the MSB byte

                // test unstuff (previous byte is >0x8F), and this byte is 0x7F
                int bits = (int) (8u - ((this.unstuff && (((val >> 24) & 0x7F) == 0x7F)) ? 1u : 0u));
                bool unstuff = (val >> 24) > 0x8F; //this is for the next byte

                tmp |= ((val >> 16) & 0xFF) << bits; //process the next byte
                bits += (int)(8u - ((unstuff && (((val >> 16) & 0x7F) == 0x7F)) ? 1u : 0u));
                unstuff = ((val >> 16) & 0xFF) > 0x8F;

                tmp |= ((val >> 8) & 0xFF) << bits;
                bits += (int)(8u - ((unstuff && (((val >> 8) & 0x7F) == 0x7F)) ? 1u : 0u));
                unstuff = ((val >> 8) & 0xFF) > 0x8F;

                tmp |= (val & 0xFF) << bits;
                bits += (int)(8u - ((unstuff && ((val & 0x7F) == 0x7F)) ? 1u : 0u));
                unstuff = (val & 0xFF) > 0x8F;

                // now move the read and unstuffed bits into vlcp->tmp
                this.tmp |= (ulong)tmp << this.bits;
                this.bits += bits;
                this.unstuff = unstuff; // this for the next read
            }

            private void read_mrp()
            {
                uint val;
                uint tmp;
                int bits;
                bool unstuff;

                //process 4 bytes at a time
                if (this.bits > 32)
                {
                    return;
                }
                val = 0;
                if (size > 3)
                { // If there are 3 byte or more
                  // (mrp->data - 3) move pointer back to read 32 bits at once
                    val = ReadUIntLE(data - 3, src); // read 32 bits
                    data -= 4;                       // move back pointer
                    size -= 4;                       // reduce count
                }
                else if (size > 0)
                {
                    int i = 24;
                    while (size > 0)
                    {
                        uint v = src[data--]; // read one byte at a time
                        val |= (v << i);      // put byte in its correct location
                        --size;
                        i -= 8;
                    }
                }

                //accumulate in tmp, and keep count in bits
                tmp = val >> 24;

                //test if the last byte > 0x8F (unstuff must be true) and this is 0x7F
                bits = (int)(8u - ((this.unstuff && (((val >> 24) & 0x7F) == 0x7F)) ? 1u : 0u));
                unstuff = (val >> 24) > 0x8F;

                //process the next byte
                tmp |= ((val >> 16) & 0xFF) << bits;
                bits += (int)(8u - ((unstuff && (((val >> 16) & 0x7F) == 0x7F)) ? 1u : 0u));
                unstuff = ((val >> 16) & 0xFF) > 0x8F;

                tmp |= ((val >> 8) & 0xFF) << bits;
                bits += (int)(8u - ((unstuff && (((val >> 8) & 0x7F) == 0x7F)) ? 1u : 0u));
                unstuff = ((val >> 8) & 0xFF) > 0x8F;

                tmp |= (val & 0xFF) << bits;
                bits += (int)(8u - ((unstuff && ((val & 0x7F) == 0x7F)) ? 1u : 0u));
                unstuff = (val & 0xFF) > 0x8F;

                this.tmp |= (ulong)tmp << this.bits; // move data to mrp pointer
                this.bits += bits;
                this.unstuff = unstuff;              // next byte
            }

            /// <summary>
            /// Retrieves 32 bits from the head of a rev_struct structure
            /// </summary>
            /// <remarks>
            /// By the end of this call, vlcp->tmp must have no less than 33 bits
            /// </remarks>
            internal uint fetch()
            {
                if (bits < 32)
                { // if there are less then 32 bits, read more
                    read();     // read 32 bits, but unstuffing might reduce this
                    if (bits < 32)
                    { // if there is still space in vlcp->tmp for 32 bits
                        read();    // read another 32
                    }
                }
                return (uint)tmp; // return the head (bottom-most) of vlcp->tmp
            }

            /// <summary>
            /// Retrieves 32 bits from the head of a rev_struct structure
            /// </summary>
            /// <remarks>
            /// By the end of this call, vlcp->tmp must have no less than 33 bits
            /// </remarks>
            internal uint fetch_mrp()
            {
                if (bits < 32)
                { // if there are less than 32 bits in mrp->tmp
                    read_mrp();    // read 30-32 bits from mrp
                    if (bits < 32)
                    { // if there is a space of 32 bits
                        read_mrp();    // read more
                    }
                }
                return (uint)tmp;  // return the head of mrp->tmp
            }

            /// <summary>
            /// Consumes num_bits from a rev_struct structure
            /// </summary>
            /// <param name="num_bits">number of bits to be removed</param>
            internal uint advance(int num_bits)
            {
                Debug.Assert(num_bits <= bits); // vlcp->tmp must have more than num_bits
                tmp >>= num_bits;               // remove bits
                bits -= num_bits;               // decrement the number of bits
                return (uint)tmp;
            }

            /// <summary>
            /// Consumes num_bits from a rev_struct structure
            /// </summary>
            /// <param name="num_bits">number of bits to be removed</param>
            internal uint advance_mrp(int num_bits)
            {
                Debug.Assert(num_bits <= bits); // we must not consume more than mrp->bits
                tmp >>= num_bits;         // discard the lowest num_bits bits
                bits -= num_bits;
                return (uint)tmp;   // return data after consumption
            }
        }


        /// <summary>
        /// State structure for reading and unstuffing of forward-growing
        /// bitstreams; these are: MagSgn and SPP bitstreams
        /// </summary>
        /// <remarks>
        /// Similarly to dec_mel, this can be either class or struct
        /// </remarks>
        internal class frwd_struct
        {
            /// <summary>
            /// The source data array
            /// </summary>
            byte[] src;

            /// <summary>
            /// Position in the src array
            /// </summary>
            int data;

            /// <summary>
            /// Temporary buffer for read data
            /// </summary>
            ulong tmp;

            /// <summary>
            /// Number of bits stored in tmp
            /// </summary>
            int bits;

            /// <summary>
            /// True if the next bit needs to be unstuffed
            /// </summary>
            /// <remarks>
            /// then the current byte is unstuffed if it is 0x7F
            /// </remarks>
            bool unstuff;

            /// <summary>
            /// Size of data
            /// </summary>
            int size;

            /// <summary>
            /// 0 or 0xFF, X's are inserted at end of bitstream
            /// </summary>
            uint X;

            /// <summary>
            /// Initializes the struct
            /// </summary>
            /// <remarks>
            /// This function is called multiple times, so we can't
            /// use a constructor
            /// </remarks>
            internal frwd_struct(byte[] buffer, int offset, int size, uint X)
            {
                src = buffer;
                data = offset;
                tmp = 0;
                bits = 0;
                unstuff = false;
                this.size = size;
                this.X = X;
                Debug.Assert(this.X == 0 || this.X == 0xFF);

                //This code is designed for an architecture that read address should
                // align to the read size (address multiple of 4 if read size is 4)
                //These few lines take care of the case where data is not at a multiple
                // of 4 boundary.  It reads 1,2,3 up to 4 bytes from the bitstream
                int num = 4 - (data & 0x3);
                for (int i = 0; i < num; ++i)
                {
                    //read a byte if the buffer is not exhausted, otherwise set it to X
                    ulong d = this.size-- > 0 ? buffer[data++] : this.X;
                    tmp |= (d << bits);      // store data in msp->tmp
                    bits += (int)(8u - (unstuff ? 1u : 0u)); // number of bits added to msp->tmp
                    unstuff = ((d & 0xFF) == 0xFF); // unstuffing for next byte
                }
                read(); // read 32 bits more
            }

            private void read()
            {
                uint val;
                int bits;
                uint t;
                bool unstuff;

                Debug.Assert(this.bits <= 32); // assert that there is a space for 32 bits

                if (size > 3)
                {
                    val = ReadUIntLE(data, src);  // read 32 bits
                    data += 4;           // increment pointer
                    size -= 4;           // reduce size
                }
                else if (size > 0)
                {
                    int i = 0;
                    val = X != 0 ? 0xFFFFFFFFu : 0;
                    while (size > 0)
                    {
                        uint v = src[data++];  // read one byte at a time
                        uint m = ~(0xFFu << i); // mask of location
                        val = (val & m) | (v << i);   // put one byte in its correct location
                        --size;
                        i += 8;
                    }
                }
                else
                {
                    val = X != 0 ? 0xFFFFFFFFu : 0;
                }

                // we accumulate in t and keep a count of the number of bits in bits
                bits = (int)(8u - (this.unstuff ? 1u : 0u));
                t = val & 0xFF;
                unstuff = ((val & 0xFF) == 0xFF);  // Do we need unstuffing next?

                t |= ((val >> 8) & 0xFF) << bits;
                bits += (int)(8u - (unstuff ? 1u : 0u));
                unstuff = (((val >> 8) & 0xFF) == 0xFF);

                t |= ((val >> 16) & 0xFF) << bits;
                bits += (int)(8u - (unstuff ? 1u : 0u));
                unstuff = (((val >> 16) & 0xFF) == 0xFF);

                t |= ((val >> 24) & 0xFF) << bits;
                bits += (int)(8u - (unstuff ? 1u : 0u));
                this.unstuff = (((val >> 24) & 0xFF) == 0xFF); // for next byte

                tmp |= ((ulong)t) << this.bits;  // move data to msp->tmp
                this.bits += bits;
            }

            /// <summary>
            /// Fetches 32 bits from the frwd_struct_t bitstream
            /// </summary>
            internal uint fetch()
            {
                if (bits < 32)
                {
                    read();
                    if (bits < 32)
                    { //need to test
                        read();
                    }
                }
                return (uint)tmp;
            }

            /// <summary>
            /// Consume num_bits bits from the bitstream of frwd_struct_t
            /// </summary>
            /// <param name="num_bits">Number of bits to consume</param>
            internal void advance(int num_bits)
            {
                Debug.Assert(num_bits <= bits);
                tmp >>= num_bits;  // consume num_bits
                bits -= num_bits;
            }
        }
    }

}
