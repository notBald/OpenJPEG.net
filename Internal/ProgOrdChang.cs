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
using System;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Progression order changes
    /// </summary>
    public class ProgOrdChang : ICloneable
    {
        #region Variables and properties

        /// <summary>
        /// Resolution num start, Component num start, given by POC
        /// </summary>
	    public uint resno0, compno0;
	    
        /// <summary>
        /// Layer num end,Resolution num end, Component num end, given by POC
        /// </summary>
	    public uint layno1, resno1, compno1;
	    
        /// <summary>
        /// Layer num start,Precinct num start, Precinct num end
        /// </summary>
	    internal uint layno0, precno0, precno1;
	    
        /// <summary>
        /// Progression order enum
        /// </summary>
	    public PROG_ORDER prg1;

        /// <summary>
        /// Progression order enum
        /// </summary>
        internal PROG_ORDER prg;
	    
        /// <summary>
        /// Progression order string
        /// </summary>
        /// <remarks>
        /// C# Not used for anything except for converting to pgr1, so I've just
        ///    made it an enum from the start
        /// </remarks>
        public PROG_ORDER progorder;
	    
        /// <summary>
        /// Tile number
        /// </summary>
	    public uint tile;
	    
        /// <summary>
        /// Start and end values for Tile width and height
        /// </summary>
	    internal uint tx0,tx1,ty0,ty1;
	    
        /// <summary>
        /// Start value, initialised in pi_initialise_encode
        /// </summary>
	    internal uint layS, resS, compS, prcS;
	    
        /// <summary>
        /// End value, initialised in pi_initialise_encode
        /// </summary>
	    internal uint layE, resE, compE, prcE;
	    
        /// <summary>
        /// Start and end values of Tile width and height, initialised in pi_initialise_encode
        /// </summary>
	    internal uint txS,txE,tyS,tyE,dx,dy;
	    
        /// <summary>
        /// Temporary values for Tile parts, initialised in pi_create_encode
        /// </summary>
	    internal uint lay_t, res_t, comp_t, prc_t,tx0_t,ty0_t;

        #endregion

        #region Init

        #endregion

        #region ICloneable

        public object Clone()
        {
            var poc = (ProgOrdChang) MemberwiseClone();
            return poc;
        }

        #endregion
    }
}
