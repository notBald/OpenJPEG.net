namespace OpenJpeg
{
    public static class Constants
    {
        /// <summary>
        /// Number of maximum resolution level authorized
        /// </summary>
        internal const int J2K_MAXRLVLS = 33;

        /// <summary>
        /// Number of maximum sub-band linked to number of resolution level
        /// </summary>
        internal const int J2K_MAXBANDS = (3 * J2K_MAXRLVLS - 2);
        internal const int J2K_DEFAULT_NB_SEGS = 10;
        internal const int J2K_TCD_MATRIX_MAX_LAYER_COUNT = 10;
        internal const int J2K_TCD_MATRIX_MAX_RESOLUTION_COUNT = 10;

        internal const int MQC_NUMCTXS = 19;

        internal const int T1_NUMCTXS = (int)T1_CTXNO.UNI + (int)OpenJpeg.T1_NUMCTXS.UNI;

        internal const int T1_NMSEDEC_BITS = 7;

        internal const int T1_NMSEDEC_FRACBITS = (T1_NMSEDEC_BITS - 1);

        public const int CINEMA_24_CS = 1302083;
        public const int CINEMA_48_CS = 651041;
        public const int CINEMA_24_COMP = 1041666;
        public const int CINEMA_48_COMP = 520833;

        internal const int MCT_DEFAULT_NB_RECORDS = 10;
        internal const int MCC_DEFAULT_NB_RECORDS = 10;

        internal const int DEFAULT_CBLK_DATA_SIZE = 8192;

        /// <summary>
        /// Org impl have this as ulong.MaxValue, but C# has a 2GB limit.
        /// </summary>
        internal const int SIZE_MAX = int.MaxValue;

        /// <summary>
        /// Default size of the buffer for header bytes
        /// </summary>
        internal const int J2K_DEFAULT_HEADER_SIZE = 1000;

        /// <summary>
        /// Margin for a fake FFFF marker
        /// </summary>
        internal const int COMMON_CBLK_DATA_EXTRA = 2;

        internal const int PARAM_DEFAULT_NUMRESOLUTION = 6;
        internal const int PARAM_DEFAULT_CBLOCKW = 64;
        internal const int PARAM_DEFAULT_CBLOCKH = 64;
        internal const PROG_ORDER PARAM_DEFAULT_PROG_ORDER = PROG_ORDER.LRCP;

        /// <summary>
        /// Maximum main level
        /// </summary>
        internal const ushort IMF_MAINLEVEL_MAX = 11;
    }
}
