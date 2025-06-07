//#define DEBUG_STREAM
using System;
using System.IO;

namespace OpenJpeg
{
    /// <summary>
    /// This class represents a stream, with convenient
    /// methods for reading and writing bytes.
    /// </summary>
    /// <remarks>
    /// IO structure:
    /// 
    /// When reading: All reads are done through the CIO
    /// class. When reading bits, there's a bit oriented
    /// BIO class that reads from CIO. BIO will buffer up
    /// to one byte for that purpose.
    /// 
    /// When writing: CIO lays at the bottom. Writes to
    /// CIO goes straight to the underlying stream with
    /// no buffering done.
    /// 
    /// There's a buffered CIO class that has a 64KB
    /// buffer. It is used by most markers, though I've
    /// not done any testing to see if there's any 
    /// performance gained by buffering these small writes.
    /// One advantage in any case is that BCIO will
    /// automatically track the size of the marker, and
    /// write that size when commiting.
    /// 
    /// Bits are written using the WBIO class. It buffers
    /// up to 256 bytes, and has to be manually flushed
    /// after use.
    /// </remarks>
    public class CIO // Character In/Out
    {
        #region Variables and properties

        /// <summary>
        /// Read buffer
        /// </summary>
        byte[] _bytes = new byte[4];

        /// <summary>
        /// Source or destination stream
        /// </summary>
        Stream _s;

#if DEBUG_STREAM
        MemoryStream _debug_buffer;
        public delegate Stream CreateDebugStreamFunc(MemoryStream ms);
        public CreateDebugStreamFunc DebugFunc;
#endif

        /// <summary>
        /// How this stream was opened
        /// </summary>
        readonly OpenMode _m;

        /**
         * Used for culling the size of the stream.
         * Only used when writing.
         */
        readonly long _size;

        /// <summary>
        /// Owner reference
        /// </summary>
        /// <remarks>Is only used for a sanity check in one location, so may as well drop this</remarks>
        internal readonly CompressionInfo _cinfo;

        /// <summary>
        /// Get/Set position in the stream
        /// </summary>
        internal long Pos 
        { 
            get { return _s.Position; }
            set { _s.Position = value; }
        }

        /// <summary>
        /// Bytes left in the stream
        /// </summary>
        internal long BytesLeft 
        { 
            get 
            {
                if (_m == OpenMode.Write)
                    return _size - Pos;
                return _s.Length - _s.Position; 
            } 
        }

        /// <summary>
        /// Only for use by BufferCIO
        /// </summary>
        internal Stream Stream => _s;

        /// <summary>
        /// Is the underlying stream seekable
        /// </summary>
        public bool CanSeek { get { return _s.CanSeek; } }

        #endregion

        #region Init

        internal CIO(CompressionInfo parent, Stream s, OpenMode m) 
        { 
            _s = s;
            _m = m;
            _cinfo = parent;
        }

        internal CIO(CompressionInfo parent, Stream s, int img_size)
        {
            _s = s;
            _m = OpenMode.Write;
            _cinfo = parent;
            //Size is the estimated size of the image.
            _size = (long)(img_size * 0.1625 + 2000);
        }

        #endregion

        internal void Skip(uint n)
        {
            _s.Seek(n, SeekOrigin.Current);
        }

        #region Write

        public void WriteUShort(ushort n)
        {
            _bytes[1] = (byte) n;
            _bytes[0] = (byte) (n >> 8);
            _s.Write(_bytes, 0, 2);
        }

        public void WriteUShort(long n)
        {
            _bytes[1] = (byte)n;
            _bytes[0] = (byte)(n >> 8);
            _s.Write(_bytes, 0, 2);
        }

        public void WriteUShort(uint n)
        {
            _bytes[1] = (byte)n;
            _bytes[0] = (byte)(n >> 8);
            _s.Write(_bytes, 0, 2);
        }

        public void WriteUShort(int n)
        {
            _bytes[1] = (byte)n;
            _bytes[0] = (byte)(n >> 8);
            _s.Write(_bytes, 0, 2);
        }

        internal void Write(J2K_Marker m)
        {
            _bytes[1] = (byte)m;
            _bytes[0] = (byte)((ushort)m >> 8);
            _s.Write(_bytes, 0, 2);
        }

