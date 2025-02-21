using System;

namespace OpenJpeg.Util
{
    public static class Scaler
    {
        /// <summary>
        /// Does an ugly rezise using the Bresenham scaling algo
        /// </summary>
        /// <param name="pixels">An array of single component pixels</param>
        /// <param name="org_width">Original width</param>
        /// <param name="org_height">Original height</param>
        /// <param name="width">New width</param>
        /// <param name="height">New height</param>
        public static int[] Rezise(int[] pixels, int org_width, int org_height, int width, int height)
        {
            if (org_height == height && org_width == width)
                return pixels;

            //Array for finished pixels.
            int[] dest = new int[width * height];

            //We calculate the size ratio differences between width and height. They are split up
            //into a "integer_part" that contains the result of the division and a "fractional_part"
            //that holds the modulus of the division.
            //
            // i. e. 50 / 100 gives 0, and 100 as the fraction. That way we know how much to advance.
            int integer_part_h = (org_height / height) * org_width, integer_part_w = org_width / width;
            int fractional_part_h = org_height % height, fractional_part_w = org_width % width;

            //Fractions are added together into this value.
            int fract = 0;

            //Remebers the start position of the last raster line. This is used to see if one should
            //copy the rasterline wholesale into the dest image. Say, if the image is twice as high
            //every other line will be copied.
            int last_rasterline_pos = -1;

            //Current position in the pixels array
            int pixels_pos = 0;

            //Current possition in the destination array
            int dest_pos = 0;

            //We itterate line by line
            for (int lines = height; lines-- > 0; )
            {
                if (pixels_pos == last_rasterline_pos)
                {
                    //This is a slight optimization. Since the pixels_pos hasn't
                    //moved it means that the line scaling algo will give the exact
                    //same result. We therefore simply copy the old result.
                    Array.Copy(dest, dest_pos - width, dest, dest_pos, width); //<-- todo: use BufferCopy instead
                    dest_pos += width;
                }
                else
                {
                    //lfract is the sum of the fractions for this line.
                    //We copy the pixel_pos since we don't want to move it
                    int lfract = 0, source_pos = pixels_pos; ;

                    //Itterates pixel by pixel
                    for (int npixels = width; npixels-- > 0; )
                    {
                        //Copies one pixel from
                        dest[dest_pos++] = pixels[source_pos];

                        //We move forwards with the integer pos. This
                        //is only relevant when shrinking an image
                        source_pos += integer_part_w;

                        //We add together the fractions
                        lfract += fractional_part_w;

                        //When the fraction adds up to one (width since
                        //we use the moduls instead of float math)
                        //we move one pixel.
                        if (lfract >= width)
                        {
                            lfract -= width;
                            source_pos++;
                        }
                    }

                    //We remeber the last rasterline pos to see if it moved
                    last_rasterline_pos = pixels_pos;
                }

                //When shrinking images one has to skip pixel/lines in the source
                //by this amount
                pixels_pos += integer_part_h;

                //Adds the fractions until they add up to one. When that happens
                //we advance one line.
                fract += fractional_part_h;
                if (fract >= height)
                {
                    fract -= height;
                    pixels_pos += org_width;
                }
            }

            return dest;
        }
    }
}
