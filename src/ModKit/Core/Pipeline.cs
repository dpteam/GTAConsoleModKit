using System;
using System.Collections.Generic;
using System.Threading;

namespace ModKit.Core
{
    /// <summary>
    /// Background job runner. Long format operations (ISO extract, IMG repack,
    /// texture dump) run here so the HTTP layer answers instantly and the UI can
    /// show progress. A failing job never takes the process down: its exception is
    /// logged, the job is marked failed and the worker moves on.
    /// </summary>
    public sealed class Pipeline : IDisposable
    {
        public enum JobState
        {
            Queued,
            Running,
            Done,
            Failed,
            Cancelled
        }

        public sealed class Job
        {
            public string Id;
            public string Name;
            public string Kind;
            public volatile JobState State;
            public volatile int Progress;      // 0..100, -1 = unknown
            public string Message = "";
            public DateTime Created;
            public DateTime Started;
            public DateTime Finished;
            public Dictionary<string, object> Result;
            public List<string> Log = new List<string>();
            public volatile bool CancelRequested;
            internal Action<Job> Work;
            private readonly object _sync = new object();

            public bool IsFinished
            {
                get { return State == JobState.Done || State == JobState.Failed || State == JobState.Cancelled; }
            }

            public void Report(int progress, string message)
            {
                Progress = progress;
                if (message != null) Message = message;
            }

            public void Note(string text)
            {
                lock (_sync)
                {
                    Log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text);
                    if (Log.Count > 200) Log.RemoveAt(0);
                }
                Message = text;
            }

            public Dictionary<string, object> ToJson()
            {
                List<object> notes;
                lock (_sync) notes = new List<object>(Log);
                return Json.O(
                    "id", Id,
                    "name", Name,
                    "kind", Kind,
                    "state", State.ToString().ToLowerInvariant(),
                    "progress", (long)Progress,
                    "message", Message ?? "",
                    "created", Created.ToString("yyyy-MM-dd HH:mm:ss"),
                    "started", Started == DateTime.MinValue ? "" : Started.ToString("yyyy-MM-dd HH:mm:ss"),
                    "finished", Finished == DateTime.MinValue ? "" : Finished.ToString("yyyy-MM-dd HH:mm:ss"),
                    "log", notes,
                    "result", Result);
            }
        }

        private readonly object _sync = new object();
        private readonly Queue<Job> _queue = new Queue<Job>();
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();
        private readonly List<Thread> _workers = new List<Thread>();
        private readonly int _maxHistory;
        private volatile bool _stopping;
        private int _counter;

        public Pipeline(int workers, int maxHistory)
        {
            _maxHistory = Math.Max(10, maxHistory);
            for (int i = 0; i < Math.Max(1, workers); i++)
            {
                var thread = new Thread(WorkerLoop);
                thread.IsBackground = true;
                thread.Name = "job-" + i;
                _workers.Add(thread);
                thread.Start();
            }
        }

        public Job Enqueue(string name, string kind, Action<Job> work)
        {
            var job = new Job
            {
                Id = "job-" + Interlocked.Increment(ref _counter).ToString("0000"),
                Name = name,
                Kind = kind,
                State = JobState.Queued,
                Progress = 0,
                Created = DateTime.Now
            };
            job.Work = work;

            lock (_sync)
            {
                _jobs[job.Id] = job;
                _order.Add(job.Id);
                TrimHistory();
                _queue.Enqueue(job);
                Monitor.PulseAll(_sync);
            }

            Log.Debug(Cat.Job, "queued " + job.Id + " " + kind + " '" + name + "'");
            return job;
        }

        public Job Get(string id)
        {
            lock (_sync)
            {
                Job job;
                return _jobs.TryGetValue(id, out job) ? job : null;
            }
        }

        public List<Job> Recent(int count)
        {
            lock (_sync)
            {
                var result = new List<Job>();
                for (int i = _order.Count - 1; i >= 0 && result.Count < count; i--)
                {
                    Job job;
                    if (_jobs.TryGetValue(_order[i], out job)) result.Add(job);
                }
                return result;
            }
        }

        public bool Cancel(string id)
        {
            Job job = Get(id);
            if (job == null || job.IsFinished) return false;
            job.CancelRequested = true;
            Log.Info(Cat.Job, "cancel requested for " + id);
            return true;
        }

        private void TrimHistory()
        {
            while (_order.Count > _maxHistory * 2)
            {
                string oldest = _order[0];
                _order.RemoveAt(0);
                Job job;
                if (_jobs.TryGetValue(oldest, out job) && job.IsFinished) _jobs.Remove(oldest);
            }
        }

        private void WorkerLoop()
        {
            while (!_stopping)
            {
                Job job;
                lock (_sync)
                {
                    while (!_stopping && _queue.Count == 0) Monitor.Wait(_sync, 1000);
                    if (_stopping) return;
                    if (_queue.Count == 0) continue;
                    job = _queue.Dequeue();
                }

                job.State = JobState.Running;
                job.Started = DateTime.Now;
                job.Report(0, "started");
                Log.Info(Cat.Job, job.Id + " " + job.Kind + " started: " + job.Name);

                try
                {
                    Action<Job> work = job.Work;
                    if (work != null) work(job);
                    job.State = job.CancelRequested ? JobState.Cancelled : JobState.Done;
                    if (job.State == JobState.Done) job.Report(100, "done");
                    Log.Info(Cat.Job, job.Id + " finished: " + job.State);
                }
                catch (OperationCanceledException)
                {
                    job.State = JobState.Cancelled;
                    job.Report(job.Progress, "cancelled");
                }
                catch (Exception ex)
                {
                    job.State = JobState.Failed;
                    job.Note("error: " + Log.Describe(ex));
                    Log.Error(Cat.Job, job.Id + " failed", ex);
                }
                finally
                {
                    job.Finished = DateTime.Now;
                    job.Work = null;
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            lock (_sync) Monitor.PulseAll(_sync);
            foreach (Thread thread in _workers) thread.Join(1500);
        }
    }
}