        internal void Write(JP2_Marker m)
        {
            Write((int)m);
        }

        public void Write(int m)
        {
            _bytes[3] = (byte)m;
            _bytes[2] = (byte)(m >> 8);
            _bytes[1] = (byte)(m >> 16);
            _bytes[0] = (byte)(m >> 24);
            _s.Write(_bytes, 0, 4);
        }

        public void Write(uint m)
        {
            _bytes[3] = (byte)m;
            _bytes[2] = (byte)(m >> 8);
            _bytes[1] = (byte)(m >> 16);
            _bytes[0] = (byte)(m >> 24);
            _s.Write(_bytes, 0, 4);
        }

        public void WriteByte(int b)
        {
            _s.WriteByte(unchecked((byte)b));
        }

        public void WriteByte(uint b)
        {
            _s.WriteByte(unchecked((byte)b));
        }

        public void WriteByte(byte b)
        {
            _s.WriteByte(b);
        }

        public void Write(byte[] ba, int offset, int count)
        {
            _s.Write(ba, offset, count);
        }

        #endregion

        #region Read

        public int Read(byte[] buf, int offset, int count)
        {
            for (int nToRead = count; nToRead > 0; )
            {
                int read = _s.Read(buf, offset, nToRead);
                if (read == 0)
                    return count - nToRead;

                offset += read;
                nToRead -= read;
            }
            return count;
        }

        /// <summary>
        /// Reads n bytes
        /// </summary>
        /// <param name="n">Number of bytes</param>
        /// <returns>Value</returns>
        public int Read(int n)
        {
         	int i;
	        uint v;
	        v = 0;
	        for (i = n - 1; i >= 0; i--) {
		        v +=  (uint) ReadByte() << (i << 3);
	        }
	        return (int) v;
        }

        /// <summary>
        /// Reads n bytes
        /// </summary>
        /// <param name="n">Number of bytes</param>
        /// <returns>Value</returns>
        public uint Read(uint n)
        {
            int i;
            uint v;
            v = 0;
            for (i = (int)(n - 1); i >= 0; i--)
            {
                v += (uint)ReadByte() << (i << 3);
            }
            return v;
        }

        /// <summary>
        /// Reads a boolean byte from the stream.
        /// </summary>
        public bool ReadBool()
        {
            int ret = _s.ReadByte();
            if (ret == -1) throw new EndOfStreamException();
            return ret != 0;
        }

        public byte ReadByte()
        {
            int ret = _s.ReadByte();
            if (ret == -1) throw new EndOfStreamException();
            return (byte)ret;
        }

        public ushort ReadUShort()
        {
            if (Read(_bytes, 0, 2) != 2)
                throw new EndOfStreamException();

            return (ushort)(_bytes[0] << 8 | _bytes[1]);
        }

        public int ReadInt()
        {
            if (Read(_bytes, 0, 4) != 4)
                throw new EndOfStreamException();

            return
                (_bytes[0]) << 24 |
                (_bytes[1]) << 16 |
                (_bytes[2]) << 8 |
                 _bytes[3];
        }

        public uint ReadUInt()
        {
            if (Read(_bytes, 0, 4) != 4)
                throw new EndOfStreamException();

            return
                ((uint)_bytes[0]) << 24 |
                ((uint)_bytes[1]) << 16 |
                ((uint)_bytes[2]) << 8 |
                 (uint)_bytes[3];
        }

        internal static uint ReadUInt(byte[] data, int pos)
        {
            return
                ((uint)data[pos + 0]) << 24 |
                ((uint)data[pos + 1]) << 16 |
                ((uint)data[pos + 2]) << 8 |
                 (uint)data[pos + 3];
        }

        #endregion
    }

    /// <summary>
    /// Used for writing markers that need a "length" in the
    /// header.
    /// </summary>
    internal class BufferCIO
    {
        internal byte[] _buffer;
        int _pos;
        Stream _cio;

        /// <summary>
        /// The position in the buffer, not the underlying stream.
        /// </summary>
        internal long BufferPos
        { 
            get { return _pos; } 
            set { _pos = (int)value; }
        }

        internal long BufferBytesLeft => _buffer.Length - _pos;

