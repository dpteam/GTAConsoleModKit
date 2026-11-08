using System;
using System.IO;
using System.Text;
using ModKit.Core;

namespace ModKit.Formats
{
    /// <summary>
    /// PlayStation VAG (PS-ADPCM) audio, used by PSP SFX sets and many streams.
    ///
    ///   "VAGp" | version(4) | dataSize(4) | sampleRate(4) | ... name ...
    ///   frames of 16 bytes: [shift|filter] [flags] 14 bytes = 28 samples
    ///
    /// Decoding gives 16 bit mono PCM; encoding uses the standard shift search
    /// with filter 0, which is what the in-game sets use anyway.
    /// </summary>
    public static class Vag
    {
        /// <summary>Header size of a Sony VAG / a RAW bank record.</summary>
        public const int HeaderSize = 48;

        private static void WriteBigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
        public sealed class Info
        {
            public int Version;
            public int DataSize;
            public int SampleRate;
            public string Name;
            public bool IsVag;
        }

        public static Info ReadInfo(byte[] data)
        {
            var info = new Info();
            if (data.Length < 16) return info;
            string magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic != "VAGp") return info;
            info.IsVag = true;
            info.Version = (int)Util.U32BE(data, 4);
            // Sony layout: magic, version, reserved, dataSize, sampleRate, ... name.
            // Reading the size at +8 (one field early) made every RAW bank decode as
            // "2880 Hz" silence - the size belongs at +12, the rate at +16.
            info.DataSize = (int)Util.U32BE(data, 12);
            info.SampleRate = (int)Util.U32BE(data, 16);
            int nameLength = Math.Min(16, Math.Max(0, data.Length - 32));
            if (nameLength > 0) info.Name = Util.CString(data, 32, nameLength);
            if (info.DataSize <= 0 || info.DataSize > data.Length - 48)
                info.DataSize = Math.Max(0, data.Length - 48);
            if (info.SampleRate <= 0 || info.SampleRate > 192000) info.SampleRate = 22050;
            return info;
        }

        public static bool IsVag(byte[] data)
        {
            return data.Length >= 16 && Encoding.ASCII.GetString(data, 0, 4) == "VAGp";
        }

        public static short[] Decode(byte[] data, int dataOffset, int dataLength)
        {
            var samples = new short[dataLength / 16 * 28];
            int sampleIndex = 0;

            // The predictor history is the last two decoded samples, full stop.
            // Keeping one pair per filter index (as an earlier version did) drifts
            // as soon as two consecutive frames use different filters: the
            // prediction then leans on stale samples from another frame.
            int h1 = 0;
            int h2 = 0;
            int[][] coefs =
            {
                new[] { 0, 0 },
                new[] { 60, 0 },
                new[] { 115, -52 },
                new[] { 98, -55 },
                new[] { 122, -60 }
            };

            int end = Math.Min(data.Length, dataOffset + dataLength);
            for (int offset = dataOffset; offset + 16 <= end; offset += 16)
            {
                byte header = data[offset];
                int shift = header & 0x0f;
                int filter = (header >> 4) & 0x0f;
                if (filter > 4) filter = 0;

                for (int i = 0; i < 14; i++)
                {
                    int b = data[offset + 2 + i];
                    for (int nibble = 0; nibble < 2; nibble++)
                    {
                        int value = nibble == 0 ? (b & 0x0f) : (b >> 4);
                        if (value >= 8) value -= 16;
                        int sample = (value << 12) >> shift;
                        sample += (h1 * coefs[filter][0] + h2 * coefs[filter][1]) / 64;
                        if (sample > 32767) sample = 32767;
                        if (sample < -32768) sample = -32768;
                        h2 = h1;
                        h1 = sample;
                        if (sampleIndex < samples.Length) samples[sampleIndex++] = (short)sample;
                    }
                }
            }

            if (sampleIndex == samples.Length) return samples;
            var trimmed = new short[sampleIndex];
            Array.Copy(samples, trimmed, sampleIndex);
            return trimmed;
        }

