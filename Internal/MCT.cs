using System;
using System.Diagnostics;

namespace OpenJpeg.Internal
{
    /// <summary>
    /// Multi-component transforms (MCT)
    /// </summary>
    internal static class MCT
    {
        static double[] mct_norms = { 1.732, .8292, .8292 };
        static double[] mct_norms_real = { 1.732, 1.805, 1.573 };
        internal static uint[] ELEMENT_SIZE = { 2, 4, 4, 8 };

        internal static void CalculateNorms(double[] pNorms,
                                            uint pNbComps,
                                            float[] pMatrix)
        {
            uint i, j, lIndex;
            float lCurrentValue;

            for (i = 0; i < pNbComps; ++i)
            {
                pNorms[i] = 0;
                lIndex = i;

                for (j = 0; j < pNbComps; ++j)
                {
                    lCurrentValue = pMatrix[lIndex];
                    lIndex += pNbComps;
                    pNorms[i] += lCurrentValue * lCurrentValue;
                }
                pNorms[i] = Math.Sqrt(pNorms[i]);
            }
        }

        //2.1
        internal static double[] GetMctNorms()
        {
            return mct_norms;
        }

        //2.1
        internal static double[] GetMctNormsReal()
        {
            return mct_norms_real;
        }

        internal static double Getnorm(int compno)
        {
            return mct_norms[compno];
        }

        internal static double Getnorm_real(int compno)
        {
            return mct_norms_real[compno];
        }

        /// <summary>
        /// Encodes using a custom Multi-component transform matrix. 
        /// </summary>
        /// <param name="mct">The custom multi-component transform matrix</param>
        /// <param name="n">Number of elements in mct</param>
        /// <param name="data">Data to transform</param>
        /// <param name="isSigned">Ignored</param>
        /// <remarks>2.5 - opj_mct_encode_custom</remarks>
        internal static void EncodeCustom(float[] mct, int n, int[][] data, bool isSigned)
        {
            Debug.Assert(false, "Untested code"); //<-- Org code has multi dimensional pointers.
            uint NMatCoeff = (uint) (data.Length * data.Length); //C#: data.Length = pNbComp
            uint multiplicator = 1 << 13;

            //C# Org impl creates two arrays with one malloc (that's why there's data.Length + NMatCoeff)
            int[] current_data = new int[data.Length];
            int[] current_matrix = new int[NMatCoeff];

            for (int i = 0; i < current_matrix.Length; i++)
                current_matrix[i] = (int)(mct[i] * multiplicator);

            //C# d = pointer on data's second dimension
            int[] d = new int[data.Length];
            for (int i = 0, cm_ptr = 0; i < n; i++)
            {
                //Current data is filled up with one int from each component
                for (int j = 0; j < data.Length; j++)
                    current_data[j] = data[j][d[j]];

                for (int j = 0; j < data.Length; j++)
                {
                    var data_j = data[j];
                    var d_j = d[j];
                    data_j[d_j] = 0;
                    for (int k = 0; k < data.Length; k++)
                    {
                        data_j[d_j] += MyMath.fix_mul(current_matrix[cm_ptr++], current_data[k]);
                    }

                    //C# Incrementing the pointer to the data, not the pointer to the pointers
                    d[j]++;
                }
            }
        }

        /// <remarks>
        /// 2.5
        /// 
        /// C# Original implementation shifts between floats and integers. We work
        ///    around this headace by using an interopt struct. It's probably not
        ///    the best solution.
        ///    
        ///    What I need is to be able to convert a int to a float, without actually
        ///    converting anything. 
        /// </remarks>
        internal static void DecodeCustom(float[] mct, int n, int[][] data, uint n_comps, bool isSigned)
        {
            IntOrFloat[] current_data = new IntOrFloat[n_comps];
            IntOrFloat[] current_result = new IntOrFloat[n_comps]; 

            for (int i = 0, d = 0; i < n; i++)
            {
                int mct_c = 0;
                for (int j = 0; j < data.Length; j++)
                    current_data[j].I = data[j][d];

                for (int j = 0; j < data.Length; j++)
                {
                    current_result[j].F = 0;
                    for (int k = 0; k < n_comps; k++)
                        current_result[j].F += mct[mct_c++] * current_data[k].F;

                    data[j][d++] = current_result[j].I;
                }
            }
        }

