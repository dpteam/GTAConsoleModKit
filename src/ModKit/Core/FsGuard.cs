using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ModKit.Core
{
    /// <summary>
    /// The single place that decides whether the web UI may touch a path.
    ///
    /// Containment is checked twice: by normalised string prefix first, then by
    /// file identity (volume serial + file index). The second pass is what makes a
    /// mapped drive, a junction, a subst drive or an 8.3 name count as "inside":
    /// T:\games\LCS and \\server\share\games\LCS are the same folder, so a plain
    /// string comparison would reject a perfectly good path.
    ///
    /// Why a sandbox at all: the server has no authentication and may listen on
    /// the LAN, so "read any file on the disk" would let anyone on the network
    /// pull C:\Windows into a project. Roots are therefore explicit, and every
    /// rejection says which roots are allowed.
    /// </summary>
    public static class FsGuard
    {
        private static readonly List<string> _roots = new List<string>();
        private static readonly object _sync = new object();

        /// <summary>Roots the web UI may read from, in configured order.</summary>
        public static IList<string> Roots
        {
            get { lock (_sync) { return new List<string>(_roots).AsReadOnly(); } }
        }

        public static void Configure(IEnumerable<string> roots)
        {
            lock (_sync)
            {
                _roots.Clear();
                if (roots == null) return;
                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string full = Canonical(root);
                    if (full == null) continue;
                    bool known = false;
                    for (int i = 0; i < _roots.Count; i++)
                    {
                        if (string.Equals(_roots[i], full, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                    }
                    if (!known) _roots.Add(full);
                }
            }
        }

        /// <summary>Human readable list for error messages.</summary>
        public static string Describe()
        {
            IList<string> roots = Roots;
            if (roots.Count == 0) return "(no roots configured)";
            var text = new StringBuilder();
            for (int i = 0; i < roots.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(roots[i]);
            }
            return text.ToString();
        }

        /// <summary>
        /// Absolute path, resolved through the file system when it exists (that is
        /// what turns a mapped drive into its real target). Null when the input
        /// cannot be turned into a path at all.
        /// </summary>
        public static string Canonical(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return null; }

            string resolved = ResolveFinalPath(full);
            if (resolved != null) full = resolved;
            return TrimSeparators(full);
        }

        /// <summary>Canonical path when it is inside one of the roots, otherwise null.</summary>
        public static string Allow(string path)
        {
            string full = Canonical(path);
            if (full == null) return null;
            return IsInsideAny(full) ? full : null;
        }

        public static bool IsInsideAny(string path)
        {
            IList<string> roots = Roots;
            for (int i = 0; i < roots.Count; i++)
            {
                if (IsInside(path, roots[i])) return true;
            }
            return false;
        }

        public static bool IsInside(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string fullPath = TrimSeparators(Path.GetFullPath(path));
            string fullRoot = TrimSeparators(Path.GetFullPath(root));

            if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
            if (fullPath.Length > fullRoot.Length &&
                fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                return true;

            // Different spellings of the same place (mapped drive, junction, share).
            FileId? rootId = TryIdentify(fullRoot);
            if (rootId == null) return false;

            string current = fullPath;
            for (int depth = 0; depth < 64 && !string.IsNullOrEmpty(current); depth++)
            {
                FileId? id = TryIdentify(current);
                if (id != null && id.Equals(rootId)) return true;
                if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)) break;

                string parent = null;
                try
                {
                    DirectoryInfo info = Directory.GetParent(current);
                    parent = info == null ? null : info.FullName;
                }
                catch (Exception) { parent = null; }
                if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            return false;
        }

        private static string TrimSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.Length <= 3) return path;                    // "C:\" or "/"
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // ------------------------------------------------------- windows bits

        private struct FileId
        {
            public uint Volume;
            public ulong Index;

            public override bool Equals(object obj)
            {
                if (!(obj is FileId)) return false;
                FileId other = (FileId)obj;
                return Volume == other.Volume && Index == other.Index;
            }

            public override int GetHashCode()
            {
                return (int)(Volume ^ (uint)Index);
            }
        }

        private static FileId? TryIdentify(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            IntPtr handle = OpenForInfo(path);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
            try
            {
                BY_HANDLE_FILE_INFORMATION info;
                if (!GetFileInformationByHandle(handle, out info)) return null;

                // A share that reports volume 0 / index 0 gives every file the same
                // identity; comparing it would say "yes" to unrelated paths.
                if (info.VolumeSerialNumber == 0 && info.FileIndexHigh == 0 && info.FileIndexLow == 0)
                    return null;

                var id = new FileId
                {
                    Volume = info.VolumeSerialNumber,
                    Index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow
                };
                return id;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string ResolveFinalPath(string path)
        {
            IntPtr handle = OpenForInfo(path);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length > buffer.Capacity) return null;

                string text = buffer.ToString();
                if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    return @"\\" + text.Substring(8);
                if (text.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    return text.Substring(4);
                return text;
            }
            catch (EntryPointNotFoundException)
            {
                return null;                    // Windows XP: no such API, string rules only
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static IntPtr OpenForInfo(string path)
        {
            // FILE_FLAG_BACKUP_SEMANTICS is what lets us open a directory.
            const uint share = 0x00000007;      // read | write | delete
            const uint openExisting = 3;
            const uint backupSemantics = 0x02000000;

            try
            {
                IntPtr handle = CreateFile(path, 0, share, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
                if (handle != new IntPtr(-1)) return handle;

                handle = CreateFile(path, 0x80000000, share, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
                return handle;
            }
            catch (Exception)
            {
                return new IntPtr(-1);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public FILETIME CreationTime;
            public FILETIME LastAccessTime;
            public FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr handle,
            out BY_HANDLE_FILE_INFORMATION information);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder path,
            uint pathLength, uint flags);
    }
}