        public static byte[] ToWav(short[] samples, int sampleRate)
        {
            int dataBytes = samples.Length * 2;
            using (var output = new MemoryStream(44 + dataBytes))
            {
                var writer = new BinaryWriter(output);
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);
                foreach (short sample in samples) writer.Write(sample);
                writer.Flush();
                return output.ToArray();
            }
        }

        /// <summary>Converts a VAG file (or a raw PS-ADPCM blob) to PCM WAV.</summary>
        public static byte[] ConvertToWav(string path, out int sampleRate)
        {
            byte[] data = File.ReadAllBytes(path);
            Info info = ReadInfo(data);
            sampleRate = info.IsVag ? info.SampleRate : 22050;
            int offset = info.IsVag ? 48 : 0;
            int length = info.IsVag ? Math.Min(info.DataSize, data.Length - offset) : data.Length - offset;
            if (length <= 0)
            {
                offset = 0;
                length = data.Length;
            }
            return ToWav(Decode(data, offset, length), sampleRate);
        }

        /// <summary>Reads a PCM WAV (16 bit mono/stereo) and writes a VAG.</summary>
        public static byte[] FromWav(byte[] wav, string name)
        {
            int sampleRate;
            short[] samples = ReadWav(wav, out sampleRate);
            return Encode(samples, sampleRate, name);
        }

        private static short[] ReadWav(byte[] data, out int sampleRate)
        {
            sampleRate = 22050;
            if (data.Length < 44) throw new InvalidDataException("not a WAV file");
            if (Encoding.ASCII.GetString(data, 0, 4) != "RIFF")
                throw new InvalidDataException("not a WAV file (no RIFF)");

            int channels = Util.U16(data, 22);
            sampleRate = (int)Util.U32(data, 24);
            int bits = Util.U16(data, 34);
            if (bits != 16) throw new InvalidDataException("only 16 bit PCM WAV is supported");

            int search = 12;
            int dataOffset = -1;
            int dataLength = 0;
            while (search + 8 <= data.Length)
            {
                string id = Encoding.ASCII.GetString(data, search, 4);
                int size = (int)Util.U32(data, search + 4);
                if (id == "data")
                {
                    dataOffset = search + 8;
                    dataLength = Math.Min(size, data.Length - dataOffset);
                    break;
                }
                search += 8 + size + (size & 1);
            }
            if (dataOffset < 0) throw new InvalidDataException("WAV has no data chunk");

            int frames = dataLength / 2 / channels;
            var samples = new short[frames];
            for (int i = 0; i < frames; i++)
            {
                int value = (short)Util.U16(data, dataOffset + i * channels * 2);
                if (channels > 1)
                {
                    // simple downmix, modding SFX are mono anyway
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += (short)Util.U16(data, dataOffset + (i * channels + c) * 2);
                    value = (short)(sum / channels);
                }
                samples[i] = (short)value;
            }
            return samples;
        }

        public static byte[] Encode(short[] samples, int sampleRate, string name)
        {
            int frames = (samples.Length + 27) / 28;
            int dataSize = frames * 16;
            using (var output = new MemoryStream(48 + dataSize))
            {
                // 48-byte Sony header, all integers big-endian: magic, version,
                // reserved, dataSize, sampleRate, three reserved words, name[16].
                // BinaryWriter writes little-endian, which produced files that the
                // game (and our own reader) could not decode.
                var header = new byte[HeaderSize];
                header[0] = (byte)'V';
                header[1] = (byte)'A';
                header[2] = (byte)'G';
                header[3] = (byte)'p';
                WriteBigEndian(header, 4, 0x00000020);
                WriteBigEndian(header, 8, 0);
                WriteBigEndian(header, 12, dataSize);
                WriteBigEndian(header, 16, sampleRate);
                if (!string.IsNullOrEmpty(name))
                {
                    byte[] raw = Encoding.ASCII.GetBytes(name);
                    Buffer.BlockCopy(raw, 0, header, HeaderSize - 16, Math.Min(16, raw.Length));
                }

                var writer = new BinaryWriter(output);
                writer.Write(header);

                for (int frame = 0; frame < frames; frame++)
                {
                    int baseIndex = frame * 28;
                    int maxValue = 0;
                    var nibbles = new int[28];
                    for (int i = 0; i < 28; i++)
                    {
                        int index = baseIndex + i;
                        short sample = index < samples.Length ? samples[index] : (short)0;
                        // remove the predictor (filter 0 -> no history)
                        int value = sample;
                        nibbles[i] = value;
                        int abs = Math.Abs(value);
                        if (abs > maxValue) maxValue = abs;
                    }

                    // Pick the FINEST step that still fits 3 bits of magnitude: the decoder
                    // computes (nibble << 12) >> shift, so one step is 4096 >> shift and
                    // seven steps must cover the peak. The old loop started at shift 0
                    // and stopped immediately (4096 >> 0 already "covers" 3000), which
                    // quantised a 3000-amplitude signal down to zeros and -1s.
                    int shift = 0;
                    if (maxValue > 0)
                    {
                        while (shift < 12 && 7L * (4096 >> (shift + 1)) >= maxValue) shift++;
                    }

                    var payload = new byte[16];
                    payload[0] = (byte)shift;
                    payload[1] = (byte)(frame == frames - 1 ? 7 : 0);
                    for (int i = 0; i < 14; i++)
                    {
                        int low = Quantize(nibbles[i * 2], shift);
                        int high = Quantize(nibbles[i * 2 + 1], shift);
                        payload[2 + i] = (byte)((low & 0x0f) | ((high & 0x0f) << 4));
                    }
                    writer.Write(payload);
                }
                writer.Flush();
                return output.ToArray();
            }
        }

        private static int Quantize(int value, int shift)
        {
            int scaled = value >> (12 - shift);
            if (scaled > 7) scaled = 7;
            if (scaled < -8) scaled = -8;
            return scaled & 0x0f;
        }
    }
}
