namespace OpenJpeg.Internal
{
    /// <summary>
    /// Keeping the math identical with the org. impl.
    /// </summary>
    internal static class MyMath
    {
        /// <summary>
        /// Smallest such that 1.0+DBL_EPSILON != 1.0
        /// </summary>
        public const double DBL_EPSILON = 2.2204460492503131e-016;

        /// <summary>
        /// Multiply two fixed-precision rational numbers.
        /// </summary>
        public static int fix_mul(int a, int b)
        {
            long temp = (long)a * (long)b;
            temp += temp & 4096;
            return (int)(temp >> 13);
        }

        public static int int_ceildiv(int a, int b) 
        {
	        return (a + b - 1) / b;
        }

        public static uint uint_ceildiv(uint a, uint b)
        {
            return (a + b - 1) / b;
        }

        public static int int_max(int a, int b)
        {
            return (a > b) ? a : b;
        }

        public static int int_min(int a, int b)
        {
            return a < b ? a : b;
        }

        public static uint uint_min(uint a, uint b)
        {
            return a < b ? a : b;
        }

        public static int int_ceildivpow2(int a, int b)
        {
            return (int)(((ulong)a + (1ul << b) - 1) >> b);
        }

        public static int int64_ceildivpow2(long a, int b)
        {
            return (int)((a + (1L << b) - 1L) >> b);
        }

        public static uint uint_ceildivpow2(uint a, int b)
        {
            return (uint) ((a + (1ul << b) - 1U) >> b);
        }

        public static int int_floordivpow2(int a, int b)
        {
            return a >> b;
        }

        public static uint uint_floordivpow2(uint a, int b)
        {
            return a >> b;
        }

        public static int int_floorlog2(int a)
        {
            int l;
            for (l = 0; a > 1; l++)
            {
                a >>= 1;
            }
            return l;
        }

        public static uint uint_floorlog2(uint a)
        {
            uint l;
            for (l = 0; a > 1; l++)
            {
                a >>= 1;
            }
            return l;
        }

        public static int int_clamp(int a, int min, int max)
        {
            if (a < min)
                return min;
            if (a > max)
                return max;
            return a;
        }

        public static long int64_clamp(long a, long min, long max)
        {
            if (a < min)
                return min;
            if (a > max)
                return max;
            return a;
        }

        /// <summary>
        /// Get absolute value of integer
        /// </summary>
        public static int int_abs(int a) 
        {
	        return a < 0 ? -a : a;
        }

        /// <summary>
        /// Get the saturated sum of two unsigned integers
        /// </summary>
        /// <returns>Returns saturated sum of a+b</returns>
        public static uint uint_adds(uint a, uint b)
        {
            var sum = (ulong) a + (ulong)b;
            return (uint)(-(uint)(sum >> 32)) | (uint)sum;
        }

        /// <summary>
        /// Get the saturated difference of two unsigned integers
        /// </summary>
        /// <returns>Returns saturated sum of a-b</returns>
        public static uint uint_subs(uint a, uint b)
        {
            return (a >= b) ? a - b : 0;
        }

        /// <summary>
        /// Addition two signed integers with a wrap-around behaviour.
        /// Assumes complement-to-two signed integers.
        /// </summary>
        /// <returns>a + b</returns>
        public static int int_add_no_overflow(int a, int b)
        {
            //C# original impl does a lot of pointer magic, to avoid casting the
            //   org. values. I'm unsure how to do this, going with an ugly hack
            //   for now.
            var pa = new IntOrUInt();
            var pb = new IntOrUInt();
            pa.I = a;
            pb.I = b;
            uint ures = pa.U + pb.U;
            pa.U = ures;
            return pa.I;

            //TODO: Test if this can replace the code above
            //return (int)((uint)a + (uint)b);
        }

        /// <summary>
        /// Subtract two signed integers with a wrap-around behaviour.
        /// Assumes complement-to-two signed integers.
        /// </summary>
        /// <returns>a - b</returns>
        public static int int_sub_no_overflow(int a, int b)
        {
            //C# original impl does a lot of pointer magic, to avoid casting the
            //   org. values. I'm unsure how to do this, going with an ugly hack
            //   for now.
            var pa = new IntOrUInt();
            var pb = new IntOrUInt();
            pa.I = a;
            pb.I = b;
            uint ures = pa.U - pb.U;
            pa.U = ures;
            return pa.I;

            //TODO: Test if this can replace the code above
            //return (int)((uint)a - (uint)b);
        }
    }
}