        public long Pos 
        { 
            get { return _cio.Position + _pos; }
            set 
            {
                var min = _cio.Position;
                var max = min + _pos;

                if (value >= min && value <= max)
                    _pos = (int)(value - min);
                else
                {
                    _cio.Position = value;
                    _pos = 0;
                }
            }
        }

        /// <summary>
        /// Ammount of bytes what will be written on commit
        /// </summary>
        public int CommitSize
        {
            get { return _pos; }
        }

        internal BufferCIO(CIO cio)
        {
            _cio = cio.Stream;
        }

        public void WriteUShort(ushort n)
        {
            _buffer[_pos++] = (byte)(n >> 8);
            _buffer[_pos++] = (byte)n;
        }
        public static void WriteUShort(byte[] data, int pos, ushort n)
        {
            data[pos++] = (byte)(n >> 8);
            data[pos++] = (byte)n;
        }

        public void WriteUShort(long n)
        {
            _buffer[_pos++] = (byte)(n >> 8);
            _buffer[_pos++] = (byte)n;
        }

        public void WriteUShort(uint n)
        {
            _buffer[_pos++] = (byte)(n >> 8);
            _buffer[_pos++] = (byte)n;
        }

        public void WriteUShort(int n)
        {
            _buffer[_pos++] = (byte)(n >> 8);
            _buffer[_pos++] = (byte)n;
        }

        public void Write(J2K_Marker m)
        {
            _buffer[_pos++] = (byte)((ushort)m >> 8);
            _buffer[_pos++] = (byte)m;
        }

        public static void Write(byte[] data, int pos, J2K_Marker m)
        {
            data[pos++] = (byte)((ushort)m >> 8);
            data[pos++] = (byte)m;
        }

        public void Write(int m)
        {
            _buffer[_pos++] = (byte)(m >> 24);
            _buffer[_pos++] = (byte)(m >> 16);
            _buffer[_pos++] = (byte)(m >> 8);
            _buffer[_pos++] = (byte)m;
        }
        public void Write(uint n)
        { Write((int)n); }
        public void Write(JP2_Marker m)
        { Write((int)m); }

        public void Write(int v, int n)
        {
            for (int i = n - 1; i >= 0; i--)
                _buffer[_pos++] = (byte)(v >> (i << 3));
        }
        public void Write(uint v, int n)
        { Write((int)v, n); }
        public void Write(bool b)
        { Write(b ? 1 : 0, 1); }

        public void Write(byte[] ba, int offset, int count)
        {
            Buffer.BlockCopy(ba, offset, _buffer, _pos, count);
            _pos += count;
        }

        public void WriteByte(uint b)
        {
            _buffer[_pos++] = ((byte)b);
        }

        public void WriteByte(int b)
        {
            _buffer[_pos++] = ((byte)b);
        }

        public void WriteByte(byte b)
        {
            _buffer[_pos++] = b;
        }

        public void Skip(int n, bool blank)
        {
            if (blank)
                Array.Clear(_buffer, _pos, n);
            _pos += n;
        }
        public void Skip(int n) { _pos += n; }

        /// <summary>
        /// Writes out all data
        /// </summary>
        public void Commit()
        {
            _cio.Write(_buffer, 0, _pos);
            _pos = 0;
        }

        internal void Memcopymove(byte[] data, long pos, uint count)
        {
            //Uglyness, but this is only used for TLM markers.

            //Original code does a memmove, I don't know of any way of doing that
            //in C#. Buffer.BlockCopy might work for all I know, but I doubt it.

            //1. We copy all existing data to a tmp buffer.
            byte[] tmp = new byte[_pos - (int)count];
            Buffer.BlockCopy(_buffer, (int)pos, tmp, 0, tmp.Length);

            //Copies the new data into buffer
            Buffer.BlockCopy(data, 0, _buffer, (int)pos, (int)count);

            //Copies old data after new data
            Buffer.BlockCopy(tmp, 0, _buffer, (int)(pos + count), tmp.Length);

            _pos += (int)count;
        }

        public void SetBuffer(ref byte[] buffer, uint needed_size)
        {
            if (needed_size > buffer.Length)
                Array.Resize(ref buffer, (int)needed_size);
            _buffer = buffer;
        }

        public void SetBuffer(uint needed_size)
        {
            if (needed_size > _buffer.Length)
                Array.Resize(ref _buffer, (int)needed_size);
        }
    }
}
