using System.Diagnostics;

namespace OpenJpeg.Internal
{
    internal static class J2KTables
    {
        /* Table A.53 from JPEG2000 standard */
        internal static ushort[] tabMaxSubLevelFromMainLevel = new ushort[] {
            15, /* unspecified */
            1,
            1,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9
        };
    }

    /// <summary>
    /// Support class for J2K tags
    /// </summary>
    /// <remarks>
    /// Yes, that's what it's called in the original source.
    /// 
    /// No, I don't know what the mangeled together letters
    /// mean either.
    /// 
    /// Renamed to j2k_memory_marker_handler_tab in 2.1
    /// </remarks>
    internal sealed class DecMstabent
    {
        #region Variables and properties

        readonly J2kMarker[] _marks;

        internal J2kMarker this[J2K_Marker mark]
        {
            get 
            {
                for (int c = 0; c < _marks.Length; c++)
                {
                    var a_mark = _marks[c];
                    if (a_mark.Mark == mark)
                        return a_mark;
                }
                return _marks[_marks.Length - 1];
            }
        }

        #endregion

        #region Init

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="parent">Parent J2K instance</param>
        //2.1
        internal DecMstabent(J2K parent)
        {
            _marks = new J2kMarker[]
            {
                new J2kMarker(J2K_Marker.SOT, J2K_STATUS.MH | J2K_STATUS.TPHSOT, parent.ReadSOT),
                new J2kMarker(J2K_Marker.COD, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadCOD),
                new J2kMarker(J2K_Marker.COC, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadCOC),
                new J2kMarker(J2K_Marker.RGN, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadRGN),
                new J2kMarker(J2K_Marker.QCD, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadQCD),
                new J2kMarker(J2K_Marker.QCC, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadQCC),
                new J2kMarker(J2K_Marker.POC, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadPOC),
                new J2kMarker(J2K_Marker.SIZ, J2K_STATUS.MHSIZ, parent.ReadSIZ),
                new J2kMarker(J2K_Marker.TLM, J2K_STATUS.MH, parent.ReadTLM),
                new J2kMarker(J2K_Marker.PLM, J2K_STATUS.MH, parent.ReadPLM),
                new J2kMarker(J2K_Marker.PLT, J2K_STATUS.TPH, parent.ReadPLT),
                new J2kMarker(J2K_Marker.PPM, J2K_STATUS.MH, parent.ReadPPM),
                new J2kMarker(J2K_Marker.PPT, J2K_STATUS.TPH, parent.ReadPPT),
                new J2kMarker(J2K_Marker.SOP, J2K_STATUS.NONE, null),
                new J2kMarker(J2K_Marker.CRG, J2K_STATUS.MH, parent.ReadCRG),
                new J2kMarker(J2K_Marker.COM, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadCOM),
                new J2kMarker(J2K_Marker.MCT, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadMCT),
                new J2kMarker(J2K_Marker.CBD, J2K_STATUS.MH, parent.ReadCBD),
                new J2kMarker(J2K_Marker.CAP, J2K_STATUS.MH, parent.ReadCAP),
                new J2kMarker(J2K_Marker.MCC, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadMCC),
                new J2kMarker(J2K_Marker.MCO, J2K_STATUS.MH | J2K_STATUS.TPH, parent.ReadMCO),

                //Unknown marker, must be last.
                new J2kMarker(J2K_Marker.NONE, J2K_STATUS.MH | J2K_STATUS.TPH, (b) => true)
            };
        }

        #endregion
    }

    [DebuggerDisplay("{Mark}")]
    internal struct J2kMarker
    {
        /// <summary>
        /// Maker value
        /// </summary>
        internal readonly J2K_Marker Mark;

        /// <summary>
        /// States this marker can appear in
        /// </summary>
        internal readonly J2K_STATUS States;

        /// <summary>
        /// Action linked to this marker
        /// </summary>
        internal readonly J2K_Action Handler;

        internal J2kMarker(J2K_Marker mark, J2K_STATUS states, J2K_Action handler)
        { Mark = mark; States = states; Handler = handler; }
    }

    delegate bool J2K_Action(int header_size);
}
