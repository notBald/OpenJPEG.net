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
    /// Tag tree decoder
    /// 
    /// The tag tree is an efficient way of storing 2d matrixes. It
    /// basically works like this:
    /// 
    ///  The tag tree has a threshold. When decoding data it decodes
    ///  just enough to identify if the data is greater than or equal
    ///  to the treshold (given as a parameter) or the current value, 
    ///  and when said data is bigger (than either the current value
    ///  or the treshold) it updates the value, and returns if it was
    ///  bigger than the treshold.
    /// 
    ///  For more detail:
    ///  http://www.dmi.unict.it/~impoco/files/tutorial_JPEG2000.pdf
    ///  
    /// The tag tree is used for compressing the packet headers (not
    /// 2d matrixes) as they lend themselves to that.
    /// 
    /// </summary>
    /// <remarks>
    /// C# impl. note: Based on tgt.h and tgt.c
    /// </remarks>
    internal sealed class TagTree
    {
        #region Variables and properties

        uint _numleafsh;
        uint _numleafsv;
        uint _numnodes;
        TagNode[] nodes;

        #endregion

        #region Init

        //2.5 - opj_tgt_create
        internal static TagTree Create(uint numleafsh, uint numleafsv, CompressionInfo cinfo)
        {
            uint n;
            var tt = new TagTree();

            tt._numleafsh = numleafsh;
            tt._numleafsv = numleafsv;

            int[] nplh = new int[32];
            int[] nplv = new int[32];

            int numlvls = 0;
            nplh[0] = (int)numleafsh;
            nplv[0] = (int)numleafsv;
            tt._numnodes = 0;
            do
            {
                n = (uint)(nplh[numlvls] * nplv[numlvls]);
                nplh[numlvls + 1] = (nplh[numlvls] + 1) / 2;
                nplv[numlvls + 1] = (nplv[numlvls] + 1) / 2;
                tt._numnodes += n;
                ++numlvls;
            } while (n > 1);

            // ADD
            if (tt._numnodes == 0)
            {
                //C# openJpeg 2.5 dosn't emit this warning
                //cinfo.Warn("tgt_create tree->numnodes == 0, no tree created.");
                return null;
            }

            //Annoying, but the org. code makes use of a invalid pointer. (I.e. a non-null pointer to nowhere)
            //I've rewritten to make C# happy, but this needs to be tested.
            // (org. code is tgt_create(..) in tft.c)
            tt.nodes = new TagNode[tt._numnodes];
            for (int c = 0; c < tt.nodes.Length; c++)
                tt.nodes[c] = new TagNode();
            TagNode parentnode;
            int node_pos = 0;
            int parrent_node_pos = (int)(numleafsh * numleafsv);
            int parrent0_node_pos = parrent_node_pos;
            if (parrent_node_pos >= tt.nodes.Length)
                parentnode = null; //<-- Invalid parrent
            else
                parentnode = tt.nodes[parrent_node_pos];
            TagNode parentnode0 = parentnode;

            for (int i = 0; i < numlvls - 1; i++)
            {
                for (int j = 0; j < nplv[i]; j++)
                {
                    int k = nplh[i];
                    while (--k >= 0)
                    {
                        tt.nodes[node_pos++].parent = parentnode;
                        if (--k >= 0)
                            tt.nodes[node_pos++].parent = parentnode;

                        parrent_node_pos++;
                        if (parrent_node_pos >= tt.nodes.Length)
                            parentnode = null;
                        else
                            parentnode = tt.nodes[parrent_node_pos];
                    }
                    if ((j & 1) == 1 || j == nplv[i] - 1)
                    {
                        parentnode0 = parentnode;
                        parrent0_node_pos = parrent_node_pos;
                    }
                    else
                    {
                        parentnode = parentnode0;
                        parrent_node_pos = parrent0_node_pos;
                        parrent0_node_pos += nplh[i];
                    }
                }
            }
            tt.nodes[node_pos].parent = null;

            Reset(tt);

            return tt;
        }

        //2.5
        private TagTree()
        { }

        /// <summary>
        /// Reinitialises a tag-tree from an existing one.
        /// </summary>
        /// <param name="p_num_leafs_h">the width of the array of leafs of the tree</param>
        /// <param name="p_num_leafs_v">the height of the array of leafs of the tree</param>
        //2.1
        internal void Init(uint num_leafs_h, uint num_leafs_v)
        {
            if (_numleafsh == num_leafs_h && _numleafsv == num_leafs_v)
                return;

            this._numleafsh = num_leafs_h;
            this._numleafsv = num_leafs_v;

            int[] nplh = new int[32];
            int[] nplv = new int[32];
            int _num_levels = 0;
            nplh[0] = (int)_numleafsh;
            nplv[0] = (int)_numleafsv;
            _numnodes = 0;

            uint n;
            do
            {
                n = (uint)(nplh[_num_levels] * nplv[_num_levels]);
                nplh[_num_levels + 1] = (nplh[_num_levels] + 1) / 2;
                nplv[_num_levels + 1] = (nplv[_num_levels] + 1) / 2;
                _numnodes += n;
                ++_num_levels;
            } while (n > 1);

            // ADD
            if (_numnodes == 0)
            {
                return;
            }
            if (nodes.Length != _numnodes)
            {
                int old_size = nodes.Length;
                Array.Resize<TagNode>(ref nodes, (int)_numnodes);
                for (int c = old_size; c < nodes.Length; c++)
                    nodes[c] = new TagNode();
            }

            //Annoying, but the org. code makes use of a invalid pointer.
            //I've rewritten to make C# happy, but this needs to be tested.
            // (org. code is tgt_create(..) in tft.c)
            TagNode parentnode;
            int node_pos = 0;
            int parrent_node_pos = (int)(_numleafsh * _numleafsv);
            int parrent0_node_pos = parrent_node_pos;
            if (parrent_node_pos >= nodes.Length)
                parentnode = null; //<-- Invalid parrent pointer is set to null
            else
                parentnode = nodes[parrent_node_pos];
            TagNode parentnode0 = parentnode;

            for (int i = 0; i < _num_levels - 1; i++)
            {
                for (int j = 0; j < nplv[i]; j++)
                {
                    int k = nplh[i];
                    while (--k >= 0)
                    {
                        nodes[node_pos++].parent = parentnode;
                        if (--k >= 0)
                            nodes[node_pos++].parent = parentnode;

                        parrent_node_pos++;
                        if (parrent_node_pos >= nodes.Length)
                            parentnode = null;
                        else
                            parentnode = nodes[parrent_node_pos];
                    }
                    if ((j & 1) == 1 || j == nplv[i] - 1)
                    {
                        parentnode0 = parentnode;
                        parrent0_node_pos = parrent_node_pos;
                    }
                    else
                    {
                        parentnode = parentnode0;
                        parrent_node_pos = parrent0_node_pos;
                        parrent0_node_pos += nplh[i];
                    }
                }
            }
            nodes[node_pos].parent = null;

            Reset(this);
        }

        /// <summary>
        /// Resets the tree
        /// </summary>
        /// <remarks>2.5 - opj_tgt_reset</remarks>
        internal static void Reset(TagTree tt)
        {
            if (tt == null) return;
            for (int i = 0; i < tt._numnodes; i++)
            {
                var current_node = tt.nodes[i];
                current_node.value = 999;
                current_node.low = 0;
                current_node.known = false;
            }
        }

        #endregion

        /// <summary>
        /// Set the value of a leaf of a tag-tree
        /// </summary>
        /// <param name="leafno">Number that identifies the leaf to modify</param>
        /// <param name="value">New value of the leaf</param>
        /// <remarks>2.5 - opj_tgt_setvalue</remarks>
        internal void SetValue(int leafno, int value)
        {
            TagNode node = nodes[leafno];
            while (node != null && node.value > value)
            {
                node.value = value;
                node = node.parent;
            }
        }

        /// <summary>
        /// Encode the value of a leaf of the tag-tree up to a given threshold
        /// </summary>
        /// <param name="bio">Pointer to a BIO handle</param>
        /// <param name="leafno">Number that identifies the leaf to encode</param>
        /// <param name="threshold">Threshold to use when encoding value of the leaf</param>
        /// <remarks>2.5 - opj_tgt_encode</remarks>
        internal void Encode(WBIO bio, int leafno, uint threshold)
        {
            var stk = new TagNode[31];

            //C# Setting it to -1 so that we can detect
            //   that it hasn't moved. Org. impl does a
            //   pointer comparison instead.
            int stkptr = -1;

            //Builds a list that goes from "great grandparent"
            //in node, to the leaf node on the beginning of the 
            //list
            TagNode node = nodes[leafno];
            while (node.parent != null)
            {
                stk[++stkptr] = node;
                node = node.parent;
            }

            //Computes the list. Works from highest parent
            //and down to the leaf node
            for (int low = 0; ; )
            {
                if (low > node.low)
                    node.low = low;
                else
                    low = node.low;

                while (low < threshold)
                {
                    if (low >= node.value)
                    {
                        if (!node.known)
                        {
                            bio.WriteBit(1);
                            node.known = true;
                        }
                        break;
                    }
                    bio.WriteBit(0);
                    low++;
                }

                node.low = low;
                if (stkptr < 0) break;

                //Gets a descendant/leaf node of this node
                node = stk[stkptr--];
            }
        }

        //2.5
        internal bool Decode(BIO bio, uint leafno, uint threshold)
        {
            var stk = new TagNode[31];
            int stk_pos = -1;

            //Builds a list that goes from "great grandparent"
            //in node, to the leaf node on the beginning of the 
            //list
            TagNode node = nodes[leafno];
            while (node.parent != null)
            {
                stk[++stk_pos] = node;
                node = node.parent;
            }

            //Computes the list. Works from highest parent
            //and down to the leaf node
            for (int low = 0; ; )
            {
                if (low > node.low)
                    node.low = low;
                else
                    low = node.low;
                while (low < threshold && low < node.value)
                {
                    if (bio.ReadBool())
                        node.value = low;
                    else
                        low++;
                }
                node.low = low;

                //C# Org impl uses a pointer, we use an array index.
                // i.e. there's stk_pos == stk here.
                if (stk_pos < 0) break;

                //Gets a descendant/leaf node of this node
                node = stk[stk_pos--];
            }

            return (node.value < threshold);
        }
    }

    [DebuggerDisplay("{parent} - {value}")]
    internal class TagNode
    {
        internal TagNode parent;
        internal int value;
        internal int low;
        internal bool known;
    }
}
