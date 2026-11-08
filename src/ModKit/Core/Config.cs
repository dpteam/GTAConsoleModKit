using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ModKit.Core
{
    /// <summary>
    /// config.ini next to the executable. Deliberately not app.config / user.config:
    /// the tool has to work from a folder copied to a USB stick or an XP machine.
    /// </summary>
    public sealed class Config
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Path { get; private set; }

        public static Config Load(string path)
        {
            var config = new Config { Path = path };
            if (File.Exists(path))
            {
                try
                {
                    config.Parse(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    Log.Error(Cat.Config, "cannot read " + path + ", using defaults", ex);
                }
            }
            return config;
        }

        private void Parse(string text)
        {
            string section = "";
            foreach (string rawLine in Util.SplitLines(text))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    if (close > 0) section = line.Substring(1, close - 1).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    value = value.Substring(1, value.Length - 2);
                _values[section + "." + key] = value;
            }
        }

        public string Get(string key, string fallback)
        {
            string value;
            return _values.TryGetValue(key, out value) && value.Length > 0 ? value : fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int value;
            return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        public long GetLong(string key, long fallback)
        {
            long value;
            return long.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            string value = Get(key, "").ToLowerInvariant();
            if (value == "1" || value == "true" || value == "yes" || value == "on") return true;
            if (value == "0" || value == "false" || value == "no" || value == "off") return false;
            return fallback;
        }

        public string[] GetList(string key, string fallback)
        {
            string value = Get(key, fallback);
            if (string.IsNullOrEmpty(value)) return new string[0];
            string[] parts = value.Split(',');
            var result = new List<string>();
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0) result.Add(trimmed);
            }
            return result.ToArray();
        }

        public void Set(string key, string value)
        {
            _values[key] = value;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; DPT.PSP GTA Modding Kit configuration");
            sb.AppendLine("; written by the tool itself - safe to edit by hand, restart afterwards");
            sb.AppendLine();

            string lastSection = null;
            var keys = new List<string>(_values.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string fullKey in keys)
            {
                int dot = fullKey.IndexOf('.');
                string section = dot < 0 ? "" : fullKey.Substring(0, dot);
                string key = dot < 0 ? fullKey : fullKey.Substring(dot + 1);
                if (section != lastSection)
                {
                    sb.AppendLine("[" + section + "]");
                    lastSection = section;
                }
                sb.AppendLine(key + "=" + _values[fullKey]);
            }

            try
            {
                Util.WriteAllTextAtomic(Path, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Error(Cat.Config, "cannot save " + Path, ex);
            }
        }

        public static string DefaultIni()
        {
            return @"; DPT.PSP GTA Modding Kit - default configuration
; Everything here is optional: missing keys fall back to these values.

[server]
; web UI (the modding kit front-end)
webPort=44700
webPortFallbacks=44710,44720,44730
; 0.0.0.0 = reachable from the LAN (phone, second PC, team sessions)
; 127.0.0.1 = this machine only
bind=0.0.0.0
; access from other machines needs this token. Empty = generated once and kept in
; bin\auth.token; requests from 127.0.0.1 never need it.
token=
; full    = token holders may do everything the kit can
; session = token holders may only work inside projects (no arbitrary paths,
;           no /api/fs browsing, no running operations). Use it when the kit is
;           reachable through a reverse proxy.
exposure=full
; how many HTTP workers serve requests
workers=4

[workspace]
; root folder for projects (created on demand)
root=workspace
; where extracted game dumps are looked up by default
gamesRoot=

[logging]
; file gets everything, console only the operational subset (see docs/LOGGING.md)
fileLevel=Verbose
consoleLevel=Info
directory=logs
; keep the ring buffer small on old machines
webTailLines=300

[resilience]
; retries for file/format operations that can fail transiently
retries=3
retryBaseMs=200
; circuit breaker: after N failures the subsystem starts reporting fast failures
breakerThreshold=5
breakerCooldownSec=30
; the supervisor restarts a dead listener with growing delays
restartDelayMinMs=500
restartDelayMaxMs=30000

[limits]
maxUploadMb=512
maxJobHistory=200
maxOpsPerSession=5000

[session]
; offline sessions store their code in the project file, online ones announce it
allowLan=true
opPollMs=800
";
        }
    }
}
