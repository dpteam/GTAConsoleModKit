using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ModKit.Cli;
using ModKit.Core;
using ModKit.Web;

namespace ModKit
{
    /// <summary>
    /// Entry point. Two front ends share one core:
    ///   * "serve" (default when no command is given) starts the web UI;
    ///   * any other first argument is a headless command.
    /// </summary>
    public static class Program
    {
        public const string Version = "0.1.0";

        private static ModKitContext _context;
        private static WebServer _server;
        private static Resilience.Supervisor _supervisor;
        private static readonly ManualResetEventSlim Shutdown = new ManualResetEventSlim(false);

        public static int Main(string[] args)
        {
            try
            {
                _context = Bootstrap(args);
                Log.Info(Cat.App, "DPT.PSP GTA Modding Kit " + Version + " starting (" +
                                  (IntPtr.Size == 8 ? "x64" : "x86") + ", " + Environment.OSVersion.VersionString + ")");

                string command = args.Length > 0 ? args[0].ToLowerInvariant() : "serve";
                if (command == "serve" || command == "--web" || command == "web" ||
                    command == "--gui" || command == "--headless")
                {
                    // --headless alone still means "serve": the flag is accepted so a
                    // starter script can pass it without changing behaviour.
                    if (args.Length > 1 && command == "--headless")
                        return CommandLine.Run(_context, Slice(args));
                    return RunWeb(args);
                }

                return CommandLine.Run(_context, args);
            }
            catch (Exception ex)
            {
                Log.Fatal(Cat.App, "unhandled failure", ex);
                Console.Error.WriteLine("fatal: " + Log.Describe(ex));
                return 1;
            }
            finally
            {
                if (_supervisor != null) _supervisor.Dispose();
                if (_server != null) _server.Dispose();
                if (_context != null && _context.Pipeline != null) _context.Pipeline.Dispose();
                Log.Stop();
            }
        }

        private static string[] Slice(string[] args)
        {
            var result = new string[args.Length - 1];
            Array.Copy(args, 1, result, 0, result.Length);
            return result;
        }

        private static ModKitContext Bootstrap(string[] args)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var context = new ModKitContext
            {
                BaseDirectory = baseDirectory,
                StartedAt = DateTime.Now,
                Version = Version
            };

            string configPath = Path.Combine(baseDirectory, "config.ini");
            if (!File.Exists(configPath))
            {
                try
                {
                    Util.WriteAllTextAtomic(configPath, Config.DefaultIni());
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("cannot write config.ini: " + ex.Message);
                }
            }

            context.Config = Config.Load(configPath);

            string logDirectory = context.Config.Get("logging.directory", "logs");
            if (!Path.IsPathRooted(logDirectory))
                logDirectory = Path.Combine(baseDirectory, logDirectory);

            Log.Start(logDirectory,
                Log.ParseLevel(context.Config.Get("logging.fileLevel", "Verbose"), LogLevel.Verbose),
                Log.ParseLevel(context.Config.Get("logging.consoleLevel", "Info"), LogLevel.Info));

            context.PublicDirectory = Path.Combine(baseDirectory, "public");
            context.DataDirectory = Path.Combine(baseDirectory, "data");
            ModKit.Formats.DtzImage.DataDirectory = context.DataDirectory;

            string workspaceRoot = context.Config.Get("workspace.root", "workspace");
            if (!Path.IsPathRooted(workspaceRoot))
                workspaceRoot = Path.Combine(baseDirectory, workspaceRoot);
            context.WorkspaceRoot = workspaceRoot;

            string gamesRoot = context.Config.Get("workspace.gamesRoot", "");
            if (gamesRoot.Length > 0 && !Path.IsPathRooted(gamesRoot))
                gamesRoot = Path.Combine(baseDirectory, gamesRoot);
            context.GamesRoot = gamesRoot;

            context.Workspace = new Workspace(workspaceRoot);
            context.Pipeline = new Pipeline(
                Math.Max(1, context.Config.GetInt("server.workers", 2)),
                context.Config.GetInt("limits.maxJobHistory", 200));

            // What the web UI may read from. workspace and gamesRoot are always in;
            // "workspace.roots" adds more (a drive, a share, another dump folder).
            // Without any configured root the whole toolkit folder is allowed, which
            // keeps refs\ usable out of the box.
            var allowed = new List<string>();
            allowed.Add(workspaceRoot);
            if (gamesRoot.Length > 0) allowed.Add(gamesRoot);

            string configuredRoots = context.Config.Get("workspace.roots", "");
            bool anyConfigured = false;
            foreach (string piece in configuredRoots.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string root = piece.Trim();
                if (root.Length == 0) continue;
                if (!Path.IsPathRooted(root)) root = Path.Combine(baseDirectory, root);
                allowed.Add(root);
                anyConfigured = true;
            }
            if (!anyConfigured)
            {
                string toolkitRoot = Directory.GetParent(baseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) == null
                    ? baseDirectory
                    : Directory.GetParent(baseDirectory.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).FullName;
                allowed.Add(toolkitRoot);
            }

            FsGuard.Configure(allowed);

            context.Token = ModKit.Web.WebGuard.LoadToken(context.Config, baseDirectory);
            context.SessionOnly = string.Equals(context.Config.Get("server.exposure", "full").Trim(),
                "session", StringComparison.OrdinalIgnoreCase);

