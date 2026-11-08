using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// ISO 9660 reader/writer (the disc image PSP games ship in).
    ///
    /// Only what a modding kit needs: walk the directory tree, extract files,
    /// and rebuild an image from a folder. No Rock Ridge / Joliet / multi-extent;
    /// PSP discs are plain ISO 9660 level 2 with uppercase names and ";1".
    /// </summary>
    public static class Iso9660
    {
        public const int SectorSize = 2048;

        public sealed class Entry
        {
            public string Name;
            public string FullPath;       // path inside the image, always with '/'
            public bool IsDirectory;
            public long Extent;           // LBA
            public long Length;           // bytes
            public DateTime? Timestamp;
            public List<Entry> Children = new List<Entry>();

            public override string ToString()
            {
                return (IsDirectory ? "[d] " : "    ") + FullPath + (IsDirectory ? "" : " (" + Length + " B)");
            }
        }

        public sealed class Volume
        {
            public string SystemIdentifier;
            public string VolumeIdentifier;
            public string ApplicationIdentifier;
            public long TotalSectors;
            public Entry Root;
            public List<Entry> AllEntries = new List<Entry>();
        }

        // ------------------------------------------------------------- reading

        public static Volume Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
                return Read(stream);
        }

        public static Volume Read(Stream stream)
        {
            var pvd = new byte[SectorSize];
            stream.Position = 16L * SectorSize;
            if (stream.Read(pvd, 0, SectorSize) != SectorSize)
                throw new InvalidDataException("file is too small to be an ISO 9660 image");
            if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
                throw new InvalidDataException("no ISO 9660 primary volume descriptor at sector 16");

            var volume = new Volume
            {
                SystemIdentifier = Util.CString(pvd, 8, 32).Trim(),
                VolumeIdentifier = Util.CString(pvd, 40, 32).Trim(),
                ApplicationIdentifier = Util.CString(pvd, 574, 128).Trim(),
                TotalSectors = Util.U32(pvd, 80)
            };

            var rootRecord = new byte[34 + 255];
            int rootLength = pvd[156];
            if (rootLength <= 0 || rootLength > rootRecord.Length)
                throw new InvalidDataException("invalid root directory record");
            Buffer.BlockCopy(pvd, 156, rootRecord, 0, rootLength);

            volume.Root = ReadDirectoryRecord(rootRecord, 0, "", null);
            WalkDirectory(stream, volume, volume.Root, 0);
            return volume;
        }

        private static void WalkDirectory(Stream stream, Volume volume, Entry directory, int depth)
        {
            if (depth > 32) return; // cycle guard
            volume.AllEntries.Add(directory);

            var records = ReadDirectoryRecords(stream, directory.Extent, directory.Length);
            foreach (byte[] record in records)
            {
                int nameLength = record[32];
                if (nameLength == 1)
                {
                    byte first = record[33];
                    if (first == 0 || first == 1) continue; // "." and ".."
                }

                string name = DecodeName(record, 33, nameLength);
                string fullPath = string.IsNullOrEmpty(directory.FullPath) ? name : directory.FullPath + "/" + name;
                Entry entry = ReadDirectoryRecord(record, 0, fullPath, name);
                entry.FullPath = fullPath;
                directory.Children.Add(entry);
                if (entry.IsDirectory) WalkDirectory(stream, volume, entry, depth + 1);
                else volume.AllEntries.Add(entry);
            }
        }

        private static string DecodeName(byte[] record, int offset, int length)
        {
            string name = Encoding.ASCII.GetString(record, offset, length);
            int semicolon = name.LastIndexOf(';');
            if (semicolon >= 0) name = name.Substring(0, semicolon);
            if (name.EndsWith(".", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 1);
            return name;
        }

        private static Entry ReadDirectoryRecord(byte[] record, int offset, string fullPath, string name)
        {
            long extent = Util.U32(record, offset + 2);
            long length = Util.U32(record, offset + 10);
            byte flags = record[offset + 25];
            var entry = new Entry
            {
                Name = name ?? "",
                FullPath = fullPath ?? "",
                Extent = extent,
                Length = length,
                IsDirectory = (flags & 0x02) != 0
            };
            if (record[offset + 18] >= 100 && record[offset + 18] <= 200)
            {
                try
                {
                    entry.Timestamp = new DateTime(1900 + record[offset + 18], record[offset + 19],
                        record[offset + 20], record[offset + 21], record[offset + 22], record[offset + 23]);
                }
                catch (Exception)
                {
                }
            }
            return entry;
        }

        private static List<byte[]> ReadDirectoryRecords(Stream stream, long extent, long length)
        {
            var result = new List<byte[]>();
            long start = extent * SectorSize;
            long remaining = length;
            var buffer = new byte[SectorSize];

            while (remaining > 0)
            {
                int want = (int)Math.Min(SectorSize, remaining);
                stream.Position = start;
                int read = stream.Read(buffer, 0, want);
                if (read <= 0) break;

                int offset = 0;
                while (offset < want)
                {
                    int recordLength = buffer[offset];
                    if (recordLength == 0) break; // rest of the sector is padding
                    if (offset + recordLength > want) break;
                    var record = new byte[recordLength];
                    Buffer.BlockCopy(buffer, offset, record, 0, recordLength);
                    result.Add(record);
                    offset += recordLength;
                }

                start += SectorSize;
                remaining -= want;
            }
            return result;
        }

        /// <summary>Extracts every file of the image below <paramref name="destination"/>.</summary>
        public static int Extract(string isoPath, string destination, Action<string, int> progress)
        {
            Volume volume = Read(isoPath);
            int count = 0;
            int total = 0;
            foreach (Entry entry in volume.AllEntries)
                if (!entry.IsDirectory) total++;

            using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024))
            {
                var files = new List<Entry>();
                foreach (Entry entry in volume.AllEntries)
                    if (!entry.IsDirectory) files.Add(entry);

                foreach (Entry entry in files)
                {
                    string target = Util.SafeCombine(destination, entry.FullPath);
                    if (target == null)
                    {
                        Log.Warn(Cat.Fmt, "refusing to extract outside the target folder: " + entry.FullPath);
                        continue;
                    }
                    string directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    stream.Position = entry.Extent * SectorSize;
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                               128 * 1024))
                    {
                        Util.CopyStream(stream, output, entry.Length, null);
                    }
                    count++;
                    if (progress != null) progress(entry.FullPath, total == 0 ? 100 : count * 100 / total);
                }
            }
            return count;
        }

        // ------------------------------------------------------------- writing

        /// <summary>
        /// Builds an ISO 9660 image from a folder. Directory order is: root, then
        /// sub directories breadth first, then all file data sector aligned.
        /// </summary>
        public static void Build(string sourceDirectory, string isoPath, string volumeLabel, Action<int> progress)
        {
            if (!Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException(sourceDirectory);

            Node root = Scan(sourceDirectory, "");
            var directories = new List<Node>();
            CollectDirectories(root, directories);

            // Directory numbers: 1 based, breadth first (as required by the path tables).
            for (int i = 0; i < directories.Count; i++) directories[i].Number = i + 1;

            // Pass 1: compute directory record sizes to know how many sectors they need.
            long nextSector = 18;
            nextSector += PathTableSectors(directories);

            foreach (Node directory in directories)
            {
                long bytes = DirectoryBytes(directory, root);
                directory.Length = bytes;
                directory.Extent = nextSector;
                nextSector += (bytes + SectorSize - 1) / SectorSize;
                if (nextSector == directory.Extent) nextSector++;
            }

            // Pass 2: file data.
            var files = new List<Node>();
            CollectFiles(root, files);
            foreach (Node file in files)
            {
                file.Extent = nextSector;
                nextSector += (file.Length + SectorSize - 1) / SectorSize;
            }

            long totalSectors = nextSector;
            if (totalSectors < 20) totalSectors = 20;

            string partial = isoPath + ".part";
            string directoryPath = Path.GetDirectoryName(isoPath);
            if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
                Directory.CreateDirectory(directoryPath);

            try
            {
                using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                           128 * 1024))
                {
                    output.SetLength(totalSectors * SectorSize);
                    WritePvd(output, volumeLabel, totalSectors, root);
                    WritePathTables(output, directories);
                    foreach (Node directory in directories)
                        WriteDirectory(output, directory, root);

                    long done = 0;
                    var buffer = new byte[128 * 1024];
                    foreach (Node file in files)
                    {
                        output.Position = file.Extent * SectorSize;
                        using (var input = File.OpenRead(file.SourcePath))
                        {
                            long copied = 0;
                            while (copied < file.Length)
                            {
                                int want = (int)Math.Min(buffer.Length, file.Length - copied);
                                int read = input.Read(buffer, 0, want);
                                if (read <= 0) throw new IOException("file changed while packing: " + file.Name);
                                output.Write(buffer, 0, read);
                                copied += read;
                            }
                        }
                        done += file.Length;
                        if (progress != null)
                            progress((int)(done * 100 / Math.Max(1, TotalFileBytes(files))));
                    }
                }

                if (File.Exists(isoPath))
                {
                    try { File.SetAttributes(isoPath, FileAttributes.Normal); } catch (Exception) { }
                    File.Delete(isoPath);
                }
                File.Move(partial, isoPath);
            }
            catch (Exception)
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { }
                throw;
            }

            if (progress != null) progress(100);
        }

        private static long TotalFileBytes(List<Node> files)
        {
            long total = 0;
            foreach (Node file in files) total += file.Length;
            return total;
        }

        private sealed class Node
        {
            public string Name;
            public string SourcePath;
            public bool IsDirectory;
            public long Length;
            public long Extent;
            public int Number;
            public Node Parent;
            public List<Node> Children = new List<Node>();

            public string IsoName
            {
                get
                {
                    string upper = Name.ToUpperInvariant();
                    if (!IsDirectory) upper += ";1";
                    return upper;
                }
            }
        }

        private static Node Scan(string path, string relative)
        {
            var node = new Node
            {
                Name = string.IsNullOrEmpty(relative) ? "" : Path.GetFileName(path),
                SourcePath = path,
                IsDirectory = true
            };
            foreach (string child in Directory.GetFiles(path))
            {
                var info = new FileInfo(child);
                node.Children.Add(new Node
                {
                    Name = info.Name,
                    SourcePath = child,
                    IsDirectory = false,
                    Length = info.Length,
                    Parent = node
                });
            }
            foreach (string child in Directory.GetDirectories(path))
            {
                Node sub = Scan(child, child);
                sub.Parent = node;
                node.Children.Add(sub);
            }
            // ISO order: directories first, then files, alphabetical inside each group.
            node.Children.Sort(delegate(Node a, Node b)
            {
                if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return node;
        }

        private static void CollectDirectories(Node node, List<Node> target)
        {
            target.Add(node);
            foreach (Node child in node.Children)
                if (child.IsDirectory) CollectDirectories(child, target);
        }

        private static void CollectFiles(Node node, List<Node> target)
        {
            foreach (Node child in node.Children)
            {
                if (child.IsDirectory) CollectFiles(child, target);
                else target.Add(child);
            }
        }

        private static long PathTableSectors(List<Node> directories)
        {
            long bytes = 0;
            foreach (Node directory in directories) bytes += 8 + Math.Max(1, directory.Name.Length) + 1;
            return (bytes * 2 + SectorSize - 1) / SectorSize + 2;
        }

        private static int RecordLength(string name)
        {
            int length = 33 + name.Length;
            if ((length & 1) != 0) length++; // records must have even length
            return length;
        }

        private static long DirectoryBytes(Node directory, Node root)
        {
            long bytes = RecordLength("\0") + RecordLength("\u0001"); // "." and ".."
            foreach (Node child in directory.Children)
                bytes += RecordLength(child.IsoName);

            // keep it simple: one sector minimum, records are laid out without
            // crossing sector boundaries (see WriteDirectory)
            long sectors = 1;
            long used = 0;
            long consumed = 0;
            var lengths = new List<long>();
            lengths.Add(RecordLength("\0"));
            lengths.Add(RecordLength("\u0001"));
            foreach (Node child in directory.Children) lengths.Add(RecordLength(child.IsoName));
            foreach (long length in lengths)
            {
                if (used + length > SectorSize)
                {
                    sectors++;
                    used = 0;
                }
                used += length;
                consumed += length;
            }
            return sectors * SectorSize;
        }

        private static void WritePvd(Stream output, string volumeLabel, long totalSectors, Node root)
        {
            var sector = new byte[SectorSize];
            sector[0] = 1;
            Encoding.ASCII.GetBytes("CD001", 0, 5, sector, 1);
            sector[6] = 1;
            WritePadded(sector, 8, 32, "PLAYSTATION");
            WritePadded(sector, 40, 32, volumeLabel ?? "MODKIT");
            BothEndian32(sector, 80, (uint)totalSectors);
            BothEndian16(sector, 120, 1);
            BothEndian16(sector, 124, 1);
            BothEndian16(sector, 128, SectorSize);
            BothEndian32(sector, 132, 0); // path table size, patched later
            PutBothEndian32At(sector, 140, 0);
            PutBothEndian32At(sector, 148, 0);
            WriteDosDate(sector, 813);
            sector[881] = 1;
            WritePadded(sector, 883, 128, "");
            WritePadded(sector, 1011, 128, "");
            WritePadded(sector, 574, 128, "DPT.PSP GTA MODKIT");
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "00";
            Encoding.ASCII.GetBytes(stamp, 0, 16, sector, 813);

            // Root directory record inside the PVD (34 bytes + name).
            var record = new byte[34];
            int length = 34;
            record[0] = (byte)length;
            BothEndianRecord(record, 2, (uint)root.Extent);
            BothEndianRecord(record, 10, (uint)root.Length);
            WriteRecordDate(record, 18);
            record[25] = 0x02;
            BothEndianRecord16(record, 28, 1);
            record[32] = 1;
            record[33] = 0;
            Buffer.BlockCopy(record, 0, sector, 156, 34);

            output.Position = 16L * SectorSize;
            output.Write(sector, 0, SectorSize);

            // Volume descriptor set terminator.
            var terminator = new byte[SectorSize];
            terminator[0] = 255;
            Encoding.ASCII.GetBytes("CD001", 0, 5, terminator, 1);
            terminator[6] = 1;
            output.Write(terminator, 0, SectorSize);
        }

        private static void WritePathTables(Stream output, List<Node> directories)
        {
            // L path table starts at sector 18, M path table right after it.
            long lba = 18;
            long offset = lba * SectorSize;
            long lSize = 0, mSize = 0;
            foreach (Node directory in directories)
            {
                lSize += 8 + Math.Max(1, directory.Name.Length) + 1;
                mSize += 8 + Math.Max(1, directory.Name.Length) + 1;
            }
            lSize += lSize & 1;
            mSize += mSize & 1;

            output.Position = offset;
            var lTable = new byte[lSize];
            int lpos = 0;
            foreach (Node directory in directories)
            {
                string name = directory.Name.Length == 0 ? "\0" : directory.Name.ToUpperInvariant();
                lTable[lpos] = (byte)name.Length;
                lTable[lpos + 1] = 0;
                Util.PutU32(lTable, lpos + 2, (uint)directory.Extent);
                Util.PutU16(lTable, lpos + 6, (ushort)(directory.Parent == null ? 1 : directory.Parent.Number));
                Encoding.ASCII.GetBytes(name, 0, name.Length, lTable, lpos + 8);
                lpos += 8 + name.Length;
                if ((name.Length & 1) != 0) lpos++; // pad to even
            }
            output.Write(lTable, 0, lTable.Length);

            long mOffset = (18 + (lSize + SectorSize - 1) / SectorSize) * SectorSize;
            output.Position = mOffset;
            var mTable = new byte[mSize];
            int mpos = 0;
            foreach (Node directory in directories)
            {
                string name = directory.Name.Length == 0 ? "\0" : directory.Name.ToUpperInvariant();
                mTable[mpos] = (byte)name.Length;
                mTable[mpos + 1] = 0;
                PutU32BE(mTable, mpos + 2, (uint)directory.Extent);
                mTable[mpos + 6] = (byte)((directory.Parent == null ? 1 : directory.Parent.Number) >> 8);
                mTable[mpos + 7] = (byte)((directory.Parent == null ? 1 : directory.Parent.Number) & 0xff);
                Encoding.ASCII.GetBytes(name, 0, name.Length, mTable, mpos + 8);
                mpos += 8 + name.Length;
                if ((name.Length & 1) != 0) mpos++;
            }
            output.Write(mTable, 0, mTable.Length);

            // Patch the two path table pointers in the PVD.
            var patch = new byte[16];
            output.Position = 16L * SectorSize + 132;
            BothEndian32(patch, 0, (uint)lSize);
            output.Write(patch, 0, 8);
            output.Position = 16L * SectorSize + 140;
            PutU32LE(patch, 0, 18);
            PutU32BE(patch, 4, 18);
            output.Write(patch, 0, 8);
            output.Position = 16L * SectorSize + 148;
            long mSector = 18 + (lSize + SectorSize - 1) / SectorSize;
            PutU32LE(patch, 0, (uint)mSector);
            PutU32BE(patch, 4, (uint)mSector);
            output.Write(patch, 0, 8);
        }

        private static void WriteDirectory(Stream output, Node directory, Node root)
        {
            output.Position = directory.Extent * SectorSize;
            var sector = new byte[SectorSize];
            int position = 0;

            WriteRecord(sector, ref position, output, "\0", directory.Extent,
                directory.Length, true, directory.Number);
            Node parent = directory.Parent ?? directory;
            WriteRecord(sector, ref position, output, "\u0001", parent.Extent,
                parent.Length, true, parent.Number);

            foreach (Node child in directory.Children)
            {
                WriteRecord(sector, ref position, output, child.IsoName, child.Extent,
                    child.Length, child.IsDirectory, 1);
            }

            // flush the remaining partial sector (records never cross a sector boundary)
            output.Write(sector, 0, SectorSize);
        }

        private static void WriteRecord(byte[] sector, ref int position, Stream output, string name,
            long extent, long length, bool isDirectory, int volumeSequence)
        {
            int recordLength = RecordLength(name);
            if (position + recordLength > SectorSize)
            {
                // flush current sector, continue in the next one
                output.Write(sector, 0, SectorSize);
                Array.Clear(sector, 0, SectorSize);
                position = 0;
            }

            int start = position;
            sector[start] = (byte)recordLength;
            sector[start + 1] = 0;
            BothEndianRecord(sector, start + 2, (uint)extent);
            BothEndianRecord(sector, start + 10, (uint)length);
            WriteRecordDate(sector, start + 18);
            sector[start + 25] = (byte)(isDirectory ? 0x02 : 0x00);
            sector[start + 26] = 0;
            sector[start + 27] = 0;
            BothEndianRecord16(sector, start + 28, (ushort)volumeSequence);
            sector[start + 32] = (byte)name.Length;
            Encoding.ASCII.GetBytes(name, 0, name.Length, sector, start + 33);
            if ((name.Length & 1) == 0) sector[start + 33 + name.Length] = 0;
            position = start + recordLength;
        }

        // ------------------------------------------------------- both-endian

        private static void BothEndian32(byte[] data, int offset, uint value)
        {
            PutU32LE(data, offset, value);
            PutU32BE(data, offset + 4, value);
        }

        private static void BothEndian16(byte[] data, int offset, ushort value)
        {
            Util.PutU16(data, offset, value);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)(value & 0xff);
        }

        private static void BothEndianRecord(byte[] data, int offset, uint value)
        {
            BothEndian32(data, offset, value);
        }

        private static void BothEndianRecord16(byte[] data, int offset, ushort value)
        {
            BothEndian16(data, offset, value);
        }

        private static void PutBothEndian32At(byte[] data, int offset, uint value)
        {
            BothEndian32(data, offset, value);
        }

        private static void PutU32LE(byte[] data, int offset, uint value)
        {
            Util.PutU32(data, offset, value);
        }

        private static void PutU32BE(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        private static void WritePadded(byte[] data, int offset, int size, string text)
        {
            if (text == null) text = "";
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            int count = Math.Min(size, bytes.Length);
            Buffer.BlockCopy(bytes, 0, data, offset, count);
            for (int i = count; i < size; i++) data[offset + i] = 0x20;
        }

        private static void WriteRecordDate(byte[] data, int offset)
        {
            DateTime now = DateTime.Now;
            data[offset] = (byte)(now.Year - 1900);
            data[offset + 1] = (byte)now.Month;
            data[offset + 2] = (byte)now.Day;
            data[offset + 3] = (byte)now.Hour;
            data[offset + 4] = (byte)now.Minute;
            data[offset + 5] = (byte)now.Second;
            data[offset + 6] = 0;
        }

        private static void WriteDosDate(byte[] data, int offset)
        {
            WriteRecordDate(data, offset);
        }
    }
}

