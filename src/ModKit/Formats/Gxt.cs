using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// GXT text tables (LCS/VCS use a different container than GTA3/VC).
    ///
    /// Stories layout:
    ///   "TABLD" + 3 bytes + entries of { char key[8]; uint32 offset } until the key
    ///   is no longer printable; each block then holds the classic
    ///   "TKEY" { uint32 size; { uint32 offset; uint32 hash } ... } "TDAT" { size }.
    ///
    /// Strings are stored in the 8 bit GTA charset; for the Latin releases that is
    /// plain ASCII plus a handful of control codes (see DecodeString).
    /// </summary>
    public static class Gxt
    {
        public sealed class Entry
        {
            public uint Hash;
            public string Key;
            public string Text;
            public string Block;
        }

        public sealed class Table
        {
            public List<Entry> Entries = new List<Entry>();
            public List<string> Blocks = new List<string>();
            public string Source;

            public string ToText()
            {
                var sb = new StringBuilder();
                sb.AppendLine("; " + Source);
                sb.AppendLine("; " + Entries.Count + " strings");
                foreach (Entry entry in Entries)
                {
                    sb.Append('[').Append(entry.Block).Append(':')
                      .Append(entry.Key).Append(']');
                    sb.Append(' ').AppendLine(entry.Text);
                }
                return sb.ToString();
            }
        }

        public static bool LooksLikeGxt(byte[] data)
        {
            if (data.Length < 12) return false;
            string magic = Encoding.ASCII.GetString(data, 0, 5);
            if (magic == "TABLD") return true;
            return Encoding.ASCII.GetString(data, 0, 4) == "TKEY";
        }

        public static Table Read(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var table = new Table { Source = System.IO.Path.GetFileName(path) };

            if (data.Length >= 5 && Encoding.ASCII.GetString(data, 0, 5) == "TABLD")
            {
                int position = 8;
                while (position + 12 <= data.Length)
                {
                    string key = Util.CString(data, position, 8);
                    if (!IsPrintable(key)) break;
                    int offset = (int)Util.U32(data, position + 8);
                    table.Blocks.Add(key);
                    if (offset > 0 && offset < data.Length)
                        ReadBlock(data, offset, key, table);
                    position += 12;
                }
            }
            else
            {
                ReadBlock(data, 0, "MAIN", table);
            }

            return table;
        }

        private static bool IsPrintable(string key)
        {
            if (key.Length == 0) return false;
            foreach (char c in key)
                if (c < 32 || c > 126) return false;
            return true;
        }

        private static void ReadBlock(byte[] data, int offset, string blockName, Table table)
        {
            if (offset + 8 > data.Length) return;
            if (Encoding.ASCII.GetString(data, offset, 4) != "TKEY") return;

            int tableSize = (int)Util.U32(data, offset + 4);
            int entries = tableSize / 8;
            var offsets = new int[entries];
            var hashes = new uint[entries];
            for (int i = 0; i < entries; i++)
            {
                int at = offset + 8 + i * 8;
                if (at + 8 > data.Length) break;
                offsets[i] = (int)Util.U32(data, at);
                hashes[i] = Util.U32(data, at + 4);
            }

            int dataStart = offset + 8 + tableSize;
            if (dataStart + 8 > data.Length) return;
            if (Encoding.ASCII.GetString(data, dataStart, 4) != "TDAT") return;
            int dataSize = (int)Util.U32(data, dataStart + 4);
            int textStart = dataStart + 8;

            for (int i = 0; i < entries; i++)
            {
                if (offsets[i] == 0) continue;
                int at = textStart + offsets[i];
                if (at < 0 || at >= data.Length) continue;
                if (dataSize > 0 && offsets[i] >= dataSize) continue;
                string text = DecodeString(data, at);
                if (text.Length == 0) continue;
                table.Entries.Add(new Entry
                {
                    Hash = hashes[i],
                    Key = "0x" + hashes[i].ToString("x8", CultureInfo.InvariantCulture),
                    Text = text,
                    Block = blockName
                });
            }
        }

        /// <summary>
        /// GTA 8 bit text: 0x00 ends the string, ~ is 0x7e, and the Latin releases
        /// keep the rest of printable ASCII as is. Bytes >= 0x80 are mapped through
        /// the shared GTA charset approximation (accented letters in RUS builds are
        /// usually stored as single byte CP1251, so we try that first).
        /// </summary>
        public static string DecodeString(byte[] data, int offset)
        {
            var sb = new StringBuilder();
            for (int i = offset; i < data.Length && i < offset + 4096; i++)
            {
                byte b = data[i];
                if (b == 0) break;
                switch (b)
                {
                    case 0x01: sb.Append(' '); break;
                    case 0x02: sb.Append('~'); break;
                    case 0x0a: sb.Append("\\n"); break;
                    case 0x0d: sb.Append("\\r"); break;
                    default:
                        if (b < 0x20) sb.Append('.');
                        else sb.Append((char)b);
                        break;
                }
            }
            string raw = sb.ToString();
            // Non ASCII text (RUS releases) is CP1251 in practice.
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] >= 0x80)
                {
                    byte[] bytes = Encoding.GetEncoding(1251).GetBytes(raw);
                    return Encoding.GetEncoding(1251).GetString(bytes);
                }
            }
            return raw;
        }
    }
}
