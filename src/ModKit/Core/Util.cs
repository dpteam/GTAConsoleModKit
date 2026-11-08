using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ModKit.Core
{
    /// <summary>Small helpers shared by formats, pipeline and web layer.</summary>
    public static class Util
    {
        public static ushort U16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        public static uint U32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        public static int I32(byte[] data, int offset)
        {
            return (int)U32(data, offset);
        }

        public static uint U32BE(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                          (data[offset + 2] << 8) | data[offset + 3]);
        }

        public static void PutU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value & 0xff);
            data[offset + 1] = (byte)((value >> 8) & 0xff);
            data[offset + 2] = (byte)((value >> 16) & 0xff);
            data[offset + 3] = (byte)((value >> 24) & 0xff);
        }

        public static void PutU16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)(value & 0xff);
            data[offset + 1] = (byte)((value >> 8) & 0xff);
        }

        public static string Hex(byte[] data, int offset, int count)
        {
            if (data == null) return "";
            if (offset < 0) offset = 0;
            if (offset > data.Length) offset = data.Length;
            if (offset + count > data.Length) count = data.Length - offset;
            var sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[offset + i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>C-string (NUL terminated) out of a fixed size field.</summary>
        public static string CString(byte[] data, int offset, int maxLength)
        {
            if (offset < 0 || offset >= data.Length) return "";
            int end = offset;
            int limit = Math.Min(data.Length, offset + maxLength);
            while (end < limit && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        public static string Ident(byte[] data, int offset)
        {
            // Stories files store the 4CC byte reversed relative to the macro form
            // ("ldm\0" on disk is the 'mdl' chunk). We normalise to what a human
            // would type: strip NULs, keep the ASCII order found on disk.
            var sb = new StringBuilder(4);
            for (int i = 0; i < 4; i++)
            {
                byte b = data[offset + i];
                if (b == 0) break;
                if (b >= 32 && b < 127) sb.Append((char)b);
                else sb.Append('?');
            }
            string s = sb.ToString();
            char[] chars = s.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        public static string HumanBytes(long bytes)
        {
            if (bytes < 0) return "-" + HumanBytes(-bytes);
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double value = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            foreach (string unit in units)
            {
                value /= 1024.0;
                if (value < 1024.0)
                    return value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + unit;
            }
            return value.ToString("0", CultureInfo.InvariantCulture) + " PB";
        }

        public static string HumanDuration(TimeSpan span)
        {
            if (span.TotalSeconds < 1) return (int)span.TotalMilliseconds + " ms";
            if (span.TotalSeconds < 60) return span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            if (span.TotalMinutes < 60)
                return ((int)span.TotalMinutes) + " min " + span.Seconds + " s";
            return ((int)span.TotalHours) + " h " + span.Minutes + " min";
        }

        /// <summary>Writes a file atomically: temp file in the same folder, then rename.</summary>
        public static void WriteAllBytesAtomic(string path, byte[] data)
        {
            string temp = path + ".part";
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(temp, data);
            if (File.Exists(path))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch (Exception) { }
                File.Delete(path);
            }
            File.Move(temp, path);
        }

        public static void WriteAllTextAtomic(string path, string text)
        {
            WriteAllBytesAtomic(path, new UTF8Encoding(false).GetBytes(text));
        }

        /// <summary>
        /// Turns user input into a safe relative path inside a root folder.
        /// Returns null when the path escapes the root (path traversal).
        /// </summary>
        public static string SafeCombine(string root, string relative)
        {
            if (root == null) return null;
            if (relative == null) relative = "";
            relative = relative.Replace('\\', '/').TrimStart('/');
            if (relative.IndexOf(':') >= 0) return null;
            string combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                fullRoot += Path.DirectorySeparatorChar;
            if (combined.Length < fullRoot.Length) return null;
            if (string.Compare(combined.Substring(0, fullRoot.Length), fullRoot,
                    StringComparison.OrdinalIgnoreCase) != 0) return null;
            return combined;
        }

        public static string ToRelative(string root, string fullPath)
        {
            string fullRoot = Path.GetFullPath(root);
            string full = Path.GetFullPath(fullPath);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                fullRoot += Path.DirectorySeparatorChar;
            if (full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return full.Substring(fullRoot.Length).Replace('\\', '/');
            return full.Replace('\\', '/');
        }

        public static ulong Fnv1a64(string text)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= (byte)text[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }

        public static string RandomCode(Random random, string alphabet, int length)
        {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) sb.Append(alphabet[random.Next(alphabet.Length)]);
            return sb.ToString();
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string ShortHash(byte[] data)
        {
            string full = Sha256Hex(data);
            return full.Substring(0, 16);
        }

        public static long FileSize(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public static void CopyStream(Stream input, Stream output, long length, Action<long> progress)
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            while (done < length)
            {
                int want = (int)Math.Min(buffer.Length, length - done);
                int read = input.Read(buffer, 0, want);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                done += read;
                if (progress != null) progress(done);
            }
            if (done != length)
                throw new IOException("short read: got " + done + " of " + length + " bytes");
        }

        public static byte[] ReadExactly(Stream stream, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) throw new IOException("unexpected end of file (wanted " + count + " bytes)");
                read += n;
            }
            return buffer;
        }

        public static IEnumerable<string> SplitLines(string text)
        {
            if (text == null) yield break;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null) yield return line;
            }
        }

        public static string JoinNonEmpty(string separator, params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (sb.Length > 0) sb.Append(separator);
                sb.Append(part);
            }
            return sb.ToString();
        }

        public static string Slug(string text)
        {
            if (string.IsNullOrEmpty(text)) return "item";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_' || c == '.') sb.Append('_');
            }
            string result = sb.ToString().Trim('_');
            while (result.IndexOf("__", StringComparison.Ordinal) >= 0)
                result = result.Replace("__", "_");
            return result.Length == 0 ? "item" : result;
        }
    }
}
