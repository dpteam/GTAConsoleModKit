using System;
using System.Collections.Generic;
using System.IO;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// Puts a working tree back into an IMG and keeps GAME.DAT in sync.
    ///
    /// Strategy (learned the hard way from retail images): **never move what you do
    /// not have to**. A Stories IMG is not packed contiguously - measured on LCS
    /// GTA3PSPHR.IMG there are 175 gaps between the 1121 streamed entries, plus
    /// ~3.5 MB of data after the last one. Those sectors belong to something the
    /// streaming table does not name, so a naive "re-lay everything from sector 0"
    /// repack would silently drop them.
    ///
    /// Therefore:
    ///   * an entry that still fits its original sector range is written in place and
    ///     keeps its position and size (zero DAT changes);
    ///   * an entry that grew is appended at the end of the image, and only *that*
    ///     entry's streaming slot is rewritten;
    ///   * the old range of a relocated entry is left alone (wasted space, never
    ///     reused - overwriting it could destroy unnamed data).
    ///
    /// The result is an image whose size may grow but whose existing layout is
    /// untouched, which is exactly what the game expects.
    /// </summary>
    public static class ImgRepack
    {
        public sealed class Result
        {
            public int Entries;
            public int InPlace;
            public int Relocated;
            public int Patched;
            public int MissingFromFolder;
            public long OldImageSize;
            public long NewImageSize;
            public string ImagePath;
            public string DirectoryPath;
            public string DtzPath;
            public List<string> Warnings = new List<string>();
        }

        public static Result Run(string imgPath, string dirPath, string datPath, string folder,
            string outImg, string outDir, string outDtz, Action<int> progress)
        {
            if (!File.Exists(imgPath)) throw new FileNotFoundException("no such IMG", imgPath);
            if (!File.Exists(datPath)) throw new FileNotFoundException("no such GAME.DTZ/DAT", datPath);

            DtzImage dat = DtzImage.Load(datPath, null);
            if (!dat.IsIndexed)
                throw new NotSupportedException("streaming table of '" + dat.GameLabel +
                    "' is not indexed yet (see docs/FORMATS.md §8)");

            ImgArchive archive = ImgArchive.Open(imgPath, dirPath);
            List<DtzImage.StreamingEntry> streaming = dat.BuildDirectory();

            var byRange = new Dictionary<string, DtzImage.StreamingEntry>(StringComparer.Ordinal);
            foreach (DtzImage.StreamingEntry entry in streaming)
            {
                string key = entry.OffsetSectors + ":" + entry.SizeSectors;
                if (!byRange.ContainsKey(key)) byRange[key] = entry;
            }

            var order = new List<ImgArchive.Entry>(archive.Entries);
            order.Sort(delegate(ImgArchive.Entry a, ImgArchive.Entry b)
            {
                return a.OffsetSectors.CompareTo(b.OffsetSectors);
            });

            string imageTarget = string.IsNullOrEmpty(outImg) ? imgPath : outImg;
            var result = new Result
            {
                OldImageSize = archive.FileSize,
                ImagePath = imageTarget,
                DirectoryPath = string.IsNullOrEmpty(outDir)
                    ? Path.ChangeExtension(imageTarget, ".dir")
                    : outDir,
                DtzPath = string.IsNullOrEmpty(outDtz)
                    ? Path.ChangeExtension(datPath, null) + "_repacked.dtz"
                    : outDtz,
                Entries = order.Count
            };

            string workImage = imageTarget;
            bool sameFile = string.Equals(Path.GetFullPath(imageTarget), Path.GetFullPath(imgPath),
                StringComparison.OrdinalIgnoreCase);
            if (sameFile)
            {
                workImage = imageTarget + ".repack";
                File.Copy(imgPath, workImage, true);
            }
            else
            {
                string directory = Path.GetDirectoryName(workImage);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                File.Copy(imgPath, workImage, true);
            }

            var dirBuffer = new byte[order.Count * 32];
            long appendAt;

            try
            {
                using (var image = new FileStream(workImage, FileMode.Open, FileAccess.Write, FileShare.Read,
                           128 * 1024))
                {
                    appendAt = (image.Length + Iso9660.SectorSize - 1) / Iso9660.SectorSize;
                    var buffer = new byte[128 * 1024];

                    for (int i = 0; i < order.Count; i++)
                    {
                        ImgArchive.Entry entry = order[i];
                        long originalSectors = entry.SizeSectors;
                        long capacity = originalSectors * Iso9660.SectorSize;

                        string fileName = ImgArchive.SanitizeFileName(entry.Name);
                        string fromFolder = Util.SafeCombine(folder, fileName);
                        bool haveFile = fromFolder != null && File.Exists(fromFolder);
                        if (!haveFile) result.MissingFromFolder++;

                        long length = haveFile ? new FileInfo(fromFolder).Length : capacity;
                        long sectors;
                        long offset;

                        if (length <= capacity && capacity > 0)
                        {
                            offset = entry.OffsetSectors;
                            sectors = originalSectors;   // keep the slot as it is
                            result.InPlace++;
                        }
                        else
                        {
                            sectors = Math.Max(1, (length + Iso9660.SectorSize - 1) / Iso9660.SectorSize);
                            offset = appendAt;
                            appendAt += sectors;
                            result.Relocated++;

                            DtzImage.StreamingEntry slot;
                            if (byRange.TryGetValue(entry.OffsetSectors + ":" + entry.SizeSectors, out slot))
                            {
                                if (dat.SetStreamingPosition(slot.Kind == "tex", slot.Index, offset, sectors))
                                    result.Patched++;
                            }
                            else
                            {
                                result.Warnings.Add("entry without a streaming slot: " + entry.Name);
                            }
                        }

                        Util.PutU32(dirBuffer, i * 32, (uint)offset);
                        Util.PutU32(dirBuffer, i * 32 + 4, (uint)sectors);
                        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(entry.Name.ToUpperInvariant());
                        Buffer.BlockCopy(nameBytes, 0, dirBuffer, i * 32 + 8, Math.Min(23, nameBytes.Length));

                        // write the payload
                        image.Position = offset * Iso9660.SectorSize;
                        if (haveFile)
                        {
                            using (var input = File.OpenRead(fromFolder))
                            {
                                long copied = 0;
                                while (copied < length)
                                {
                                    int want = (int)Math.Min(buffer.Length, length - copied);
                                    int read = input.Read(buffer, 0, want);
                                    if (read <= 0) break;
                                    image.Write(buffer, 0, read);
                                    copied += read;
                                }
                            }
                        }

                        long paddedEnd = (offset + sectors) * Iso9660.SectorSize;
                        if (image.Position < paddedEnd)
                        {
                            // zero the rest of the entry (harmless for the ones that
                            // already contain zeros, required for grown slots)
                            Array.Clear(buffer, 0, buffer.Length);
                            long remaining = paddedEnd - image.Position;
                            while (remaining > 0)
                            {
                                int want = (int)Math.Min(buffer.Length, remaining);
                                image.Write(buffer, 0, want);
                                remaining -= want;
                            }
                        }

                        if (progress != null) progress((i + 1) * 80 / Math.Max(1, order.Count));
                    }
                }

                if (sameFile)
                {
                    Util.WriteAllBytesAtomic(imgPath, File.ReadAllBytes(workImage));
                    try { File.Delete(workImage); } catch (Exception) { }
                }

                Util.WriteAllBytesAtomic(result.DirectoryPath, dirBuffer);

                string datOut = Path.ChangeExtension(result.DtzPath, ".dat");
                dat.SaveDat(datOut);
                dat.SaveDtz(result.DtzPath);

                result.NewImageSize = new FileInfo(result.ImagePath).Length;
                if (progress != null) progress(88);

                Verify(result);
                if (progress != null) progress(100);
                return result;
            }
            finally
            {
                try
                {
                    if (sameFile && File.Exists(workImage)) File.Delete(workImage);
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Reads the result back and proves that the directory, the image and the
        /// streaming table agree. A repack that cannot be read back is worse than
        /// no repack at all.
        /// </summary>
        private static void Verify(Result result)
        {
            ImgArchive repacked = ImgArchive.Open(result.ImagePath, result.DirectoryPath);

            DtzImage check;
            if (File.Exists(Path.ChangeExtension(result.DtzPath, ".dat")))
                check = DtzImage.Load(Path.ChangeExtension(result.DtzPath, ".dat"), null);
            else
                check = DtzImage.Load(result.DtzPath, null);

            List<DtzImage.StreamingEntry> entries = check.BuildDirectory();
            var ranges = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (DtzImage.StreamingEntry entry in entries)
                ranges[entry.OffsetSectors + ":" + entry.SizeSectors] = true;

            int mismatched = 0;
            foreach (ImgArchive.Entry entry in repacked.Entries)
                if (!ranges.ContainsKey(entry.OffsetSectors + ":" + entry.SizeSectors)) mismatched++;

            if (mismatched > 0)
                result.Warnings.Add(mismatched + " IMG entries do not match the rewritten streaming table");

            if (repacked.Entries.Count != result.Entries)
                result.Warnings.Add("entry count changed: " + repacked.Entries.Count + " vs " + result.Entries);

            foreach (string warning in result.Warnings) Log.Warn(Cat.Fmt, "repack: " + warning);
            if (result.Warnings.Count == 0)
                Log.Info(Cat.Fmt, "repack verified: " + repacked.Entries.Count + " entries, " +
                                  result.InPlace + " in place, " + result.Relocated + " relocated");
        }
    }
}