        //2.5 - opj_mct_encode_real
        internal static void EncodeReal(int[] c0, int[] c1, int[] c2, int n)
        {
            IntOrFloat r = new IntOrFloat(), g = new IntOrFloat(), b = new IntOrFloat();
            IntOrFloat y = new IntOrFloat(), u = new IntOrFloat(), v = new IntOrFloat();
            for (int i = 0; i < n; ++i)
            {
                r.I = c0[i];
                g.I = c1[i];
                b.I = c2[i];
#if !TEST_MATH_MODE
                y.F = 0.299f * r.F + 0.587f * g.F + 0.114f * b.F;
                u.F = -0.16875f * r.F - 0.331260f * g.F + 0.5f * b.F;
                v.F = 0.5f * r.F - 0.41869f * g.F - 0.08131f * b.F;
#else
                y.F = (float)((float)(0.299f * r.F) + (float)(0.587f * g.F)) + (float)(0.114f * b.F);
                u.F = (float)((float)(-0.16875f * r.F) - (float)(0.331260f * g.F)) + (float)(0.5f * b.F);
                v.F = (float)((float)(0.5f * r.F) - (float)(0.41869f * g.F)) - (float)(0.08131f * b.F);
#endif
                c0[i] = y.I;
                c1[i] = u.I;
                c2[i] = v.I;
            }
        }

        /// <summary>
        /// Apply a reversible multi-component transform to an image
        /// </summary>
        /// <param name="c0">Samples for red component</param>
        /// <param name="c1">Samples for green component</param>
        /// <param name="c2">Samples blue component</param>
        /// <param name="n">Number of samples for each component</param>
        /// <remarks>2.5 - opj_mct_encode</remarks>
        internal static void Encode(int[] c0, int[] c1, int[] c2, int n)
        {
            //C# Snip SSE code

            for (int i = 0; i < n; ++i)
            {
                int r = c0[i];
                int g = c1[i];
                int b = c2[i];
                int y = (r + (g * 2) + b) >> 2;
                int u = b - g;
                int v = r - g;
                c0[i] = y;
                c1[i] = u;
                c2[i] = v;
            }
        }

        /// <summary>
        /// Apply a reversible multi-component inverse transform to an image
        /// </summary>
        /// <param name="c0">Samples for luminance component</param>
        /// <param name="c1">Samples for red chrominance component</param>
        /// <param name="c2">Samples for blue chrominance component</param>
        /// <param name="n">Number of samples for each component</param>
        ///<remarks>
        ///2.5
        ///
        /// There's a SSE2 variant of this algo. 
        /// </remarks>
        internal static void Decode(int[] c0, int[] c1, int[] c2, int n)
        {
            for (int i = 0; i < n; ++i)
            {
                int y = c0[i];
                int u = c1[i];
                int v = c2[i];
                int g = y - ((u + v) >> 2);
                int r = v + g;
                int b = u + g;
                c0[i] = r;
                c1[i] = g;
                c2[i] = b;
            }
        }

