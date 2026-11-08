using System;
using System.IO;
using System.IO.Compression;

namespace ModKit.Formats
{
    /// <summary>
    /// zlib on top of .NET 4.0 DeflateStream.
    ///
    /// DeflateStream in .NET 4.0 speaks *raw* deflate (no zlib wrapper), so we add
    /// and strip the 2 byte header / 4 byte adler32 ourselves. That keeps the
    /// build free of third party code while still reading real zlib streams
    /// (GAME.DTZ, *.LVZ, CSO blocks are all deflate based).
    /// </summary>
    public static class Zlib
    {
        public static byte[] Inflate(byte[] data, int offset, int count)
        {
            if (count < 2) throw new InvalidDataException("deflate stream too short");
            // skip zlib header (CMF/FLG); tolerate raw deflate without it
            int start = offset;
            int length = count;
            byte cmf = data[offset];
            byte flg = data[offset + 1];
            bool isZlib = (cmf & 0x0f) == 8 && ((cmf << 8) + flg) % 31 == 0;
            if (isZlib)
            {
                start = offset + 2;
                length = count - 2;
            }

            using (var input = new MemoryStream(data, start, length, false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(Math.Max(1024, count * 4)))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    output.Write(buffer, 0, read);
                return output.ToArray();
            }
        }

        public static byte[] Inflate(byte[] data)
        {
            return Inflate(data, 0, data.Length);
        }

        public static byte[] InflateFile(string path)
        {
            return Inflate(File.ReadAllBytes(path));
        }

        /// <summary>Produces a zlib stream (0x78 0x9C header + adler32 checksum).</summary>
        public static byte[] Deflate(byte[] data)
        {
            return Deflate(data, 0, data.Length);
        }

        public static byte[] Deflate(byte[] data, int offset, int count)
        {
            byte[] raw;
            using (var output = new MemoryStream(count / 2 + 64))
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                    deflate.Write(data, offset, count);
                raw = output.ToArray();
            }

            var result = new byte[raw.Length + 6];
            result[0] = 0x78;
            result[1] = 0x9C;
            Buffer.BlockCopy(raw, 0, result, 2, raw.Length);
            uint adler = Adler32(data, offset, count);
            result[result.Length - 4] = (byte)(adler >> 24);
            result[result.Length - 3] = (byte)(adler >> 16);
            result[result.Length - 2] = (byte)(adler >> 8);
            result[result.Length - 1] = (byte)adler;
            return result;
        }

        public static uint Adler32(byte[] data, int offset, int count)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            for (int i = 0; i < count; i++)
            {
                a = (a + data[offset + i]) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }

        /// <summary>True if the buffer starts with a zlib header (78 01/9C/DA/5E).</summary>
        public static bool LooksLikeZlib(byte[] data, int offset)
        {
            if (data.Length - offset < 2) return false;
            byte cmf = data[offset];
            byte flg = data[offset + 1];
            if ((cmf & 0x0f) != 8) return false;
            return ((cmf << 8) + flg) % 31 == 0;
        }
    }
}
