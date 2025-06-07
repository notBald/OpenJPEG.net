namespace OpenJpeg.Util
{
    /// <summary>
    /// Helper class for linear interpolation
    /// </summary>
    /// <remarks>
    /// This is a reworked WindowToViewport mapper, so may look a little odd
    /// 
    /// About interpolation techniques: http://paulbourke.net/miscellaneous/interpolation/
    /// http://stackoverflow.com/questions/1146281/cubic-curve-smooth-interpolation-in-c
    /// </remarks>
    internal class LinearInterpolator
    {
        #region Variables and properties

        /***
         * Used to transform X from window til viewport.
         */
        readonly double MAPPING_U;

        readonly double _y_min, _x_min;

        #endregion

        #region Init

        /// <summary>
        /// Maps from x_min/x_max to y_min/y_max
        /// </summary>
        /// <param name="x_min">Min value of the input range</param>
        /// <param name="x_max">Max value of the input range</param>
        /// <param name="y_min">Min value of the output range</param>
        /// <param name="y_max">Max value of the output range</param>
        public LinearInterpolator(double x_min, double x_max, double y_min, double y_max)
        {
            //Calcs the size of the window.
            //double Y_HEIGHT = y_max - y_min;
            double X_WIDTH = x_max - x_min;

            MAPPING_U = (y_max - y_min) / X_WIDTH;
            //MAPPING_V = (V_MAX - V_MIN) / Y_HEIGHT;

            _x_min = x_min;
            _y_min = y_min;
        }

        #endregion

        /// <summary>
        /// Reverses an interpolation
        /// </summary>
        /// <remarks>
        /// getWindowX
        /// </remarks>
        public double Deinterpolate(double x)
        {
            return (x - _y_min) / MAPPING_U + _x_min;
        }

        /// <summary>
        /// Interpolates x
        /// </summary>
        /// <remarks>
        /// getViewportX
        /// </remarks>
        public double Interpolate(double x)
        {
            return (x - _x_min) * MAPPING_U + _y_min;
        }

        /// <summary>
        /// Interpolates between two ranges.
        /// </summary>
        /// <param name="x">The number to convert</param>
        /// <param name="x_min">Minimum input range</param>
        /// <param name="x_max">Maximum input range</param>
        /// <param name="y_min">Minimum output range</param>
        /// <param name="y_max">Maximum output range</param>
        public static double Interpolate(double x, double x_min, double x_max, double y_min, double y_max)
        {
            return (x - x_min) * (y_max - y_min) / (x_max - x_min) + y_min;
        }
    }
}
