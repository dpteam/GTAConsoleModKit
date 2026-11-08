using System;
using System.IO;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// CISO (CSO/ZSO) compressed disc image: the wrapper PSP ISOs are distributed in.
    ///
    ///   0x00 char[4]  "CISO" (deflate) or "ZISO" (LZ4)
    ///   0x04 uint32   header size (24)
    ///   0x08 uint64   uncompressed size
    ///   0x10 uint32   block size (2048 on retail images)
    ///   0x14 uint8    version (0/1 deflate, 2 mixed)
    ///   0x15 uint8    index alignment shift
    ///   0x16 uint16   reserved
    ///   then (blocks + 1) uint32 index entries, each (offset >> align) | 0x80000000 for plain blocks.
    ///
    /// LZ4 blocks (ZISO, and CSO v2 sometimes) are rejected with a clear message:
    /// no third party code is allowed in the build, and PSP releases use deflate.
    /// </summary>
    public static class Cso
    {
        public const int HeaderSize = 24;
        private const uint PlainFlag = 0x80000000;
        private const uint PositionMask = 0x7fffffff;

        public sealed class Info
        {
            public string Magic;
            public int HeaderSize;
            public long TotalBytes;
            public int BlockSize;
            public int Version;
            public int Align;
            public long Blocks;
            public long CompressedSize;

            public string Codec
            {
                get
                {
                    if (Magic == "ZISO") return "LZ4";
                    return Version >= 2 ? "deflate/lz4" : "deflate";
                }
            }

            public double Ratio
            {
                get { return TotalBytes == 0 ? 1.0 : (double)CompressedSize / TotalBytes; }
            }

            public override string ToString()
            {
                return string.Format("{0} v{1} ({2}), block {3} B, {4} blocks, {5} -> {6} ({7:0.0}%)",
                    Magic, Version, Codec, BlockSize, Blocks,
                    Util.HumanBytes(CompressedSize), Util.HumanBytes(TotalBytes), Ratio * 100);
            }
        }

        public static bool IsCso(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var head = new byte[4];
                    if (stream.Read(head, 0, 4) != 4) return false;
                    return IsMagic(head);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsMagic(byte[] head)
        {
            string magic = System.Text.Encoding.ASCII.GetString(head);
            return magic == "CISO" || magic == "ZISO" || magic == "DAX\0";
        }

        public static Info ReadInfo(string path)
        {
            using (var stream = File.OpenRead(path))
                return ReadInfo(stream);
        }

        public static Info ReadInfo(Stream stream)
        {
            long origin = stream.Position;
            var header = Util.ReadExactly(stream, HeaderSize);
            string magic = System.Text.Encoding.ASCII.GetString(header, 0, 4);
            if (magic == "DAX\0")
                throw new InvalidDataException("DAX images are not supported, convert to ISO with DAXcr or UMDGen first");
            if (magic != "CISO" && magic != "ZISO")
                throw new InvalidDataException("not a CSO/ZSO image (magic '" + magic + "')");

            var info = new Info
            {
                Magic = magic,
                HeaderSize = (int)Util.U32(header, 4),
                TotalBytes = (long)Util.U32(header, 8) | ((long)Util.U32(header, 12) << 32),
                BlockSize = (int)Util.U32(header, 16),
                Version = header[20],
                Align = header[21]
            };
            if (info.HeaderSize < HeaderSize) info.HeaderSize = HeaderSize;
            if (info.BlockSize <= 0 || info.BlockSize > 64 * 1024 * 1024)
                throw new InvalidDataException("implausible block size " + info.BlockSize);
            if (info.Align > 31)
                throw new InvalidDataException("implausible index alignment " + info.Align);
            info.Blocks = (info.TotalBytes + info.BlockSize - 1) / info.BlockSize;
            stream.Position = origin;
            try
            {
                info.CompressedSize = stream.Length - origin;
            }
            catch (NotSupportedException)
            {
                info.CompressedSize = 0;
            }
            return info;
        }

        /// <summary>Expands the compressed image into a plain .iso file.</summary>
        public static Info ConvertToIso(string source, string destination, Action<int> progress)
        {
            Info info = ReadInfo(source);
            if (info.Magic == "ZISO" || info.Version >= 2)
                throw new InvalidDataException("image uses " + info.Codec +
                                               " blocks; only deflate CSO v0/v1 is supported");

            long blocks = info.Blocks;
            var index = new uint[blocks + 1];

            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024))
            {
                input.Position = info.HeaderSize;
                var rawIndex = Util.ReadExactly(input, (int)((blocks + 1) * 4));
                for (long i = 0; i <= blocks; i++) index[i] = Util.U32(rawIndex, (int)(i * 4));

                string partial = destination + ".part";
                string directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                try
                {
                    using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                               128 * 1024))
                    {
                        long written = 0;
                        var payload = new byte[info.BlockSize + 64];
                        for (long i = 0; i < blocks; i++)
                        {
                            long offset = (long)(index[i] & PositionMask) << info.Align;
                            long stored = ((long)(index[i + 1] & PositionMask) << info.Align) - offset;
                            if (stored <= 0 || stored > payload.Length)
                                throw new InvalidDataException("block " + i + " has an invalid stored size");

                            input.Position = offset;
                            int read = 0;
                            while (read < stored)
                            {
                                int n = input.Read(payload, read, (int)stored - read);
                                if (n <= 0) throw new InvalidDataException("file ends inside block " + i);
                                read += n;
                            }

                            int wanted = (int)Math.Min(info.BlockSize, info.TotalBytes - written);
                            byte[] block;
                            if ((index[i] & PlainFlag) != 0)
                            {
                                block = payload;
                            }
                            else
                            {
                                block = Zlib.Inflate(payload, 0, (int)stored);
                            }
                            if (block.Length < wanted)
                                throw new InvalidDataException("block " + i + " decoded to " + block.Length +
                                                               " bytes, expected " + wanted);
                            output.Write(block, 0, wanted);
                            written += wanted;

                            if (progress != null && (i % 64) == 0)
                                progress((int)(written * 100 / Math.Max(1, info.TotalBytes)));
                        }
                    }
                    if (File.Exists(destination))
                    {
                        try { File.SetAttributes(destination, FileAttributes.Normal); } catch (Exception) { }
                        File.Delete(destination);
                    }
                    File.Move(partial, destination);
                }
                catch (Exception)
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { }
                    throw;
                }
            }

            if (progress != null) progress(100);
            return info;
        }

        /// <summary>Compresses a plain .iso into a CSO v1 (deflate) image.</summary>
        public static Info CompressFromIso(string source, string destination, int blockSize, Action<int> progress)
        {
            if (blockSize <= 0) blockSize = 2048;
            var sourceInfo = new FileInfo(source);
            long total = sourceInfo.Length;
            long blocks = (total + blockSize - 1) / blockSize;

            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024))
            {
                string partial = destination + ".part";
                string directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                try
                {
                    using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                               128 * 1024))
                    {
                        var header = new byte[HeaderSize];
                        System.Text.Encoding.ASCII.GetBytes("CISO", 0, 4, header, 0);
                        Util.PutU32(header, 4, HeaderSize);
                        Util.PutU32(header, 8, (uint)(total & 0xffffffff));
                        Util.PutU32(header, 12, (uint)(total >> 32));
                        Util.PutU32(header, 16, (uint)blockSize);
                        header[20] = 1;
                        header[21] = 0;
                        output.Write(header, 0, header.Length);

                        var indexBytes = new byte[(blocks + 1) * 4];
                        output.Write(indexBytes, 0, indexBytes.Length);

                        long position = HeaderSize + indexBytes.Length;
                        var block = new byte[blockSize];
                        long written = 0;

                        for (long i = 0; i < blocks; i++)
                        {
                            int wanted = (int)Math.Min(blockSize, total - written);
                            int read = 0;
                            while (read < wanted)
                            {
                                int n = input.Read(block, read, wanted - read);
                                if (n <= 0) throw new IOException("source got shorter while compressing");
                                read += n;
                            }
                            if (wanted < blockSize) Array.Clear(block, wanted, blockSize - wanted);

                            byte[] packed = Zlib.Deflate(block, 0, blockSize);
                            uint entry;
                            if (packed.Length < blockSize)
                            {
                                output.Write(packed, 0, packed.Length);
                                entry = (uint)position;
                                position += packed.Length;
                            }
                            else
                            {
                                output.Write(block, 0, blockSize);
                                entry = (uint)position | PlainFlag;
                                position += blockSize;
                            }
                            Util.PutU32(indexBytes, (int)(i * 4), entry);
                            written += wanted;
                            if (progress != null && (i % 64) == 0)
                                progress((int)(written * 100 / Math.Max(1, total)));
                        }

                        Util.PutU32(indexBytes, (int)(blocks * 4), (uint)position);
                        output.Position = HeaderSize;
                        output.Write(indexBytes, 0, indexBytes.Length);
                    }

                    if (File.Exists(destination))
                    {
                        try { File.SetAttributes(destination, FileAttributes.Normal); } catch (Exception) { }
                        File.Delete(destination);
                    }
                    File.Move(partial, destination);
                }
                catch (Exception)
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { }
                    throw;
                }
            }

            if (progress != null) progress(100);
            return ReadInfo(destination);
        }
    }
}
