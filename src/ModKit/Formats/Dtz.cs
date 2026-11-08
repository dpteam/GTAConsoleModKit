using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// GAME.DTZ / GAME.DAT: the streaming image that holds all game data of
    /// LCS/VCS (model info, texture pool, streaming table, handling, IDE/IPL ...).
    ///
    /// GAME.DTZ is a zlib stream of GAME.DAT; GAME.DAT is a relocatable chunk
    /// (ident "GTAG") whose first field after the header is sResourceImage.
    ///
    /// We do not need to relocate anything to *read* it: without relocation every
    /// pointer is a file offset, which is exactly what we want. What we cannot do
    /// (yet) is rebuild the game structures after editing one of them - that part
    /// is documented as a known limit in docs/FORMATS.md.
    ///
    /// Offsets below were verified on retail LCS and VCS PSP images; see
    /// docs/FORMATS.md for the measured values and the struct layout sources.
    /// </summary>
    public sealed class DtzImage
    {
        // sResourceImage field offsets (PSP, 4 byte pointers, 1 byte booleans)
        private const int OffNumModelInfos = 24;
        private const int OffModelInfoPtrs = 28;
        private const int OffTexListPool = 64;
        private const int OffStoredTexList = 68;
        private const int OffStreaming = 92;

        private const int StreamingInfoSize = 20;

        public enum GameKind
        {
            Unknown,
            LcsPsp,
            VcsPsp,
            LcsPs2,
            VcsPs2
        }

        public GameKind Game { get; private set; }
        public ChunkHeader Header { get; private set; }
        public byte[] Data { get; private set; }
        public string SourcePath { get; private set; }
        public bool WasCompressed { get; private set; }

        public int ModelCount { get; private set; }
        public int TextureCount { get; private set; }
        public int StreamingModelCount { get; private set; }
        public int StreamingTextureCount { get; private set; }

        private ModelNameDatabase _names;

        public static DtzImage Load(string path, ModelNameDatabase names)
        {
            byte[] raw = File.ReadAllBytes(path);
            var image = new DtzImage { SourcePath = path, _names = names };
            image.Data = raw;

            if (raw.Length >= ChunkHeader.Size)
            {
                string ident = Util.Ident(raw, 0);
                if (string.Equals(ident, "GTAG", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug(Cat.Fmt, "GAME.DAT input (uncompressed)");
                }
                else if (Zlib.LooksLikeZlib(raw, 0))
                {
                    image.WasCompressed = true;
                    Log.Verbose(Cat.Fmt, "inflating GAME.DTZ (" + Util.HumanBytes(raw.Length) + ")");
                    image.Data = Zlib.Inflate(raw);
                    Log.Info(Cat.Fmt, "GAME.DAT is " + Util.HumanBytes(image.Data.Length));
                }
                else
                {
                    throw new InvalidDataException("neither GAME.DAT (GTAG) nor a zlib compressed GAME.DTZ");
                }
            }

            image.Header = ChunkHeader.Parse(image.Data, 0);
            if (!image.Header.Is("GTAG"))
                throw new InvalidDataException("unexpected chunk ident '" + image.Header.Ident +
                                               "', expected GTAG");
            image.Game = image.DetectGame();
            image.Index();
            return image;
        }

        private GameKind DetectGame()
        {
            // numModelInfos is a very stable fingerprint: LCS PSP has 4101 entries.
            int count = (int)Util.U32(Data, ChunkHeader.Size + OffNumModelInfos);
            int ptrs = (int)Util.U32(Data, ChunkHeader.Size + OffModelInfoPtrs);
            int streaming = StreamingBase;

            if (count <= 0 || count > 20000 || ptrs <= 0 || ptrs >= Data.Length ||
                streaming <= 0 || streaming + 160 >= Data.Length)
            {
                Log.Warn(Cat.Fmt, "DAT does not look like a PSP resource image " +
                                  "(count=" + count + ", ptrs=" + ptrs + ", streaming=" + streaming + ")");
                return GameKind.Unknown;
            }

            if (count >= 3900 && count <= 4600) return GameKind.LcsPsp;

            // VCS keeps the streaming table behind pointers instead of fixed arrays.
            int texOffset = (int)Util.U32(Data, streaming + 8);
            int colOffset = (int)Util.U32(Data, streaming + 12);
            int anmOffset = (int)Util.U32(Data, streaming + 16);
            int numInfos = (int)Util.U32(Data, streaming + 20);
            int array = (int)Util.U32(Data, streaming + 152);

            bool vcsLayout = texOffset > 0 && colOffset > texOffset && anmOffset > colOffset &&
                             numInfos > anmOffset && numInfos < 200000 &&
                             array > 0 && array < Data.Length;
            if (vcsLayout) return GameKind.VcsPsp;

            return GameKind.Unknown;
        }

        public string GameLabel
        {
            get
            {
                switch (Game)
                {
                    case GameKind.LcsPsp: return "LCS PSP";
                    case GameKind.VcsPsp: return "VCS PSP";
                    case GameKind.LcsPs2: return "LCS PS2";
                    case GameKind.VcsPs2: return "VCS PS2";
                    default: return "unknown";
                }
            }
        }

        private int StreamingBase
        {
            // note: the field holds an *offset*, not the struct itself
            get { return (int)Util.U32(Data, ChunkHeader.Size + OffStreaming); }
        }

        /// <summary>Reads the streaming table layout for the detected build.</summary>
        private void Index()
        {
            try
            {
                ModelCount = (int)Util.U32(Data, ChunkHeader.Size + OffNumModelInfos);
            }
            catch (Exception)
            {
                ModelCount = 0;
            }

            if (Game == GameKind.LcsPsp)
            {
                StreamingModelCount = 4900;
                StreamingTextureCount = 1200;
            }
            else if (Game == GameKind.VcsPsp)
            {
                int texOffset = (int)Util.U32(Data, StreamingBase + 8);
                int colOffset = (int)Util.U32(Data, StreamingBase + 12);
                StreamingModelCount = texOffset;
                StreamingTextureCount = Math.Max(0, colOffset - texOffset);
            }
            else
            {
                StreamingModelCount = 0;
                StreamingTextureCount = 0;
            }

            int pool = (int)Util.U32(Data, ChunkHeader.Size + OffTexListPool);
            if (pool > 0 && pool + 12 < Data.Length)
            {
                int size = (int)Util.U32(Data, pool + 8);
                if (size > 0 && size < 65536) TextureCount = size;
            }

            Log.Debug(Cat.Fmt, "streaming: " + StreamingModelCount + " models, " +
                               StreamingTextureCount + " textures, model info " + ModelCount);
        }

        public sealed class StreamingEntry
        {
            public int Index;
            public string Name;
            public long OffsetSectors;
            public long SizeSectors;
            public string Kind; // mdl / tex / col / anim
        }

        private sealed class RawStream
        {
            public long Offset;
            public long Size;
        }

        private RawStream ReadStream(int arrayOffsetBytes, int index)
        {
            int at = arrayOffsetBytes + index * StreamingInfoSize;
            if (at + StreamingInfoSize > Data.Length) return null;
            // next(4) prev(4) status(1) flags(1) nextModelOnCd(2) pos(4) size(4)
            return new RawStream
            {
                Offset = Util.U32(Data, at + 12),
                Size = Util.U32(Data, at + 16)
            };
        }

        private int ModelArray
        {
            get { return StreamingBase + 4; }
        }

        private int TextureArray
        {
            get
            {
                if (Game == GameKind.LcsPsp) return StreamingBase + 4 + 4900 * StreamingInfoSize;
                int texOffset = (int)Util.U32(Data, StreamingBase + 8);
                return StreamingBase + 4 + texOffset * StreamingInfoSize;
            }
        }

        /// <summary>Builds the .DIR the game would use for the main IMG.</summary>
        public List<StreamingEntry> BuildDirectory()
        {
            var result = new List<StreamingEntry>();

            if (Game == GameKind.VcsPsp)
                throw new NotSupportedException(
                    "VCS streaming table is not decoded yet (see docs/FORMATS.md §8). " +
                    "Use img-list/img-extract without a .DIR: it scans the IMG chunks instead.");

            if (Game != GameKind.LcsPsp)
                throw new NotSupportedException(
                    "this GAME.DAT is '" + GameLabel + "'; only LCS PSP is indexed so far");

            for (int i = 0; i < StreamingModelCount; i++)
            {
                RawStream stream = ReadStream(ModelArray, i);
                if (stream == null || stream.Size == 0) continue;
                result.Add(new StreamingEntry
                {
                    Index = i,
                    Name = ModelName(i) + ".mdl",
                    OffsetSectors = stream.Offset,
                    SizeSectors = stream.Size,
                    Kind = "mdl"
                });
            }

            for (int i = 0; i < StreamingTextureCount; i++)
            {
                RawStream stream = ReadStream(TextureArray, i);
                if (stream == null || stream.Size == 0) continue;
                result.Add(new StreamingEntry
                {
                    Index = i,
                    Name = TextureName(i) + (Game == GameKind.VcsPsp ? ".xtx" : ".chk"),
                    OffsetSectors = stream.Offset,
                    SizeSectors = stream.Size,
                    Kind = "tex"
                });
            }

            result.Sort(delegate(StreamingEntry a, StreamingEntry b)
            {
                return a.OffsetSectors.CompareTo(b.OffsetSectors);
            });
            return result;
        }

        public void WriteDirectory(string path)
        {
            List<StreamingEntry> entries = BuildDirectory();
            var buffer = new byte[entries.Count * 32];
            for (int i = 0; i < entries.Count; i++)
            {
                Util.PutU32(buffer, i * 32, (uint)entries[i].OffsetSectors);
                Util.PutU32(buffer, i * 32 + 4, (uint)entries[i].SizeSectors);
                byte[] name = Encoding.ASCII.GetBytes(entries[i].Name.ToUpperInvariant());
                Buffer.BlockCopy(name, 0, buffer, i * 32 + 8, Math.Min(23, name.Length));
            }
            Util.WriteAllBytesAtomic(path, buffer);
            Log.Info(Cat.Fmt, "wrote " + entries.Count + " directory entries to " + path);
        }

        // ------------------------------------------------------------ names

        public uint ModelHash(int index)
        {
            int ptrs = (int)Util.U32(Data, ChunkHeader.Size + OffModelInfoPtrs);
            int at = ptrs + index * 4;
            if (at + 4 > Data.Length) return 0;
            int info = (int)Util.U32(Data, at);
            if (info <= 0 || info + 12 > Data.Length) return 0;
            return Util.U32(Data, info + 8);
        }

        public string ModelName(int index)
        {
            uint hash = ModelHash(index);
            if (hash == 0) return "model_" + index.ToString("0000", CultureInfo.InvariantCulture);
            string name = _names == null ? null : _names.Lookup(hash);
            if (string.IsNullOrEmpty(name))
                name = "hash_" + hash.ToString("x8", CultureInfo.InvariantCulture);
            return Util.Slug(name);
        }

        public string TextureName(int index)
        {
            int pool = (int)Util.U32(Data, ChunkHeader.Size + OffTexListPool);
            if (pool <= 0 || pool + 12 > Data.Length) return "tex_" + index.ToString("0000");
            int entries = (int)Util.U32(Data, pool);
            int size = (int)Util.U32(Data, pool + 8);
            if (index >= size || entries <= 0) return "tex_" + index.ToString("0000");
            int at = entries + index * 28;
            if (at + 28 > Data.Length) return "tex_" + index.ToString("0000");
            string name = Util.CString(Data, at + 8, 20);
            if (name.Length == 0) return "tex_" + index.ToString("0000");
            return Util.Slug(name);
        }

        /// <summary>ModelInfo dump used by the web asset browser and CLI.</summary>
        public List<Dictionary<string, object>> DescribeModels(int limit)
        {
            var result = new List<Dictionary<string, object>>();
            int count = ModelCount;
            if (limit > 0 && count > limit) count = limit;
            int ptrs = (int)Util.U32(Data, ChunkHeader.Size + OffModelInfoPtrs);

            for (int i = 0; i < count; i++)
            {
                int at = ptrs + i * 4;
                if (at + 4 > Data.Length) break;
                int info = (int)Util.U32(Data, at);
                if (info <= 0 || info + 32 > Data.Length) continue;
                uint hash = Util.U32(Data, info + 8);
                byte type = Data[info + 16];
                short texSlot = (short)Util.U16(Data, info + 30);
                RawStream stream = i < StreamingModelCount ? ReadStream(ModelArray, i) : null;
                result.Add(Json.O(
                    "id", (long)i,
                    "name", ModelName(i),
                    "hash", "0x" + hash.ToString("x8", CultureInfo.InvariantCulture),
                    "type", (long)type,
                    "texSlot", (long)texSlot,
                    "texName", texSlot >= 0 ? TextureName(texSlot) : "",
                    "offset", stream == null ? 0L : stream.Offset,
                    "sectors", stream == null ? 0L : stream.Size
                    ));
            }
            return result;
        }

        // ------------------------------------------------------------ writing

        public void SaveDat(string path)
        {
            Util.WriteAllBytesAtomic(path, Data);
        }

        public void SaveDtz(string path)
        {
            Util.WriteAllBytesAtomic(path, Zlib.Deflate(Data));
        }
    }

    /// <summary>
    /// hash -> name table extracted from the community name dumps
    /// (data/modelnames.lcs.txt / .vcs.txt, one "aabbccdd name" per line).
    /// </summary>
    public sealed class ModelNameDatabase
    {
        private readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();

        public int Count { get { return _names.Count; } }

        public static ModelNameDatabase Load(string path)
        {
            var db = new ModelNameDatabase();
            if (!File.Exists(path)) return db;
            try
            {
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int space = line.IndexOf(' ');
                    if (space <= 0) continue;
                    string hashText = line.Substring(0, space).Trim();
                    string name = line.Substring(space + 1).Trim();
                    if (hashText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        hashText = hashText.Substring(2);
                    uint hash;
                    if (!uint.TryParse(hashText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
                        continue;
                    db._names[hash] = name;
                }
            }
            catch (Exception ex)
            {
                Log.Error(Cat.Fmt, "cannot read model name database " + path, ex);
            }
            return db;
        }

        public string Lookup(uint hash)
        {
            string name;
            return _names.TryGetValue(hash, out name) ? name : null;
        }
    }
}
