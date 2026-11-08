using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using ModKit.Core;
using ModKit.Formats;

namespace ModKit.Web
{
    /// <summary>
    /// JSON API of the modding kit. Everything the UI can do goes through here;
    /// long operations are queued in the Pipeline and polled by job id.
    /// </summary>
    public static class WebApi
    {
        public static HttpResponse Handle(ModKitContext ctx, WebServer server, HttpRequest request)
        {
            string path = request.Path.TrimEnd('/');
            if (path.Length == 0) path = "/";

            try
            {
                // who may ask: localhost always, other machines with the token
                HttpResponse denied = WebGuard.Check(ctx, request);
                if (denied != null) return denied;

                if (path == "/api/health") return Health(ctx, server);
                if (path == "/api/status") return Status(ctx, server);
                if (path == "/api/logs") return Logs(request);
                if (path == "/api/jobs") return HttpResponse.Json(Json.O("ok", true, "jobs", Jobs(ctx, request)));
                if (path == "/api/identify") return Identify(ctx, request);
                if (path == "/api/fs") return Browse(request);
                if (path == "/api/ops") return RunOperation(ctx, request);

                if (path.StartsWith("/api/jobs/", StringComparison.OrdinalIgnoreCase))
                    return JobAction(ctx, request, path.Substring("/api/jobs/".Length));

                if (path == "/api/projects")
                {
                    if (request.Method == "POST") return CreateProject(ctx, request);
                    return HttpResponse.Json(Json.O("ok", true, "projects", Projects(ctx)));
                }

                if (path.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = path.Substring("/api/projects/".Length);
                    int slash = rest.IndexOf('/');
                    string projectId = slash < 0 ? rest : rest.Substring(0, slash);
                    string tail = slash < 0 ? "" : rest.Substring(slash + 1);
                    return ProjectApi(ctx, request, projectId, tail);
                }

                return HttpResponse.Error(404, "unknown endpoint: " + request.Path);
            }
            catch (Exception ex)
            {
                Log.Error(Cat.Api, request.Method + " " + request.Path + " failed", ex);
                return HttpResponse.Error(500, Log.Describe(ex));
            }
        }

        // ------------------------------------------------------------ status

        private static HttpResponse Health(ModKitContext ctx, WebServer server)
        {
            return HttpResponse.Json(Json.O(
                "ok", true,
                "version", ctx.Version,
                "uptimeSec", (long)(DateTime.UtcNow - ctx.StartedAt.ToUniversalTime()).TotalSeconds,
                "port", (long)server.Port));
        }

        private static HttpResponse Status(ModKitContext ctx, WebServer server)
        {
            List<Project> projects = ctx.Workspace.List();
            var online = new List<object>();
            foreach (Project project in projects)
            {
                int members = ctx.Workspace.Sessions.OnlineCount(project.Id);
                if (members > 0)
                    online.Add(Json.O("project", project.Id, "name", project.Name,
                        "members", (long)members, "code", project.SessionCode));
            }

            return HttpResponse.Json(Json.O(
                "ok", true,
                "version", ctx.Version,
                "started", ctx.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "uptimeSec", (long)(DateTime.UtcNow - ctx.StartedAt.ToUniversalTime()).TotalSeconds,
                "port", (long)server.Port,
                "bind", server.BindAddress,
                "requests", server.Requests,
                "errors", server.Errors,
                "connections", (long)server.ActiveConnections,
                "projects", (long)projects.Count,
                "online", online,
                "workspace", ctx.WorkspaceRoot,
                "gamesRoot", ctx.GamesRoot,
                "exposure", ctx.SessionOnly ? "session" : "full",
                "tokenRequired", ctx.Token.Length > 0,
                "logFile", Log.CurrentFile ?? "",
                "jobs", (long)ctx.Pipeline.Recent(100).Count));
        }

        /// <summary>
        /// Server-side folder browser for the web UI. A browser on another machine
        /// cannot hand the server its own paths (a mapped drive on the client does
        /// not exist for the server), so the UI browses what the server can see.
        ///
        /// Response: roots + the folders of the requested directory + the parent
        /// (null when the parent is outside every allowed root).
        /// </summary>
        private static HttpResponse Browse(HttpRequest request)
        {
            var roots = new List<object>();
            foreach (string root in FsGuard.Roots)
            {
                string name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = root;
                roots.Add(Json.O("path", root, "name", name));
            }

            string requested = ParseString(request, "path", "");
            if (string.IsNullOrEmpty(requested))
                return HttpResponse.Json(Json.O("ok", true, "path", "", "parent", null,
                    "roots", roots, "folders", new List<object>(), "files", 0L));

            string target = FsGuard.Allow(requested);
            if (target == null)
                return HttpResponse.Error(403, "not inside the allowed roots. Allowed: " + FsGuard.Describe());
            if (!Directory.Exists(target))
                return HttpResponse.Error(404, "no such folder: " + requested);

            var names = new List<string>(Directory.GetDirectories(target));
            names.Sort(delegate(string a, string b)
            {
                return string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
            });

            var folders = new List<object>();
            foreach (string directory in names)
            {
                long files = 0, subfolders = 0;
                try
                {
                    files = Directory.GetFiles(directory).Length;
                    subfolders = Directory.GetDirectories(directory).Length;
                }
                catch (Exception) { }
                folders.Add(Json.O("name", Path.GetFileName(directory), "path", directory,
                    "files", files, "folders", subfolders));
            }

            string parent = null;
            DirectoryInfo up = Directory.GetParent(target);
            if (up != null) parent = FsGuard.Allow(up.FullName);

            long here = 0;
            try { here = Directory.GetFiles(target).Length; } catch (Exception) { }

            return HttpResponse.Json(Json.O("ok", true, "path", target, "parent", parent,
                "roots", roots, "folders", folders, "files", here));
        }

