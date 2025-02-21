#region License
/*
 * Copyright (c) 2002-2007, Communications and Remote Sensing Laboratory, Universite catholique de Louvain (UCL), Belgium
 * Copyright (c) 2002-2007, Professor Benoit Macq
 * Copyright (c) 2001-2003, David Janssens
 * Copyright (c) 2002-2003, Yannick Verschueren
 * Copyright (c) 2003-2007, Francois-Olivier Devaux and Antonin Descampe
 * Copyright (c) 2005, Herve Drolon, FreeImage Team
 * Copyright (c) 2006-2007, Parvatha Elangovan
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
using System.IO;

namespace OpenJpeg
{
    /// <summary>
    /// This class contains all information needed
    /// for compression or decompression
    /// </summary>
    public sealed class CompressionInfo
    {
        /// <summary>
        /// Version number of this libary
        /// </summary>
        public static string Version { get { return "2.5.2"; } }

        /// <summary>
        /// Whenever this object is a decompressor or
        /// a compressor
        /// </summary>
        readonly bool _is_decompressor;

        /// <summary>
        /// Type of compression or decompression
        /// </summary>
        CodecFormat _codec_format;

        /// <summary>
        /// Handles j2k stuff
        /// </summary>
        J2K _j2k;

        /// <summary>
        /// Handles JP2 stuff
        /// </summary>
        JP2 _jp2;

        /// <summary>
        /// For sending messages back to the client
        /// </summary>
        EventMgr _mgr;

        /// <summary>
        /// Get or set event manager
        /// </summary>
        public EventMgr EventManager { get { return _mgr; } set { _mgr = value; } }

        /// <summary>
        /// Optional data object for events
        /// </summary>
        public object ClientData { get; set; }

        /// <summary>
        /// Whenever this object is set up for decompression
        /// </summary>
        public bool IsDecompressor { get { return _is_decompressor; } }

        /// <summary>
        /// Allow multithreading
        /// </summary>
        internal bool DisableMultiThreading { get; set; }

        /// <summary>
        /// Functions for compressing an image
        /// </summary>
        /// <remarks>Openjpeg 2.1 API</remarks>
        private Compression CFuncs = new Compression();

        /// <summary>
        /// Functions for decompresssing an image
        /// </summary>
        /// <remarks>Openjpeg 2.1 API</remarks>
        private Decompression DFuncs = new Decompression();

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="is_decompressor">
        /// Set true if you intent to decompress
        /// </param>
        /// <remarks>
        /// opj_create_compress
        /// </remarks>
        public CompressionInfo(bool is_decompressor, CodecFormat format)
        { 
            _is_decompressor = is_decompressor;
            _codec_format = format;
            _j2k = J2K.Create(this);
            if (format == CodecFormat.Jpeg2P)
            {
                _jp2 = new JP2(this, _j2k);
                if (_is_decompressor)
                {
                    DFuncs.SetupDecoder = new Decompression.SetupDecoderFunc(_jp2.SetupDecode);
                    DFuncs.ReadHeader = new Decompression.ReadHeaderFunc(_jp2.ReadHeader);
                    DFuncs.SetDecodeArea = new Decompression.SetDecodeAreaFunc(_jp2.SetDecodeArea);
                    DFuncs.Decode = new Decompression.DecodeFunc(_jp2.Decode);
                    DFuncs.TileDecode = new Decompression.TileDecodeFunc(_jp2.Decode);
                    DFuncs.EndDecompress = new Decompression.EndDecompressFunc(_jp2.EndDecompress);
                }
                else
                {                    
                    CFuncs.SetupEncoder = new Compression.SetupEncoderFunc(_jp2.SetupEncoder);
                    CFuncs.StartCompress = new Compression.StartCompressFunc(_jp2.StartCompress);
                    CFuncs.EndCompress = new Compression.EndCompressFunc(_jp2.EndCompress);
                    CFuncs.Encode = new Compression.EncodeFunc(_jp2.Encode);
                }
            }
            else if (format == CodecFormat.Jpeg2K)
            {
                //_j2k = J2K.Create(this);
                if (_is_decompressor)
                {
                    DFuncs.SetupDecoder = new Decompression.SetupDecoderFunc(_j2k.SetupDecode);
                    DFuncs.ReadHeader = new Decompression.ReadHeaderFunc(_j2k.ReadHeader);
                    DFuncs.SetDecodeArea = new Decompression.SetDecodeAreaFunc(_j2k.SetDecodeArea);
                    DFuncs.Decode = new Decompression.DecodeFunc(_j2k.Decode);
                    DFuncs.TileDecode = new Decompression.TileDecodeFunc(_j2k.Decode);
                    DFuncs.EndDecompress = new Decompression.EndDecompressFunc(_j2k.EndDecompress);
                }
                else
                {
                    CFuncs.SetupEncoder = new Compression.SetupEncoderFunc(_j2k.SetupEncoder);
                    CFuncs.StartCompress = new Compression.StartCompressFunc(_j2k.StartCompress);
                    CFuncs.EndCompress = new Compression.EndCompressFunc(_j2k.EndCompress);
                    CFuncs.Encode = new Compression.EncodeFunc(_j2k.Encode);
                }
            }
            else
                throw new NotSupportedException(format.ToString());
        }

        /// <summary>
        /// Configures the decoder
        /// </summary>
        /// <param name="cio">File data</param>
        /// <param name="parameters">Configuration</param>
        /// <returns>True if the setup was a success</returns>
        /// <remarks>
        /// 2.5
        /// </remarks>
        public bool SetupDecoder(CIO cio, DecompressionParameters parameters)
        {
            if (cio != null && cio.CanSeek && parameters != null && _is_decompressor)
            {
                DisableMultiThreading = parameters.DisableMultiThreading;
                DFuncs.SetupDecoder(cio, parameters);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the header of the file
        /// </summary>
        /// <param name="image">Image with header information</param>
        /// <returns>True on success</returns>
        /// <remarks>
        /// 2.5
        /// </remarks>
        public bool ReadHeader(out JPXImage image)
        {
            if (_is_decompressor)
            {
                return DFuncs.ReadHeader(out image);
            }
            else
            {
                image = null;
                return false;
            }
        }

        //2.5
        public bool SetDecodeArea(JPXImage image, int start_x, int start_y, int end_x, int end_y)
        {
            if (_is_decompressor)
                return DFuncs.SetDecodeArea(image, start_x, start_y, end_x, end_y);                
            return false;
        }

        /// <summary>
        /// Sets up the encoder. Required before encoding.
        /// </summary>
        /// <param name="parameters"></param>
        /// <param name="image"></param>
        /// <returns>True if setup was a sucess</returns>
        public bool SetupEncoder(CompressionParameters parameters, JPXImage image)
        {
            if (parameters != null && image != null && !_is_decompressor && parameters.Valid)
            {
                DisableMultiThreading = parameters.DisableMultiThreading;
                return CFuncs.SetupEncoder(parameters, image);
            }

            return false;
        }

        public bool SetExtraOptions(ExtraOption extra)
        {
            if (extra != null && _j2k != null)
                return _j2k.SetExtraOptions(extra);

            return false;
        }

        //2.5 - opj_start_compress
        public bool StartCompress(CIO cio)
        {
            if (cio != null)
            {
                if (!_is_decompressor)
                     return CFuncs.StartCompress(cio);
            }
            return false;
        }

        public bool Encode()
        {
            if (!_is_decompressor)
                return CFuncs.Encode();
            return false;
        }

        /// <summary>
        /// End to compress the current image
        /// </summary>
        /// <remarks>2.5 - opj_end_compress</remarks>
        public bool EndCompress()
        {
            if (!_is_decompressor)
                return CFuncs.EndCompress();
            return false;
        }

        /// <summary>
        /// Opens a CIO stream
        /// </summary>
        /// <param name="file">File to read or write</param>
        /// <param name="read">Whenever to read or write</param>
        /// <remarks>
        /// Openjpeg 2.1 now supports streams. Yay. But since my impl.
        /// already supports streams, I'll keep this old 1.4 method.
        /// 
        /// I.e. this is now eq. with "opj_stream_create_default_file_stream"
        /// </remarks>
        public CIO OpenCIO(Stream file, bool read)
        {
            if (file == null) throw new ArgumentNullException(); ;

            if (read)
                return new CIO(this, file, OpenMode.Read);
            else
                return new CIO(this, file, _j2k.ImageLength);
        }

        //2.5
        public bool Decode(JPXImage image)
        {
            if (image == null) throw new ArgumentNullException();

            if (_is_decompressor)
                return DFuncs.Decode(image);

            return false;
        }

        public bool Decode(JPXImage image, uint tile_no)
        {
            if (image == null) throw new ArgumentNullException();

            if (_is_decompressor)
                return DFuncs.TileDecode(image, tile_no);

            return false;
        }

        //2.5 - opj_end_decompress
        public bool EndDecompress()
        {
            if (_is_decompressor)
            {
                return DFuncs.EndDecompress();
            }
            return false;
        }

        /// <summary>
        /// Emits a formated error string
        /// </summary>
        /// <param name="msg">Message</param>
        /// <param name="arg0">Parameters to put into the message</param>
        internal void Error(string msg, params object[] arg0)
        {
            if (_mgr == null || _mgr._error == null) return;

            _mgr._error(string.Format(msg, arg0), ClientData);
        }

        /// <summary>
        /// Emits a formated error string
        /// </summary>
        /// <param name="msg">Message</param>
        /// <param name="arg0">Parameters to put into the message</param>
        internal void ErrorMT(string msg, params object[] arg0)
        {
            if (_mgr == null || _mgr._error == null) return;

            //Locking on _mgr is not ideal, as this object is publically visible
            lock (_mgr) _mgr._error(string.Format(msg, arg0), ClientData);
        }

        /// <summary>
        /// Emits a formated info string
        /// </summary>
        /// <param name="msg">Message</param>
        /// <param name="arg0">Parameters to put into the message</param>
        internal void Info(string msg, params object[] arg0)
        {
            if (_mgr == null || _mgr._info == null) return;

            _mgr._info(string.Format(msg, arg0), ClientData);
        }

        /// <summary>
        /// Emits a formated warning string
        /// </summary>
        /// <param name="msg">Message</param>
        /// <param name="arg0">Parameters to put into the message</param>
        internal void Warn(string msg, params object[] arg0)
        {
            if (_mgr == null || _mgr._warning == null) return;

            _mgr._warning(string.Format(msg, arg0), ClientData);
        }

        /// <summary>
        /// Emits a formated warning string
        /// </summary>
        /// <param name="msg">Message</param>
        /// <param name="arg0">Parameters to put into the message</param>
        internal void WarnMT(string msg, params object[] arg0)
        {
            if (_mgr == null || _mgr._warning == null) return;

            //Locking on _mgr is not ideal, as this object is publically visible
            lock(_mgr) _mgr._warning(string.Format(msg, arg0), ClientData);
        }
    }

    /// <summary>
    /// OpenJpeg 2.1 collects the relevant compression functions into
    /// a struct.
    /// </summary>
    internal struct Compression
    {
        internal delegate bool StartCompressFunc(CIO cio);
        internal delegate bool EncodeFunc();
        internal delegate bool WriteTileFunc(uint tile_index, byte[] data, int data_size, CIO cio);
        internal delegate bool EndCompressFunc();
        internal delegate void DestroyFunc();
        internal delegate bool SetupEncoderFunc(CompressionParameters cparameters, JPXImage image);

        internal StartCompressFunc StartCompress;
        internal EncodeFunc Encode;
        internal WriteTileFunc WriteTile;
        internal EndCompressFunc EndCompress;
        internal DestroyFunc Destroy;
        internal SetupEncoderFunc SetupEncoder;
    }
    internal struct Decompression
    {
        internal delegate void SetupDecoderFunc(CIO cio, DecompressionParameters param);
        internal delegate bool ReadHeaderFunc(out JPXImage image);
        internal delegate bool SetDecodeAreaFunc(JPXImage image, int start_x, int start_y, int end_x, int end_y);
        internal delegate bool DecodeFunc(JPXImage image);
        internal delegate bool TileDecodeFunc(JPXImage image, uint tile_index);
        internal delegate bool EndDecompressFunc();

        internal SetupDecoderFunc SetupDecoder;
        internal ReadHeaderFunc ReadHeader;
        internal SetDecodeAreaFunc SetDecodeArea;
        internal DecodeFunc Decode;
        internal TileDecodeFunc TileDecode;
        internal EndDecompressFunc EndDecompress;
    }
}
