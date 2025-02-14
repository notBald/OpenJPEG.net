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
using OPJ_UINT32 = System.UInt32;
#endregion

namespace OpenJpeg.Internal
{
    public class ImageComp : ICloneable
    {
        #region Variables and properties

        /// <summary>
        /// XRsiz: horizontal separation of a sample of this component with respect to the reference grid
        /// </summary>
        internal uint dx;

        /// <summary>
        /// YRsiz: vertical separation of a sample of this component with respect to the reference grid
        /// </summary>
        internal uint dy;

        /// <summary>
        /// Data width
        /// </summary>
        internal uint w;

        /// <summary>
        /// Data height
        /// </summary>
        internal uint h;

        /// <summary>
        /// x component offset compared to the whole image
        /// </summary>
        internal uint x0;

        /// <summary>
        /// y component offset compared to the whole image
        /// </summary>
        internal uint y0;

        /// <summary>
        /// Precision
        /// </summary>
        internal OPJ_UINT32 prec;

        /// <summary>
        /// Image depth in bits
        /// </summary>
        /// <remarks>Deprecated. Use prec instead</remarks>
        internal int bpp;

        /// <summary>
        /// signed (1) / unsigned (0)
        /// </summary>
        internal bool sgnd;

        /// <summary>
        /// Number of decoded resolution
        /// </summary>
        internal uint resno_decoded;

        /// <summary>
        /// Number of division by 2 of the out image compared to the original size of image
        /// </summary>
        internal uint factor;
        
        /// <summary>
        /// Image component data
        /// </summary>
        internal int[] data;

        /// <summary>
        /// Alpha channel
        /// </summary>
        internal ushort alpha;

        public bool IsAlpha
        {
            get { return alpha != 0; }
            set { alpha = (ushort) (value ? 1 : 0); }
        }

        /// <summary>
        /// Bits per pixel for this component
        /// </summary>
        /// <remarks>Is usualy zero after opening an image</remarks>
        public int BPP { get { return bpp; } }

        /// <summary>
        /// Bits per pixel for this component
        /// </summary>
        public int Prec { get { return (int) prec; } }

        /// <summary>
        /// If the component values are signed
        /// </summary>
        public bool Signed => sgnd;

        /// <summary>
        /// Width of this component
        /// </summary>
        public int Width { get { return (int) w; } }

        /// <summary>
        /// Height of this component
        /// </summary>
        public int Height { get { return (int) h; } }

        /// <summary>
        /// Image data
        /// </summary>
        public int[] Data { get { return data; } }

        /// <summary>
        /// Horizontal separation of a sample of this component with respect to the reference grid
        /// </summary>
        public uint DX => dx;

        /// <summary>
        /// Vertical separation of a sample of this component with respect to the reference grid
        /// </summary>
        public uint DY => dy;

        #endregion

        #region Init

        internal ImageComp() {}

        /// <summary>
        /// Creates an image component
        /// </summary>
        /// <param name="prec">Precisision</param>
        /// <param name="bpp">Bits per pixel</param>
        /// <param name="sgnd">Signed</param>
        /// <param name="dx">Size compared with whole image</param>
        /// <param name="dy">Size compared with whole image</param>
        /// <param name="w">Width</param>
        /// <param name="h">Height</param>
        /// <param name="data">Component data</param>
        public ImageComp(int prec, int bpp, bool sgnd, int dx, int dy, int w, int h, int[] data)
        {
            this.prec = (uint) prec;
            this.bpp = bpp;
            this.sgnd = sgnd;
            this.dx = (uint) dx;
            this.dy = (uint) dy;
            this.w = (uint) w;
            this.h = (uint) h;
            this.data = data;
        }

        #endregion

        #region ICloneable

        public object Clone() { return MemberwiseClone(); }

        #endregion

        /// <summary>
        /// Makes this a 1bpp channel
        /// </summary>
        /// <param name="threshold">Colors above this value are set 1</param>
        public void MakeBILevel(int threshold)
        {
            prec = 1;
            bpp = 1;

            for (int c = 0; c < data.Length; c++)
                data[c] = data[c] > threshold ? 1 : 0;
        }

        /// <summary>
        /// Change the precision of the component
        /// </summary>
        /// <param name="new_prec">New precision</param>
        public void ScaleBPC(int new_prec)
        {
            if (new_prec == prec)
                return;

            if (new_prec > prec)
            {
                //Scale up
                if (Signed)
                {
                    long newMax = 1u << (new_prec);
                    long oldMax = 1u << (int)prec;
                    for (int c = 0; c < data.Length; c++)
                        data[c] = (int)((data[c] * newMax) / oldMax);
                }
                else
                {
                    ulong newMax = 1u << (new_prec);
                    ulong oldMax = 1u << (int)prec;
                    for (int c = 0; c < data.Length; c++)
                        data[c] = (int)(((ulong)data[c] * newMax) / oldMax);
                }
            }
            else
            {
                //Scale down
                int shift = (int)(prec - new_prec);
                if (Signed)
                {
                    for (int c = 0; c < data.Length; c++)
                        data[c] >>= shift;
                }
                else
                {
                    for (int c = 0; c < data.Length; c++)
                        data[c] = (int)(((uint)data[c]) >> shift);
                }
            }
            prec = (uint)new_prec;
            bpp = new_prec;
        }
    }
}