        private static HttpResponse Logs(HttpRequest request)
        {
            int lines = ParseInt(request, "lines", 200, 10, 500);
            List<string> tail = new List<string>(Log.Tail(lines));
            return HttpResponse.Json(Json.O("ok", true, "lines", tail));
        }

        private static List<object> Jobs(ModKitContext ctx, HttpRequest request)
        {
            int count = ParseInt(request, "count", 30, 1, 200);
            var result = new List<object>();
            foreach (Pipeline.Job job in ctx.Pipeline.Recent(count)) result.Add(job.ToJson());
            return result;
        }

        private static HttpResponse JobAction(ModKitContext ctx, HttpRequest request, string tail)
        {
            int slash = tail.IndexOf('/');
            string jobId = slash < 0 ? tail : tail.Substring(0, slash);
            string action = slash < 0 ? "" : tail.Substring(slash + 1);
            Pipeline.Job job = ctx.Pipeline.Get(jobId);
            if (job == null) return HttpResponse.Error(404, "no such job: " + jobId);

            if (action == "cancel" && request.Method == "POST")
            {
                bool cancelled = ctx.Pipeline.Cancel(jobId);
                return HttpResponse.Json(Json.O("ok", true, "cancelled", cancelled));
            }
            return HttpResponse.Json(Json.O("ok", true, "job", job.ToJson()));
        }

        // ----------------------------------------------------------- projects

        private static List<object> Projects(ModKitContext ctx)
        {
            var result = new List<object>();
            foreach (Project project in ctx.Workspace.List())
            {
                result.Add(Json.O(
                    "id", project.Id,
                    "name", project.Name,
                    "game", project.Game,
                    "platform", project.Platform,
                    "description", project.Description,
                    "owner", project.Owner,
                    "created", project.Created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    "modified", project.Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    "code", project.SessionCode,
                    "online", project.Online,
                    "members", (long)ctx.Workspace.Sessions.OnlineCount(project.Id),
                    "ops", (long)project.Ops.Count,
                    "files", (long)project.ListFiles(0).Count));
            }
            return result;
        }

        /// <summary>
        /// Packs the project into a playable image: the base game tree supplies the
        /// files, the project's <c>files/</c> folder wins over them. That is the last
        /// step of the modding loop, so it is one button in the UI instead of a
        /// hand-typed override folder.
        /// </summary>
        private static HttpResponse BuildProject(ModKitContext ctx, HttpRequest request, Project project)
        {
            if (request.Method != "POST") return HttpResponse.Error(405, "POST only");
            Dictionary<string, object> body = BodyJson(request);
            string user = Json.GetString(body, "user", project.Owner);

            string basePath = Json.GetString(body, "base", "");
            if (string.IsNullOrEmpty(basePath)) return HttpResponse.Error(400, "base is required");

            string baseTree = ResolveAnyPath(ctx, project, basePath);
            if (baseTree == null || !Directory.Exists(baseTree))
                return HttpResponse.Error(400, "no such folder: " + basePath +
                    " (allowed roots: " + FsGuard.Describe() + ")");
            if (!Directory.Exists(Path.Combine(baseTree, "PSP_GAME")))
                return HttpResponse.Error(400, "the base folder has no PSP_GAME inside: " + basePath);

            bool compress = Json.GetBool(body, "compress", false);
            string label = Json.GetString(body, "label", "");
            string requested = Json.GetString(body, "target", "");
            string target;
            if (!string.IsNullOrEmpty(requested))
            {
                target = ResolveOutputPath(ctx, project, requested);
                if (target == null) return HttpResponse.Error(403, "target outside the allowed roots");
            }
            else
            {
                Directory.CreateDirectory(project.ExportPath);
                target = Path.Combine(project.ExportPath, project.Id + (compress ? ".cso" : ".iso"));
            }

            if (!target.EndsWith(".cso", StringComparison.OrdinalIgnoreCase) &&
                !target.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                if (compress) target += ".cso"; else target += ".iso";
            }

            string baseTreeCopy = baseTree;
            string targetCopy = target;

            Pipeline.Job job = ctx.Pipeline.Enqueue("build-project", "build-project", delegate(Pipeline.Job j)
            {
                Action<int> progress = delegate(int percent)
                {
                    j.Report(percent, "");
                    if (j.CancelRequested) throw new OperationCanceledException();
                };

                Operations.Outcome outcome = Operations.BuildImage(baseTreeCopy, project.FilesPath,
                    targetCopy, label, compress, progress);

                j.Result = outcome.Data;
                foreach (KeyValuePair<string, object> item in outcome.Data) j.Note(item.Key + " = " + item.Value);
                if (!outcome.Ok) throw new InvalidOperationException(outcome.Message);
                j.Note(outcome.Message);

                project.AddOp(user, "build", Path.GetFileName(targetCopy), outcome.Message);
                project.Save();
                ctx.Workspace.Sessions.NotifyChange(project.Id);
            });

            return HttpResponse.Json(Json.O("ok", true, "job", job.ToJson(),
                "target", target, "overrides", (long)project.ListFiles(0).Count), 202);
        }

