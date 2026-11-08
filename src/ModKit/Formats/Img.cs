using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// IMG archive support for the whole GTA 3D family:
    ///
    ///  * PC layout, "version 1": GTA3.IMG holds data, GTA3.DIR holds 32 byte
    ///    records { offset, size, name[24] } in *sectors* (2048 bytes);
    ///  * PC layout, "version 2": "VER2" + entry count, records inline;
    ///  * PSP Stories layout: the IMG has no directory of its own -- names and
    ///    sector ranges come from the streaming table of GAME.DAT (which g3DTZ
    ///    style extraction exports as a .DIR). That is exactly the same 32 byte
    ///    record, so one reader covers both.
    ///
    /// When no .DIR is available the archive is scanned chunk by chunk
    /// (mdl/tex/anim/WRLD headers), which recovers usable entries from a raw dump.
    /// </summary>
    public sealed class ImgArchive
    {
        public string Path { get; private set; }
        public string Format { get; private set; }
        public List<Entry> Entries { get; private set; }
        public long FileSize { get; private set; }

        public sealed class Entry
        {
            public string Name;
            public long OffsetSectors;
            public long SizeSectors;
            public string SourcePath;   // used when repacking

            public long OffsetBytes { get { return OffsetSectors * Iso9660.SectorSize; } }
            public long SizeBytes { get { return SizeSectors * Iso9660.SectorSize; } }

            public override string ToString()
            {
                return Name + " @" + OffsetSectors + " +" + SizeSectors;
            }
        }

        private ImgArchive()
        {
            Entries = new List<Entry>();
        }

        public static ImgArchive Open(string path, string directoryPath)
        {
            var archive = new ImgArchive { Path = path, FileSize = Util.FileSize(path) };

            string dirPath = directoryPath;
            if (string.IsNullOrEmpty(dirPath))
            {
                string candidate = System.IO.Path.ChangeExtension(path, ".dir");
                if (File.Exists(candidate)) dirPath = candidate;
                else
                {
                    candidate = System.IO.Path.ChangeExtension(path, ".DIR");
                    if (File.Exists(candidate)) dirPath = candidate;
                }
            }

            if (!string.IsNullOrEmpty(dirPath) && File.Exists(dirPath))
            {
                archive.ReadDir(dirPath);
                archive.Format = "dir+sectors (GTA3/VC/Stories)";
                return archive;
            }

            if (LooksLikeVer2(path))
            {
                archive.ReadVer2();
                archive.Format = "VER2 (San Andreas)";
                return archive;
            }

            archive.ScanChunks();
            archive.Format = archive.Entries.Count > 0 ? "chunk scan" : "raw (no index)";
            return archive;
        }

        private static bool LooksLikeVer2(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var head = new byte[4];
                    if (stream.Read(head, 0, 4) != 4) return false;
                    return Encoding.ASCII.GetString(head) == "VER2";
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ReadDir(string dirPath)
        {
            byte[] data = File.ReadAllBytes(dirPath);
            int count = data.Length / 32;
            for (int i = 0; i < count; i++)
            {
                int offset = i * 32;
                var entry = new Entry
                {
                    OffsetSectors = Util.U32(data, offset),
                    SizeSectors = Util.U32(data, offset + 4),
                    Name = Util.CString(data, offset + 8, 24)
                };
                if (entry.Name.Length == 0) entry.Name = "entry_" + i.ToString("0000");
                Entries.Add(entry);
            }
            Log.Debug(Cat.Fmt, "read " + Entries.Count + " entries from " + System.IO.Path.GetFileName(dirPath));
        }

        private void ReadVer2()
        {
            byte[] data = File.ReadAllBytes(Path);
            uint count = Util.U32(data, 4);
            for (int i = 0; i < count; i++)
            {
                int offset = 8 + i * 32;
                if (offset + 32 > data.Length) break;
                long sectors = Util.U16(data, offset + 4);
                if (sectors == 0) sectors = Util.U16(data, offset + 6);
                Entries.Add(new Entry
                {
                    OffsetSectors = Util.U32(data, offset),
                    SizeSectors = sectors,
                    Name = Util.CString(data, offset + 8, 24)
                });
            }
        }

        private void ScanChunks()
        {
            if (FileSize <= 0) return;
            byte[] data;
            using (var stream = File.OpenRead(Path))
            {
                int take = (int)Math.Min(FileSize, 128L * 1024 * 1024);
                data = Util.ReadExactly(stream, take);
            }

            // A file that is not an archive at all (text, a short stub) must come back
            // as "no entries" instead of throwing: callers then report a clean
            // "raw (no index)" instead of an error.
            try
            {
                foreach (Rsl.ChunkRef chunk in Rsl.ScanChunks(data, 0, data.Length, Iso9660.SectorSize, 200000))
                {
                    Entries.Add(new Entry
                    {
                        OffsetSectors = chunk.Offset / Iso9660.SectorSize,
                        SizeSectors = (chunk.Header.FileEnd + Iso9660.SectorSize - 1) / Iso9660.SectorSize,
                        Name = chunk.Header.Ident.ToLowerInvariant() + "_" +
                               (chunk.Offset / Iso9660.SectorSize).ToString("000000")
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug(Cat.Fmt, "chunk scan found nothing usable in " +
                                   System.IO.Path.GetFileName(Path) + ": " + Log.Describe(ex));
            }
            Log.Info(Cat.Fmt, "scanned " + Entries.Count + " chunks in " + System.IO.Path.GetFileName(Path));
        }

        /// <summary>
        /// Name lookup that tolerates the extension being present or not. Exact
        /// names win over bare names: "PLAYER.MDL" must not resolve to "PLAYER.CHK"
        /// just because that one comes first in the archive.
        /// </summary>
        public Entry Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string needle = name.Replace('\\', '/');
            int slash = needle.LastIndexOf('/');
            if (slash >= 0) needle = needle.Substring(slash + 1);

            foreach (Entry entry in Entries)
                if (string.Equals(entry.Name, needle, StringComparison.OrdinalIgnoreCase)) return entry;

            string bare = System.IO.Path.GetFileNameWithoutExtension(needle);
            foreach (Entry entry in Entries)
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(entry.Name), bare,
                        StringComparison.OrdinalIgnoreCase)) return entry;

            return null;
        }

        public byte[] ReadEntry(Entry entry)
        {
            using (var stream = File.OpenRead(Path))
            {
                stream.Position = entry.OffsetBytes;
                long available = Math.Max(0, Math.Min(entry.SizeBytes, stream.Length - stream.Position));
                if (available > int.MaxValue)
                    throw new InvalidDataException("entry too large to load into memory: " + entry.Name);
                return Util.ReadExactly(stream, (int)available);
            }
        }

        public int ExtractAll(string destination, Action<string, int> progress)
        {
            int written = 0;
            using (var stream = File.OpenRead(Path))
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    Entry entry = Entries[i];
                    string safeName = SanitizeFileName(entry.Name);
                    if (safeName.Length == 0) safeName = "entry_" + i;
                    string target = Util.SafeCombine(destination, safeName);
                    if (target == null) continue;

                    stream.Position = entry.OffsetBytes;
                    long available = Math.Max(0, Math.Min(entry.SizeBytes, stream.Length - stream.Position));
                    string directory = System.IO.Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                               128 * 1024))
                        Util.CopyStream(stream, output, available, null);

                    written++;
                    if (progress != null)
                        progress(entry.Name, Entries.Count == 0 ? 100 : written * 100 / Entries.Count);
                }
            }
            return written;
        }

        /// <summary>
        /// Packs entries (in the given order) into a fresh IMG plus an optional .DIR.
        /// Sector aligned, never leaves gaps: the game reads across neighbouring
        /// entries, so gaps are a real crash risk.
        /// </summary>
        public static void Build(string imgPath, string dirPath, IList<Entry> entries, Action<int> progress)
        {
            long position = 0;
            string partial = imgPath + ".part";
            string directory = System.IO.Path.GetDirectoryName(imgPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var dirData = new byte[entries.Count * 32];

            try
            {
                using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                           128 * 1024))
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        Entry entry = entries[i];
                        if (string.IsNullOrEmpty(entry.SourcePath) || !File.Exists(entry.SourcePath))
                            throw new FileNotFoundException("missing source for " + entry.Name, entry.SourcePath);

                        var info = new FileInfo(entry.SourcePath);
                        long sectors = (info.Length + Iso9660.SectorSize - 1) / Iso9660.SectorSize;
                        if (sectors == 0) sectors = 1;

                        Util.PutU32(dirData, i * 32, (uint)position);
                        Util.PutU32(dirData, i * 32 + 4, (uint)sectors);
                        byte[] nameBytes = Encoding.ASCII.GetBytes(entry.Name.ToUpperInvariant());
                        int nameLength = Math.Min(23, nameBytes.Length);
                        Buffer.BlockCopy(nameBytes, 0, dirData, i * 32 + 8, nameLength);

                        output.Position = position * Iso9660.SectorSize;
                        using (var input = File.OpenRead(entry.SourcePath))
                        {
                            long copied = 0;
                            var buffer = new byte[128 * 1024];
                            while (copied < info.Length)
                            {
                                int want = (int)Math.Min(buffer.Length, info.Length - copied);
                                int read = input.Read(buffer, 0, want);
                                if (read <= 0) break;
                                output.Write(buffer, 0, read);
                                copied += read;
                            }
                        }
                        long padded = sectors * Iso9660.SectorSize;
                        if (info.Length < padded)
                        {
                            output.Position = padded;
                            if (output.Length < padded) output.SetLength(padded);
                        }

                        position += sectors;
                        if (progress != null) progress((i + 1) * 100 / entries.Count);
                    }
                    output.SetLength(position * Iso9660.SectorSize);
                }

                if (File.Exists(imgPath))
                {
                    try { File.SetAttributes(imgPath, FileAttributes.Normal); } catch (Exception) { }
                    File.Delete(imgPath);
                }
                File.Move(partial, imgPath);

                if (!string.IsNullOrEmpty(dirPath))
                {
                    var trimmed = new List<byte>(dirData.Length);
                    foreach (byte b in dirData) trimmed.Add(b);
                    Util.WriteAllBytesAtomic(dirPath, dirData);
                }
            }
            catch (Exception)
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>
        /// Puts a file back into an existing entry. The entry keeps its sector
        /// range, so the replacement must fit (the remainder is zero padded and the
        /// streaming table in GAME.DAT keeps pointing at the right place).
        /// Growing an entry would shift every following sector and invalidate the
        /// game's streaming table, so it is refused with an explanation instead of
        /// silently corrupting the archive.
        /// </summary>
        public bool Replace(Entry entry, string sourcePath, out string message)
        {
            if (entry == null)
            {
                message = "no such entry";
                return false;
            }
            if (!File.Exists(sourcePath))
            {
                message = "no such file: " + sourcePath;
                return false;
            }

            long capacity = entry.SizeBytes;
            long length = new FileInfo(sourcePath).Length;
            if (length > capacity)
            {
                message = string.Format(
                    "file is {0} but the entry only holds {1} ({2} sectors). " +
                    "Growing an entry shifts every following sector, which would break the " +
                    "streaming table in GAME.DAT - rebuild the IMG instead (img-pack).",
                    Util.HumanBytes(length), Util.HumanBytes(capacity), entry.SizeSectors);
                return false;
            }

            var buffer = new byte[128 * 1024];
            using (var target = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                target.Position = entry.OffsetBytes;
                using (var input = File.OpenRead(sourcePath))
                {
                    long copied = 0;
                    while (copied < length)
                    {
                        int want = (int)Math.Min(buffer.Length, length - copied);
                        int read = input.Read(buffer, 0, want);
                        if (read <= 0) break;
                        target.Write(buffer, 0, read);
                        copied += read;
                    }
                }

                long remaining = capacity - length;
                if (remaining > 0)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        target.Write(buffer, 0, want);
                        remaining -= want;
                    }
                }
            }

            message = string.Format("replaced {0}: {1} of {2} used", entry.Name,
                Util.HumanBytes(length), Util.HumanBytes(capacity));
            Log.Info(Cat.Fmt, message);
            return true;
        }

        private static class Sector
        {
            public const int Size = Iso9660.SectorSize;
        }

        /// <summary>Keeps the extension and the dots, drops anything a file system hates.</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "entry";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c < 32 || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' ||
                    c == '"' || c == '<' || c == '>' || c == '|') sb.Append('_');
                else sb.Append(c);
            }
            string result = sb.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? "entry" : result;
        }
    }
}