            Log.Info(Cat.Config, "workspace=" + workspaceRoot +
                                 (gamesRoot.Length > 0 ? ", gamesRoot=" + gamesRoot : ", gamesRoot=(unset)"));
            Log.Info(Cat.Config, "readable roots: " + FsGuard.Describe());
            Log.Info(Cat.Config, "remote access: token " + context.Token +
                                 (context.SessionOnly ? ", exposure=session" : ", exposure=full"));
            return context;
        }

        private static int RunWeb(string[] args)
        {
            int port = _context.Config.GetInt("server.webPort", 44700);
            string bind = _context.Config.Get("server.bind", "127.0.0.1");
            int[] fallbacks = ParsePorts(_context.Config.Get("server.webPortFallbacks", "44710,44720,44730"));

            for (int i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == "--port") int.TryParse(args[i + 1], out port);
                if (args[i] == "--bind") bind = args[i + 1];
            }

            _server = new WebServer(_context);
            _server.ConnectionLimit = _context.Config.GetInt("server.maxConnections", 64);
            _server.Start(port, bind, fallbacks);

            // The listener runs on its own thread; the supervisor restarts it if the
            // accept loop ever dies (port stolen, socket reset storms, ...).
            _supervisor = new Resilience.Supervisor("web", delegate
            {
                while (!Shutdown.IsSet && _server != null && _server.IsAlive)
                    Thread.Sleep(1000);
                if (Shutdown.IsSet) return false;
                if (_server != null && !_server.IsAlive) _server.Restart();
                return true;
            }, _context.Config.GetInt("resilience.restartDelayMinMs", 500),
               _context.Config.GetInt("resilience.restartDelayMaxMs", 30000));
            _supervisor.Start();

            PrintBanner();

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                Shutdown.Set();
            };

            // Console REPL: the important events are printed by the log listener,
            // these commands are for the operator sitting at the machine.
            var input = new Thread(ReplLoop);
            input.IsBackground = true;
            input.Name = "console";
            input.Start();

            Shutdown.Wait();
            Log.Info(Cat.App, "shutting down");
            return 0;
        }

        private static void PrintBanner()
        {
            Console.WriteLine();
            Console.WriteLine("  DPT.PSP GTA Modding Kit " + Version);
            Console.WriteLine("  web  : " + _server.BaseUrl);
            Console.WriteLine("  bind : " + _server.BindAddress + ":" + _server.Port +
                              "   workspace: " + _context.WorkspaceRoot);
            Console.WriteLine("  log  : " + (Log.CurrentFile ?? "(console only)"));
            if (!string.IsNullOrEmpty(_context.Token))
            {
                Console.WriteLine("  token: " + _context.Token +
                                  (_context.SessionOnly ? "   (exposure=session)" : ""));
                if (_server.BindAddress != "127.0.0.1")
                    Console.WriteLine("         other machines need this token once; 127.0.0.1 never does");
            }
            Console.WriteLine();
            Console.WriteLine("  commands: status | logs | projects | code <id> | jobs | quit");
            Console.WriteLine();
        }

        private static void ReplLoop()
        {
            while (!Shutdown.IsSet)
            {
                string line;
                try
                {
                    line = Console.ReadLine();
                }
                catch (Exception)
                {
                    return;
                }
                if (line == null)
                {
                    // stdin closed (double click / service) - keep serving
                    Thread.Sleep(500);
                    continue;
                }

                string text = line.Trim();
                if (text.Length == 0) continue;
                string command = text.ToLowerInvariant();
                try
                {
                    if (command == "quit" || command == "exit" || command == "q")
                    {
                        Shutdown.Set();
                        return;
                    }
                    if (command == "status")
                    {
                        Console.WriteLine("  uptime   : " +
                            Util.HumanDuration(DateTime.Now - _context.StartedAt));
                        Console.WriteLine("  projects : " + _context.Workspace.List().Count);
                        Console.WriteLine("  requests : " + _server.Requests + " (" + _server.Errors + " errors)");
                        Console.WriteLine("  jobs     : " + _context.Pipeline.Recent(100).Count);
                        Console.WriteLine("  log      : " + (Log.CurrentFile ?? "-"));
                        continue;
                    }
                    if (command == "logs")
                    {
                        foreach (string entry in Log.Tail(25)) Console.WriteLine("  " + entry);
                        continue;
                    }
                    if (command == "jobs")
                    {
                        foreach (Pipeline.Job job in _context.Pipeline.Recent(10))
                            Console.WriteLine(string.Format("  {0} {1,-10} {2,3}% {3}",
                                job.Id, job.State.ToString().ToLowerInvariant(), job.Progress, job.Name));
                        continue;
                    }
                    if (command == "projects")
                    {
                        foreach (Project project in _context.Workspace.List())
                            Console.WriteLine(string.Format("  {0,-20} {1,-4} code={2} files={3}",
                                project.Id, project.Game, project.SessionCode, project.ListFiles(0).Count));
                        continue;
                    }
                    if (command.StartsWith("code "))
                    {
                        string id = text.Substring(5).Trim();
                        Project project = _context.Workspace.Get(id);
                        Console.WriteLine(project == null
                            ? "  no such project"
                            : "  session code: " + project.SessionCode);
                        continue;
                    }
                    Console.WriteLine("  unknown command: " + text);
                }
                catch (Exception ex)
                {
                    Log.Error(Cat.App, "console command failed", ex);
                    Console.WriteLine("  error: " + Log.Describe(ex));
                }
            }
        }

        private static int[] ParsePorts(string text)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(text)) return result.ToArray();
            foreach (string part in text.Split(','))
            {
                int port;
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                    result.Add(port);
            }
            return result.ToArray();
        }
    }
}
