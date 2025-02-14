using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenJpeg.Internal
{
    [DebuggerDisplay("{expn} - {mant}")]
    internal struct StepSize
    {
        /// <summary>
        /// Exponent
        /// </summary>
        internal readonly int expn;

        /// <summary>
        /// Mantissa
        /// </summary>
        internal readonly int mant;

        internal StepSize(int e, int m)
        { expn = e; mant = m; }
    }

    /// <summary>
    /// This structure is used when one need to emulate
    /// a *ptr, without too much fuzz
    /// </summary>
    /// <remarks>
    /// int i = *ptr    ~=  int i = ptr.Data[Ptr.Pos]
    /// ptr = ptr2      ~=  ptr = ptr2.Clone()
    /// ptr = &ptr2[2]  ~=  ptr = ptr.Set(ptr2.Data, ptr2.Pos + 2)
    /// </remarks>
    internal sealed class ArPtr<T>
    {
        internal int Pos;
        internal T[] Data;
        internal ArPtr(T[] data, int pos)
        { Data = data; Pos = pos; }
        internal ArPtr(T[] data)
        { Data = data; Pos = 0; }
        internal ArPtr<T> Clone()
        { return new ArPtr<T>(Data, Pos); }
        internal void Set(T[] data, int pos)
        { Data = data; Pos = pos; }

        /// <summary>
        /// Creates a new ArPtr with Pos
        /// incremented by val
        /// </summary>
        /// <remarks>
        /// Useed for stuff like:
        /// int *ptr = ptr2 + 2 ~=  ArPtr ptr = ptr2.Add(2)
        /// </remarks>
        internal ArPtr<T> Add(int val)
        {
            return new ArPtr<T>(Data, Pos + val);
        }

        /// <summary>
        /// Derefs the pointer.
        /// </summary>
        internal T Deref { get { return Data[Pos]; } set { Data[Pos] = value; } }
    }

    /// <summary>
    /// Used to simulate pointers to pointers, for those scenarios
    /// where pointers are updated, and the pointers to the pointers
    /// then indirectly points at the new value.
    /// </summary>
    internal sealed class Ptr<T>
    {
        internal T P;
    }

    /// <summary>
    /// Experimental class for storing float data in integer arrays
    /// </summary>
    /// <remarks>
    /// This is used with TcdTilecomp.data in Tier1Coding to convert
    /// float data into raw integer data. I'm not sure if this is a
    /// good idea or not, but it does work.
    /// </remarks>
    [StructLayoutAttribute(LayoutKind.Explicit)]
    public struct IntOrFloat
    {
        [FieldOffset(0)]
        internal int I;
        [FieldOffset(0)]
        internal float F;
    }

    /// <summary>
    /// Experimental class
    /// </summary>
    [StructLayoutAttribute(LayoutKind.Explicit)]
    internal struct IntOrUInt
    {
        [FieldOffset(0)]
        internal int I;
        [FieldOffset(0)]
        internal uint U;
    }

    /// <summary>
    /// Experimental class for storing float data in integer arrays
    /// </summary>
    /// <remarks>
    /// This is used with TcdTilecomp.data in Tier1Coding to convert
    /// float data into raw integer data. I'm not sure if this is a
    /// good idea or not, but it does work.
    /// </remarks>
    [StructLayoutAttribute(LayoutKind.Explicit)]
    internal struct ShortOrIntOrFloatOrDoubleAr
    {
        [FieldOffset(0)]
        internal ELEMENT_TYPE Type;

        [FieldOffset(8)]
        internal short[] SA;
        [FieldOffset(8)]
        internal int[] IA;
        [FieldOffset(8)]
        internal float[] FA;
        [FieldOffset(8)]
        internal double[] DA;

        public bool IsNull { get { return Type == ELEMENT_TYPE.NULL; } }
        public static int SizeDiv(MCT_ELEMENT_TYPE type, int byte_size)
        {
            switch (type)
            {
                case MCT_ELEMENT_TYPE.MCT_TYPE_INT16: return byte_size / 2;
                case MCT_ELEMENT_TYPE.MCT_TYPE_INT32:
                case MCT_ELEMENT_TYPE.MCT_TYPE_FLOAT: return byte_size / 4;
                case MCT_ELEMENT_TYPE.MCT_TYPE_DOUBLE: return byte_size / 8;
            }
            return byte_size;
        }

        public ShortOrIntOrFloatOrDoubleAr(MCT_ELEMENT_TYPE type, int size)
            : this()
        {
            SA = null; IA = null; FA = null; DA = null;
            Type = (ELEMENT_TYPE) ((int) type + 1);
            switch (Type)
            {
                case ELEMENT_TYPE.INT16:
                    SA = new short[size];
                    break;
                case ELEMENT_TYPE.INT32:
                    IA = new int[size];
                    break;
                case ELEMENT_TYPE.FLOAT:
                    FA = new float[size];
                    break;
                case ELEMENT_TYPE.DOUBLE:
                    DA = new double[size];
                    break;
            }
        }

        public void CopyFromFloat(float[] ar, int num_to_copy)
        {
            switch (Type)
            {
                case ELEMENT_TYPE.INT16:
                    for (int c = 0; c < num_to_copy; c++)
                        SA[c] = (short)ar[c];
                    break;
                case ELEMENT_TYPE.INT32:
                    for (int c = 0; c < num_to_copy; c++)
                        IA[c] = (int)ar[c];
                    break;
                case ELEMENT_TYPE.FLOAT:
                    Array.Copy(ar, FA, num_to_copy);
                    break;
                case ELEMENT_TYPE.DOUBLE:
                    for (int c = 0; c < num_to_copy; c++)
                        DA[c] = (double)ar[c];
                    break;
            }
        }

        public void CopyToFloat(float[] ar, int num_to_copy)
        {
            switch (Type)
            {
                case ELEMENT_TYPE.INT16:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (float)SA[c];
                    break;
                case ELEMENT_TYPE.INT32:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (float)IA[c];
                    break;
                case ELEMENT_TYPE.FLOAT:
                    Buffer.BlockCopy(FA, 0, ar, 0, num_to_copy * sizeof(float));
                    break;
                case ELEMENT_TYPE.DOUBLE:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (float)DA[c];
                    break;
            }
        }

        public void CopyToInt(int[] ar, int num_to_copy)
        {
            switch (Type)
            {
                case ELEMENT_TYPE.INT16:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (int)SA[c];
                    break;
                case ELEMENT_TYPE.INT32:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (int)IA[c];
                    break;
                case ELEMENT_TYPE.FLOAT:
                    Buffer.BlockCopy(FA, 0, ar, 0, num_to_copy * sizeof(float));
                    break;
                case ELEMENT_TYPE.DOUBLE:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (int)DA[c];
                    break;
            }
        }

        public void CopyToInt(uint[] ar, int num_to_copy)
        {
            switch (Type)
            {
                case ELEMENT_TYPE.INT16:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (uint)SA[c];
                    break;
                case ELEMENT_TYPE.INT32:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (uint)IA[c];
                    break;
                case ELEMENT_TYPE.FLOAT:
                    Buffer.BlockCopy(FA, 0, ar, 0, num_to_copy * sizeof(float));
                    break;
                case ELEMENT_TYPE.DOUBLE:
                    for (int c = 0; c < num_to_copy; c++)
                        ar[c] = (uint)DA[c];
                    break;
            }
        }

        public void SetBytes(byte[] bytes)
        {
            Array ar;
            switch (Type)
            {
                case ELEMENT_TYPE.INT16:
                    SA = new short[bytes.Length / 2];
                    ar = SA;
                    break;
                case ELEMENT_TYPE.INT32:
                    IA = new int[bytes.Length / 4];
                    ar = IA;
                    break;
                case ELEMENT_TYPE.FLOAT:
                    FA = new float[bytes.Length / 4];
                    ar = FA;
                    break;
                case ELEMENT_TYPE.DOUBLE:
                    DA = new double[bytes.Length / 8];
                    ar = DA;
                    break;
                default:
                    throw new NotSupportedException();
            }
            Buffer.BlockCopy(bytes, 0, ar, 0, bytes.Length);
        }

        public byte[] ToArray()
        {
            int size;
            Array ar;
            switch (Type)
            {
                case ELEMENT_TYPE.INT16:
                    size = SA.Length * sizeof(short);
                    ar = SA;
                    break;
                case ELEMENT_TYPE.INT32:
                    size = IA.Length * sizeof(int);
                    ar = IA;
                    break;
                case ELEMENT_TYPE.FLOAT:
                    size = FA.Length * sizeof(float);
                    ar = FA;
                    break;
                case ELEMENT_TYPE.DOUBLE:
                    size = DA.Length * sizeof(double);
                    ar = DA;
                    break;
                default:
                    throw new NotSupportedException();
            }
            var r = new byte[size];
            Buffer.BlockCopy(ar, 0, r, 0, size);
            return r;
        }

        public void Null()
        {
            Type = ELEMENT_TYPE.NULL;
            SA = null;
        }
    }

    struct JP2Comps
    {
        internal uint depth, sgnd, bpcc;
    }

    struct JP2Box
    {
        public JP2_Marker type;
        public long init_pos;
        public uint length, data_length;
    }

    /// <summary>
    /// Channel description: channel index, type, assocation
    /// </summary>
    struct JP2cdefInfo
    {
        internal ushort cn, typ, asoc;
    }

    /// <summary>
    /// Channel descriptions and number of descriptions
    /// </summary>
    /*class JP2cdef
    {
        internal JP2cdefInfo[] info;
        internal ushort n;
    }*/

    /// <summary>
    /// Component mappings: channel index, mapping type, palette index
    /// </summary>
    struct JP2cmap_comp
    {
        internal ushort cmp;
        internal byte mtyp, pcol;
    }

    /// <summary>
    /// Palette data: table entries, palette columns
    /// </summary>
    class JP2pclr
    {
        internal uint[] entries;
        internal byte[] channel_sign;
        internal byte[] channel_size;
        internal JP2cmap_comp[] cmap;
        internal ushort nr_entries, nr_channels;
    }

    /// <summary>
    /// Collector for ICC profile, palette, component mapping, channel description 
    /// </summary>
    sealed class JP2Color
    {
        internal byte[] icc_profile_buf;

        //The original impl. puts this on icc_profile_buf with a icc_profile_len of 0
        internal uint[] icc_cielab_buf;
        //int icc_profile_len;

        internal JP2cdefInfo[] channel_definitions;
        internal JP2pclr jp2_pclr;
        bool jp2_has_colr;
        internal bool ignore_pclr_cmap_cdef;
        internal bool ignore_cmap;
        internal bool HasColor { get { return jp2_has_colr; } set { jp2_has_colr = value; } }
    }

    /// <summary>
    /// Index structure of the codestream
    /// </summary>
    internal sealed class CodestreamIndex
    {
        /// <summary>
        /// Main header start position (SOC position)
        /// </summary>
        internal long main_head_start;

        /// <summary>
        /// Main header end position (first SOT position)
        /// </summary>
        internal long main_head_end;

        /// <summary>
        /// List of markers
        /// </summary>
        internal MarkerInfo[] marker;

        /// <summary>
        /// Number of markers
        /// </summary>
        internal uint marknum;

        /// <summary>
        /// Actual size of markers array
        /// </summary>
        internal uint maxmarknum;

        internal uint n_of_tiles;

        internal TileIndex[] tile_index;

        //2.5
        internal CodestreamIndex()
        {
            maxmarknum = 100;
            marker = new MarkerInfo[maxmarknum];
        }
    }

    internal struct TileIndex
    {
        /// <summary>
        /// Tile index
        /// </summary>
        public uint tileno;

        /// <summary>
        /// Number of tile parts
        /// </summary>
        public uint n_tps;

        /// <summary>
        /// Current nb of tile part (allocated)
        /// </summary>
        public uint current_n_tps;

        /// <summary>
        /// Current tile-part index
        /// </summary>
        public uint current_tpsno;

        /// <summary>
        /// Information concerning tile parts
        /// </summary>
        public TPIndex[] tp_index;

        /// <summary>
        /// Number of markers
        /// </summary>
        public uint marknum;

        /// <summary>
        /// List of markers
        /// </summary>
        public MarkerInfo[] marker;

        /// <summary>
        /// Actual size of markers array
        /// </summary>
        public uint maxmarknum;
    }

    internal struct MarkerInfo
    {
        /// <summary>
        /// Marker type
        /// </summary>
        public J2K_Marker type;

        /// <summary>
        /// Position in codestream
        /// </summary>
        public long pos;

        /// <summary>
        /// Length, marker val included
        /// </summary>
        public int len;
    }

    /// <summary>
    /// Structure to hold information needed to generate some markers.
    /// Used by encoder.
    /// </summary>
    /// <remarks>
    /// 2.5 - opj_tcd_marker_info
    /// 
    /// Implemented as class instead of struct
    /// </remarks>
    internal class TcdMarkerInfo
    {
        /// <summary>
        /// In: Whether information to generate PLT markers in needed
        /// </summary>
        public bool need_PLT;

        /// <summary>
        /// OUT: Number of elements in p_packet_size[] array
        /// </summary>
        public uint packet_count;

        /// <summary>
        /// OUT: Array of size packet_count, such that p_packet_size[i] is
        ///      the size in bytes of the ith packet
        /// </summary>
        public uint[] p_packet_size;
    }

    internal struct TPIndex
    {
        /// <summary>
        /// Start position
        /// </summary>
        public long start_pos;

        /// <summary>
        /// End position of the header
        /// </summary>
        public long end_header;

        /// <summary>
        /// End position
        /// </summary>
        public long end_pos;
    }
}