        /// <summary>
        /// Apply a reversible multi-component inverse transform to an image
        /// </summary>
        /// <remarks>
        /// 2.5 - opj_mct_decode_real
        /// </remarks>
        /// <param name="c0">Samples for luminance component</param>
        /// <param name="c1">Samples for red chrominance component</param>
        /// <param name="c2">Samples for blue chrominance component</param>
        /// <param name="n">Number of samples for each component</param>
        internal static void DecodeReal(int[] c0, int[] c1, int[] c2, int n)
        {
            IntOrFloat y = new IntOrFloat();
            IntOrFloat u = new IntOrFloat();
            IntOrFloat v = new IntOrFloat();
            IntOrFloat r = new IntOrFloat();
            IntOrFloat g = new IntOrFloat();
            IntOrFloat b = new IntOrFloat();

            //C# Snip SSE2

            //C# Liberal application of (float) to reduce math precision to the same as the org. impl
            for (int i = 0; i < n; ++i)
            {
                y.I = c0[i];
                u.I = c1[i];
                v.I = c2[i];
#if !TEST_MATH_MODE
                r.F = y.F + (v.F * 1.402f);
                g.F = y.F - (u.F * 0.34413f) - (v.F * 0.71414f);
                b.F = y.F + (u.F * 1.772f);
#else
                r.F = y.F + (float)(v.F * 1.402f);
                g.F = y.F - (float)(u.F * 0.34413f) - (float)(v.F * 0.71414f);
                {
                    //c# This isn't more correct than the formula above, the goal
                    //   here is to have the exact same result as the c impl. 
                    //
                    //   This lets us do a byte for byte comparison check, with the
                    //   caviat that we're comparing against the result of a MSVC
                    //   build of the c libary.
                    //
                    //   Other builds may give different results.
                    g.F = y.F;
                    float tmp = u.F * 0.344130009f;
                    g.F -= tmp;
                    tmp = v.F * 0.714139998f;
                    g.F -= tmp;
                }
                b.F = y.F + (float)(u.F * 1.772f);
#endif
                c0[i] = r.I;
                c1[i] = g.I;
                c2[i] = b.I;

                //C# Code useful for math comparison
                //if (i == 12)
                //{
                //    IntOrFloat calc1, calc2, res;

                //    calc1.F = (float)(v.F * 0.714139998f);
                //    calc2.F = (float)(u.F * 0.344130009f);
                //    res.F = y.F;
                //    res.F -= calc2.F;
                //    res.F -= calc1.F;

                //    i = i;
                //}
            }
        }
    }


    internal enum MCT_ELEMENT_TYPE
    {
        NULL = -1,

        /// <summary>
        /// MCT data is stored as signed shorts
        /// </summary>
        MCT_TYPE_INT16 = 0,

        /// <summary>
        /// MCT data is stored as signed integers
        /// </summary>
        MCT_TYPE_INT32 = 1,

        /// <summary>
        /// MCT data is stored as floats
        /// </summary>
        MCT_TYPE_FLOAT = 2,

        /// <summary>
        /// MCT data is stored as doubles
        /// </summary>
        MCT_TYPE_DOUBLE = 3,
    }

    internal enum MCT_ARRAY_TYPE
    {
        MCT_TYPE_DEPENDENCY = 0,
        MCT_TYPE_DECORRELATION = 1,
        MCT_TYPE_OFFSET = 2,
    }

    /// <summary>
    /// Multi component transform data
    /// </summary>
    internal class MctData : ICloneable
    {
        public MCT_ELEMENT_TYPE element_type;
        public MCT_ARRAY_TYPE array_type;
        public int index;
        public ShortOrIntOrFloatOrDoubleAr data;
        public int data_size;

        public object Clone() { return MemberwiseClone(); }
        //Size of the data in bytes. 
        //public int data_size;
    }

    internal class SimpleMccDecorrelationData : ICloneable
    {
        public uint index;
        public uint n_comps;
        public ArPtr<MctData> decorrelation_array;
        public ArPtr<MctData> offset_array;
        public bool is_irreversible;

        public object Clone()
        {
            var clone = (SimpleMccDecorrelationData)MemberwiseClone();
            if (decorrelation_array != null)
            {
                var ap = new ArPtr<MctData>(new MctData[decorrelation_array.Data.Length], decorrelation_array.Pos);
                for(int c=0; c < ap.Data.Length; c++)
                    ap.Data[c] = (MctData) decorrelation_array.Data[c].Clone();
                clone.decorrelation_array = ap;
            }
            if (offset_array != null)
            {
                var ap = new ArPtr<MctData>(new MctData[offset_array.Data.Length], offset_array.Pos);
                for (int c = 0; c < ap.Data.Length; c++)
                    ap.Data[c] = (MctData) offset_array.Data[c].Clone();
                clone.offset_array = ap;
            }
            return clone;
        }
    }
}
