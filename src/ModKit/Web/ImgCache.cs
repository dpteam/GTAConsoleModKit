using System;
using System.Collections.Generic;
using System.IO;
using ModKit.Core;
using ModKit.Formats;

namespace ModKit.Web
{
    /// <summary>
    /// Small cache for the texture gallery.
    ///
    /// Opening an IMG costs a full scan of the file (a bare GTA3PSPHR.IMG has no
    /// .DIR, so the entries are found by scanning chunk headers), and building the
    /// texture index parses all 582 dictionaries - a few seconds. A gallery then
    /// asks for one PNG per tile, so without a cache every thumbnail would rescan
    /// 38 MB. One slot is enough: the UI looks at one image at a time.
    /// </summary>
    public static class ImgCache
    {
        private static readonly object _sync = new object();
        private static string _key;
        private static ImgArchive _archive;
        private static Dictionary<string, object> _index;

        private static string Key(string path)
        {
            var info = new FileInfo(path);
            return path.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }

        /// <summary>Open archive for a file, reused while the file does not change.</summary>
        public static ImgArchive Archive(string path)
        {
            string key = Key(path);
            lock (_sync)
            {
                if (_archive != null && _key == key) return _archive;
            }

            ImgArchive archive = ImgArchive.Open(path, null);

            lock (_sync)
            {
                _key = key;
                _archive = archive;
                _index = null;                 // a different file invalidates the index too
            }
            return archive;
        }

        /// <summary>Texture list (what <c>tex-index</c> prints) for the same file.</summary>
        public static Dictionary<string, object> Index(string path)
        {
            string key = Key(path);
            lock (_sync)
            {
                if (_index != null && _key == key) return _index;
            }

            ImgArchive archive = Archive(path);
            Operations.Outcome outcome = Operations.TexIndex(archive, path);
            if (!outcome.Ok) return null;

            lock (_sync)
            {
                if (_key == key) _index = outcome.Data;
            }
            return outcome.Data;
        }

        public static void Forget()
        {
            lock (_sync)
            {
                _key = null;
                _archive = null;
                _index = null;
            }
        }
    }
}