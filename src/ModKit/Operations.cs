using System;
using System.Collections.Generic;
using System.IO;
using ModKit.Core;
using ModKit.Formats;

namespace ModKit
{
    /// <summary>
    /// One place where every real operation lives. The CLI and the web API both
    /// call into this, so behaviour cannot drift between the two front ends.
    /// </summary>
    public static class Operations
    {
        public sealed class Outcome
        {
            public bool Ok;
            public string Message = "";
            public Dictionary<string, object> Data = new Dictionary<string, object>();

            public static Outcome Fail(string message)
            {
                return new Outcome { Ok = false, Message = message };
            }
        }

        // ------------------------------------------------------------- images

        public static Outcome CsoToIso(string source, string target, Action<int> progress)
        {
            if (!File.Exists(source)) return Outcome.Fail("no such file: " + source);
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, ".iso");
            var info = Cso.ConvertToIso(source, target, progress);
            return new Outcome
            {
                Ok = true,
                Message = "expanded " + Path.GetFileName(source) + " -> " + Path.GetFileName(target),
                Data = Json.O("iso", target, "size", new FileInfo(target).Length,
                    "codec", info.Codec, "blocks", info.Blocks, "blockSize", (long)info.BlockSize)
            };
        }

        public static Outcome IsoToCso(string source, string target, int blockSize, Action<int> progress)
        {
            if (!File.Exists(source)) return Outcome.Fail("no such file: " + source);
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, ".cso");
            var info = Cso.CompressFromIso(source, target, blockSize, progress);
            return new Outcome
            {
                Ok = true,
                Message = "compressed " + Path.GetFileName(source) + " -> " + Path.GetFileName(target),
                Data = Json.O("cso", target, "size", new FileInfo(target).Length,
                    "ratio", info.Ratio, "codec", info.Codec)
            };
        }

        public static Outcome IsoList(string source)
        {
            Iso9660.Volume volume = Iso9660.Read(source);
            var files = new List<object>();
            int directories = 0;
            foreach (Iso9660.Entry entry in volume.AllEntries)
            {
                if (entry.IsDirectory) directories++;
                else files.Add(Json.O("path", entry.FullPath, "size", entry.Length,
                    "sector", entry.Extent));
            }
            return new Outcome
            {
                Ok = true,
                Message = files.Count + " files, " + directories + " directories",
                Data = Json.O("volume", volume.VolumeIdentifier, "system", volume.SystemIdentifier,
                    "files", files, "directories", (long)directories)
            };
        }

        public static Outcome IsoExtract(string source, string target, Action<int> progress)
        {
            if (string.IsNullOrEmpty(target))
                target = Path.Combine(Path.GetDirectoryName(source) ?? ".",
                    Path.GetFileNameWithoutExtension(source));
            int count = Iso9660.Extract(source, target, delegate(string path, int percent)
            {
                if (progress != null) progress(percent);
            });
            return new Outcome
            {
                Ok = true,
                Message = "extracted " + count + " files to " + target,
                Data = Json.O("target", target, "files", (long)count)
            };
        }

        public static Outcome IsoBuild(string source, string target, string label, Action<int> progress)
        {
            if (!Directory.Exists(source)) return Outcome.Fail("no such folder: " + source);
            if (string.IsNullOrEmpty(target))
                target = Path.GetFullPath(source.TrimEnd('\\', '/')) + ".iso";
            Iso9660.Build(source, target, string.IsNullOrEmpty(label) ? "GTA_PSP" : label, progress);
            return new Outcome
            {
                Ok = true,
                Message = "built " + Path.GetFileName(target),
                Data = Json.O("iso", target, "size", new FileInfo(target).Length)
            };
        }

        // ---------------------------------------------------------------- IMG

        public static Outcome ImgList(string source, string directory)
        {
            ImgArchive archive = ImgArchive.Open(source, directory);
            var entries = new List<object>();
            foreach (ImgArchive.Entry entry in archive.Entries)
                entries.Add(Json.O("name", entry.Name, "offset", entry.OffsetSectors,
                    "size", entry.SizeSectors));
            return new Outcome
            {
                Ok = true,
                Message = archive.Format + ": " + archive.Entries.Count + " entries",
                Data = Json.O("format", archive.Format, "entries", entries,
                    "imgSize", archive.FileSize)
            };
        }

        public static Outcome ImgExtract(string source, string directory, string target,
            Action<string, int> progress)
        {
            ImgArchive archive = ImgArchive.Open(source, directory);
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, null) + "_unpacked";
            int count = archive.ExtractAll(target, progress);
            return new Outcome
            {
                Ok = true,
                Message = "extracted " + count + " entries to " + target,
                Data = Json.O("target", target, "files", (long)count, "format", archive.Format)
            };
        }

        public static Outcome ImgReplace(string image, string directory, string entryName,
            string sourcePath)
        {
            if (!File.Exists(image)) return Outcome.Fail("no such IMG: " + image);
            if (!File.Exists(sourcePath)) return Outcome.Fail("no such file: " + sourcePath);

            ImgArchive archive = ImgArchive.Open(image, directory);
            ImgArchive.Entry entry = archive.Find(entryName);
            if (entry == null)
                return Outcome.Fail("no entry '" + entryName + "' in " + Path.GetFileName(image));

            string message;
            bool ok = archive.Replace(entry, sourcePath, out message);
            if (!ok) return Outcome.Fail(message);

            return new Outcome
            {
                Ok = true,
                Message = message,
                Data = Json.O("img", image, "entry", entry.Name,
                    "offset", entry.OffsetSectors, "sectors", entry.SizeSectors,
                    "bytes", new FileInfo(sourcePath).Length)
            };
        }

        public static Outcome ImgPack(string folder, string target, string directory,
            string extension, Action<int> progress)
        {
            if (!Directory.Exists(folder)) return Outcome.Fail("no such folder: " + folder);
            if (string.IsNullOrEmpty(target))
                target = Path.GetFullPath(folder.TrimEnd('\\', '/')) + ".img";

            var entries = new List<ImgArchive.Entry>();
            foreach (string file in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (!string.IsNullOrEmpty(extension) &&
                    !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    continue;
                entries.Add(new ImgArchive.Entry
                {
                    Name = name,
                    SourcePath = file
                });
            }

            if (entries.Count == 0) return Outcome.Fail("no files to pack in " + folder);
            entries.Sort(delegate(ImgArchive.Entry a, ImgArchive.Entry b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            string dirPath = string.IsNullOrEmpty(directory) ? Path.ChangeExtension(target, ".dir") : directory;
            ImgArchive.Build(target, dirPath, entries, progress);
            return new Outcome
            {
                Ok = true,
                Message = "packed " + entries.Count + " entries into " + Path.GetFileName(target),
                Data = Json.O("img", target, "dir", dirPath, "entries", (long)entries.Count,
                    "size", new FileInfo(target).Length)
            };
        }

        // ---------------------------------------------------------------- DTZ        // ---------------------------------------------------------------- DTZ

        public static Outcome DtzInspect(string source, string namesPath)
        {
            ModelNameDatabase names = string.IsNullOrEmpty(namesPath) ? null : ModelNameDatabase.Load(namesPath);
            DtzImage image = DtzImage.Load(source, names);
            List<DtzImage.StreamingEntry> directory;
            string note = "";
            try
            {
                directory = image.BuildDirectory();
            }
            catch (NotSupportedException ex)
            {
                directory = new List<DtzImage.StreamingEntry>();
                note = ex.Message;
                Log.Warn(Cat.Fmt, "streaming table not available: " + ex.Message);
            }

            int mdl = 0, tex = 0;
            foreach (DtzImage.StreamingEntry entry in directory)
            {
                if (entry.Kind == "mdl") mdl++;
                else if (entry.Kind == "tex") tex++;
            }

            return new Outcome
            {
                Ok = true,
                Message = image.GameLabel + ": " + image.ModelCount + " model infos, " +
                          mdl + " streamed models, " + tex + " textures",
                Data = Json.O(
                    "game", image.GameLabel,
                    "compressed", image.WasCompressed,
                    "datSize", (long)image.Data.Length,
                    "modelInfos", (long)image.ModelCount,
                    "textureSlots", (long)image.TextureCount,
                    "streamedModels", (long)mdl,
                    "streamedTextures", (long)tex,
                    "note", note,
                    "header", image.Header.ToString())
            };
        }

        public static Outcome DtzExportDat(string source, string target)
        {
            DtzImage image = DtzImage.Load(source, null);
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, ".dat");
            image.SaveDat(target);
            return new Outcome
            {
                Ok = true,
                Message = "wrote " + Path.GetFileName(target),
                Data = Json.O("dat", target, "size", (long)image.Data.Length)
            };
        }

        public static Outcome DtzExportDtz(string source, string target)
        {
            DtzImage image = DtzImage.Load(source, null);
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, ".dtz");
            image.SaveDtz(target);
            return new Outcome
            {
                Ok = true,
                Message = "recompressed " + Path.GetFileName(target),
                Data = Json.O("dtz", target, "size", new FileInfo(target).Length)
            };
        }

        public static Outcome DtzExportDir(string source, string target, string namesPath)
        {
            ModelNameDatabase names = string.IsNullOrEmpty(namesPath) ? null : ModelNameDatabase.Load(namesPath);
            DtzImage image = DtzImage.Load(source, names);
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, ".dir");
            image.WriteDirectory(target);
            List<DtzImage.StreamingEntry> entries = image.BuildDirectory();
            int mdl = 0, tex = 0;
            foreach (DtzImage.StreamingEntry entry in entries)
            {
                if (entry.Kind == "mdl") mdl++;
                else if (entry.Kind == "tex") tex++;
            }
            return new Outcome
            {
                Ok = true,
                Message = "directory written: " + mdl + " mdl + " + tex + " tex entries",
                Data = Json.O("dir", target, "models", (long)mdl, "textures", (long)tex,
                    "entries", (long)entries.Count)
            };
        }

        public static Outcome DtzModels(string source, string namesPath, int limit)
        {
            ModelNameDatabase names = string.IsNullOrEmpty(namesPath) ? null : ModelNameDatabase.Load(namesPath);
            DtzImage image = DtzImage.Load(source, names);
            List<Dictionary<string, object>> models = image.DescribeModels(limit);
            return new Outcome
            {
                Ok = true,
                Message = models.Count + " model infos",
                Data = Json.O("models", models)
            };
        }

        // ---------------------------------------------------------------- CHK

        public static Outcome ChkList(string source)
        {
            ChkFile chk = ChkFile.Load(source);
            var textures = new List<object>();
            foreach (ChkFile.Texture texture in chk.Textures)
            {
                textures.Add(Json.O(
                    "name", texture.Name,
                    "width", (long)texture.Width,
                    "height", (long)texture.Height,
                    "format", Rsl.FormatName(texture.Format),
                    "dataOffset", (long)texture.DataOffset,
                    "dataLength", (long)texture.DataLength,
                    "palette", (long)texture.PaletteEntries));
            }
            return new Outcome
            {
                Ok = true,
                Message = chk.Textures.Count + " textures",
                Data = Json.O("chunk", chk.Header.ToString(), "textures", textures)
            };
        }

        public static Outcome ChkExport(string source, string target)
        {
            ChkFile chk = ChkFile.Load(source);
            if (string.IsNullOrEmpty(target))
                target = Path.ChangeExtension(source, null) + "_png";
            int count = chk.ExportAllPng(target);
            return new Outcome
            {
                Ok = true,
                Message = "exported " + count + " textures to " + target,
                Data = Json.O("target", target, "textures", (long)count)
            };
        }

        /// <summary>
        /// Replaces one texture inside a CHK/XTX chunk with a PNG and writes the
        /// chunk back. The palette is reused when it already covers the image, so
        /// "export -> edit -> import" keeps the original colours exactly. Mip levels
        /// are regenerated for pal8.
        /// </summary>
        public static Outcome ChkReplace(string source, string pngPath, int index, string textureName,
            string target)
        {
            if (!File.Exists(source)) return Outcome.Fail("no such file: " + source);
            if (!File.Exists(pngPath)) return Outcome.Fail("no such image: " + pngPath);

            ChkFile chk = ChkFile.Load(source);
            if (chk.Textures.Count == 0) return Outcome.Fail("the file contains no textures");

            int resolved = index;
            if (!string.IsNullOrEmpty(textureName))
            {
                resolved = -1;
                for (int i = 0; i < chk.Textures.Count; i++)
                {
                    if (string.Equals(chk.Textures[i].Name, textureName, StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = i;
                        break;
                    }
                }
                if (resolved < 0) return Outcome.Fail("no texture named '" + textureName + "'");
            }
            if (resolved < 0 || resolved >= chk.Textures.Count)
                return Outcome.Fail("texture index " + resolved + " is out of range, the file has " +
                                    chk.Textures.Count);

            ChkFile.Texture texture = chk.Textures[resolved];
            using (var image = new System.Drawing.Bitmap(pngPath))
            {
                if (image.Width != texture.Width || image.Height != texture.Height)
                    return Outcome.Fail("image is " + image.Width + "x" + image.Height +
                                        " but texture '" + texture.Name + "' is " +
                                        texture.Width + "x" + texture.Height);
                chk.ReplacePixels(texture, image);
            }

            string output = string.IsNullOrEmpty(target) ? source : target;
            chk.Save(output);
            return new Outcome
            {
                Ok = true,
                Message = "replaced texture '" + texture.Name + "' and wrote " + Path.GetFileName(output),
                Data = Json.O("output", output, "texture", texture.Name,
                    "index", (long)resolved, "width", (long)texture.Width,
                    "height", (long)texture.Height, "format", Rsl.FormatName(texture.Format),
                    "size", new FileInfo(output).Length)
            };
        }

        // ---------------------------------------------------------------- GXT

        public static Outcome GxtDump(string source, string target)
        {
            Gxt.Table table = Gxt.Read(source);
            string text = table.ToText();
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, ".txt");
            Util.WriteAllTextAtomic(target, text);
            return new Outcome
            {
                Ok = true,
                Message = table.Entries.Count + " strings -> " + Path.GetFileName(target),
                Data = Json.O("target", target, "strings", (long)table.Entries.Count,
                    "blocks", table.Blocks.Count)
            };
        }

        public static Outcome GxtStrings(string source)
        {
            Gxt.Table table = Gxt.Read(source);
            var items = new List<object>();
            foreach (Gxt.Entry entry in table.Entries)
                items.Add(Json.O("key", entry.Key, "block", entry.Block, "text", entry.Text));
            return new Outcome
            {
                Ok = true,
                Message = table.Entries.Count + " strings",
                Data = Json.O("strings", items, "blocks", table.Blocks.ToArray())
            };
        }

        // ---------------------------------------------------------------- VAG

        public static Outcome VagToWav(string source, string target)
        {
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, ".wav");
            int sampleRate;
            byte[] wav = Vag.ConvertToWav(source, out sampleRate);
            Util.WriteAllBytesAtomic(target, wav);
            return new Outcome
            {
                Ok = true,
                Message = "decoded to " + Path.GetFileName(target) + " (" + sampleRate + " Hz)",
                Data = Json.O("wav", target, "sampleRate", (long)sampleRate, "size", wav.Length)
            };
        }

        public static Outcome WavToVag(string source, string target)
        {
            if (string.IsNullOrEmpty(target)) target = Path.ChangeExtension(source, ".vag");
            byte[] vag = Vag.FromWav(File.ReadAllBytes(source), Path.GetFileNameWithoutExtension(source));
            Util.WriteAllBytesAtomic(target, vag);
            return new Outcome
            {
                Ok = true,
                Message = "encoded " + Path.GetFileName(target),
                Data = Json.O("vag", target, "size", vag.Length)
            };
        }

        // ------------------------------------------------------------ generic

        public static Outcome Identify(string source)
        {
            if (!File.Exists(source)) return Outcome.Fail("no such file: " + source);
            FormatId.Info info = FormatId.Identify(source);
            return new Outcome
            {
                Ok = true,
                Message = info.Kind + ": " + info.Label,
                Data = Json.O("kind", info.Kind, "label", info.Label, "platform", info.Platform,
                    "game", info.Game, "details", info.Details)
            };
        }
    }
}
