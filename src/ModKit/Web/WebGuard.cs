using System;
using System.IO;
using ModKit.Core;

namespace ModKit.Web
{
    /// <summary>
    /// Who is allowed to ask the server what.
    ///
    ///   * 127.0.0.1 / ::1        - the machine the kit runs on: full power, no token.
    ///   * another address + token - full power (exposure=full) or project-only
    ///                               (exposure=session).
    ///   * another address, no token - 401 with a hint that a token is needed.
    ///
    /// The token is not a replacement for the roots in <see cref="FsGuard"/>: it
    /// decides *who* may call, the roots decide *what* a caller may reach.
    /// </summary>
    public static class WebGuard
    {
        /// <summary>Returns the response to send instead of running the request, or null.</summary>
        public static HttpResponse Check(ModKitContext ctx, HttpRequest request)
        {
            // a health probe is useful to any monitor and reveals nothing
            if (string.Equals(request.Path.TrimEnd('/'), "/api/health", StringComparison.OrdinalIgnoreCase))
                return null;

            if (IsLoopback(request.ClientAddress)) return null;

            if (!HasToken(ctx, request))
            {
                var body = Json.O(
                    "ok", false,
                    "error", "this server needs a token for requests from other machines",
                    "auth", "token",
                    "hint", "the token is printed at startup and stored in bin\\auth.token");
                return HttpResponse.Json(body, 401);
            }

            if (ctx.SessionOnly && !AllowedInSessionMode(request))
            {
                var body = Json.O(
                    "ok", false,
                    "error", "server runs with exposure=session: only project collaboration is allowed from the network",
                    "auth", "session",
                    "hint", "edit server.exposure in config.ini to allow more");
                return HttpResponse.Json(body, 403);
            }

            return null;
        }

        public static bool IsLoopback(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            if (address == "127.0.0.1" || address == "::1") return true;
            if (address.StartsWith("127.", StringComparison.Ordinal)) return true;
            if (address.StartsWith("::ffff:127.", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool HasToken(ModKitContext ctx, HttpRequest request)
        {
            if (string.IsNullOrEmpty(ctx.Token)) return false;

            string given = request.GetHeader("X-DPT-Token", null);
            if (string.IsNullOrEmpty(given))
            {
                string value;
                if (request.Query.TryGetValue("token", out value)) given = value;
            }
            if (string.IsNullOrEmpty(given)) return false;

            // fixed-time-ish compare: tokens are short, the extra cost is nothing
            if (given.Length != ctx.Token.Length) return false;
            int diff = 0;
            for (int i = 0; i < given.Length; i++) diff |= given[i] ^ ctx.Token[i];
            return diff == 0;
        }

        /// <summary>
        /// What a remote caller may touch when the server is exposed for a shared
        /// session: read the project, upload into it, notes, session presence.
        /// No arbitrary paths, no jobs, no /api/fs, no deleting projects.
        /// </summary>
        private static bool AllowedInSessionMode(HttpRequest request)
        {
            string path = request.Path.TrimEnd('/');
            bool read = request.Method == "GET" || request.Method == "HEAD";
            bool post = request.Method == "POST";

            if (path == "/api/health") return true;

            if (read)
            {
                if (path == "/api/projects") return true;
                if (path.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase))
                {
                    // GET /api/projects/{id}[/files|ops|session*|file|inspect|...]
                    return true;
                }
            }

            if (post && path.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase))
            {
                if (path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase)) return true;
                if (path.EndsWith("/note", StringComparison.OrdinalIgnoreCase)) return true;
                if (path.IndexOf("/session/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- token

        /// <summary>
        /// Token from config, or from bin\auth.token, or newly generated and stored
        /// there. Stable across restarts without rewriting config.ini (which would
        /// throw away its comments).
        /// </summary>
        public static string LoadToken(Config config, string baseDirectory)
        {
            string configured = config.Get("server.token", "").Trim();
            if (configured.Length > 0) return configured;

            string tokenFile = Path.Combine(baseDirectory, "auth.token");
            try
            {
                if (File.Exists(tokenFile))
                {
                    string saved = File.ReadAllText(tokenFile).Trim();
                    if (saved.Length >= 8) return saved;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(Cat.Config, "cannot read " + tokenFile + ": " + Log.Describe(ex));
            }

            string token = Util.RandomCode(new Random(), "0123456789abcdef", 24);
            try { Util.WriteAllTextAtomic(tokenFile, token); }
            catch (Exception ex) { Log.Warn(Cat.Config, "cannot write " + tokenFile + ": " + Log.Describe(ex)); }
            return token;
        }
    }
}