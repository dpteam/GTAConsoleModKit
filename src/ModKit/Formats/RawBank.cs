using System;
using System.Collections.Generic;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// Rockstar SFX bank: the "*.RAW" files in LCS/VCS `AUDIO\SET*`.
    ///
    /// It is a straight concatenation of VAG-like records, each with a 48-byte
    /// header and PS-ADPCM payload:
    ///
    ///   0x00  "VAGp"  | 0x00 * 4 (a few records have a zeroed magic and are unused)
    ///   0x04  version     (u32 BE: 0x04 or 0x20)
    ///   0x08  reserved    (u32 BE, always 0)
    ///   0x0C  dataSize    (u32 BE, multiple of 16)
    ///   0x10  sampleRate  (u32 BE, e.g. 12000, 22050)
    ///   0x14  reserved * 3
    ///   0x20  name[16]    (ASCII, NUL padded)
    ///   0x30  PS-ADPCM frames, 16 bytes each = 28 samples
    ///
    /// The record chain is the only index there is: record N+1 starts exactly at
    /// `offset + 48 + dataSize`. Walking it must land on the last byte of the file -
    /// that is what <see cref="Read"/> reports as <see cref="Result.ChainsExactly"/>.
    /// </summary>
    public static class RawBank
    {
        public const int HeaderSize = 48;

        public sealed class Entry
        {
            public int Index;
            public int Offset;
            public int Size;
            public int SampleRate;
            public int Version;
            public string Name;
            public bool MagicOk;

            public int Samples { get { return Size / 16 * 28; } }
            public double Seconds { get { return SampleRate <= 0 ? 0 : Samples / (double)SampleRate; } }
        }

        public sealed class Result
        {
            public List<Entry> Entries = new List<Entry>();
            public bool ChainsExactly;
            public int Consumed;
            public string Problem;
        }

        /// <summary>Cheap test: a VAGp header whose first record fits inside the file.</summary>
        public static bool LooksLikeBank(byte[] data)
        {
            if (data == null || data.Length < HeaderSize + 16) return false;
            if (Encoding.ASCII.GetString(data, 0, 4) != "VAGp") return false;
            int size = (int)Util.U32BE(data, 12);
            return size > 0 && size % 16 == 0 && HeaderSize + size <= data.Length;
        }

        public static Result Read(byte[] data)
        {
            var result = new Result();
            if (data == null || data.Length < HeaderSize)
            {
                result.Problem = "file is shorter than one header";
                return result;
            }

            int offset = 0;
            int index = 0;
            while (offset + HeaderSize <= data.Length)
            {
                bool magic = Encoding.ASCII.GetString(data, offset, 4) == "VAGp";
                bool blank = data[offset] == 0 && data[offset + 1] == 0 &&
                             data[offset + 2] == 0 && data[offset + 3] == 0;
                int version = (int)Util.U32BE(data, offset + 4);
                int size = (int)Util.U32BE(data, offset + 12);
                int rate = (int)Util.U32BE(data, offset + 16);

                if (!magic && !blank)
                {
                    result.Problem = "record " + index + " at " + offset + " has no VAGp magic";
                    break;
                }
                if (size <= 0 || size % 16 != 0)
                {
                    result.Problem = "record " + index + " at " + offset + " has size " + size;
                    break;
                }
                if (offset + HeaderSize + size > data.Length)
                {
                    result.Problem = "record " + index + " at " + offset + " runs past the end";
                    break;
                }

                result.Entries.Add(new Entry
                {
                    Index = index,
                    Offset = offset,
                    Size = size,
                    SampleRate = rate > 0 && rate <= 192000 ? rate : 12000,
                    Version = version,
                    Name = Util.CString(data, offset + 0x20, 16),
                    MagicOk = magic
                });

                offset += HeaderSize + size;
                index++;
            }

            result.Consumed = offset;
            result.ChainsExactly = result.Problem == null && offset == data.Length;
            return result;
        }

        /// <summary>One entry as PCM WAV (16 bit mono).</summary>
        public static byte[] ToWav(byte[] data, Entry entry)
        {
            short[] samples = Vag.Decode(data, entry.Offset + HeaderSize, entry.Size);
            return Vag.ToWav(samples, entry.SampleRate);
        }

        /// <summary>One entry as a standalone VAG file (48-byte header + payload).</summary>
        public static byte[] ToVag(byte[] data, Entry entry)
        {
            var output = new byte[HeaderSize + entry.Size];
            Buffer.BlockCopy(data, entry.Offset, output, 0, HeaderSize);
            Buffer.BlockCopy(data, entry.Offset + HeaderSize, output, HeaderSize, entry.Size);
            return output;
        }
    }
}