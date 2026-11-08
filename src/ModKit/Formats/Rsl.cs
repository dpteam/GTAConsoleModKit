using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// Leeds Engine "relocatable chunk".
    ///
    /// Every Stories data blob (GAME.DAT, *.MDL, *.CHK, *.LVZ after inflate) starts
    /// with this 32 byte header. All pointers inside the blob are *file offsets*
    /// until <see cref="Relocate"/> adds the load address; because we work in a
    /// byte[] where the base is 0, offsets can be used as-is and relocation only
    /// matters when we hand the blob back to the game.
    /// </summary>
    public sealed class ChunkHeader
    {
        public const int Size = 32;

        public uint IdentRaw;      // 4CC as stored on disk
        public uint Shrink;
        public uint FileEnd;       // total size of the chunk
        public uint DataEnd;       // where the relocation table starts
        public uint RelocTab;      // offset of the relocation table
        public uint NumRelocs;
        public uint GlobalTab;
        public ushort NumClasses;
        public ushort NumFuncs;

        /// <summary>Normalised 4CC, e.g. "tex", "mdl", "WRLD", "GTAG".</summary>
        public string Ident { get; private set; }

        public static ChunkHeader Parse(byte[] data, int offset)
        {
            if (data.Length - offset < Size)
                throw new InvalidDataException("chunk header truncated");

            var header = new ChunkHeader
            {
                IdentRaw = Util.U32(data, offset),
                Shrink = Util.U32(data, offset + 4),
                FileEnd = Util.U32(data, offset + 8),
                DataEnd = Util.U32(data, offset + 12),
                RelocTab = Util.U32(data, offset + 16),
                NumRelocs = Util.U32(data, offset + 20),
                GlobalTab = Util.U32(data, offset + 24),
                NumClasses = Util.U16(data, offset + 28),
                NumFuncs = Util.U16(data, offset + 30)
            };
            header.Ident = Util.Ident(data, offset);
            return header;
        }

        public bool Is(string ident)
        {
            return string.Equals(Ident, ident, StringComparison.OrdinalIgnoreCase);
        }

        public bool IsSane(long fileLength)
        {
            if (FileEnd < Size) return false;
            if (fileLength > 0 && FileEnd > fileLength) return false;
            if (RelocTab > FileEnd) return false;
            if (NumRelocs > 0 && RelocTab + (long)NumRelocs * 4 > FileEnd) return false;
            if (DataEnd > FileEnd) return false;
            return true;
        }

        public override string ToString()
        {
            return string.Format("{0} size={1} dataEnd={2} relocs={3} classes={4} funcs={5}",
                Ident, FileEnd, DataEnd, NumRelocs, NumClasses, NumFuncs);
        }
    }

    public static class Rsl
    {
        // 4CC as they appear on disk (byte order preserved so a raw comparison works).
        public static readonly uint TagTex = 0x00746578;  // "xet\0"
        public static readonly uint TagMdl = 0x006c646d;  // "ldm\0"
        public static readonly uint TagWorld = 0x57524c44; // "DLRW"
        public static readonly uint TagGtag = 0x47544147;  // "GTAG"

        /// <summary>
        /// Applies the relocation table in place. Offsets become absolute
        /// (base + offset), which is what the game expects. Our analysis code
        /// never needs this; the repacker does before writing a chunk back.
        /// </summary>
        public static void Relocate(byte[] data, ChunkHeader header, int baseOffset)
        {
            long tableOffset = baseOffset + (long)header.RelocTab;
            for (uint i = 0; i < header.NumRelocs; i++)
            {
                int entry = (int)(tableOffset + i * 4);
                if (entry < 0 || entry + 4 > data.Length) break;
                uint target = Util.U32(data, entry);
                int at = (int)(baseOffset + target);
                if (at < 0 || at + 4 > data.Length) continue;
                Util.PutU32(data, at, Util.U32(data, at) + (uint)baseOffset);
            }
        }

        /// <summary>
        /// Reads an offset stored in a chunk. Values are already file offsets, so a
        /// simple bounds check is all we need to decide whether a pointer is usable.
        /// </summary>
        public static int Ptr(byte[] data, int fieldOffset)
        {
            if (fieldOffset < 0 || fieldOffset + 4 > data.Length) return 0;
            uint value = Util.U32(data, fieldOffset);
            if (value == 0 || value >= data.Length) return 0;
            return (int)value;
        }

        /// <summary>
        /// Walks a chunk table written as consecutive chunks (an IMG image, a
        /// concatenation of MDL/TEX blocks). Stops at the first implausible header.
        /// </summary>
        public static IEnumerable<ChunkRef> ScanChunks(byte[] data, int start, int limit, int alignment, int maxChunks)
        {
            int offset = start;
            int found = 0;
            while (offset + ChunkHeader.Size <= Math.Min(limit, data.Length) && found < maxChunks)
            {
                ChunkHeader header;
                try
                {
                    header = ChunkHeader.Parse(data, offset);
                }
                catch (Exception)
                {
                    break;
                }

                if (!header.IsSane(data.Length - offset))
                {
                    if (alignment <= 1) break;
                    offset += alignment;
                    continue;
                }

                string ident = header.Ident;
                bool known = string.Equals(ident, "mdl", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ident, "tex", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ident, "WRLD", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ident, "anim", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ident, "GTAG", StringComparison.OrdinalIgnoreCase);
                if (!known)
                {
                    if (alignment <= 1) break;
                    offset += alignment;
                    continue;
                }

                yield return new ChunkRef { Offset = offset, Header = header };
                found++;
                int next = offset + (int)header.FileEnd;
                if (next <= offset) break;
                int aligned = alignment <= 1 ? next : ((next + alignment - 1) / alignment) * alignment;
                offset = aligned;
            }
        }

        /// <summary>Converts a 4CC into a readable label ("xet" -> "tex").</summary>
        public static string ReadIdent(byte[] data, int offset)
        {
            return Util.Ident(data, offset);
        }

        public sealed class ChunkRef
        {
            public int Offset;
            public ChunkHeader Header;

            public override string ToString()
            {
                return Offset.ToString() + ": " + Header;
            }
        }

        /// <summary>RSL object header shared by texlists, clumps and elements.</summary>
        public struct ObjectHeader
        {
            public byte Type;
            public byte SubType;
            public byte Flags;
            public byte PrivateFlags;
            public int Parent;

            public static ObjectHeader Read(byte[] data, int offset)
            {
                return new ObjectHeader
                {
                    Type = data[offset],
                    SubType = data[offset + 1],
                    Flags = data[offset + 2],
                    PrivateFlags = data[offset + 3],
                    Parent = Util.I32(data, offset + 4)
                };
            }
        }

        /// <summary>PSP raster descriptor (RslRasterPSP), 16 bytes.</summary>
        public sealed class Raster
        {
            public int Offset;
            public uint Unknown;
            public int DataOffset;
            public ushort Flags1;
            public byte WidthLog;
            public byte HeightLog;
            public uint Flags2;

            public int Width { get { return 1 << WidthLog; } }
            public int Height { get { return 1 << HeightLog; } }

            public static Raster Read(byte[] data, int offset)
            {
                var raster = new Raster
                {
                    Offset = offset,
                    Unknown = Util.U32(data, offset),
                    DataOffset = (int)Util.U32(data, offset + 4),
                    Flags1 = Util.U16(data, offset + 8),
                    WidthLog = data[offset + 10],
                    HeightLog = data[offset + 11],
                    Flags2 = Util.U32(data, offset + 12)
                };
                return raster;
            }

            public bool IsSane(int limit)
            {
                if (WidthLog > 12 || HeightLog > 12) return false;
                if (DataOffset <= 0 || DataOffset >= limit) return false;
                return true;
            }

            /// <summary>Format hint derived from the descriptor flags (see docs/FORMATS.md).</summary>
            public TextureFormat GuessFormat()
            {
                // Observed on retail data (LCS PSP):
                //   flags1 0x0100 / 0x0200 -> 4bpp / 8bpp paletted (loading screens)
                //   flags2 byte0 (0x04 / 0x08) selects paletted vs direct colour
                byte low = (byte)(Flags2 & 0xFF);
                if (low == 0x08) return TextureFormat.Pal8;
                if (low == 0x04) return TextureFormat.Pal4;
                if (low == 0x10) return TextureFormat.Rgba8888;
                if (low == 0x20) return TextureFormat.Rgb565;
                if (low == 0x40) return TextureFormat.Rgba5551;
                if (low == 0x80) return TextureFormat.Rgba4444;
                return TextureFormat.Unknown;
            }
        }

        public enum TextureFormat
        {
            Unknown = 0,
            Pal4 = 1,
            Pal8 = 2,
            Rgba8888 = 3,
            Rgba4444 = 4,
            Rgba5551 = 5,
            Rgb565 = 6,
            Dxt1 = 7,
            Dxt5 = 8,
            Unknown16 = 9
        }

        public static string FormatName(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.Pal4: return "pal4";
                case TextureFormat.Pal8: return "pal8";
                case TextureFormat.Rgba8888: return "rgba8888";
                case TextureFormat.Rgba4444: return "rgba4444";
                case TextureFormat.Rgba5551: return "rgba5551";
                case TextureFormat.Rgb565: return "rgb565";
                case TextureFormat.Dxt1: return "dxt1";
                case TextureFormat.Dxt5: return "dxt5";
                case TextureFormat.Unknown16: return "16bpp";
                default: return "unknown";
            }
        }

        public static int BytesPerPixel(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.Pal4: return 0; // half a byte
                case TextureFormat.Pal8: return 1;
                case TextureFormat.Unknown16: return 2;
                case TextureFormat.Rgba4444:
                case TextureFormat.Rgba5551:
                case TextureFormat.Rgb565: return 2;
                case TextureFormat.Rgba8888: return 4;
                case TextureFormat.Dxt1: return 0;
                case TextureFormat.Dxt5: return 1;
                default: return 0;
            }
        }
    }
}