        private static HttpResponse CreateProject(ModKitContext ctx, HttpRequest request)
        {
            Dictionary<string, object> body = BodyJson(request);
            string name = Json.GetString(body, "name", "");
            if (string.IsNullOrEmpty(name)) return HttpResponse.Error(400, "name is required");
            string game = Json.GetString(body, "game", "LCS");
            string owner = Json.GetString(body, "owner", "local");
            string description = Json.GetString(body, "description", "");

            Project project = ctx.Workspace.Create(name, game, owner, description);
            return HttpResponse.Json(Json.O("ok", true, "id", project.Id, "code", project.SessionCode), 201);
        }

        private static HttpResponse ProjectApi(ModKitContext ctx, HttpRequest request, string projectId, string tail)
        {
            Project project = ctx.Workspace.Get(projectId);
            if (project == null) return HttpResponse.Error(404, "no such project: " + projectId);

            if (tail.Length == 0)
            {
                if (request.Method == "DELETE")
                {
                    bool deleted = ctx.Workspace.Delete(projectId);
                    return HttpResponse.Json(Json.O("ok", deleted));
                }
                return HttpResponse.Json(Json.O(
                    "ok", true,
                    "project", Json.O(
                        "id", project.Id,
                        "name", project.Name,
                        "game", project.Game,
                        "platform", project.Platform,
                        "description", project.Description,
                        "owner", project.Owner,
                        "code", project.SessionCode,
                        "online", project.Online,
                        "created", project.Created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        "modified", project.Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    "files", project.ListFiles(2000),
                    "ops", Ops(project, 0, 200)));
            }

            if (tail == "img-textures")
            {
                string relative = ParseString(request, "path", "");
                string file = Util.SafeCombine(project.FilesPath, relative);
                if (file == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(file)) return HttpResponse.Error(404, "no such file: " + relative);

                Dictionary<string, object> index;
                try { index = ImgCache.Index(file); }
                catch (Exception ex) { return HttpResponse.Error(400, "cannot read as an IMG: " + Log.Describe(ex)); }
                if (index == null) return HttpResponse.Error(400, "cannot read as an IMG: " + relative);
                index["ok"] = true;
                return HttpResponse.Json(index);
            }

            if (tail == "img-texture")
            {
                string relative = ParseString(request, "path", "");
                string entryName = ParseString(request, "entry", "");
                int textureIndex = ParseInt(request, "index", 0, 0, 100000);
                int width = ParseInt(request, "w", 0, 0, 2048);

                string file = Util.SafeCombine(project.FilesPath, relative);
                if (file == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(file)) return HttpResponse.Error(404, "no such file: " + relative);

                ImgArchive archive = ImgCache.Archive(file);
                ImgArchive.Entry entry = archive.Find(entryName);
                if (entry == null) return HttpResponse.Error(404, "no entry '" + entryName + "' in the IMG");

                ChkFile chk = ChkFile.Parse(archive.ReadEntry(entry));
                if (textureIndex >= chk.Textures.Count)
                    return HttpResponse.Error(400, "texture index " + textureIndex + " is out of range (" +
                        chk.Textures.Count + ")");

                byte[] png = chk.RenderPng(chk.Textures[textureIndex], width);
                HttpResponse response = HttpResponse.Bytes(png, "image/png");
                response.Headers["Cache-Control"] = "private, max-age=120";
                return response;
            }

            if (tail == "build")
                return BuildProject(ctx, request, project);

            if (tail == "files")
                return HttpResponse.Json(Json.O("ok", true, "files", project.ListFiles(5000)));

            if (tail == "ops")
            {
                long since = ParseLong(request, "since", 0);
                return HttpResponse.Json(Json.O("ok", true, "ops", Ops(project, since, 500),
                    "next", project.NextSeq));
            }

            if (tail == "note" && request.Method == "POST")
            {
                Dictionary<string, object> body = BodyJson(request);
                string text = Json.GetString(body, "text", "");
                string user = Json.GetString(body, "user", project.Owner);
                if (string.IsNullOrEmpty(text)) return HttpResponse.Error(400, "text is required");
                Project.OpRecord op = project.AddOp(user, "note", "-", text);
                project.Save();
                ctx.Workspace.Sessions.NotifyChange(project.Id);
                return HttpResponse.Json(Json.O("ok", true, "op", op.ToJson()));
            }

            if (tail.StartsWith("session", StringComparison.OrdinalIgnoreCase))
                return SessionApi(ctx, request, project, tail);

            if (tail == "upload" && request.Method == "POST")
            {
                string relative = ParseString(request, "path", "");
                if (string.IsNullOrEmpty(relative)) return HttpResponse.Error(400, "path is required");
                string target = Util.SafeCombine(project.FilesPath, relative);
                if (target == null) return HttpResponse.Error(403, "path traversal denied");
                string directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                Util.WriteAllBytesAtomic(target, request.Body);

                Project.OpRecord op = project.AddOp(ParseString(request, "user", project.Owner),
                    "import", relative, Util.HumanBytes(request.Body.Length));
                project.Save();
                ctx.Workspace.Sessions.NotifyChange(project.Id);
                return HttpResponse.Json(Json.O("ok", true, "path", relative, "size", (long)request.Body.Length,
                    "op", op.ToJson()));
            }

            if (tail == "folder" && request.Method == "POST")
            {
                Dictionary<string, object> body = BodyJson(request);
                string path = Json.GetString(body, "path", "");
                string asName = Json.GetString(body, "as", "");
                string source = ResolveAnyPath(ctx, project, path);
                if (source == null)
                    return HttpResponse.Error(403, "not inside the allowed roots. Allowed: " + FsGuard.Describe());

                // a relative path is normally project-relative, but "add a folder from
                // the game dumps" is the common case, so fall back to gamesRoot
                if (!Directory.Exists(source) && !Path.IsPathRooted(path) &&
                    !string.IsNullOrEmpty(ctx.GamesRoot))
                {
                    string fromGames = Util.SafeCombine(ctx.GamesRoot, path);
                    if (fromGames != null && Directory.Exists(fromGames)) source = fromGames;
                }
                if (!Directory.Exists(source)) return HttpResponse.Error(400, "no such folder: " + path);
                if (IsSameOrInside(project.FilesPath, source))
                    return HttpResponse.Error(400, "that folder is already inside the project");

                if (string.IsNullOrEmpty(asName)) asName = Path.GetFileName(source.TrimEnd('\\', '/'));
                asName = asName.Replace('\\', '/').Trim('/');
                if (string.IsNullOrEmpty(asName)) return HttpResponse.Error(400, "cannot derive a folder name");

                string target = Util.SafeCombine(project.FilesPath, asName);
                if (target == null) return HttpResponse.Error(403, "path traversal denied");

                Util.CopyStats stats = Util.CopyTree(source, target);
                Project.OpRecord op = project.AddOp(Json.GetString(body, "user", project.Owner),
                    "import", asName + "/",
                    stats.Files + " file(s), " + Util.HumanBytes(stats.Bytes));
                project.Save();
                ctx.Workspace.Sessions.NotifyChange(project.Id);
                return HttpResponse.Json(Json.O("ok", true, "folder", asName, "files", (long)stats.Files,
                    "folders", (long)stats.Folders, "bytes", stats.Bytes, "op", op.ToJson()));
            }

            if (tail == "mkdir" && request.Method == "POST")
            {
                string relative = Json.GetString(BodyJson(request), "path", "");
                string target = Util.SafeCombine(project.FilesPath, relative);
                if (target == null || string.IsNullOrEmpty(relative.Trim('/', '\\')))
                    return HttpResponse.Error(403, "path traversal denied");
                Directory.CreateDirectory(target);
                ctx.Workspace.Sessions.NotifyChange(project.Id);
                return HttpResponse.Json(Json.O("ok", true, "path", Util.ToRelative(project.FilesPath, target)));
            }

            if (tail == "delete" && request.Method == "POST")
            {
                Dictionary<string, object> body = BodyJson(request);
                string relative = Json.GetString(body, "path", "").Trim('/', '\\');
                if (string.IsNullOrEmpty(relative)) return HttpResponse.Error(400, "path is required");
                string target = Util.SafeCombine(project.FilesPath, relative);
                if (target == null) return HttpResponse.Error(403, "path traversal denied");
                if (!target.StartsWith(Path.GetFullPath(project.FilesPath), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFullPath(target), Path.GetFullPath(project.FilesPath),
                        StringComparison.OrdinalIgnoreCase))
                    return HttpResponse.Error(400, "refusing to delete the project root");

                bool wasFolder = Directory.Exists(target);
                if (wasFolder) Util.DeleteTree(target);
                else if (File.Exists(target)) Util.DeleteTree(target);
                else return HttpResponse.Error(404, "no such file or folder: " + relative);

                Project.OpRecord op = project.AddOp(Json.GetString(body, "user", project.Owner),
                    "delete", relative + (wasFolder ? "/" : ""), wasFolder ? "folder" : "file");
                project.Save();
                ctx.Workspace.Sessions.NotifyChange(project.Id);
                return HttpResponse.Json(Json.O("ok", true, "path", relative, "folder", wasFolder,
                    "op", op.ToJson()));
            }

            if (tail == "file")
            {
                string relative = ParseString(request, "path", "");
                string target = Util.SafeCombine(project.FilesPath, relative);
                if (target == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(target)) return HttpResponse.Error(404, "no such file");
                return HttpResponse.Bytes(File.ReadAllBytes(target), HttpCore.ContentTypeFor(target));
            }

            if (tail == "inspect")
            {
                string relative = ParseString(request, "path", "");
                string target = Util.SafeCombine(project.FilesPath, relative);
                if (target == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(target)) return HttpResponse.Error(404, "no such file");
                FormatId.Info info = FormatId.Identify(target);

                var payload = Json.O(
                    "ok", true,
                    "path", relative,
                    "kind", info.Kind,
                    "label", info.Label,
                    "platform", info.Platform,
                    "game", info.Game,
                    "openable", info.Openable,
                    "details", info.Details);

                // Is it an archive of chunks rather than a single chunk? A packed
                // IMG can start with a "tex" chunk and look like a CHK to the
                // identifier, so the UI needs the entry count to tell them apart.
                long length = 0;
                try { length = new FileInfo(target).Length; } catch (Exception) { }
                if ((info.Kind == "img" || info.Kind == "chk" || info.Kind == "mdl") &&
                    length > 0 && length < 300L * 1024 * 1024)
                {
                    try { payload["imgEntries"] = (long)ImgCache.Archive(target).Entries.Count; }
                    catch (Exception) { payload["imgEntries"] = 0L; }
                }
                else
                {
                    payload["imgEntries"] = 0L;
                }

                if (info.Kind == "chk")
                {
                    try
                    {
                        ChkFile chk = ChkFile.Load(target);
                        var textures = new List<object>();
                        for (int i = 0; i < chk.Textures.Count; i++)
                        {
                            ChkFile.Texture texture = chk.Textures[i];
                            textures.Add(Json.O("index", (long)i, "name", texture.Name,
                                "width", (long)texture.Width, "height", (long)texture.Height,
                                "format", Rsl.FormatName(texture.Format),
                                "bytes", (long)texture.DataLength));
                        }
                        payload["textures"] = textures;
                    }
                    catch (Exception ex)
                    {
                        payload["error"] = Log.Describe(ex);
                    }
                }
                else if (info.Kind == "gxt")
                {
                    try
                    {
                        Gxt.Table table = Gxt.Read(target);
                        var strings = new List<object>();
                        int shown = 0;
                        foreach (Gxt.Entry entry in table.AllEntries())
                        {
                            if (shown++ >= 300) break;
                            strings.Add(Json.O("key", entry.Key, "block", entry.Block,
                                "text", entry.Text));
                        }
                        payload["strings"] = strings;
                        payload["stringCount"] = (long)table.Count;
                    }
                    catch (Exception ex)
                    {
                        payload["error"] = Log.Describe(ex);
                    }
                }
                else if (info.Kind == "dtz" || info.Kind == "dat")
                {
                    try
                    {
                        DtzImage image = DtzImage.Load(target, null);
                        payload["models"] = image.DescribeModels(200);
                        payload["game"] = image.GameLabel;
                    }
                    catch (Exception ex)
                    {
                        payload["error"] = Log.Describe(ex);
                    }
                }

                return HttpResponse.Json(payload);
            }

            if (tail == "texture")
            {
                string relative = ParseString(request, "path", "");
                int index = ParseInt(request, "index", 0, 0, 100000);
                string target = Util.SafeCombine(project.FilesPath, relative);
                if (target == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(target)) return HttpResponse.Error(404, "no such file");
                ChkFile chk = ChkFile.Load(target);
                if (index >= chk.Textures.Count) return HttpResponse.Error(404, "no such texture index");
                using (System.Drawing.Bitmap bitmap = chk.Decode(chk.Textures[index]))
                using (var buffer = new MemoryStream())
                {
                    bitmap.Save(buffer, ImageFormat.Png);
                    var response = HttpResponse.Bytes(buffer.ToArray(), "image/png");
                    response.Headers["Cache-Control"] = "private, max-age=60";
                    return response;
                }
            }

            if (tail == "gxt-text")
            {
                string relative = ParseString(request, "path", "");
                string gxtPath = Util.SafeCombine(project.FilesPath, relative);
                if (gxtPath == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(gxtPath)) return HttpResponse.Error(404, "no such file");
                Gxt.Table table = Gxt.Read(gxtPath);
                var response = HttpResponse.Text(Gxt.ToText(table, true), "text/plain; charset=utf-8", 200);
                response.Headers["Content-Disposition"] = "attachment; filename=\"gxt.txt\"";
                return response;
            }

            if (tail == "gxt-import" && request.Method == "POST")
            {
                string relative = ParseString(request, "path", "");
                string gxtPath = Util.SafeCombine(project.FilesPath, relative);
                if (gxtPath == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(gxtPath)) return HttpResponse.Error(404, "no such file");
                if (request.Body == null || request.Body.Length == 0)
                    return HttpResponse.Error(400, "empty text body");

                string temp = Path.Combine(Path.GetTempPath(),
                    "modkit-gxt-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    Util.WriteAllTextAtomic(temp, Encoding.UTF8.GetString(request.Body));
                    Operations.Outcome outcome = Operations.GxtImport(gxtPath, temp, null);
                    if (!outcome.Ok) return HttpResponse.Error(400, outcome.Message);
                    Project.OpRecord op = project.AddOp(ParseString(request, "user", project.Owner),
                        "gxt-import", relative, outcome.Message);
                    project.Save();
                    ctx.Workspace.Sessions.NotifyChange(project.Id);
                    return HttpResponse.Json(Json.O("ok", true, "op", op.ToJson(), "data", outcome.Data));
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                }
            }

            if (tail == "texture-import" && request.Method == "POST")
            {
                string relative = ParseString(request, "path", "");
                int index = ParseInt(request, "index", 0, 0, 100000);
                string target = Util.SafeCombine(project.FilesPath, relative);
                if (target == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(target)) return HttpResponse.Error(404, "no such file");
                if (request.Body == null || request.Body.Length == 0)
                    return HttpResponse.Error(400, "empty image body");

                string temp = Path.Combine(Path.GetTempPath(),
                    "modkit-tex-" + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    Util.WriteAllBytesAtomic(temp, request.Body);
                    Operations.Outcome outcome = Operations.ChkReplace(target, temp, index, null, null);
                    if (!outcome.Ok) return HttpResponse.Error(400, outcome.Message);
                    Project.OpRecord op = project.AddOp(ParseString(request, "user", project.Owner),
                        "texture-import", relative, outcome.Message);
                    project.Save();
                    ctx.Workspace.Sessions.NotifyChange(project.Id);
                    return HttpResponse.Json(Json.O("ok", true, "op", op.ToJson(), "data", outcome.Data));
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                }
            }

            if (tail == "audio")
            {
                string relative = ParseString(request, "path", "");
                string target = Util.SafeCombine(project.FilesPath, relative);
                if (target == null) return HttpResponse.Error(403, "path traversal denied");
                if (!File.Exists(target)) return HttpResponse.Error(404, "no such file");
                int sampleRate;
                byte[] wav = Vag.ConvertToWav(target, out sampleRate);
                return HttpResponse.Bytes(wav, "audio/wav");
            }

            return HttpResponse.Error(404, "unknown project endpoint: " + tail);
        }

        private static List<object> Ops(Project project, long since, int limit)
        {
            var result = new List<object>();
            foreach (Project.OpRecord op in project.Ops)
            {
                if (op.Seq <= since) continue;
                result.Add(op.ToJson());
                if (result.Count >= limit) break;
            }
            return result;
        }

        private static HttpResponse SessionApi(ModKitContext ctx, HttpRequest request, Project project, string tail)
        {
            if (tail == "session")
            {
                var members = new List<object>();
                foreach (SessionHub.Participant participant in ctx.Workspace.Sessions.Members(project.Id))
                {
                    members.Add(Json.O(
                        "clientId", participant.ClientId,
                        "nick", participant.Nickname,
                        "joined", participant.Joined.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                        "lastSeen", participant.LastSeen.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
                }
                return HttpResponse.Json(Json.O(
                    "ok", true,
                    "code", project.SessionCode,
                    "online", project.Online,
                    "members", members,
                    "activity", (long)ctx.Workspace.Sessions.Activity(project.Id),
                    "next", project.NextSeq));
            }

            if (tail == "session/join" && request.Method == "POST")
            {
                Dictionary<string, object> body = BodyJson(request);
                string nick = Json.GetString(body, "nick", "guest");
                string clientId = Json.GetString(body, "clientId", "");
                SessionHub.Participant participant = ctx.Workspace.Sessions.Join(project.Id, nick,
                    clientId, request.ClientAddress);
                project.Online = true;
                project.AddOp(nick, "join", "-", "joined the session from " + request.ClientAddress);
                project.Save();
                return HttpResponse.Json(Json.O("ok", true, "clientId", participant.ClientId,
                    "code", project.SessionCode, "next", project.NextSeq));
            }

            if (tail == "session/leave" && request.Method == "POST")
            {
                string clientId = ParseString(request, "clientId", "");
                if (clientId.Length == 0)
                {
                    Dictionary<string, object> body = BodyJson(request);
                    clientId = Json.GetString(body, "clientId", "");
                }
                ctx.Workspace.Sessions.Leave(project.Id, clientId);
                return HttpResponse.Json(Json.O("ok", true));
            }

            if (tail == "session/wait")
            {
                int activity = ParseInt(request, "activity", 0, 0, int.MaxValue);
                int ms = ParseInt(request, "ms", 15000, 100, 60000);
                string clientId = ParseString(request, "clientId", "");
                if (clientId.Length > 0) ctx.Workspace.Sessions.Touch(project.Id, clientId);
                ctx.Workspace.Sessions.WaitForChange(project.Id, activity, ms);
                var members = new List<object>();
                foreach (SessionHub.Participant participant in ctx.Workspace.Sessions.Members(project.Id))
                    members.Add(Json.O("clientId", participant.ClientId, "nick", participant.Nickname));
                return HttpResponse.Json(Json.O("ok", true,
                    "activity", (long)ctx.Workspace.Sessions.Activity(project.Id),
                    "next", project.NextSeq,
                    "members", members));
            }

            return HttpResponse.Error(404, "unknown session endpoint: " + tail);
        }

        // --------------------------------------------------------- operations

        private static HttpResponse Identify(ModKitContext ctx, HttpRequest request)
        {
            string path = ParseString(request, "path", "");
            string resolved = ResolveAnyPath(ctx, null, path);
            if (resolved == null) return HttpResponse.Error(403, "path outside the allowed roots");
            Operations.Outcome outcome = Operations.Identify(resolved);
            return HttpResponse.Json(Json.O("ok", outcome.Ok, "message", outcome.Message,
                "data", outcome.Data));
        }

        /// <summary>
        /// POST /api/ops?name=cso-to-iso&amp;source=...&amp;target=...&amp;project=...
        /// Runs in the pipeline and returns the job id immediately.
        /// </summary>
        private static HttpResponse RunOperation(ModKitContext ctx, HttpRequest request)
        {
            if (request.Method != "POST") return HttpResponse.Error(405, "POST only");
            Dictionary<string, object> body = BodyJson(request);
            string name = ParseString(request, "name", Json.GetString(body, "name", ""));
            string projectId = ParseString(request, "project", Json.GetString(body, "project", ""));
            string source = Json.GetString(body, "source", ParseString(request, "source", ""));
            string target = Json.GetString(body, "target", ParseString(request, "target", ""));
            string extra = Json.GetString(body, "extra", ParseString(request, "extra", ""));
            string user = Json.GetString(body, "user", "web");

            Project project = string.IsNullOrEmpty(projectId) ? null : ctx.Workspace.Get(projectId);
            string resolvedSource = ResolveAnyPath(ctx, project, source);
            if (resolvedSource == null) return HttpResponse.Error(403, "source outside the allowed roots");

            string resolvedTarget;
            if (!string.IsNullOrEmpty(target))
            {
                resolvedTarget = ResolveOutputPath(ctx, project, target);
                if (resolvedTarget == null) return HttpResponse.Error(403, "target outside the allowed roots");
            }
            else
            {
                resolvedTarget = null;
            }

            Pipeline.Job job = ctx.Pipeline.Enqueue(name, name, delegate(Pipeline.Job j)
            {
                Action<int> progress = delegate(int percent)
                {
                    j.Report(percent, "");
                    if (j.CancelRequested) throw new OperationCanceledException();
                };
                Operations.Outcome outcome;
                switch (name)
                {
                    case "identify":
                        outcome = Operations.Identify(resolvedSource);
                        break;
                    case "cso-to-iso":
                        outcome = Operations.CsoToIso(resolvedSource, resolvedTarget, progress);
                        break;
                    case "iso-to-cso":
                        outcome = Operations.IsoToCso(resolvedSource, resolvedTarget, 2048, progress);
                        break;
                    case "iso-extract":
                        outcome = Operations.IsoExtract(resolvedSource, resolvedTarget, progress);
                        break;
                    case "iso-build":
                        outcome = Operations.IsoBuild(resolvedSource, resolvedTarget, extra, progress);
                        break;
                    case "build-image":
                        outcome = Operations.BuildImage(resolvedSource, extra, resolvedTarget,
                            Json.GetString(body, "label", ""),
                            Json.GetBool(body, "compress", false), progress);
                        break;
                    case "img-list":
                        outcome = Operations.ImgList(resolvedSource, extra);
                        break;
                    case "img-extract":
                        outcome = Operations.ImgExtract(resolvedSource, extra, resolvedTarget,
                            delegate(string path, int percent) { progress(percent); });
                        break;
                    case "img-replace":
                        outcome = Operations.ImgReplace(resolvedSource, extra,
                            Json.GetString(body, "entry", ""), resolvedTarget);
                        break;
                    case "img-repack":
                        outcome = Operations.ImgRepackCmd(resolvedSource,
                            Json.GetString(body, "dat", extra), extra,
                            Json.GetString(body, "folder", ""), resolvedTarget,
                            Json.GetString(body, "outDir", ""),
                            Json.GetString(body, "outDtz", ""), progress);
                        break;
                    case "img-pack":
                        outcome = Operations.ImgPack(resolvedSource, resolvedTarget, extra,
                            Json.GetString(body, "extension", ""), progress);
                        break;
                    case "dtz-inspect":
                        outcome = Operations.DtzInspect(resolvedSource, "");
                        break;
                    case "dtz-dat":
                        outcome = Operations.DtzExportDat(resolvedSource, resolvedTarget);
                        break;
                    case "dtz-dtz":
                        outcome = Operations.DtzExportDtz(resolvedSource, resolvedTarget);
                        break;
                    case "dtz-dir":
                        outcome = Operations.DtzExportDir(resolvedSource, resolvedTarget, "");
                        break;
                    case "dtz-models":
                        outcome = Operations.DtzModels(resolvedSource, "", 500);
                        break;
                    case "chk-list":
                        outcome = Operations.ChkList(resolvedSource);
                        break;
                    case "chk-export":
                        outcome = Operations.ChkExport(resolvedSource, resolvedTarget);
                        break;
                    case "tex-index":
                        outcome = Operations.TexIndex(resolvedSource, extra);
                        break;
                    case "tex-export-all":
                        outcome = Operations.TexExportAll(resolvedSource, extra, resolvedTarget,
                            delegate(string path, int count) { j.Note(path); });
                        break;
                    case "tex-import-all":
                        outcome = Operations.TexImportAll(resolvedSource, extra, resolvedTarget,
                            Json.GetBool(body, "all", false),
                            delegate(string path, int count) { j.Note(path); });
                        break;
                    case "gxt-dump":
                        outcome = Operations.GxtDump(resolvedSource, resolvedTarget);
                        break;
                    case "gxt-import":
                        outcome = Operations.GxtImport(resolvedSource, extra, resolvedTarget);
                        break;
                    case "vag-to-wav":
                        outcome = Operations.VagToWav(resolvedSource, resolvedTarget);
                        break;
                    case "wav-to-vag":
                        outcome = Operations.WavToVag(resolvedSource, resolvedTarget);
                        break;
                    default:
                        outcome = Operations.Outcome.Fail("unknown operation: " + name);
                        break;
                }

                j.Result = outcome.Data;
                foreach (var item in outcome.Data) j.Note(item.Key + " = " + item.Value);
                if (!outcome.Ok) throw new InvalidOperationException(outcome.Message);
                j.Note(outcome.Message);
                if (project != null)
                {
                    project.AddOp(user, name, source, outcome.Message);
                    project.Save();
                    ctx.Workspace.Sessions.NotifyChange(project.Id);
                }
            });

            return HttpResponse.Json(Json.O("ok", true, "job", job.ToJson()), 202);
        }

        // ------------------------------------------------------------- helpers

        private static Dictionary<string, object> BodyJson(HttpRequest request)
        {
            if (request.Body == null || request.Body.Length == 0)
                return new Dictionary<string, object>();
            string text = Encoding.UTF8.GetString(request.Body);
            object parsed;
            if (!Json.TryParse(text, out parsed)) return new Dictionary<string, object>();
            return Json.AsObject(parsed) ?? new Dictionary<string, object>();
        }

        private static string ParseString(HttpRequest request, string key, string fallback)
        {
            string value;
            return request.Query.TryGetValue(key, out value) ? value : fallback;
        }

        private static int ParseInt(HttpRequest request, string key, int fallback, int min, int max)
        {
            string text = ParseString(request, key, "");
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return fallback;
            if (value < min) value = min;
            if (value > max) value = max;
            return value;
        }

        private static long ParseLong(HttpRequest request, string key, long fallback)
        {
            string text = ParseString(request, key, "");
            long value;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        /// <summary>
        /// Absolute paths are only accepted inside the allowed roots (workspace,
        /// gamesRoot and whatever <c>workspace.roots</c> adds). A relative path is
        /// project-relative first, then gamesRoot, then any other root - so
        /// "refs/temp/VCS" or "extracted/LCS" work without typing a drive letter.
        /// </summary>
        private static string ResolveAnyPath(ModKitContext ctx, Project project, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Path.IsPathRooted(path)) return FsGuard.Allow(path);

            if (project != null)
            {
                string inside = Util.SafeCombine(project.FilesPath, path);
                if (inside != null && (File.Exists(inside) || Directory.Exists(inside))) return inside;
            }
            if (!string.IsNullOrEmpty(ctx.GamesRoot))
            {
                string fromGames = Util.SafeCombine(ctx.GamesRoot, path);
                if (fromGames != null && (File.Exists(fromGames) || Directory.Exists(fromGames))) return fromGames;
            }
            foreach (string root in FsGuard.Roots)
            {
                string fromRoot = Util.SafeCombine(root, path);
                if (fromRoot != null && (File.Exists(fromRoot) || Directory.Exists(fromRoot))) return fromRoot;
            }

            // nothing exists yet: keep the old behaviour and let the caller say "no such file"
            if (project != null) return Util.SafeCombine(project.FilesPath, path);
            return null;
        }

        private static string ResolveOutputPath(ModKitContext ctx, Project project, string path)
        {
            if (Path.IsPathRooted(path)) return FsGuard.Allow(path);
            if (project != null) return Util.SafeCombine(project.ExportPath, path);
            if (!string.IsNullOrEmpty(ctx.GamesRoot)) return Util.SafeCombine(ctx.GamesRoot, path);
            return null;
        }

        private static bool Inside(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                fullRoot += Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when <paramref name="path"/> is the project folder itself or below it.</summary>
        private static bool IsSameOrInside(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase) ||
                   Inside(fullPath, fullRoot);
        }
    }
}
