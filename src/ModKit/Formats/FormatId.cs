using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// Content sniffing for every file the kit meets. Extensions lie
    /// (GTA3PSP.IMG, LOADSC0.CHK, GAME.DTZ...) so the decision is made on magic
    /// bytes first and on the extension only as a tie breaker.
    /// </summary>
    public static class FormatId
    {
        public sealed class Info
        {
            public string Kind;          // stable id used by the API
            public string Label;         // human readable
            public string Platform;      // PSP / PS2 / PC / unknown
            public string Game;          // LCS / VCS / SA / any
            public bool Openable;        // does the kit have a parser for it?
            public Dictionary<string, object> Details = new Dictionary<string, object>();

            public override string ToString()
            {
                return Kind + " (" + Label + ")";
            }
        }

        public static Info Identify(string path)
        {
            var info = new Info { Kind = "unknown", Label = "Unknown data", Platform = "unknown", Game = "unknown" };
            if (!File.Exists(path))
            {
                info.Label = "Missing file";
                return info;
            }

            byte[] head;
            long length = Util.FileSize(path);
            // Small game files are read whole: zlib sizes must be exact, not "first 64 KB".
            int readSize = (int)Math.Min(length, length <= 64L * 1024 * 1024 ? length : 64 * 1024);
            using (var stream = File.OpenRead(path))
                head = Util.ReadExactly(stream, readSize);

            info.Details["size"] = length;
            info.Details["extension"] = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            string magic4 = Encoding.ASCII.GetString(head, 0, Math.Min(4, head.Length));

            // --- disc images ------------------------------------------------
            if (magic4 == "CISO" || magic4 == "ZISO")
            {
                info.Kind = magic4 == "CISO" ? "cso" : "zso";
                info.Label = "Compressed disc image (PSP)";
                info.Platform = "PSP";
                info.Openable = true;
                try
                {
                    Cso.Info cso = Cso.ReadInfo(path);
                    info.Details["codec"] = cso.Codec;
                    info.Details["uncompressed"] = cso.TotalBytes;
                    info.Details["blockSize"] = (long)cso.BlockSize;
                    info.Details["ratio"] = cso.Ratio;
                }
                catch (Exception ex)
                {
                    info.Details["error"] = Log.Describe(ex);
                }
                return info;
            }
            if (magic4 == "DAX\0")
            {
                info.Kind = "dax";
                info.Label = "DAX compressed image (unsupported)";
                info.Platform = "PSP";
                return info;
            }
            if (head.Length > 0x8006 && Encoding.ASCII.GetString(head, 0x8001, 5) == "CD001")
            {
                info.Kind = "iso";
                info.Label = "ISO 9660 disc image";
                info.Platform = "PSP";
                info.Openable = true;
                try
                {
                    Iso9660.Volume volume = Iso9660.Read(path);
                    info.Details["volume"] = volume.VolumeIdentifier;
                    info.Details["system"] = volume.SystemIdentifier;
                    int files = 0, directories = 0;
                    foreach (Iso9660.Entry entry in volume.AllEntries)
                        if (entry.IsDirectory) directories++;
                        else files++;
                    info.Details["files"] = (long)files;
                    info.Details["directories"] = (long)directories;
                }
                catch (Exception ex)
                {
                    info.Details["error"] = Log.Describe(ex);
                }
                return info;
            }

            // --- Stories data ----------------------------------------------
            if (Zlib.LooksLikeZlib(head, 0))
            {
                info.Kind = "zlib";
                info.Label = "zlib compressed blob";
                info.Openable = true;
                try
                {
                    byte[] inflated = Zlib.Inflate(head);
                    string inner = Util.Ident(inflated, 0);
                    if (string.Equals(inner, "GTAG", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Kind = "dtz";
                        info.Label = "GAME.DTZ (zlib packed GAME.DAT)";
                        info.Platform = "PSP";
                        info.Game = "LCS/VCS";
                        info.Details["uncompressed"] = (long)inflated.Length;
                        info.Details["expectedUncompressed"] = (long)Util.U32(inflated, 8);
                    }
                    else if (string.Equals(inner, "WRLD", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Kind = "lvz";
                        info.Label = "LVZ (zlib packed world chunk)";
                        info.Platform = "PSP";
                        info.Game = "LCS/VCS";
                        info.Details["uncompressed"] = (long)inflated.Length;
                    }
                    else
                    {
                        info.Details["inner"] = inner;
                        info.Details["uncompressed"] = (long)inflated.Length;
                    }
                }
                catch (Exception ex)
                {
                    info.Details["error"] = Log.Describe(ex);
                }
                return info;
            }

            string ident = Util.Ident(head, 0);
            if (string.Equals(ident, "GTAG", StringComparison.OrdinalIgnoreCase))
            {
                info.Kind = "dat";
                info.Label = "GAME.DAT (relocatable streaming chunk)";
                info.Platform = "PSP";
                info.Game = "LCS/VCS";
                info.Openable = true;
                info.Details["fileEnd"] = (long)Util.U32(head, 8);
                return info;
            }
            if (string.Equals(ident, "tex", StringComparison.OrdinalIgnoreCase))
            {
                info.Kind = "chk";
                info.Label = "Texture archive (CHK/XTX)";
                info.Platform = "PSP";
                info.Game = "LCS/VCS";
                info.Openable = true;
                try
                {
                    ChkFile chk = ChkFile.Parse(head);
                    info.Details["textures"] = (long)chk.Textures.Count;
                }
                catch (Exception ex)
                {
                    info.Details["error"] = Log.Describe(ex);
                }
                return info;
            }
            if (string.Equals(ident, "mdl", StringComparison.OrdinalIgnoreCase))
            {
                info.Kind = "mdl";
                info.Label = "Model chunk (MDL)";
                info.Platform = "PSP";
                info.Game = "LCS/VCS";
                info.Openable = false;
                info.Details["fileEnd"] = (long)Util.U32(head, 8);
                return info;
            }
            if (string.Equals(ident, "anim", StringComparison.OrdinalIgnoreCase))
            {
                info.Kind = "anim";
                info.Label = "Animation chunk";
                info.Platform = "PSP";
                info.Game = "LCS/VCS";
                return info;
            }
            if (magic4 == "VER2")
            {
                info.Kind = "img";
                info.Label = "IMG archive (version 2)";
                info.Platform = "PC";
                info.Openable = true;
                info.Details["entries"] = (long)Util.U32(head, 4);
                return info;
            }
            if (Encoding.ASCII.GetString(head, 0, 5) == "TABLD" ||
                Encoding.ASCII.GetString(head, 0, 4) == "TKEY")
            {
                info.Kind = "gxt";
                info.Label = "GXT text table";
                info.Platform = "PSP/PC";
                info.Openable = true;
                return info;
            }
            if (magic4 == "VAGp")
            {
                info.Kind = "vag";
                info.Label = "VAG audio (PS-ADPCM)";
                info.Platform = "PSP/PS2";
                info.Openable = true;
                Vag.Info vag = Vag.ReadInfo(head);
                info.Details["sampleRate"] = (long)vag.SampleRate;
                return info;
            }
            if (magic4 == "PSMF")
            {
                info.Kind = "pmf";
                info.Label = "PSP movie (PMF/PSS)";
                info.Platform = "PSP";
                info.Details["note"] = "video is MPEG-PS; edit with ffmpeg, not with this tool";
                return info;
            }
            if (magic4 == "RIFF" && head.Length > 20)
            {
                string type = Encoding.ASCII.GetString(head, 8, 4);
                info.Platform = "PSP/PC";
                if (type == "WAVE")
                {
                    bool at3 = head.Length > 0x2a && Encoding.ASCII.GetString(head, 0x14, 2) == "AT";
                    info.Kind = at3 ? "at3" : "wav";
                    info.Label = at3 ? "ATRAC3+ audio (RIFF WAVE)" : "RIFF WAVE audio";
                    info.Details["note"] = at3 ? "decode with at3tool/ffmpeg; kit reads headers only" : "";
                }
                else
                {
                    info.Kind = "riff";
                    info.Label = "RIFF container";
                }
                return info;
            }

            // Lua 5.1 bytecode
            if (head.Length > 4 && head[0] == 0x1b && magic4.Substring(1) == "Lua")
            {
                info.Kind = "lua";
                info.Label = "Compiled Lua bytecode";
                info.Platform = "PSP";
                info.Game = "LCS/VCS";
                info.Details["note"] = "MP scripts; decompile with unluac, the kit only lists constants";
                return info;
            }

            // PSP containers
            if (head.Length > 4 && head[0] == 0 && magic4.Substring(1) == "PSF")
            {
                info.Kind = "sfo";
                info.Label = "PARAM.SFO";
                info.Platform = "PSP";
                return info;
            }
            if (magic4 == "\0PBP")
            {
                info.Kind = "pbp";
                info.Label = "PSP EBOOT.PBP";
                info.Platform = "PSP";
                return info;
            }
            if (head.Length > 0x8001 && Encoding.ASCII.GetString(head, 1, 4) == "PBP")
            {
                info.Kind = "pbp";
                info.Label = "PSP PBP container";
                info.Platform = "PSP";
                return info;
            }
            if (head.Length > 12 && Encoding.ASCII.GetString(head, 0, 4) == "PSMF")
            {
                info.Kind = "pmf";
                info.Label = "PSP movie";
                info.Platform = "PSP";
                return info;
            }

            // Common media / archives, so the UI does not call them "unknown"
            if (magic4 == "\x89PNG")
            {
                info.Kind = "png";
                info.Label = "PNG image";
            }
            else if (head.Length > 3 && head[0] == 0xff && head[1] == 0xd8)
            {
                info.Kind = "jpg";
                info.Label = "JPEG image";
            }
            else if (magic4.StartsWith("PK", StringComparison.Ordinal))
            {
                info.Kind = "zip";
                info.Label = "ZIP archive";
            }

            return info;
        }

        /// <summary>Short text used in tables and logs.</summary>
        public static string Describe(string path)
        {
            Info info = Identify(path);
            return info.Kind + ": " + info.Label;
        }
    }
}
