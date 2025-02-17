using System;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Tile coding parameters
    /// 
    /// This class is used to store coding/decoding
    /// parameters common to all tiles.
    /// </summary>
    /// <remarks>opj_tcp_t</remarks>
    internal class TileCodingParams : ICloneable
    {
        #region Variables and properties

        /// <summary>
        /// 1 : first part-tile of a tile
        /// </summary>
        //internal bool first;

        /// <summary>
        /// Coding style
        /// </summary>
        internal CP_CSTY csty;

        /// <summary>
        /// Progression order
        /// </summary>
        internal PROG_ORDER prg;

        /// <summary>
        /// Number of layers
        /// </summary>
        internal uint numlayers;
        internal uint num_layers_to_decode;

        /// <summary>
        /// Multi-component transform identifier
        /// </summary>
        internal uint mct;

        /// <summary>
        /// Rates of layers
        /// </summary>
        internal float[] rates = new float[100];

        /// <summary>
        /// Number of progression order changes
        /// </summary>
        internal uint numpocs;

        /// <summary>
        /// Progression order changes
        /// </summary>
        internal ProgOrdChang[] pocs = new ProgOrdChang[32];

        /// <summary>
        /// Number of ppt markers
        /// </summary>
        /// <remarks>Can probably be replaced by ppt_markers.Length</remarks>
        internal uint ppt_markers_count;

        /// <summary>
        /// PPT markers data (table indexed by Zppt)
        /// </summary>
        internal PPX[] ppt_markers;

        /// <summary>
        /// Packet header store there for futur use in t2_decode_packet
        /// </summary>
        internal byte[] ppt_data; internal int ppt_buffer;

        /// <summary>
        /// This is not ppt_data.Length, that's "ppt_len"
        /// </summary>
        internal int ppt_data_size;

        /// <summary>
        /// C# impl.
        /// From where to start reading the ppt_data array
        /// </summary>
        internal int ppt_data_start;

        /// <summary>
        /// Length of the ppt_data
        /// </summary>
        /// <remarks>
        /// C# impl. note:
        /// 
        /// This is always from ppt_position to ppt_data.Length. Though
        /// this may be changed if there's a need, but if there's no need
        /// ppt_len can be dropped.
        /// </remarks>
        internal int ppt_len;

        /// <summary>
        /// Add fixed_quality
        /// </summary>
        internal float[] distoratio = new float[100];

        /// <summary>
        /// Tile-component coding parameters
        /// </summary>
        internal TileCompParams[] tccps;

        /// <summary>
        /// Number of tile parts for the tile
        /// </summary>
        internal uint n_tile_parts;

        /// <summary>
        /// Current tile part number or -1 if first time into this tile
        /// </summary>
        internal int current_tile_part_number;

        /// <summary>
        /// Data for the tile
        /// </summary>
        internal byte[] data;

        /// <summary>
        /// Size of data
        /// </summary>
        /// <remarks>data_size is not always data.Length</remarks>
        internal int data_size;

        /// <summary>
        /// Multi-component transform normals
        /// </summary>
        internal double[] mct_norms;

        /// <summary>
        /// The Multi-component transform coding matrix
        /// </summary>
        internal float[] mct_coding_matrix, mct_decoding_matrix;

        internal MctData[] mct_records;

        /// <summary>
        /// The number of mct records.
        /// </summary>
        /// <remarks>Can be dropped for mct_records.Length</remarks>
        internal uint n_mct_records;

        /// <summary>
        /// The max number of mct records.
        /// </summary>
        internal uint n_max_mct_records;

        internal SimpleMccDecorrelationData[] mcc_records;

        /// <summary>
        /// The number of mcc records.
        /// </summary>
        internal uint n_mcc_records;

        /// <summary>
        /// The max number of mcc records.
        /// </summary>
        /// <remarks>Can be dropped for mcc_records.Length</remarks>
        internal uint n_max_mcc_records;

        /// <summary>
        /// used in case of multiple marker PPT (number of info already stored)
        /// </summary>
        //internal int ppt_store;

        /// <summary>
        /// Backingstore for cod, ppt and poc
        /// </summary>
        private uint bitvector1;

        /// <summary>
        /// There was a COD marker for the present tile
        /// </summary>
        internal bool cod
        {
            get { return (bitvector1 & 1u) != 0; }
            set { bitvector1 = value ? 1u | bitvector1 : ~1u & bitvector1; }
        }

        /// <summary>
        /// If pptthere was a PPT marker for the present tile
        /// </summary>
        internal bool ppt
        {
            get { return (bitvector1 & 2u) != 0; }
            set { bitvector1 = value ? 2u | bitvector1 : ~2u & bitvector1; }
        }

        /// <summary>
        /// Indicates if a POC marker has been used
        /// </summary>
        internal bool POC
        {
            get { return (bitvector1 & 4u) != 0; }
            set { bitvector1 = value ? 4u | bitvector1 : ~4u & bitvector1; }
        }


        #endregion

        #region Init

        internal TileCodingParams()
        {
            for (int c = 0; c < pocs.Length; c++)
                pocs[c] = new ProgOrdChang();
        }

        #endregion

        #region ICloneable

        public object Clone()
        {
            var tcp = (TileCodingParams) MemberwiseClone();
            tcp.rates = (float[]) rates.Clone();
            var p = new ProgOrdChang[pocs.Length];
            for (int c = 0; c < p.Length; c++)
                p[c] = (ProgOrdChang) pocs[c].Clone();
            tcp.pocs = p;
            var t = new TileCompParams[tccps.Length];
            for (int c = 0; c < t.Length; c++)
                t[c] = (TileCompParams)tccps[c].Clone();
            tcp.tccps = t;
            tcp.distoratio = (float[]) distoratio.Clone();
            return tcp;
        }

        #endregion
    }
}
