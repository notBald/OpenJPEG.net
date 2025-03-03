# OpenJpeg.Net

This is a port of OpenJpeg to C#. This is a libary for encoding and decoding Jpeg 2000 images.

## Usage

I recommend using PdfLib instead of using this library directly, as it's much easier to work with, but
here is an incomplete example of how to decode Jpeg 2000:

```code
public override byte[] Decode(byte[] data, out int width, out int height, out int bpc)
{
    //Detects the format
    CodecFormat format = data[0] == 0 ? CodecFormat.Jpeg2P : CodecFormat.Jpeg2K;

    //OpenJpeg.Net uses the OpenJpeg 1.4 API
    var cinfo = new CompressionInfo(true, format);

    //Sets up decoding parameters. Can for instance be used to
    //speed up decoding of thumbnails by decoding less resolutions
    var parameters = new DecompressionParameters();

    //Destination for the decoded image
    JPXImage img = null;

    using (var ms = new MemoryStream(data, false))
    {
        //cio is a wrapper that is used by the libary when
        //reading. A bit like "BinaryReader"
        var cio = cinfo.OpenCIO(ms, true);
        cinfo.SetupDecoder(cio, parameters);

        //Decodes the image
        if (!cinfo.ReadHeader(out img) || !cinfo.Decode(img) || !cinfo.EndDecompress())
            throw new PdfFilterException(ErrCode.General);

        //If there's an error, you won't get an image. To get the error message,
        //set up cinfo with message callback functions
        if (img == null)
            throw new PdfFilterException(ErrCode.General);
    }

    //Makes the bits per channel uniform so that it's easier
    //to work with.
    img.MakeUniformBPC();

    //Jpeg 2000m images can have a color palette, this removes
    //that.
    img.ApplyIndex();

    //Handle some color spaces. Note, img.CMYKtoRGB() implements the
    //reference conversion algorithm. You should propbably use something
    //better. See CMYKFilter in PdfLib for ways to do this.
    switch(img.ColorSpace)
    {
        case COLOR_SPACE.YCCK:
            if (!img.ESyccToRGB())
                throw new Exception("Failed to RGB convert image");
            break;
        case COLOR_SPACE.CMYK:
            if (!img.CMYKtoRGB())
                throw new Exception("Failed to RGB convert image");
            break;
    }
    //Note, we don't here handle grayscale or CMY format. 
    
    //Assuming the image is in RGB format, this is now enough to
    //work with the image
    width = img.Width;
    height = img.Height;
    bpc = img.MaxBPC;

    //Assembles the image into a stream of bytes
    return img.ToArray();
}
```

Encoding:
```code
var cp = new CompressionParameters();

//Comments are optional
cp.Comment = "Created by OpenJPEG version " + CompressionInfo.Version;

//Sets lossy compression. Lossless compression is worse than PNG
cp.Irreversible = true;

//Jpeg2000 is built around layers. You should have a few, two are probably
//         less than there should be
cp.NumberOfLayers = 2;

//There are different strategies for compression.
cp.DistroAlloc = true;

//These numbers are the compression rates for each layer. Should be in decreasing order and must match number of layers.
cp.Rates = new float[] { 60, 30 };

//This libary uses the OpenJpeg 1.4 API
var cinfo = new CompressionInfo(false, CodecFormat.Jpeg2P);
if (!cinfo.SetupEncoder(cp, image))
    throw new Exception("Failed to setup encoder");

//We'll compress to a memory stream for this example.
var dest = new MemoryStream();

//Cio is from the OpenJpeg 1.4 API. It's the wrapper used when reading from streams.
var cio = cinfo.OpenCIO(dest, false);

//Goes through the motions.
if (!cinfo.StartCompress(cio) || !cinfo.Encode() || !cinfo.EndCompress())
    throw new Exception("Failed to encode image");

//The encoded image is now in "dest"
```

## About the port

There is no usage of unsafe code.