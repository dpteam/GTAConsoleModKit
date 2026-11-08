using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ModKit.Core
{
    /// <summary>
    /// A modding project: one folder under workspace/ with a manifest, a working
    /// tree of game files and an append-only operation log (which doubles as the
    /// collaborative session history).
    /// </summary>
    public sealed class Project
    {
        public string Id;
        public string Name;
        public string Game;          // "LCS" | "VCS" | "LCS/VCS"
        public string Platform;      // "PSP"
        public string Description;
        public string Owner;
        public DateTime Created;
        public DateTime Modified;
        public string SessionCode;
        public bool Online;
        public List<OpRecord> Ops = new List<OpRecord>();
        public long NextSeq = 1;

        public string RootPath { get; private set; }
        public string FilesPath { get { return System.IO.Path.Combine(RootPath, "files"); } }
        public string ExportPath { get { return System.IO.Path.Combine(RootPath, "export"); } }
        public string ManifestPath { get { return System.IO.Path.Combine(RootPath, "project.json"); } }

        public sealed class OpRecord
        {
            public long Seq;
            public DateTime When;
            public string User;
            public string Kind;      // import / extract / replace / note / build / inspect
            public string Target;
            public string Note;

            public Dictionary<string, object> ToJson()
            {
                return Json.O(
                    "seq", Seq,
                    "when", When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    "user", User ?? "",
                    "kind", Kind ?? "",
                    "target", Target ?? "",
                    "note", Note ?? "");
            }

            public static OpRecord FromJson(Dictionary<string, object> json)
            {
                var op = new OpRecord
                {
                    Seq = Json.GetLong(json, "seq", 0),
                    User = Json.GetString(json, "user", ""),
                    Kind = Json.GetString(json, "kind", ""),
                    Target = Json.GetString(json, "target", ""),
                    Note = Json.GetString(json, "note", "")
                };
                DateTime when;
                if (DateTime.TryParse(Json.GetString(json, "when", ""), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out when)) op.When = when;
                return op;
            }
        }

        public static string Slugify(string text)
        {
            string slug = Util.Slug(text);
            return slug.Length == 0 ? "project" : slug;
        }

        public static Project Create(string workspaceRoot, string name, string game, string owner,
            string description)
        {
            string id = Slugify(name);
            string root = System.IO.Path.Combine(workspaceRoot, id);
            int suffix = 1;
            while (Directory.Exists(root))
            {
                id = Slugify(name) + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                root = System.IO.Path.Combine(workspaceRoot, id);
                suffix++;
            }

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(System.IO.Path.Combine(root, "files"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "export"));

            var project = new Project
            {
                Id = id,
                Name = name,
                Game = string.IsNullOrEmpty(game) ? "LCS" : game,
                Platform = "PSP",
                Description = description ?? "",
                Owner = string.IsNullOrEmpty(owner) ? "local" : owner,
                Created = DateTime.Now,
                Modified = DateTime.Now,
                RootPath = root,
                SessionCode = SessionCodes.NewCode(new Random(Environment.TickCount))
            };
            project.AddOp(project.Owner, "create", "-", "project created");
            project.Save();
            Log.Info(Cat.Project, "created project '" + name + "' at " + root);
            return project;
        }

        public static Project Load(string root)
        {
            string manifest = System.IO.Path.Combine(root, "project.json");
            if (!File.Exists(manifest))
                throw new FileNotFoundException("not a project folder (no project.json)", manifest);

            object parsed;
            if (!Json.TryParse(File.ReadAllText(manifest), out parsed))
                throw new InvalidDataException("project.json is not valid JSON");

            var json = Json.AsObject(parsed);
            var project = new Project
            {
                RootPath = root,
                Id = Json.GetString(json, "id", System.IO.Path.GetFileName(root)),
                Name = Json.GetString(json, "name", System.IO.Path.GetFileName(root)),
                Game = Json.GetString(json, "game", "LCS"),
                Platform = Json.GetString(json, "platform", "PSP"),
                Description = Json.GetString(json, "description", ""),
                Owner = Json.GetString(json, "owner", "local"),
                SessionCode = Json.GetString(json, "sessionCode", ""),
                Online = Json.GetBool(json, "online", false),
                NextSeq = Json.GetLong(json, "nextSeq", 1)
            };
            DateTime created;
            if (DateTime.TryParse(Json.GetString(json, "created", ""), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out created)) project.Created = created;
            DateTime modified;
            if (DateTime.TryParse(Json.GetString(json, "modified", ""), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out modified)) project.Modified = modified;

            List<object> ops = Json.AsArray(json.ContainsKey("ops") ? json["ops"] : null);
            if (ops != null)
            {
                foreach (object item in ops)
                {
                    var opJson = Json.AsObject(item);
                    if (opJson != null) project.Ops.Add(OpRecord.FromJson(opJson));
                }
            }

            if (string.IsNullOrEmpty(project.SessionCode))
            {
                project.SessionCode = SessionCodes.NewCode(new Random(Environment.TickCount));
                project.Save();
            }
            return project;
        }

        public static List<Project> ListAll(string workspaceRoot)
        {
            var result = new List<Project>();
            if (!Directory.Exists(workspaceRoot)) return result;
            foreach (string directory in Directory.GetDirectories(workspaceRoot))
            {
                if (!File.Exists(System.IO.Path.Combine(directory, "project.json"))) continue;
                try
                {
                    result.Add(Load(directory));
                }
                catch (Exception ex)
                {
                    Log.Warn(Cat.Project, "skipping broken project " + directory + " (" + Log.Describe(ex) + ")");
                }
            }
            result.Sort(delegate(Project a, Project b) { return b.Modified.CompareTo(a.Modified); });
            return result;
        }

        public OpRecord AddOp(string user, string kind, string target, string note)
        {
            var op = new OpRecord
            {
                Seq = NextSeq++,
                When = DateTime.Now,
                User = string.IsNullOrEmpty(user) ? "local" : user,
                Kind = kind,
                Target = target,
                Note = note
            };
            Ops.Add(op);
            Modified = DateTime.Now;
            // soft cap: the log is a session history, not an audit archive
            if (Ops.Count > 5000) Ops.RemoveRange(0, Ops.Count - 5000);
            return op;
        }

        public void Save()
        {
            Modified = DateTime.Now;
            var ops = new List<object>();
            foreach (OpRecord op in Ops) ops.Add(op.ToJson());

            var json = Json.O(
                "id", Id,
                "name", Name,
                "game", Game,
                "platform", Platform,
                "description", Description,
                "owner", Owner,
                "created", Created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "modified", Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "sessionCode", SessionCode,
                "online", Online,
                "nextSeq", NextSeq,
                "ops", ops);

            Util.WriteAllTextAtomic(ManifestPath, Json.Stringify(json, true));
        }

        /// <summary>Files of the working tree as relative paths (for the UI tree).</summary>
        public List<Dictionary<string, object>> ListFiles(int limit)
        {
            var result = new List<Dictionary<string, object>>();
            if (!Directory.Exists(FilesPath)) return result;
            foreach (string file in Directory.GetFiles(FilesPath, "*", SearchOption.AllDirectories))
            {
                string relative = Util.ToRelative(FilesPath, file);
                var info = new FileInfo(file);
                result.Add(Json.O("path", relative, "size", info.Length,
                    "modified", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                if (limit > 0 && result.Count >= limit) break;
            }
            result.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                return string.CompareOrdinal(Json.GetString(a, "path", ""), Json.GetString(b, "path", ""));
            });
            return result;
        }
    }

    public static class SessionCodes
    {
        // no 0/O/1/I to keep codes dictatable over voice chat
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        public static string NewCode(Random random)
        {
            return Util.RandomCode(random, Alphabet, 4) + "-" + Util.RandomCode(random, Alphabet, 4);
        }

        public static bool LooksValid(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            string cleaned = code.Replace("-", "").Trim().ToUpperInvariant();
            if (cleaned.Length != 8) return false;
            foreach (char c in cleaned)
                if (Alphabet.IndexOf(c) < 0) return false;
            return true;
        }

        public static string Normalize(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            string cleaned = code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
            if (cleaned.Length != 8) return cleaned;
            return cleaned.Substring(0, 4) + "-" + cleaned.Substring(4);
        }
    }
}
