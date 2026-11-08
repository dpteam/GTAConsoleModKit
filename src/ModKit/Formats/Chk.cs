using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// Stories texture archive: LCS ".CHK", VCS ".XTX" and the "tex" chunks found
    /// inside the model IMGs.
    ///
    /// Layout (all offsets are chunk relative):
    ///   0x00  chunk header (see Rsl/ChunkHeader)         ident "tex"
    ///   0x20  RslTexList: object(8) + texturesInDict(8) + lInInstance(8)
    ///   0x38+ RslTexture nodes, 80 bytes each:
    ///           raster(4) dict(4) lInDict.next(4) prev(4) name[32] mask[32]
    ///         the list links point at lInDictionary, i.e. node = link - 8
    ///   RslRasterPSP (16 bytes): unk(4) data(4) flags1(2) wLog(1) hLog(1) flags2(4)
    ///
    /// Pixel data sits at raster.data and is PSP tiled ("swizzled"): 128 byte
    /// tiles, 8 rows of 16 bytes. Paletted formats keep the palette at the end of
    /// the data region, 4 bytes (RGBA) per entry.
    /// </summary>
    public sealed class ChkFile
    {
        public string Path { get; private set; }
        public ChunkHeader Header { get; private set; }
        public List<Texture> Textures { get; private set; }
        public byte[] Data { get; private set; }

        private ChkFile()
        {
            Textures = new List<Texture>();
        }

        public sealed class Texture
        {
            public string Name;
            public string Mask;
            public Rsl.Raster Raster;
            public int DataOffset;
            public int DataLength;
            public int PaletteOffset = -1;
            public int PaletteEntries;
            public Rsl.TextureFormat Format;

            public int Width { get { return Raster == null ? 0 : Raster.Width; } }
            public int Height { get { return Raster == null ? 0 : Raster.Height; } }

            public override string ToString()
            {
                return string.Format("{0} {1}x{2} {3}", Name, Width, Height, Rsl.FormatName(Format));
            }
        }

        public static ChkFile Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            ChkFile chk = Parse(data);
            chk.Path = path;
            return chk;
        }

        public static ChkFile Parse(byte[] data)
        {
            return Parse(data, 0, data.Length);
        }

        /// <summary>Parses a "tex" chunk that lives at <paramref name="baseOffset"/>.</summary>
        public static ChkFile Parse(byte[] data, int baseOffset, int limit)
        {
            var result = new ChkFile { Data = data };
            result.Header = ChunkHeader.Parse(data, baseOffset);

            // A texture dictionary has its own chunk id ("tex"/"xtx"). Model chunks
            // ("mdl"), world chunks ("WRLD") and random data would otherwise be
            // "parsed" into nonsense textures - an archive is a mixed bag, so this
            // check is what tells a texture chunk apart from everything else.
            string ident = result.Header.Ident ?? "";
            if (!string.Equals(ident, "tex", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ident, "xtx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("not a texture chunk (id '" + ident + "')");

            int chunkEnd = Math.Min(limit, baseOffset + (int)result.Header.FileEnd);
            if (chunkEnd > data.Length) chunkEnd = data.Length;

            var nodes = new List<int>();
            const int headLink = ChunkHeader.Size + 8; // chunk relative offset of list head link
            int head = baseOffset + headLink;

            int cursor = Rsl.Ptr(data, head);
            if (cursor != 0)
            {
                int guard = 0;
                while (cursor != 0 && guard++ < 4096)
                {
                    int node = baseOffset + cursor - 8;
                    if (node <= baseOffset + headLink || node + 80 > chunkEnd) break;
                    if (nodes.Contains(node)) break;
                    nodes.Add(node);
                    int next = Rsl.Ptr(data, node + 8);
                    if (next == 0 || next == headLink) break;
                    cursor = next;
                }
            }

            // Fallback: strict list walk failed (damaged file / unknown variant).
            // Scan for valid texture nodes instead of refusing the whole archive.
            if (nodes.Count == 0)
            {
                for (int offset = baseOffset + ChunkHeader.Size; offset + 80 <= chunkEnd; offset += 4)
                {
                    if (LooksLikeTextureNode(data, offset, baseOffset, chunkEnd))
                        nodes.Add(offset);
                }
                if (nodes.Count > 0)
                    Log.Warn(Cat.Fmt, "texture list broken, recovered " + nodes.Count + " nodes by scanning");
            }

            foreach (int node in nodes)
            {
                var texture = new Texture
                {
                    Name = Util.CString(data, node + 16, 32),
                    Mask = Util.CString(data, node + 48, 32)
                };
                int rasterOffset = Rsl.Ptr(data, node);
                if (rasterOffset != 0)
                {
                    int absolute = baseOffset + rasterOffset;
                    if (absolute + 16 <= chunkEnd)
                    {
                        texture.Raster = Rsl.Raster.Read(data, absolute);
                        texture.DataOffset = texture.Raster.DataOffset;
                    }
                }
                if (texture.Name.Length == 0) texture.Name = "texture" + result.Textures.Count;
                result.Textures.Add(texture);
            }

            result.AssignDataRanges(baseOffset, chunkEnd);
            foreach (Texture texture in result.Textures)
                texture.Format = result.DetectFormat(texture, baseOffset, chunkEnd);

            return result;
        }

        private static bool LooksLikeTextureNode(byte[] data, int node, int baseOffset, int chunkEnd)
        {
            int raster = Rsl.Ptr(data, node);
            if (raster == 0) return false;
            int absolute = baseOffset + raster;
            if (absolute + 16 > chunkEnd) return false;
            var candidate = Rsl.Raster.Read(data, absolute);
            if (!candidate.IsSane(chunkEnd - baseOffset)) return false;
            string name = Util.CString(data, node + 16, 32);
            if (name.Length < 2) return false;
            foreach (char c in name)
                if (c < 32 || c > 126) return false;
            return true;
        }

        /// <summary>
        /// Sequential rasters: a texture's pixel data ends where the next texture's
        /// data begins. The last one is bounded by the end of the chunk.
        /// </summary>
        private void AssignDataRanges(int baseOffset, int chunkEnd)
        {
            var ordered = new List<Texture>();
            foreach (Texture texture in Textures)
                if (texture.DataOffset > 0) ordered.Add(texture);
            ordered.Sort(delegate(Texture a, Texture b) { return a.DataOffset.CompareTo(b.DataOffset); });

            for (int i = 0; i < ordered.Count; i++)
            {
                Texture current = ordered[i];
                int start = baseOffset + current.DataOffset;
                int end = i + 1 < ordered.Count
                    ? baseOffset + ordered[i + 1].DataOffset
                    : chunkEnd;
                if (end <= start) end = Math.Min(chunkEnd, start + current.Width * current.Height * 4);
                current.DataLength = Math.Max(0, end - start);
            }
        }

        private Rsl.TextureFormat DetectFormat(Texture texture, int baseOffset, int chunkEnd)
        {
            Rsl.TextureFormat hint = texture.Raster == null ? Rsl.TextureFormat.Unknown : texture.Raster.GuessFormat();
            if (texture.Raster == null || texture.DataLength <= 0) return hint;

            int pixels = texture.Width * texture.Height;
            if (pixels <= 0) return Rsl.TextureFormat.Unknown;

            int length = texture.DataLength;
            long dxt1 = pixels / 2;
            long dxt5 = pixels;

            if (hint == Rsl.TextureFormat.Pal8 || hint == Rsl.TextureFormat.Pal4)
            {
                int palette = hint == Rsl.TextureFormat.Pal8 ? 1024 : 64;
                if (length >= palette)
                {
                    texture.PaletteOffset = baseOffset + texture.DataOffset + length - palette;
                    texture.PaletteEntries = palette / 4;
                }
                return hint;
            }

            if (length >= pixels * 4 * 0.9 && length <= pixels * 4 * 1.1) return Rsl.TextureFormat.Rgba8888;
            if (length >= pixels * 2 * 0.9 && length <= pixels * 2 * 1.1 + 1024)
                return hint == Rsl.TextureFormat.Unknown ? Rsl.TextureFormat.Rgb565 : hint;

            // Ambiguous half/single byte ratios: paletted or DXT. The palette size is
            // the giveaway -- retail paletted textures always carry one.
            if (Math.Abs(length - (pixels / 2 + 64)) <= 16 || Math.Abs(length - (pixels / 2 + 1024)) <= 16)
            {
                int palette = length - pixels / 2 >= 512 ? 1024 : 64;
                texture.PaletteOffset = baseOffset + texture.DataOffset + length - palette;
                texture.PaletteEntries = palette / 4;
                return Rsl.TextureFormat.Pal4;
            }
            if (Math.Abs(length - (pixels + 1024)) <= 16 || Math.Abs(length - (pixels + 64)) <= 16)
            {
                int palette = length - pixels >= 512 ? 1024 : 64;
                texture.PaletteOffset = baseOffset + texture.DataOffset + length - palette;
                texture.PaletteEntries = palette / 4;
                return Rsl.TextureFormat.Pal8;
            }
            if (length >= dxt1 * 0.9 && length <= dxt1 * 1.1) return Rsl.TextureFormat.Dxt1;
            if (length >= dxt5 * 0.9 && length <= dxt5 * 1.1) return Rsl.TextureFormat.Dxt5;

            return hint == Rsl.TextureFormat.Unknown ? Rsl.TextureFormat.Pal8 : hint;
        }

        // ------------------------------------------------------------- decode

        /// <summary>Decodes a texture into 32bpp BGRA (the order System.Drawing wants).</summary>
        public byte[] DecodeBgra(Texture texture)
        {
            if (texture.Raster == null) throw new InvalidDataException("texture has no raster");
            int width = texture.Width;
            int height = texture.Height;
            int pixels = width * height;
            var output = new byte[pixels * 4];
            int dataStart = texture.DataOffset;
            int available = Math.Min(texture.DataLength, Data.Length - dataStart);

            if (texture.Format == Rsl.TextureFormat.Pal4 || texture.Format == Rsl.TextureFormat.Pal8)
                RefinePaletteOffset(texture);

            switch (texture.Format)
            {
                case Rsl.TextureFormat.Pal4:
                case Rsl.TextureFormat.Pal8:
                    DecodePaletted(texture, output, width, height, dataStart, available);
                    break;
                case Rsl.TextureFormat.Rgba8888:
                    DecodeRgba8888(output, width, height, dataStart, available);
                    break;
                case Rsl.TextureFormat.Rgba4444:
                    Decode16(output, width, height, dataStart, available, 0x0f, 4, 0x0f, 4, 0x0f, 4, 0x0f, 4);
                    break;
                case Rsl.TextureFormat.Rgba5551:
                    Decode16(output, width, height, dataStart, available, 0x1f, 5, 0x1f, 5, 0x1f, 5, 0x01, 1);
                    break;
                case Rsl.TextureFormat.Rgb565:
                    Decode16(output, width, height, dataStart, available, 0x1f, 5, 0x3f, 6, 0x1f, 5, 0, 1);
                    break;
                case Rsl.TextureFormat.Dxt1:
                    DecodeDxt(output, width, height, dataStart, available, false);
                    break;
                case Rsl.TextureFormat.Dxt5:
                    DecodeDxt(output, width, height, dataStart, available, true);
                    break;
                default:
                    DecodePaletted(texture, output, width, height, dataStart, available);
                    break;
            }
            return output;
        }

        public Bitmap Decode(Texture texture)
        {
            int width = texture.Width;
            int height = texture.Height;
            byte[] pixels = DecodeBgra(texture);
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (locked.Stride == width * 4)
                {
                    Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(pixels, y * width * 4, (IntPtr)((long)locked.Scan0 + y * locked.Stride),
                            width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(locked);
            }
            return bitmap;
        }

        public void ExportPng(Texture texture, string outputPath)
        {
            string directory = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            using (Bitmap bitmap = Decode(texture))
                bitmap.Save(outputPath, ImageFormat.Png);
        }

        /// <summary>
        /// Texture as PNG bytes, optionally downscaled: the web UI shows a grid of
        /// hundreds of 64-96 px thumbnails, and decoding a 512x256 texture only to
        /// throw most of it away would cost both time and traffic.
        /// Nearest-neighbour on purpose - PSP art is palette based and smoothed
        /// thumbnails look like a different texture.
        /// </summary>
        public byte[] RenderPng(Texture texture, int maxWidth)
        {
            using (Bitmap bitmap = Decode(texture))
            {
                if (maxWidth <= 0 || bitmap.Width <= maxWidth)
                    return ToPng(bitmap);

                int height = Math.Max(1, (int)Math.Round(bitmap.Height * (double)maxWidth / bitmap.Width));
                using (var scaled = new Bitmap(maxWidth, height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(scaled))
                    {
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        graphics.DrawImage(bitmap, 0, 0, maxWidth, height);
                    }
                    return ToPng(scaled);
                }
            }
        }

        private static byte[] ToPng(Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        public int ExportAllPng(string outputDirectory)
        {
            int written = 0;
            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Texture texture in Textures)
            {
                string name = Util.Slug(texture.Name);
                int count;
                if (used.TryGetValue(name, out count))
                {
                    count++;
                    used[name] = count;
                    name = name + "_" + count;
                }
                else
                {
                    used[name] = 0;
                }
                Resilience.Guard("export texture " + texture.Name, delegate
                {
                    ExportPng(texture, System.IO.Path.Combine(outputDirectory, name + ".png"));
                    written++;
                });
            }
            return written;
        }

        // ------------------------------------------------------------ helpers

        private void DecodePaletted(Texture texture, byte[] output, int width, int height,
            int dataStart, int available)
        {
            int pixels = width * height;
            int bytesPerPixel = texture.Format == Rsl.TextureFormat.Pal4 ? 0 : 1;
            int tileWidth = texture.Format == Rsl.TextureFormat.Pal4 ? 32 : 16;

            byte[] palette = BuildPalette(texture);
            int paletteEntries = palette.Length / 4;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index;
                    if (texture.Format == Rsl.TextureFormat.Pal4)
                    {
                        int offset = SwizzledOffset(x, y, width, tileWidth, 0);
                        int byteIndex = offset / 2;
                        if (byteIndex >= available) continue;
                        byte b = Data[dataStart + byteIndex];
                        index = (offset & 1) == 0 ? (b & 0x0f) : (b >> 4);
                    }
                    else
                    {
                        int offset = SwizzledOffset(x, y, width, tileWidth, bytesPerPixel);
                        if (offset >= available) continue;
                        index = Data[dataStart + offset];
                    }
                    if (index >= paletteEntries) index = 0;
                    int p = (y * width + x) * 4;
                    output[p] = palette[index * 4];
                    output[p + 1] = palette[index * 4 + 1];
                    output[p + 2] = palette[index * 4 + 2];
                    output[p + 3] = palette[index * 4 + 3];
                }
            }
            if (pixels * 4 != output.Length) throw new InvalidDataException("buffer mismatch");
        }

        /// <summary>
        /// The paletted payload of a texture is followed by alignment padding, so
        /// "end of data minus palette size" is only a guess: retail model textures
        /// were off by a few dozen bytes. We search a small window and keep the
        /// offset that makes neighbouring pixels most similar, which is a very
        /// reliable signal for real image data.
        /// </summary>
        private void RefinePaletteOffset(Texture texture)
        {
            if (texture.PaletteOffset < 0 || texture.PaletteEntries <= 0) return;
            int paletteBytes = texture.PaletteEntries * 4;
            int guess = texture.PaletteOffset;
            int best = guess;
            int bestDelta = 0;
            double bestScore = double.MaxValue;
            // Trailing alignment padding sits *after* the palette, so the real
            // palette is never later than the guess. Searching only backwards also
            // makes the choice stable when the pixels change: a shifted candidate
            // can score nearly as well as the real one.
            int window = 768;

            for (int delta = -window; delta <= 0; delta += 4)
            {
                int candidate = guess + delta;
                if (candidate < texture.DataOffset) continue;
                if (candidate + paletteBytes > Data.Length) continue;

                double spread = PaletteSpread(candidate, paletteBytes);
                // A palette with no variation cannot be scored (and is not a palette).
                if (spread < 2.0) continue;

                int distinct;
                double average = ScorePalette(texture, candidate, paletteBytes, out distinct);
                // A palette that flattens the image is wrong even if it looks "smooth".
                if (distinct < 4) continue;

                // Dividing by the number of distinct colours matters: a shifted
                // palette can make the image look locally smooth (few colours, low
                // neighbour difference) and would otherwise win the search.
                double score = average / spread / distinct;
                bool better = score < bestScore * 0.99;
                bool tie = score <= bestScore * 1.01 && candidate > best;
                if (better || tie)
                {
                    bestScore = Math.Min(bestScore, score);
                    best = candidate;
                    bestDelta = delta;
                }
            }
            texture.PaletteOffset = best;
        }

        /// <summary>Mean colour distance between neighbouring palette entries.</summary>
        private double PaletteSpread(int paletteOffset, int paletteBytes)
        {
            int entries = paletteBytes / 4;
            if (entries < 2) return 0;
            long total = 0;
            for (int i = 0; i < entries - 1; i++)
            {
                int a = paletteOffset + i * 4;
                int b = a + 4;
                if (b + 3 > Data.Length) break;
                total += Math.Abs(Data[a] - Data[b]) + Math.Abs(Data[a + 1] - Data[b + 1]) +
                         Math.Abs(Data[a + 2] - Data[b + 2]);
            }
            return (double)total / (entries - 1);
        }

        private double ScorePalette(Texture texture, int paletteOffset, int paletteBytes, out int distinct)
        {
            int width = texture.Width;
            int height = texture.Height;
            int dataStart = texture.DataOffset;
            int tileWidth = texture.Format == Rsl.TextureFormat.Pal4 ? 32 : 16;
            long total = 0;
            int samples = 0;
            var seen = new HashSet<int>();

            for (int y = 0; y < height; y += 4)
            {
                int previous = -1;
                for (int x = 0; x < width; x += 4)
                {
                    int offset;
                    if (texture.Format == Rsl.TextureFormat.Pal4)
                    {
                        int o = SwizzledOffset(x, y, width, tileWidth, 0);
                        int byteIndex = dataStart + o / 2;
                        if (byteIndex >= Data.Length) continue;
                        byte b = Data[byteIndex];
                        offset = (o & 1) == 0 ? (b & 0x0f) : (b >> 4);
                    }
                    else
                    {
                        int o = SwizzledOffset(x, y, width, tileWidth, 1);
                        if (dataStart + o >= Data.Length) continue;
                        offset = Data[dataStart + o];
                    }

                    int entry = paletteOffset + offset * 4;
                    if (entry + 3 > Data.Length) continue;
                    int r = Data[entry];
                    int g = Data[entry + 1];
                    int b2 = Data[entry + 2];
                    seen.Add((r >> 3 << 10) | (g >> 3 << 5) | (b2 >> 3));
                    if (previous >= 0)
                    {
                        total += Math.Abs(r - (previous >> 16 & 0xff)) +
                                 Math.Abs(g - (previous >> 8 & 0xff)) +
                                 Math.Abs(b2 - (previous & 0xff));
                        samples++;
                    }
                    previous = (r << 16) | (g << 8) | b2;
                }
            }
            distinct = seen.Count;
            return samples == 0 ? double.MaxValue : (double)total / samples;
        }

        private byte[] BuildPalette(Texture texture)
        {
            int entries = texture.PaletteEntries > 0 ? texture.PaletteEntries
                : (texture.Format == Rsl.TextureFormat.Pal4 ? 16 : 256);
            var palette = new byte[entries * 4];
            if (texture.PaletteOffset >= 0)
            {
                for (int i = 0; i < entries; i++)
                {
                    int src = texture.PaletteOffset + i * 4;
                    if (src + 4 > Data.Length) break;
                    // RGBA in file, BGRA in output
                    palette[i * 4] = Data[src + 2];
                    palette[i * 4 + 1] = Data[src + 1];
                    palette[i * 4 + 2] = Data[src];
                    palette[i * 4 + 3] = Data[src + 3];
                }
            }
            else
            {
                // No palette (or not found): generate a grey ramp so the export is
                // still viewable instead of a black rectangle.
                for (int i = 0; i < entries; i++)
                {
                    byte v = (byte)(i * 255 / Math.Max(1, entries - 1));
                    palette[i * 4] = v;
                    palette[i * 4 + 1] = v;
                    palette[i * 4 + 2] = v;
                    palette[i * 4 + 3] = 255;
                }
            }
            return palette;
        }

        private void DecodeRgba8888(byte[] output, int width, int height, int dataStart, int available)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = SwizzledOffset(x, y, width, 4, 4);
                    if (offset + 4 > available) continue;
                    int src = dataStart + offset;
                    int p = (y * width + x) * 4;
                    output[p] = Data[src + 2];
                    output[p + 1] = Data[src + 1];
                    output[p + 2] = Data[src];
                    output[p + 3] = Data[src + 3];
                }
            }
        }

        private void Decode16(byte[] output, int width, int height, int dataStart, int available,
            int rMask, int rShift, int gMask, int gShift, int bMask, int bShift, int aMask, int aShift)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = SwizzledOffset(x, y, width, 8, 2);
                    if (offset + 2 > available) continue;
                    int src = dataStart + offset;
                    ushort value = (ushort)(Data[src] | (Data[src + 1] << 8));
                    int r = (value >> rShift) & rMask;
                    int g = (value >> gShift) & gMask;
                    int b = (value >> bShift) & bMask;
                    int a = aMask == 0 ? 255 : (value >> aShift) & aMask;
                    int p = (y * width + x) * 4;
                    output[p] = (byte)(b * 255 / Math.Max(1, bMask));
                    output[p + 1] = (byte)(g * 255 / Math.Max(1, gMask));
                    output[p + 2] = (byte)(r * 255 / Math.Max(1, rMask));
                    output[p + 3] = (byte)(aMask == 0 ? 255 : a * 255 / Math.Max(1, aMask));
                }
            }
        }

        private void DecodeDxt(byte[] output, int width, int height, int dataStart, int available, bool dxt5)
        {
            int blockSize = dxt5 ? 16 : 8;
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int offset = (by * blocksX + bx) * blockSize;
                    if (offset + blockSize > available) continue;
                    int src = dataStart + offset;
                    byte[] colors = new byte[16];
                    byte[] alphas = null;
                    if (dxt5)
                    {
                        alphas = new byte[8];
                        DecodeDxt5Alpha(alphas, Data, src);
                        src += 8;
                    }
                    DecodeColorBlock(colors, Data, src);
                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            int y = by * 4 + py;
                            if (x >= width || y >= height) continue;
                            int index = py * 4 + px;
                            int p = (y * width + x) * 4;
                            output[p] = colors[index * 4];
                            output[p + 1] = colors[index * 4 + 1];
                            output[p + 2] = colors[index * 4 + 2];
                            output[p + 3] = alphas != null ? alphas[index] : colors[index * 4 + 3];
                        }
                    }
                }
            }
        }

        private static void DecodeDxt5Alpha(byte[] alphas, byte[] data, int offset)
        {
            int a0 = data[offset];
            int a1 = data[offset + 1];
            var table = new int[8];
            table[0] = a0;
            table[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i <= 6; i++) table[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            }
            else
            {
                for (int i = 1; i <= 4; i++) table[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                table[6] = 0;
                table[7] = 255;
            }
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)data[offset + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++) alphas[i] = (byte)table[(bits >> (3 * i)) & 7];
        }

        private static void DecodeColorBlock(byte[] colors, byte[] data, int offset)
        {
            int c0 = data[offset] | (data[offset + 1] << 8);
            int c1 = data[offset + 2] | (data[offset + 3] << 8);
            var r = new int[4];
            var g = new int[4];
            var b = new int[4];
            var a = new int[] { 255, 255, 255, 255 };

            Expand565(c0, out r[0], out g[0], out b[0]);
            Expand565(c1, out r[1], out g[1], out b[1]);

            if (c0 > c1)
            {
                r[2] = (2 * r[0] + r[1]) / 3;
                g[2] = (2 * g[0] + g[1]) / 3;
                b[2] = (2 * b[0] + b[1]) / 3;
                r[3] = (r[0] + 2 * r[1]) / 3;
                g[3] = (g[0] + 2 * g[1]) / 3;
                b[3] = (b[0] + 2 * b[1]) / 3;
            }
            else
            {
                r[2] = (r[0] + r[1]) / 2;
                g[2] = (g[0] + g[1]) / 2;
                b[2] = (b[0] + b[1]) / 2;
                a[2] = 255;
                r[3] = 0;
                g[3] = 0;
                b[3] = 0;
                a[3] = 0;
            }

            uint indices = Util.U32(data, offset + 4);
            for (int i = 0; i < 16; i++)
            {
                int index = (int)((indices >> (2 * i)) & 3);
                colors[i * 4] = (byte)b[index];
                colors[i * 4 + 1] = (byte)g[index];
                colors[i * 4 + 2] = (byte)r[index];
                colors[i * 4 + 3] = (byte)a[index];
            }
        }

        private static void Expand565(int value, out int r, out int g, out int b)
        {
            r = ((value >> 11) & 0x1f) * 255 / 31;
            g = ((value >> 5) & 0x3f) * 255 / 63;
            b = (value & 0x1f) * 255 / 31;
        }

        /// <summary>
        /// PSP tiled layout: the image is cut into 128 byte tiles, each tile holds
        /// 8 rows of 16 bytes; tiles themselves are stored row major.
        /// </summary>
        public static int SwizzledOffset(int x, int y, int width, int tileWidth, int bytesPerPixel)
        {
            int tilesX = Math.Max(1, (width + tileWidth - 1) / tileWidth);
            int tile = (y / 8) * tilesX + (x / tileWidth);
            int inTile = (y % 8) * 16 + (x % tileWidth) * Math.Max(1, bytesPerPixel);
            return tile * 128 + inTile;
        }

        // ------------------------------------------------------------- encode

        /// <summary>
        /// Replaces the pixels of one texture with the contents of an image and
        /// writes them back in the on-disc layout (palette + swizzle + mip chain).
        ///
        /// Palette policy:
        ///   * if every colour of the new image already exists in the current
        ///     palette, the palette bytes are left untouched (byte exact round trip
        ///     for "export, tweak, import" workflows);
        ///   * otherwise a new palette is built from the most frequent colours and
        ///     pixels are mapped to the nearest entry.
        ///
        /// Mip levels are re-generated with a box filter so distant geometry does
        /// not show the old artwork.
        /// </summary>
        public void ReplacePixels(Texture texture, Bitmap image)
        {
            if (texture == null) throw new ArgumentNullException("texture");
            if (texture.Raster == null) throw new InvalidOperationException("texture has no raster");
            if (image == null) throw new ArgumentNullException("image");

            if (texture.Format != Rsl.TextureFormat.Pal4 && texture.Format != Rsl.TextureFormat.Pal8)
                throw new NotSupportedException("only paletted textures can be written back (this one is " +
                                                Rsl.FormatName(texture.Format) + ")");

            int width = texture.Width;
            int height = texture.Height;
            if (image.Width != width || image.Height != height)
                throw new InvalidDataException("image is " + image.Width + "x" + image.Height +
                                               ", texture is " + width + "x" + height);

            int paletteBytes = texture.PaletteEntries * 4;
            if (texture.PaletteOffset <= 0 || texture.PaletteOffset + paletteBytes > Data.Length)
                throw new InvalidOperationException("palette location for this texture is unknown");

            // the "end of data minus palette size" guess is a few dozen bytes off on
            // retail files - locate the real palette before touching it
            RefinePaletteOffset(texture);

            byte[] pixels = ReadBgra(image, width, height);
            int[] indices = MapToPalette(pixels, width, height, texture, paletteBytes);
            WritePaletted(texture, indices, width, height);
            WriteMips(texture, indices, width, height);
        }

        private static byte[] ReadBgra(Bitmap image, int width, int height)
        {
            var pixels = new byte[width * height * 4];
            BitmapData locked = image.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy((IntPtr)((long)locked.Scan0 + y * locked.Stride), row, 0, row.Length);
                    Buffer.BlockCopy(row, 0, pixels, y * width * 4, row.Length);
                }
            }
            finally
            {
                image.UnlockBits(locked);
            }
            return pixels;
        }

        /// <summary>
        /// Maps every pixel to a palette index, rebuilding the palette only when the
        /// current one really cannot express the image.
        ///
        /// Two details matter for a byte exact round trip:
        ///   * PNG encoders are allowed to clear the RGB of fully transparent
        ///     pixels, so an A=0 pixel is matched to any transparent palette entry
        ///     instead of demanding an exact colour;
        ///   * palette entries that repeat the same colour are kept as they are, the
        ///     first one wins (the game does not care which duplicate is used).
        /// </summary>
        private int[] MapToPalette(byte[] pixels, int width, int height, Texture texture, int paletteBytes)
        {
            int entries = paletteBytes / 4;
            int count = width * height;
            var indices = new int[count];

            var exact = new Dictionary<int, int>();   // rgb -> index (opaque entries)
            int transparent = -1;
            for (int i = 0; i < entries; i++)
            {
                int at = texture.PaletteOffset + i * 4;
                if (Data[at + 3] == 0)
                {
                    if (transparent < 0) transparent = i;
                    continue;
                }
                // key order follows the decoded pixels: B | G<<8 | R<<16
                int key = Data[at + 2] | (Data[at + 1] << 8) | (Data[at] << 16);
                if (!exact.ContainsKey(key)) exact[key] = i;
            }

            bool covered = true;
            for (int i = 0; i < count && covered; i++)
            {
                if (pixels[i * 4 + 3] == 0) continue;
                int key = pixels[i * 4] | (pixels[i * 4 + 1] << 8) | (pixels[i * 4 + 2] << 16);
                if (!exact.ContainsKey(key)) covered = false;
            }

            if (covered)
            {
                for (int i = 0; i < count; i++)
                {
                    if (pixels[i * 4 + 3] == 0)
                    {
                        indices[i] = transparent >= 0 ? transparent : 0;
                        continue;
                    }
                    int key = pixels[i * 4] | (pixels[i * 4 + 1] << 8) | (pixels[i * 4 + 2] << 16);
                    indices[i] = exact[key];
                }
                return indices;
            }

            int missingCount = 0;
            var missingSample = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (pixels[i * 4 + 3] == 0) continue;
                int key = pixels[i * 4] | (pixels[i * 4 + 1] << 8) | (pixels[i * 4 + 2] << 16);
                if (exact.ContainsKey(key)) continue;
                missingCount++;
                if (missingSample.Length < 120)
                    missingSample.AppendFormat(" b={0} g={1} r={2} a={3};",
                        pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]);
            }
            Log.Warn(Cat.Fmt, "palette rebuild: " + missingCount + " pixels out of palette, sample:" +
                              missingSample);

            // build a palette from the most frequent colours (rgb + alpha)
            var histogram = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int key = (pixels[i * 4] | (pixels[i * 4 + 1] << 8) | (pixels[i * 4 + 2] << 16)) |
                          (pixels[i * 4 + 3] << 24);
                int value;
                histogram.TryGetValue(key, out value);
                histogram[key] = value + 1;
            }

            var ordered = new List<KeyValuePair<int, int>>(histogram);
            ordered.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            var paletteKeys = new List<int>();
            for (int i = 0; i < ordered.Count && paletteKeys.Count < entries; i++)
                paletteKeys.Add(ordered[i].Key);

            for (int i = 0; i < entries; i++)
            {
                int at = texture.PaletteOffset + i * 4;
                int key = i < paletteKeys.Count ? paletteKeys[i] : 0;
                Data[at] = (byte)((key >> 16) & 0xff);      // R
                Data[at + 1] = (byte)((key >> 8) & 0xff);   // G
                Data[at + 2] = (byte)(key & 0xff);          // B
                Data[at + 3] = i < paletteKeys.Count ? (byte)((key >> 24) & 0xff) : (byte)255;
            }

            var lookup = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int key = (pixels[i * 4] | (pixels[i * 4 + 1] << 8) | (pixels[i * 4 + 2] << 16)) |
                          (pixels[i * 4 + 3] << 24);
                int index;
                if (!lookup.TryGetValue(key, out index))
                {
                    index = NearestIndex(key & 0xffffff, paletteKeys);
                    lookup[key] = index;
                }
                indices[i] = index;
            }
            return indices;
        }

        private static int NearestIndex(int colour, List<int> paletteKeys)
        {
            int b = colour & 0xff, g = (colour >> 8) & 0xff, r = (colour >> 16) & 0xff;
            int best = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < paletteKeys.Count; i++)
            {
                int key = paletteKeys[i];
                int dr = ((key >> 16) & 0xff) - r;
                int dg = ((key >> 8) & 0xff) - g;
                int db = (key & 0xff) - b;
                int distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                    if (distance == 0) break;
                }
            }
            return best;
        }

        private void WritePaletted(Texture texture, int[] indices, int width, int height)
        {
            bool fourBit = texture.Format == Rsl.TextureFormat.Pal4;
            int tileWidth = fourBit ? 32 : 16;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = SwizzledOffset(x, y, width, tileWidth, fourBit ? 0 : 1);
                    int value = indices[y * width + x];
                    int at = texture.DataOffset + (fourBit ? offset / 2 : offset);
                    if (at < 0 || at >= Data.Length) continue;
                    if (fourBit)
                    {
                        // low nibble = even offset, high nibble = odd offset
                        if ((offset & 1) == 0) Data[at] = (byte)((Data[at] & 0xf0) | (value & 0x0f));
                        else Data[at] = (byte)((Data[at] & 0x0f) | ((value & 0x0f) << 4));
                    }
                    else
                    {
                        Data[at] = (byte)value;
                    }
                }
            }
        }

        /// <summary>
        /// Rebuilds the mip chain (box filter, 2x2) inside the space the original
        /// texture used, so the game does not keep showing the previous artwork at
        /// distance.
        /// </summary>
        private void WriteMips(Texture texture, int[] indices, int width, int height)
        {
            // Only 8bpp is verified: its mip chain is a sequence of swizzled levels
            // right after the base image (5461 bytes for a 128x128 texture, which is
            // exactly 64^2+32^2+... ). For 4bpp we keep the original chain and say so,
            // because nibble packing inside the chain is not confirmed on retail data.
            if (texture.Format != Rsl.TextureFormat.Pal8)
            {
                Log.Warn(Cat.Fmt, "mip chain of " + texture.Name + " left untouched (only pal8 mips are verified)");
                return;
            }

            int baseBytes = texture.Width * texture.Height;
            int limit = texture.DataLength - texture.PaletteEntries * 4;

            // Many textures simply have no mip chain: the palette sits right after
            // the base level. Writing "mips" there would shred the palette, so check
            // that the first level fits before touching anything.
            if (limit - baseBytes < baseBytes / 4)
            {
                Log.Debug(Cat.Fmt, "texture " + texture.Name + " has no mip chain, palette kept");
                return;
            }

            // Mip levels are generated in colour space (average the RGB of a 2x2
            // block, then map back to the palette). Sampling index values directly
            // shifts hues badly, because palette indices carry no colour order.
            int entries = texture.PaletteEntries;
            var paletteRgb = new int[entries];
            for (int i = 0; i < entries; i++)
            {
                int at = texture.PaletteOffset + i * 4;
                paletteRgb[i] = Data[at + 2] | (Data[at + 1] << 8) | (Data[at] << 16);
            }
            var nearestCache = new Dictionary<int, int>();

            int current = width;
            int currentHeight = height;
            int offset = baseBytes;
            var source = indices;

            while (current > 1 && currentHeight > 1)
            {
                int nextWidth = current / 2;
                int nextHeight = currentHeight / 2;
                if (offset + nextWidth * nextHeight > limit) break;

                var next = new int[nextWidth * nextHeight];
                for (int y = 0; y < nextHeight; y++)
                {
                    for (int x = 0; x < nextWidth; x++)
                    {
                        int r = 0, g = 0, b = 0;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int index = source[(y * 2 + dy) * current + (x * 2 + dx)];
                                int colour = paletteRgb[index < entries ? index : 0];
                                b += colour & 0xff;
                                g += (colour >> 8) & 0xff;
                                r += (colour >> 16) & 0xff;
                            }
                        }
                        int average = ((b / 4) & 0xff) | (((g / 4) & 0xff) << 8) | (((r / 4) & 0xff) << 16);
                        int mapped;
                        if (!nearestCache.TryGetValue(average, out mapped))
                        {
                            mapped = NearestIndex(average, paletteRgb, entries);
                            nearestCache[average] = mapped;
                        }
                        next[y * nextWidth + x] = mapped;
                    }
                }

                for (int y = 0; y < nextHeight; y++)
                {
                    for (int x = 0; x < nextWidth; x++)
                    {
                        int swizzled = SwizzledOffset(x, y, nextWidth, 16, 1);
                        int at = texture.DataOffset + offset + swizzled;
                        if (at >= 0 && at < Data.Length && offset + swizzled < limit)
                            Data[at] = (byte)next[y * nextWidth + x];
                    }
                }

                offset += nextWidth * nextHeight;
                source = next;
                current = nextWidth;
                currentHeight = nextHeight;
            }
        }

        /// <summary>Nearest palette entry for a packed BGR colour.</summary>
        private static int NearestIndex(int colour, int[] paletteRgb, int entries)
        {
            int b = colour & 0xff, g = (colour >> 8) & 0xff, r = (colour >> 16) & 0xff;
            int best = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < entries; i++)
            {
                int key = paletteRgb[i];
                int dr = ((key >> 16) & 0xff) - r;
                int dg = ((key >> 8) & 0xff) - g;
                int db = (key & 0xff) - b;
                int distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                    if (distance == 0) break;
                }
            }
            return best;
        }

        private int BaseLevelBytes(Texture texture)
        {
            return texture.Width * texture.Height;
        }

        /// <summary>Writes the modified chunk back to disk (atomic).</summary>
        public void Save(string path)
        {
            Util.WriteAllBytesAtomic(path, Data);
        }
    }
}
