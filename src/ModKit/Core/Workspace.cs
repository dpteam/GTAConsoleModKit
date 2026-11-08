using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ModKit.Core
{
    /// <summary>
    /// Project cache + collaborative session presence.
    ///
    /// Session model (see docs/SESSIONS.md):
    ///   * every project carries a code (ABCD-2345), so a session can be joined
    ///     offline by handing over the folder, or online through the web server;
    ///   * all changes are appended to the project's operation log with a
    ///     monotonically growing sequence number;
    ///   * clients poll "everything after seq N", which is a poor man's WebSocket
    ///     that works behind any proxy and on Windows XP;
    ///   * presence is kept in memory only (nickname + last seen + client id).
    /// </summary>
    public sealed class Workspace
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Project> _cache =
            new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
        private readonly SessionHub _hub = new SessionHub();

        public string RootPath { get; private set; }
        public SessionHub Sessions { get { return _hub; } }

        public Workspace(string rootPath)
        {
            RootPath = rootPath;
            if (!Directory.Exists(RootPath)) Directory.CreateDirectory(RootPath);
        }

        public Project Create(string name, string game, string owner, string description)
        {
            Project project = Project.Create(RootPath, name, game, owner, description);
            lock (_sync) _cache[project.Id] = project;
            return project;
        }

        public Project Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_sync)
            {
                Project cached;
                if (_cache.TryGetValue(id, out cached)) return cached;
            }

            string root = Util.SafeCombine(RootPath, id);
            if (root == null || !Directory.Exists(root)) return null;
            try
            {
                Project project = Project.Load(root);
                lock (_sync) _cache[project.Id] = project;
                return project;
            }
            catch (Exception ex)
            {
                Log.Warn(Cat.Project, "cannot load project " + id + ": " + Log.Describe(ex));
                return null;
            }
        }

        public List<Project> List()
        {
            List<Project> projects = Project.ListAll(RootPath);
            lock (_sync)
            {
                foreach (Project project in projects) _cache[project.Id] = project;
            }
            return projects;
        }

        public bool Delete(string id)
        {
            string root = Util.SafeCombine(RootPath, id);
            if (root == null || !Directory.Exists(root)) return false;
            try
            {
                lock (_sync) _cache.Remove(id);
                Directory.Delete(root, true);
                Log.Info(Cat.Project, "deleted project " + id);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(Cat.Project, "cannot delete project " + id, ex);
                return false;
            }
        }

        public Project BySessionCode(string code)
        {
            string normalized = SessionCodes.Normalize(code);
            foreach (Project project in List())
                if (string.Equals(project.SessionCode, normalized, StringComparison.OrdinalIgnoreCase))
                    return project;
            return null;
        }
    }

    /// <summary>Live participants of one project session.</summary>
    public sealed class SessionHub
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Dictionary<string, Participant>> _rooms =
            new Dictionary<string, Dictionary<string, Participant>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _activity = new Dictionary<string, int>(StringComparer.Ordinal);

        public sealed class Participant
        {
            public string ClientId;
            public string Nickname;
            public DateTime Joined;
            public DateTime LastSeen;
            public string Address;
        }

        public Participant Join(string projectId, string nickname, string clientId, string address)
        {
            lock (_sync)
            {
                Dictionary<string, Participant> room = Room(projectId);
                if (string.IsNullOrEmpty(clientId)) clientId = Guid.NewGuid().ToString("N");
                Participant participant;
                if (!room.TryGetValue(clientId, out participant))
                {
                    participant = new Participant
                    {
                        ClientId = clientId,
                        Joined = DateTime.Now,
                        Nickname = string.IsNullOrEmpty(nickname) ? "guest" : nickname
                    };
                    room[clientId] = participant;
                    Log.Info(Cat.Session, participant.Nickname + " joined session " + projectId +
                                         " (" + room.Count + " online)");
                }
                participant.Nickname = string.IsNullOrEmpty(nickname) ? participant.Nickname : nickname;
                participant.LastSeen = DateTime.Now;
                participant.Address = address;
                Bump(projectId);
                return participant;
            }
        }

        public void Touch(string projectId, string clientId)
        {
            lock (_sync)
            {
                Participant participant;
                if (Room(projectId).TryGetValue(clientId, out participant))
                    participant.LastSeen = DateTime.Now;
            }
        }

        public void Leave(string projectId, string clientId)
        {
            lock (_sync)
            {
                Dictionary<string, Participant> room = Room(projectId);
                Participant participant;
                if (room.TryGetValue(clientId, out participant))
                {
                    room.Remove(clientId);
                    Log.Info(Cat.Session, participant.Nickname + " left session " + projectId +
                                         " (" + room.Count + " online)");
                }
                Bump(projectId);
            }
        }

        public List<Participant> Members(string projectId)
        {
            lock (_sync)
            {
                Prune();
                var result = new List<Participant>(Room(projectId).Values);
                result.Sort(delegate(Participant a, Participant b) { return a.Joined.CompareTo(b.Joined); });
                return result;
            }
        }

        public int OnlineCount(string projectId)
        {
            lock (_sync)
            {
                Prune();
                return Room(projectId).Count;
            }
        }

        /// <summary>Sequence number of the last change in the room (for cheap polling).</summary>
        public int Activity(string projectId)
        {
            lock (_sync)
            {
                int value;
                return _activity.TryGetValue(projectId, out value) ? value : 0;
            }
        }

        public void NotifyChange(string projectId)
        {
            lock (_sync) Bump(projectId);
        }

        private void Bump(string projectId)
        {
            int value;
            _activity.TryGetValue(projectId, out value);
            _activity[projectId] = value + 1;
            Monitor.PulseAll(_sync);
        }

        /// <summary>Waits until something happens in the room or the timeout expires.</summary>
        public bool WaitForChange(string projectId, int seenActivity, int timeoutMs)
        {
            lock (_sync)
            {
                if (Activity(projectId) != seenActivity) return true;
                Monitor.Wait(_sync, Math.Max(50, timeoutMs));
                return Activity(projectId) != seenActivity;
            }
        }

        private Dictionary<string, Participant> Room(string projectId)
        {
            Dictionary<string, Participant> room;
            if (!_rooms.TryGetValue(projectId, out room))
            {
                room = new Dictionary<string, Participant>(StringComparer.Ordinal);
                _rooms[projectId] = room;
            }
            return room;
        }

        private void Prune()
        {
            DateTime cutoff = DateTime.Now.AddSeconds(-30);
            foreach (Dictionary<string, Participant> room in _rooms.Values)
            {
                var stale = new List<string>();
                foreach (var pair in room)
                    if (pair.Value.LastSeen < cutoff) stale.Add(pair.Key);
                foreach (string key in stale) room.Remove(key);
            }
        }
    }
}
