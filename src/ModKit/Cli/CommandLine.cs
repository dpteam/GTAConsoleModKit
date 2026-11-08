using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ModKit.Core;
using ModKit.Formats;

namespace ModKit.Cli
{
    /// <summary>
    /// Headless front end. Every command is a thin wrapper around Operations, so
    /// the web UI and the CLI can never disagree about what a format operation does.
    /// </summary>
    public static class CommandLine
    {
        public static int Run(ModKitContext ctx, string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h" || args[0] == "help")
            {
                PrintHelp();
                return 0;
            }

            string command = args[0].ToLowerInvariant();
            var rest = new List<string>();
            for (int i = 1; i < args.Length; i++) rest.Add(args[i]);

            try
            {
                switch (command)
                {
                    case "identify":
                    case "id":
                        return Identify(rest);
                    case "cso":
                        return CsoCommand(rest);
                    case "iso-list":
                        return IsoList(rest);
                    case "iso-extract":
                        return IsoExtract(rest);
                    case "iso-build":
                        return IsoBuild(rest);
                    case "build-image":
                        return BuildImage(rest);
                    case "img-list":
                        return ImgList(rest);
                    case "img-extract":
                        return ImgExtract(rest);
                    case "img-replace":
                        return ImgReplace(rest);
                    case "img-pack":
                        return ImgPack(rest);
                    case "img-repack":
                        return ImgRepackCommand(rest);
                    case "dtz-inspect":
                        return DtzInspect(ctx, rest);
                    case "dtz-dat":
                        return DtzDat(rest);
                    case "dtz-dtz":
                        return DtzDtz(rest);
                    case "dtz-dir":
                        return DtzDir(ctx, rest);
                    case "dtz-models":
                        return DtzModels(ctx, rest);
                    case "chk-list":
                        return ChkListCommand(rest);
                    case "chk-export":
                        return ChkExport(rest);
                    case "chk-replace":
                        return ChkReplace(rest);
                    case "gxt-dump":
                        return GxtDump(rest);
                    case "gxt-import":
                        return GxtImport(rest);
                    case "vag-to-wav":
                        return VagToWav(rest);
                    case "wav-to-vag":
                        return WavToVag(rest);
                    case "project-list":
                        return ProjectList(ctx);
                    case "project-new":
                        return ProjectNew(ctx, rest);
                    case "project-info":
                        return ProjectInfo(ctx, rest);
                    default:
                        Log.Error(Cat.App, "unknown command: " + command);
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Log.Error(Cat.App, command + " failed", ex);
                Console.Error.WriteLine("error: " + Log.Describe(ex));
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
DPT.PSP GTA Modding Kit - headless commands

  identify <file>                      what is this file?
  cso <src.cso> [out.iso]              expand a CSO/ZSO image
  cso --compress <src.iso> [out.cso]   compress an ISO back to CSO
  iso-list <image.iso>                 list an ISO 9660 image
  iso-extract <image.iso> [folder]     extract an ISO 9660 image
  iso-build <folder> [out.iso] [label] build an ISO 9660 image
  img-list <image.img> [dir]           list an IMG archive (uses .DIR when present)
  img-extract <img> [folder] [--dir <dir>]
  img-replace <img> <entry> <file> [--dir <dir>]   put a file back into an entry
  img-pack <folder> [out.img] [--ext .mdl]         build an IMG (+ .DIR) from a folder
  img-repack <img> <GAME.DTZ> [--folder dir] [--out new.img] [--out-dtz new.dtz]
                                                   repack an IMG and rewrite the streaming
                                                   table so entries may change size
  dtz-inspect <GAME.DTZ|GAME.DAT>      streaming table summary
  dtz-dat <GAME.DTZ> [out.dat]         inflate to GAME.DAT
  dtz-dtz <GAME.DAT> [out.dtz]         recompress to GAME.DTZ
  dtz-dir <GAME.DTZ> [out.dir]           export the .DIR for the matching IMG
                                         (game and name table are auto-detected)
  dtz-models <GAME.DTZ> [--limit N]
  chk-list <file.chk|.xtx>             list textures
  chk-export <file.chk> [folder]       export every texture as PNG
  chk-replace <chk> <png> [i] [out]    put a PNG back into a texture (mip + palette)
  gxt-dump <file.gxt> [out.txt]        dump the text table (editable)
  gxt-import <file.gxt> <edited.txt> [out.gxt]   rebuild the GXT from the edited text
  vag-to-wav <file.vag> [out.wav]      decode PS-ADPCM
  wav-to-vag <file.wav> [out.vag]      encode PS-ADPCM
  project-list                         projects in the workspace
  project-new <name> [game]            create a project
  project-info <id>                    show project + session code

  serve [--port 44700] [--bind 127.0.0.1]   start the web UI

Notes
  * paths are plain file system paths; nothing is copied implicitly
  * exit code 0 = success, 1 = failure, 2 = usage error
");
        }

        private static int Identify(List<string> args)
        {
            if (args.Count < 1) return Usage("identify <file>");
            Operations.Outcome outcome = Operations.Identify(args[0]);
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int CsoCommand(List<string> args)
        {
            bool compress = args.Remove("--compress");
            if (args.Count < 1) return Usage("cso [--compress] <source> [target]");
            string source = args[0];
            string target = args.Count > 1 ? args[1] : null;
            Operations.Outcome outcome = compress
                ? Operations.IsoToCso(source, target, 2048, Progress())
                : Operations.CsoToIso(source, target, Progress());
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int IsoList(List<string> args)
        {
            if (args.Count < 1) return Usage("iso-list <image.iso>");
            Iso9660.Volume volume = Iso9660.Read(args[0]);
            Console.WriteLine("volume: " + volume.VolumeIdentifier + "  system: " + volume.SystemIdentifier);
            int files = 0;
            foreach (Iso9660.Entry entry in volume.AllEntries)
            {
                if (entry.IsDirectory) continue;
                files++;
                Console.WriteLine("  {0,10}  {1}", entry.Length, entry.FullPath);
            }
            Console.WriteLine(files + " files");
            return 0;
        }

        private static int IsoExtract(List<string> args)
        {
            if (args.Count < 1) return Usage("iso-extract <image.iso> [folder]");
            string target = args.Count > 1 ? args[1] : null;
            Operations.Outcome outcome = Operations.IsoExtract(args[0], target, Progress());
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int IsoBuild(List<string> args)
        {
            if (args.Count < 1) return Usage("iso-build <folder> [out.iso] [label]");
            string target = args.Count > 1 ? args[1] : null;
            string label = args.Count > 2 ? args[2] : "GTA_PSP";
            Operations.Outcome outcome = Operations.IsoBuild(args[0], target, label, Progress());
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int BuildImage(List<string> args)
        {
            string label = TakeOption(args, "--label");
            bool compress = args.Remove("--cso");
            if (args.Count < 2) return Usage("build-image <game-tree> <overrides-folder> <out.iso|out.cso> [--label X] [--cso]");
            string target = args.Count > 2 ? args[2] : null;
            Operations.Outcome outcome = Operations.BuildImage(args[0], args[1], target,
                label, compress, Progress());
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int ImgList(List<string> args)
        {
            if (args.Count < 1) return Usage("img-list <image.img> [dir]");
            string directory = args.Count > 1 ? args[1] : null;
            Operations.Outcome outcome = Operations.ImgList(args[0], directory);
            if (outcome.Ok)
            {
                Console.WriteLine(outcome.Message);
                if (outcome.Data.ContainsKey("entries"))
                {
                    var entries = outcome.Data["entries"] as List<object>;
                    if (entries != null)
                        foreach (object item in entries)
                        {
                            var entry = item as Dictionary<string, object>;
                            if (entry == null) continue;
                            Console.WriteLine(string.Format("  {0,8} +{1,-8} {2}",
                                Json.GetLong(entry, "offset", 0), Json.GetLong(entry, "size", 0),
                                Json.GetString(entry, "name", "")));
                        }
                }
                return 0;
            }
            PrintOutcome(outcome);
            return 1;
        }

        private static int ImgExtract(List<string> args)
        {
            string directory = null;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (args[i] == "--dir" && i + 1 < args.Count)
                {
                    directory = args[i + 1];
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                }
            }
            if (args.Count < 1) return Usage("img-extract <img> [folder] [--dir <dir>]");
            string target = args.Count > 1 ? args[1] : null;
            Operations.Outcome outcome = Operations.ImgExtract(args[0], directory, target,
                delegate(string path, int percent) { });
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int ImgReplace(List<string> args)
        {
            string directory = null;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (args[i] == "--dir" && i + 1 < args.Count)
                {
                    directory = args[i + 1];
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                }
            }
            if (args.Count < 3)
                return Usage("img-replace <image.img> <entry> <file> [--dir <dir>]");
            Operations.Outcome outcome = Operations.ImgReplace(args[0], directory, args[1], args[2]);
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int ImgPack(List<string> args)
        {
            string extension = null;
            string directory = null;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (args[i] == "--ext" && i + 1 < args.Count)
                {
                    extension = args[i + 1];
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                }
                else if (args[i] == "--dir" && i + 1 < args.Count)
                {
                    directory = args[i + 1];
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                }
            }
            if (args.Count < 1) return Usage("img-pack <folder> [out.img] [--dir out.dir] [--ext .mdl]");
            Operations.Outcome outcome = Operations.ImgPack(args[0], args.Count > 1 ? args[1] : null,
                directory, extension, Progress());
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int ImgRepackCommand(List<string> args)
        {
            string directory = null, folder = null, outImg = null, outDir = null, outDtz = null;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (i + 1 >= args.Count) continue;
                switch (args[i])
                {
                    case "--dir": directory = args[i + 1]; args.RemoveAt(i + 1); args.RemoveAt(i); break;
                    case "--folder": folder = args[i + 1]; args.RemoveAt(i + 1); args.RemoveAt(i); break;
                    case "--out": outImg = args[i + 1]; args.RemoveAt(i + 1); args.RemoveAt(i); break;
                    case "--out-dir": outDir = args[i + 1]; args.RemoveAt(i + 1); args.RemoveAt(i); break;
                    case "--out-dtz": outDtz = args[i + 1]; args.RemoveAt(i + 1); args.RemoveAt(i); break;
                }
            }
            if (args.Count < 2)
                return Usage("img-repack <image.img> <GAME.DTZ> [--dir x.dir] [--folder dir] " +
                             "[--out new.img] [--out-dir new.dir] [--out-dtz new.dtz]");

            Operations.Outcome outcome = Operations.ImgRepackCmd(args[0], args[1], directory, folder,
                outImg, outDir, outDtz, Progress());
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int DtzInspect(ModKitContext ctx, List<string> args)
        {
            string names = TakeOption(args, "--names");
            if (args.Count < 1) return Usage("dtz-inspect <GAME.DTZ> [--names file]");
            Operations.Outcome outcome = Operations.DtzInspect(args[0], names);
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int DtzDat(List<string> args)
        {
            if (args.Count < 1) return Usage("dtz-dat <GAME.DTZ> [out.dat]");
            PrintOutcome(Operations.DtzExportDat(args[0], args.Count > 1 ? args[1] : null));
            return 0;
        }

        private static int DtzDtz(List<string> args)
        {
            if (args.Count < 1) return Usage("dtz-dtz <GAME.DAT> [out.dtz]");
            PrintOutcome(Operations.DtzExportDtz(args[0], args.Count > 1 ? args[1] : null));
            return 0;
        }

        private static int DtzDir(ModKitContext ctx, List<string> args)
        {
            string names = TakeOption(args, "--names");
            if (args.Count < 1) return Usage("dtz-dir <GAME.DTZ> [out.dir] [--names file]");
            Operations.Outcome outcome = Operations.DtzExportDir(args[0],
                args.Count > 1 ? args[1] : null, names);
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int DtzModels(ModKitContext ctx, List<string> args)
        {
            string names = TakeOption(args, "--names");
            int limit = 50;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (args[i] == "--limit" && i + 1 < args.Count)
                {
                    int.TryParse(args[i + 1], out limit);
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                }
            }
            if (args.Count < 1) return Usage("dtz-models <GAME.DTZ> [--names file] [--limit N]");
            Operations.Outcome outcome = Operations.DtzModels(args[0], names, limit);
            if (!outcome.Ok)
            {
                PrintOutcome(outcome);
                return 1;
            }
            var models = outcome.Data.ContainsKey("models") ? outcome.Data["models"] as List<object> : null;
            if (models != null)
                foreach (object item in models)
                {
                    var model = item as Dictionary<string, object>;
                    if (model == null) continue;
                    Console.WriteLine(string.Format("  {0,5}  {1,-24} {2,-10} {3,8} +{4,-6} tex={5}",
                        Json.GetLong(model, "id", 0), Json.GetString(model, "name", ""),
                        Json.GetString(model, "hash", ""), Json.GetLong(model, "offset", 0),
                        Json.GetLong(model, "sectors", 0), Json.GetString(model, "texName", "")));
                }
            Console.WriteLine(outcome.Message);
            return 0;
        }

        private static int ChkListCommand(List<string> args)
        {
            if (args.Count < 1) return Usage("chk-list <file.chk>");
            Operations.Outcome outcome = Operations.ChkList(args[0]);
            if (outcome.Ok && outcome.Data.ContainsKey("textures"))
            {
                var textures = outcome.Data["textures"] as List<object>;
                if (textures != null)
                    foreach (object item in textures)
                    {
                        var texture = item as Dictionary<string, object>;
                        if (texture == null) continue;
                        Console.WriteLine(string.Format("  {0,-24} {1,5}x{2,-5} {3,-10} {4} bytes",
                            Json.GetString(texture, "name", ""),
                            Json.GetLong(texture, "width", 0), Json.GetLong(texture, "height", 0),
                            Json.GetString(texture, "format", ""),
                            Json.GetLong(texture, "dataLength", 0)));
                    }
            }
            Console.WriteLine(outcome.Message);
            return outcome.Ok ? 0 : 1;
        }

        private static int ChkExport(List<string> args)
        {
            if (args.Count < 1) return Usage("chk-export <file.chk> [folder]");
            Operations.Outcome outcome = Operations.ChkExport(args[0], args.Count > 1 ? args[1] : null);
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int ChkReplace(List<string> args)
        {
            string name = null;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (args[i] == "--name" && i + 1 < args.Count)
                {
                    name = args[i + 1];
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                }
            }
            if (args.Count < 2) return Usage("chk-replace <file.chk> <image.png> [index] [out.chk] [--name tex]");

            int index = 0;
            string target = null;
            if (args.Count > 2)
            {
                if (!int.TryParse(args[2], out index))
                {
                    target = args[2];
                }
                else if (args.Count > 3)
                {
                    target = args[3];
                }
            }

            Operations.Outcome outcome = Operations.ChkReplace(args[0], args[1], index, name, target);
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int GxtDump(List<string> args)
        {
            if (args.Count < 1) return Usage("gxt-dump <file.gxt> [out.txt]");
            Operations.Outcome outcome = Operations.GxtDump(args[0], args.Count > 1 ? args[1] : null);
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int GxtImport(List<string> args)
        {
            if (args.Count < 2) return Usage("gxt-import <file.gxt> <edited.txt> [out.gxt]");
            Operations.Outcome outcome = Operations.GxtImport(args[0], args[1],
                args.Count > 2 ? args[2] : null);
            PrintOutcome(outcome);
            return outcome.Ok ? 0 : 1;
        }

        private static int VagToWav(List<string> args)
        {
            if (args.Count < 1) return Usage("vag-to-wav <file.vag> [out.wav]");
            PrintOutcome(Operations.VagToWav(args[0], args.Count > 1 ? args[1] : null));
            return 0;
        }

        private static int WavToVag(List<string> args)
        {
            if (args.Count < 1) return Usage("wav-to-vag <file.wav> [out.vag]");
            PrintOutcome(Operations.WavToVag(args[0], args.Count > 1 ? args[1] : null));
            return 0;
        }

        private static int ProjectList(ModKitContext ctx)
        {
            List<Project> projects = ctx.Workspace.List();
            if (projects.Count == 0) Console.WriteLine("no projects yet (project-new <name>)");
            foreach (Project project in projects)
                Console.WriteLine(string.Format("  {0,-24} {1,-4} {2}  code={3}  files={4}",
                    project.Id, project.Game, project.Name, project.SessionCode,
                    project.ListFiles(0).Count));
            return 0;
        }

        private static int ProjectNew(ModKitContext ctx, List<string> args)
        {
            if (args.Count < 1) return Usage("project-new <name> [game]");
            string game = args.Count > 1 ? args[1] : "LCS";
            Project project = ctx.Workspace.Create(args[0], game, "cli", "");
            Console.WriteLine("created " + project.Id + " with session code " + project.SessionCode);
            return 0;
        }

        private static int ProjectInfo(ModKitContext ctx, List<string> args)
        {
            if (args.Count < 1) return Usage("project-info <id>");
            Project project = ctx.Workspace.Get(args[0]);
            if (project == null)
            {
                Console.Error.WriteLine("no such project: " + args[0]);
                return 1;
            }
            Console.WriteLine("id        : " + project.Id);
            Console.WriteLine("name      : " + project.Name);
            Console.WriteLine("game      : " + project.Game + " / " + project.Platform);
            Console.WriteLine("owner     : " + project.Owner);
            Console.WriteLine("code      : " + project.SessionCode);
            Console.WriteLine("created   : " + project.Created.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine("modified  : " + project.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine("files     : " + project.ListFiles(0).Count);
            Console.WriteLine("ops       : " + project.Ops.Count);
            Console.WriteLine("online    : " + ctx.Workspace.Sessions.OnlineCount(project.Id));
            return 0;
        }

        private static Action<int> Progress()
        {
            return delegate(int percent)
            {
                if (percent % 10 == 0) Console.Write("\r  " + percent.ToString(CultureInfo.InvariantCulture) + "% ");
            };
        }

        /// <summary>Removes "--name value" from the argument list and returns the value.</summary>
        private static string TakeOption(List<string> args, string option)
        {
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (args[i] == option && i + 1 < args.Count)
                {
                    string value = args[i + 1];
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                    return value;
                }
            }
            return null;
        }

        private static int Usage(string text)
        {
            Console.Error.WriteLine("usage: " + text);
            return 2;
        }

        private static void PrintOutcome(Operations.Outcome outcome)
        {
            Console.WriteLine((outcome.Ok ? "ok: " : "fail: ") + outcome.Message);
            foreach (var pair in outcome.Data)
            {
                string value;
                if (pair.Value == null)
                {
                    value = "";
                }
                else if (pair.Value is System.Collections.IDictionary || pair.Value is System.Collections.IList)
                {
                    value = Json.Stringify(pair.Value, false);
                }
                else
                {
                    value = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
                if (value.Length > 300) value = value.Substring(0, 297) + "...";
                Console.WriteLine("  " + pair.Key + ": " + value);
            }
        }
    }
}
