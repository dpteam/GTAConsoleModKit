using System;
using System.IO;

namespace ModKit.Core
{
    /// <summary>Everything the front ends need, wired once in Program.</summary>
    public sealed class ModKitContext
    {
        public Config Config;
        public Workspace Workspace;
        public Pipeline Pipeline;
        public string BaseDirectory;
        public string PublicDirectory;
        public string DataDirectory;
        public string LogsDirectory;
        public string WorkspaceRoot;
        public string GamesRoot;
        public string Token;
        public bool SessionOnly;
        public string Version;
        public DateTime StartedAt;

        public string ModelNameFile(string game)
        {
            string name = string.Equals(game, "VCS", StringComparison.OrdinalIgnoreCase)
                ? "modelnames.vcs.txt"
                : "modelnames.lcs.txt";
            return Path.Combine(DataDirectory, name);
        }
    }
}
