using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ModKit.Core
{
    public enum LogLevel
    {
        Verbose = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Fatal = 5
    }

    public static class Cat
    {
        public const string App = "APP";
        public const string Config = "CONFIG";
        public const string Net = "NET";
        public const string Http = "HTTP";
        public const string Api = "API";
        public const string Fmt = "FMT";
        public const string Job = "JOB";
        public const string Fs = "FS";
        public const string Project = "PROJ";
        public const string Session = "SESS";
        public const string Log = "LOG";
        public const string Ui = "UI";
        public const string Fault = "FAULT";
    }

    /// <summary>
    /// Trace based logging without App.config, without a second console sink.
    ///
    ///   * one file listener  -> complete verbose log on disk;
    ///   * one console listener -> only the operational subset;
    ///   * the framework DefaultTraceListener is removed, so nothing is printed twice;
    ///   * the application never writes log text with Console.Write itself.
    /// </summary>
    public static class Log
    {
        private const int RingCapacity = 500;

        private static readonly object Sync = new object();
        private static readonly TraceSource Source = new TraceSource("DPT.GTA.ModKit", SourceLevels.All);
        private static readonly Queue<string> Ring = new Queue<string>(RingCapacity);

        private static FileTraceListener _file;
        private static ConsoleTraceListener _console;
        private static bool _ready;

        public static event Action<string> LineWritten;

        public static string LogDirectory { get; private set; }
        public static string CurrentFile { get { return _file == null ? null : _file.Path; } }

        public static void Start(string logDirectory, LogLevel fileLevel, LogLevel consoleLevel)
        {
            lock (Sync)
            {
                if (_ready)
                {
                    SetLevels(fileLevel, consoleLevel);
                    return;
                }

                LogDirectory = logDirectory;
                try
                {
                    if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
                        Directory.CreateDirectory(logDirectory);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("log directory unavailable (" + ex.Message + "), console only");
                }

                Trace.Listeners.Clear();
                Source.Listeners.Clear();

                _file = new FileTraceListener(logDirectory, "modkit", fileLevel, 8L * 1024 * 1024, 5);
                _console = new ConsoleTraceListener(consoleLevel);
                Source.Listeners.Add(_file);
                Source.Listeners.Add(_console);
                _ready = true;

                Info(Cat.Log, "logging started: dir=" + logDirectory +
                              " file=" + fileLevel + " console=" + consoleLevel);
            }
        }

        public static void SetLevels(LogLevel fileLevel, LogLevel consoleLevel)
        {
            lock (Sync)
            {
                if (_file != null) _file.MinLevel = fileLevel;
                if (_console != null) _console.MinLevel = consoleLevel;
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                if (!_ready) return;
                _ready = false;
                try
                {
                    Source.Flush();
                    Source.Close();
                }
                catch (Exception) { }
            }
        }

        public static void Verbose(string category, string message) { Write(LogLevel.Verbose, category, message); }
        public static void Debug(string category, string message) { Write(LogLevel.Debug, category, message); }
        public static void Info(string category, string message) { Write(LogLevel.Info, category, message); }
        public static void Warn(string category, string message) { Write(LogLevel.Warn, category, message); }
        public static void Error(string category, string message) { Write(LogLevel.Error, category, message); }

        public static void Error(string category, string message, Exception ex)
        {
            Write(LogLevel.Error, category, message + " :: " + Describe(ex));
        }

        public static void Fatal(string category, string message, Exception ex)
        {
            Write(LogLevel.Fatal, category, message + " :: " + Describe(ex));
        }

        public static string Describe(Exception ex)
        {
            if (ex == null) return "(no exception)";
            Exception root = ex;
            while (root.InnerException != null) root = root.InnerException;
            string text = root.GetType().Name + ": " + root.Message;
            if (!ReferenceEquals(root, ex)) text += " (via " + ex.GetType().Name + ")";
            return text.Replace("\r", " ").Replace("\n", " ");
        }

        public static IList<string> Tail(int count)
        {
            lock (Ring)
            {
                var all = new List<string>(Ring);
                int start = all.Count - count;
                if (start < 0) start = 0;
                return all.GetRange(start, all.Count - start);
            }
        }

        private static void Write(LogLevel level, string category, string message)
        {
            if (message == null) message = "";
            string text = string.Format(CultureInfo.InvariantCulture, "{0} [{1}] {2}",
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), LevelTag(level), category);

            string line = text + " " + message;
            lock (Ring)
            {
                Ring.Enqueue(line);
                while (Ring.Count > RingCapacity) Ring.Dequeue();
            }

            if (_ready)
            {
                try
                {
                    Source.TraceEvent(ToEventType(level), 0, "{0} {1}", category,
                        message.Replace("\r", " ").Replace("\n", " "));
                }
                catch (Exception) { }
            }

            Action<string> handler = LineWritten;
            if (handler != null)
            {
                try { handler(line); } catch (Exception) { }
            }
        }

        private static TraceEventType ToEventType(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Verbose:
                case LogLevel.Debug: return TraceEventType.Verbose;
                case LogLevel.Info: return TraceEventType.Information;
                case LogLevel.Warn: return TraceEventType.Warning;
                case LogLevel.Error: return TraceEventType.Error;
                default: return TraceEventType.Critical;
            }
        }

        public static string LevelTag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Verbose: return "VRB";
                case LogLevel.Debug: return "DBG";
                case LogLevel.Info: return "INF";
                case LogLevel.Warn: return "WRN";
                case LogLevel.Error: return "ERR";
                default: return "FTL";
            }
        }

        public static LogLevel ParseLevel(string text, LogLevel fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            switch (text.Trim().ToLowerInvariant())
            {
                case "verbose": return LogLevel.Verbose;
                case "debug": return LogLevel.Debug;
                case "info": return LogLevel.Info;
                case "warn":
                case "warning": return LogLevel.Warn;
                case "error": return LogLevel.Error;
                default: return fallback;
            }
        }

        public static LogLevel FromEventType(TraceEventType type)
        {
            switch (type)
            {
                case TraceEventType.Verbose: return LogLevel.Verbose;
                case TraceEventType.Information: return LogLevel.Info;
                case TraceEventType.Warning: return LogLevel.Warn;
                case TraceEventType.Error: return LogLevel.Error;
                case TraceEventType.Critical: return LogLevel.Fatal;
                default: return LogLevel.Info;
            }
        }

        internal abstract class BaseListener : TraceListener
        {
            public LogLevel MinLevel;

            protected BaseListener(LogLevel minLevel)
            {
                MinLevel = minLevel;
            }

            public override void TraceEvent(TraceEventCache eventCache, string source,
                TraceEventType eventType, int id, string message)
            {
                LogLevel level = FromEventType(eventType);
                if (level < MinLevel) return;
                Emit(level, message);
            }

            public override void TraceEvent(TraceEventCache eventCache, string source,
                TraceEventType eventType, int id, string format, params object[] args)
            {
                LogLevel level = FromEventType(eventType);
                if (level < MinLevel) return;
                string message = args == null || args.Length == 0
                    ? format
                    : string.Format(CultureInfo.InvariantCulture, format, args);
                Emit(level, message);
            }

            public override void Write(string message) { if (MinLevel <= LogLevel.Verbose) Emit(LogLevel.Verbose, message); }
            public override void WriteLine(string message) { if (MinLevel <= LogLevel.Verbose) Emit(LogLevel.Verbose, message); }

            protected abstract void Emit(LogLevel level, string message);
        }

        private sealed class FileTraceListener : BaseListener
        {
            private readonly object _sync = new object();
            private readonly string _directory;
            private readonly string _prefix;
            private readonly long _maxBytes;
            private readonly int _keep;
            private StreamWriter _writer;
            private DateTime _day = DateTime.MinValue;
            private int _index;
            private string _currentPath;

            public FileTraceListener(string directory, string prefix, LogLevel minLevel, long maxBytes, int keep)
                : base(minLevel)
            {
                _directory = directory;
                _prefix = prefix;
                _maxBytes = maxBytes;
                _keep = keep;
            }

            public string Path
            {
                get { lock (_sync) return _currentPath; }
            }

            protected override void Emit(LogLevel level, string message)
            {
                lock (_sync)
                {
                    try
                    {
                        EnsureWriter();
                        if (_writer == null) return;
                        _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                                          + " [" + LevelTag(level) + "] " + message);
                        _writer.Flush();
                        if (_writer.BaseStream.Length > _maxBytes) Roll();
                    }
                    catch (Exception) { }
                }
            }

            private void EnsureWriter()
            {
                DateTime today = DateTime.Now.Date;
                if (_writer != null && today == _day) return;
                CloseWriter();
                _day = today;
                _index = 0;
                OpenFile();
                Prune();
            }

            private void Roll()
            {
                CloseWriter();
                _index++;
                OpenFile();
                Prune();
            }

            private void CloseWriter()
            {
                if (_writer == null) return;
                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch (Exception) { }
                _writer = null;
            }

            private void OpenFile()
            {
                if (string.IsNullOrEmpty(_directory)) return;
                if (!Directory.Exists(_directory)) Directory.CreateDirectory(_directory);
                string name = _prefix + "-" + _day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (_index > 0) name += "." + _index.ToString("000", CultureInfo.InvariantCulture);
                _currentPath = System.IO.Path.Combine(_directory, name + ".log");
                var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                // UTF-8 with BOM: PowerShell/Notepad then show Cyrillic correctly.
                _writer = new StreamWriter(stream, new UTF8Encoding(true));
            }

            private void Prune()
            {
                try
                {
                    string[] files = Directory.GetFiles(_directory, _prefix + "-*.log");
                    if (files.Length <= _keep) return;
                    Array.Sort(files, StringComparer.Ordinal);
                    for (int i = 0; i < files.Length - _keep; i++)
                    {
                        try { File.Delete(files[i]); } catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }

            protected override void Dispose(bool disposing)
            {
                lock (_sync) CloseWriter();
                base.Dispose(disposing);
            }
        }

        private sealed class ConsoleTraceListener : BaseListener
        {
            private readonly object _sync = new object();

            public ConsoleTraceListener(LogLevel minLevel) : base(minLevel) { }

            protected override void Emit(LogLevel level, string message)
            {
                if (level < MinLevel) return;
                int space = message.IndexOf(' ');
                string category = space < 0 ? message : message.Substring(0, space);
                if (!Important(category, level)) return;

                lock (_sync)
                {
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                                      + " [" + LevelTag(level) + "] " + message);
                }
            }

            /// <summary>
            /// File keeps everything; the console keeps what an operator can act on.
            /// Errors and warnings of any category always show up.
            /// </summary>
            private static bool Important(string category, LogLevel level)
            {
                if (level >= LogLevel.Warn) return true;
                switch (category)
                {
                    case Cat.App:
                    case Cat.Config:
                    case Cat.Net:
                    case Cat.Api:
                    case Cat.Project:
                    case Cat.Session:
                    case Cat.Fault:
                    case Cat.Fs:
                        return true;
                    default:
                        return false;
                }
            }
        }
    }
}
