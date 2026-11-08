using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// GXT text tables.
    ///
    /// Two layouts exist and the kit reads both:
    ///
    /// **Stories (LCS/VCS)** - measured on the retail English file:
    /// <code>
    /// 0x00  "TABLD" + 3 bytes
    /// 0x08  { char block[8]; u32 offset } * N        (block table)
    ///       at every offset: [char name[8]] "TKEY" u32 size
    ///                        { u32 stringOffset; char key[8] } * (size/12)
    ///                        "TDAT" u32 size  UTF-16LE strings, NUL terminated
    /// </code>
    /// Keys are plain 8-character names (not hashes) and are stored sorted, so the
    /// game can binary search. The first block (MAIN) has no name prefix; the rest do.
    /// Blocks start on 4-byte boundaries.
    ///
    /// **Classic (GTA III / Vice City, PC)** - "TKEY" + u32 size + { u32 offset; u32 hash }
    /// and 8-bit strings. Read only; the importer targets the Stories layout.
    /// </summary>
    public static class Gxt
    {
        public sealed class Entry
        {
            public string Key;
            public string Text;
            public string Block;

            /// <summary>Offset inside the original TDAT blob (0 when unknown).</summary>
            public int OriginalOffset;
            /// <summary>Text as it was read, used to detect "not edited".</summary>
            public string OriginalText;
            /// <summary>True when the edited text is identical to the original one.</summary>
            public bool Unchanged;
        }

        public sealed class Block
        {
            public string Name;
            public List<Entry> Entries = new List<Entry>();
            /// <summary>Raw TDAT payload of the original file (for byte exact rebuilds).</summary>
            public byte[] OriginalText;
        }

        public sealed class Table
        {
            public List<Block> Blocks = new List<Block>();
            public string Source;
            public bool Stories = true;
            public byte[] Header = new byte[8];

            public int Count
            {
                get
                {
                    int total = 0;
                    foreach (Block block in Blocks) total += block.Entries.Count;
                    return total;
                }
            }

            public IEnumerable<Entry> AllEntries()
            {
                foreach (Block block in Blocks)
                    foreach (Entry entry in block.Entries)
                        yield return entry;
            }
        }

        public static bool LooksLikeGxt(byte[] data)
        {
            if (data.Length < 12) return false;
            if (Encoding.ASCII.GetString(data, 0, 5) == "TABLD") return true;
            return Encoding.ASCII.GetString(data, 0, 4) == "TKEY";
        }

        // ------------------------------------------------------------------ read

        public static Table Read(string path)
        {
            return Read(File.ReadAllBytes(path), Path.GetFileName(path));
        }

        public static Table Read(byte[] data, string sourceName)
        {
            var table = new Table { Source = sourceName ?? "" };

            bool stories = data.Length > 8 && Encoding.ASCII.GetString(data, 0, 5) == "TABLD";
            table.Stories = stories;

            if (stories)
            {
                Buffer.BlockCopy(data, 0, table.Header, 0, 8);
                int position = 8;
                while (position + 12 <= data.Length)
                {
                    string name;
                    if (!TryReadBlockName(data, position, out name)) break;
                    int offset = (int)Util.U32(data, position + 8);
                    position += 12;
                    if (offset <= 0 || offset + 8 >= data.Length) continue;
                    Block block = ReadStoriesBlock(data, offset, name);
                    if (block != null) table.Blocks.Add(block);
                }
            }
            else
            {
                Block block = ReadClassicBlock(data, 0, "MAIN");
                if (block != null) table.Blocks.Add(block);
            }

            return table;
        }

        /// <summary>
        /// Block names are 8 raw bytes. The check has to look at the bytes, not at
        /// the decoded string: a byte like 0xD0 becomes '?' in ASCII and would make
        /// junk look like a valid name (that is how a second, bogus "MAIN" table
        /// entry used to appear).
        /// </summary>
        private static bool TryReadBlockName(byte[] data, int position, out string name)
        {
            name = null;
            var sb = new StringBuilder(8);
            for (int i = 0; i < 8; i++)
            {
                byte b = data[position + i];
                if (b == 0) break;
                bool ok = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                          (b >= '0' && b <= '9') || b == '_' || b == '-';
                if (!ok) return false;
                sb.Append((char)b);
            }
            if (sb.Length == 0) return false;
            name = sb.ToString();
            return true;
        }

        private static Block ReadStoriesBlock(byte[] data, int offset, string name)
        {
            int tkey = offset;
            if (Encoding.ASCII.GetString(data, tkey, Math.Min(4, data.Length - tkey)) != "TKEY")
                tkey = offset + 8; // 8 byte name prefix
            if (tkey + 8 > data.Length) return null;
            if (Encoding.ASCII.GetString(data, tkey, 4) != "TKEY") return null;

            int size = (int)Util.U32(data, tkey + 4);
            if (size <= 0 || size % 12 != 0) return null;
            int count = size / 12;
            int tdat = tkey + 8 + size;
            if (tdat + 8 > data.Length) return null;
            if (Encoding.ASCII.GetString(data, tdat, 4) != "TDAT") return null;
            int tdatSize = (int)Util.U32(data, tdat + 4);
            int textBase = tdat + 8;
            if (textBase + tdatSize > data.Length) tdatSize = data.Length - textBase;

            var block = new Block { Name = name };
            block.OriginalText = new byte[tdatSize];
            if (tdatSize > 0) Buffer.BlockCopy(data, textBase, block.OriginalText, 0, tdatSize);

            for (int i = 0; i < count; i++)
            {
                int at = tkey + 8 + i * 12;
                if (at + 12 > data.Length) break;
                int textOffset = (int)Util.U32(data, at);
                string key = Util.CString(data, at + 4, 8);
                if (key.Length == 0) continue;
                if (textOffset < 0 || textOffset >= tdatSize) continue;

                string text = DecodeUtf16(data, textBase + textOffset, textBase + tdatSize);
                block.Entries.Add(new Entry
                {
                    Key = key,
                    Text = text,
                    OriginalText = text,
                    OriginalOffset = textOffset,
                    Unchanged = true,
                    Block = name
                });
            }
            return block;
        }

        private static Block ReadClassicBlock(byte[] data, int offset, string name)
        {
            if (offset + 8 > data.Length) return null;
            if (Encoding.ASCII.GetString(data, offset, 4) != "TKEY") return null;
            int size = (int)Util.U32(data, offset + 4);
            if (size <= 0 || size % 8 != 0) return null;
            int count = size / 8;
            int tdat = offset + 8 + size;
            if (tdat + 8 > data.Length) return null;
            if (Encoding.ASCII.GetString(data, tdat, 4) != "TDAT") return null;
            int tdatSize = (int)Util.U32(data, tdat + 4);
            int textBase = tdat + 8;
            if (textBase + tdatSize > data.Length) tdatSize = data.Length - textBase;

            var block = new Block { Name = name };
            for (int i = 0; i < count; i++)
            {
                int at = offset + 8 + i * 8;
                if (at + 8 > data.Length) break;
                int textOffset = (int)Util.U32(data, at);
                uint hash = Util.U32(data, at + 4);
                if (textOffset < 0 || textOffset >= tdatSize) continue;
                block.Entries.Add(new Entry
                {
                    Key = "0x" + hash.ToString("x8", CultureInfo.InvariantCulture),
                    Text = DecodeAnsi(data, textBase + textOffset, textBase + tdatSize),
                    Block = name
                });
            }
            return block;
        }

        /// <summary>UTF-16LE string up to the terminator (or the end of the block).</summary>
        private static string DecodeUtf16(byte[] data, int offset, int limit)
        {
            int end = offset;
            while (end + 1 < limit && !(data[end] == 0 && data[end + 1] == 0)) end += 2;
            if (end <= offset) return "";
            return Encoding.Unicode.GetString(data, offset, end - offset);
        }

        /// <summary>Classic 8 bit GTA charset (ASCII + the few control codes).</summary>
        private static string DecodeAnsi(byte[] data, int offset, int limit)
        {
            var sb = new StringBuilder();
            for (int i = offset; i < limit; i++)
            {
                byte b = data[i];
                if (b == 0) break;
                switch (b)
                {
                    case 0x01: sb.Append(' '); break;
                    case 0x02: sb.Append('~'); break;
                    default:
                        if (b < 0x20) sb.Append('.');
                        else sb.Append((char)b);
                        break;
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ text

        /// <summary>
        /// Dump format. One line per string, tab separated, blocks introduced by
        /// [NAME] - it can be edited in any text editor and fed back to
        /// <see cref="FromText"/>.
        /// </summary>
        public static string ToText(Table table, bool withHeader)
        {
            var sb = new StringBuilder();
            if (withHeader)
            {
                sb.AppendLine("; DPT.PSP GTA Modding Kit - GXT text export");
                sb.AppendLine("; edit the right side, keep the keys, then import the file back");
                sb.AppendLine("; tabs separate key and text, \\n is a line break inside the game");
                sb.AppendLine();
            }
            foreach (Block block in table.Blocks)
            {
                sb.Append('[').Append(block.Name).AppendLine("]");
                foreach (Entry entry in block.Entries)
                    sb.Append(entry.Key).Append('\t').AppendLine(Escape(entry.Text));
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static Table FromText(string text, Table template)
        {
            var table = new Table
            {
                Source = template == null ? "import" : template.Source,
                Stories = template == null || template.Stories
            };
            if (template != null) Buffer.BlockCopy(template.Header, 0, table.Header, 0, 8);

            Block current = null;
            foreach (string rawLine in Util.SplitLines(text))
            {
                string line = rawLine;
                if (line.Length == 0) continue;
                if (line[0] == ';') continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    if (close > 1)
                    {
                        string blockName = line.Substring(1, close - 1).Trim();
                        current = new Block { Name = blockName };
                        Block source = FindBlock(template, blockName);
                        // keep the original TDAT blob so an untouched block can be
                        // written back byte for byte
                        if (source != null) current.OriginalText = source.OriginalText;
                        table.Blocks.Add(current);
                    }
                    continue;
                }
                if (current == null) continue;

                int tab = line.IndexOf('\t');
                string key, value;
                if (tab > 0)
                {
                    key = line.Substring(0, tab).Trim();
                    value = line.Substring(tab + 1);
                }
                else
                {
                    int space = line.IndexOf(' ');
                    if (space <= 0) continue;
                    key = line.Substring(0, space).Trim();
                    value = line.Substring(space + 1);
                }
                if (key.Length == 0) continue;

                string parsed = Unescape(value.TrimEnd('\r'));
                var entry = new Entry { Key = key, Text = parsed, Block = current.Name };
                Entry original = FindInTemplate(template, current.Name, key);
                if (original != null)
                {
                    entry.OriginalOffset = original.OriginalOffset;
                    entry.OriginalText = original.OriginalText;
                    entry.Unchanged = string.Equals(original.OriginalText, parsed, StringComparison.Ordinal);
                }
                current.Entries.Add(entry);
            }
            return table;
        }

        private static Block FindBlock(Table template, string blockName)
        {
            if (template == null) return null;
            foreach (Block block in template.Blocks)
                if (string.Equals(block.Name, blockName, StringComparison.OrdinalIgnoreCase)) return block;
            return null;
        }

        private static Entry FindInTemplate(Table template, string blockName, string key)
        {
            if (template == null) return null;
            foreach (Block block in template.Blocks)
            {
                if (!string.Equals(block.Name, blockName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Entry entry in block.Entries)
                    if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)) return entry;
                return null;
            }
            return null;
        }

        private static string Escape(string text)
        {
            if (text == null) return "";
            return text.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string Unescape(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    switch (next)
                    {
                        case 'n': sb.Append('\n'); i++; continue;
                        case 't': sb.Append('\t'); i++; continue;
                        case 'r': sb.Append('\r'); i++; continue;
                        case '\\': sb.Append('\\'); i++; continue;
                    }
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        // ----------------------------------------------------------------- build

        /// <summary>
        /// Rebuilds the file. Keys are sorted per block (the game binary searches
        /// them) and strings are laid out in key order, so the output is
        /// deterministic. Blocks start on 4-byte boundaries like the retail file.
        /// </summary>
        public static byte[] Build(Table table)
        {
            var header = new byte[8];
            Buffer.BlockCopy(table.Header, 0, header, 0, 8);
            if (Encoding.ASCII.GetString(header, 0, 5) != "TABLD")
            {
                header = new byte[8];
                Encoding.ASCII.GetBytes("TABLD", 0, 5, header, 0);
                header[5] = 4;
            }

            // pre-build every block body so the table can point at final offsets
            var bodies = new List<byte[]>();
            foreach (Block block in table.Blocks)
                bodies.Add(BuildBlock(block));

            int tableOffset = 8;
            int blockStart = tableOffset + table.Blocks.Count * 12;
            var offsets = new int[table.Blocks.Count];
            for (int i = 0; i < table.Blocks.Count; i++)
            {
                blockStart = (blockStart + 3) & ~3;   // 4 byte alignment, as retail
                offsets[i] = blockStart;
                blockStart += bodies[i].Length;
            }

            var output = new byte[blockStart];
            Buffer.BlockCopy(header, 0, output, 0, 8);

            int position = tableOffset;
            for (int i = 0; i < table.Blocks.Count; i++)
            {
                var name = new byte[8];
                byte[] raw = Encoding.ASCII.GetBytes(table.Blocks[i].Name.ToUpperInvariant());
                Buffer.BlockCopy(raw, 0, name, 0, Math.Min(8, raw.Length));
                Buffer.BlockCopy(name, 0, output, position, 8);
                Util.PutU32(output, position + 8, (uint)offsets[i]);
                position += 12;
            }

            for (int i = 0; i < bodies.Count; i++)
                Buffer.BlockCopy(bodies[i], 0, output, offsets[i], bodies[i].Length);

            return output;
        }

        private static byte[] BuildBlock(Block block)
        {
            var entries = new List<Entry>(block.Entries);
            // keys are binary searched by the game, so they must stay sorted
            entries.Sort(delegate(Entry a, Entry b)
            {
                return string.CompareOrdinal(a.Key.ToUpperInvariant(), b.Key.ToUpperInvariant());
            });

            byte[] original = block.OriginalText;
            bool identical = original != null && original.Length > 0 && entries.Count > 0;
            if (identical)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    // offset 0 is a legitimate position (first string in the blob)
                    if (!entries[i].Unchanged)
                    {
                        identical = false;
                        break;
                    }
                }
            }

            if (identical)
            {
                // nothing was edited: reproduce the original block byte for byte
                var originalOffsets = new int[entries.Count];
                for (int i = 0; i < entries.Count; i++) originalOffsets[i] = entries[i].OriginalOffset;
                return ComposeBlock(block, entries, original, originalOffsets);
            }

            var text = new MemoryStream();
            var textOffsets = new int[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                textOffsets[i] = (int)text.Position;
                byte[] bytes = Encoding.Unicode.GetBytes(entries[i].Text ?? "");
                text.Write(bytes, 0, bytes.Length);
                text.WriteByte(0);
                text.WriteByte(0);
            }
            return ComposeBlock(block, entries, text.ToArray(), textOffsets);
        }

        /// <summary>Lays out name? + TKEY + key table + TDAT + text blob.</summary>
        private static byte[] ComposeBlock(Block block, List<Entry> entries, byte[] text, int[] textOffsets)
        {
            int tdatSize = text.Length;
            int tkeySize = entries.Count * 12;
            bool withName = !string.Equals(block.Name, "MAIN", StringComparison.OrdinalIgnoreCase);
            int total = (withName ? 8 : 0) + 8 + tkeySize + 8 + tdatSize;
            var body = new byte[total];
            int position = 0;

            if (withName)
            {
                byte[] raw = Encoding.ASCII.GetBytes(block.Name.ToUpperInvariant());
                Buffer.BlockCopy(raw, 0, body, 0, Math.Min(8, raw.Length));
                position += 8;
            }

            Encoding.ASCII.GetBytes("TKEY", 0, 4, body, position);
            Util.PutU32(body, position + 4, (uint)tkeySize);
            position += 8;

            for (int i = 0; i < entries.Count; i++)
            {
                Util.PutU32(body, position, (uint)textOffsets[i]);
                byte[] key = Encoding.ASCII.GetBytes(entries[i].Key.ToUpperInvariant());
                Buffer.BlockCopy(key, 0, body, position + 4, Math.Min(8, key.Length));
                position += 12;
            }

            Encoding.ASCII.GetBytes("TDAT", 0, 4, body, position);
            Util.PutU32(body, position + 4, (uint)tdatSize);
            position += 8;

            Buffer.BlockCopy(text, 0, body, position, text.Length);
            return body;
        }
    }
}