using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ModKit.Core
{
    /// <summary>
    /// Dynamic fault tolerance.
    ///
    /// "Dynamic" means: the toolkit keeps working when one piece misbehaves.
    ///  * every risky operation goes through <see cref="Guard"/>, which converts an
    ///    exception into a logged failure instead of a crash;
    ///  * <see cref="Retry"/> adds exponential backoff with jitter for transient IO;
    ///  * <see cref="CircuitBreaker"/> stops hammering a subsystem that keeps failing;
    ///  * <see cref="Supervisor"/> restarts supervised workers (web listener, watchers)
    ///    with a growing delay and records why they stopped.
    /// </summary>
    public static class Resilience
    {
        public sealed class Result
        {
            public bool Ok;
            public string Message;

            public static Result Success()
            {
                return new Result { Ok = true, Message = "" };
            }

            public static Result Failure(string message)
            {
                return new Result { Ok = false, Message = message };
            }
        }

        /// <summary>Runs the action, logs and swallows any exception.</summary>
        public static Result Guard(string name, Action action)
        {
            try
            {
                action();
                return Result.Success();
            }
            catch (Exception ex)
            {
                Log.Error(Cat.Fault, name + " failed", ex);
                return Result.Failure(Log.Describe(ex));
            }
        }

        public static Result Guard(string name, Action action, string fallbackMessage)
        {
            Result result = Guard(name, action);
            if (!result.Ok && !string.IsNullOrEmpty(fallbackMessage))
                Log.Warn(Cat.Fault, name + " degraded: " + fallbackMessage);
            return result;
        }

        public static T Guard<T>(string name, Func<T> func, T fallback)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                Log.Error(Cat.Fault, name + " failed", ex);
                return fallback;
            }
        }

        /// <summary>Retries with exponential backoff and jitter; returns null on success.</summary>
        public static Exception Retry(string name, int attempts, int baseDelayMs, Action action)
        {
            Exception last = null;
            var random = new Random(Environment.TickCount);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    action();
                    if (attempt > 1) Log.Info(Cat.Fault, name + " succeeded on attempt " + attempt);
                    return null;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == attempts) break;
                    int delay = baseDelayMs * (1 << (attempt - 1));
                    delay += random.Next(0, Math.Max(1, delay / 2));
                    Log.Warn(Cat.Fault, name + " attempt " + attempt + "/" + attempts +
                                       " failed (" + Log.Describe(ex) + "), retrying in " + delay + " ms");
                    Thread.Sleep(delay);
                }
            }
            Log.Error(Cat.Fault, name + " gave up after " + attempts + " attempts", last);
            return last;
        }

        /// <summary>
        /// Classic circuit breaker: closed -> open after N failures, half-open after
        /// the cooldown, closed again on success.
        /// </summary>
        public sealed class CircuitBreaker
        {
            private readonly object _sync = new object();
            private readonly string _name;
            private readonly int _threshold;
            private readonly TimeSpan _cooldown;
            private int _failures;
            private DateTime _openedAt = DateTime.MinValue;
            private bool _open;

            public CircuitBreaker(string name, int threshold, TimeSpan cooldown)
            {
                _name = name;
                _threshold = Math.Max(1, threshold);
                _cooldown = cooldown;
            }

            public bool IsOpen
            {
                get
                {
                    lock (_sync)
                    {
                        if (!_open) return false;
                        if (DateTime.UtcNow - _openedAt >= _cooldown)
                        {
                            _open = false;
                            _failures = _threshold - 1; // half open: one more failure re-opens
                            Log.Info(Cat.Fault, "circuit " + _name + " half-open after cooldown");
                            return false;
                        }
                        return true;
                    }
                }
            }

            public void Success()
            {
                lock (_sync)
                {
                    if (_failures > 0) Log.Debug(Cat.Fault, "circuit " + _name + " closed");
                    _failures = 0;
                    _open = false;
                }
            }

            public void Failure()
            {
                lock (_sync)
                {
                    _failures++;
                    if (_failures >= _threshold && !_open)
                    {
                        _open = true;
                        _openedAt = DateTime.UtcNow;
                        Log.Warn(Cat.Fault, "circuit " + _name + " opened after " + _failures +
                                            " failures, cooling down " + (int)_cooldown.TotalSeconds + " s");
                    }
                }
            }

            public Result Run(string operation, Action action)
            {
                if (IsOpen)
                    return Result.Failure("circuit " + _name + " is open (" + operation + " skipped)");
                try
                {
                    action();
                    Success();
                    return Result.Success();
                }
                catch (Exception ex)
                {
                    Failure();
                    Log.Error(Cat.Fault, operation + " failed", ex);
                    return Result.Failure(Log.Describe(ex));
                }
            }
        }

        /// <summary>
        /// Restarts a long running worker when it dies. Delay grows from min to max and
        /// resets once the worker survived a minute.
        /// </summary>
        public sealed class Supervisor : IDisposable
        {
            private readonly string _name;
            private readonly Func<bool> _worker;
            private readonly int _minDelayMs;
            private readonly int _maxDelayMs;
            private Thread _thread;
            private volatile bool _stopping;

            public int Restarts { get; private set; }
            public string LastError { get; private set; }
            public bool Running { get { return _thread != null && _thread.IsAlive; } }

            public Supervisor(string name, Action worker, int minDelayMs, int maxDelayMs)
                : this(name, delegate { worker(); return true; }, minDelayMs, maxDelayMs)
            {
            }

            /// <summary>
            /// The worker returns true to be supervised (restart it if it dies),
            /// false to stop the supervisor (clean shutdown).
            /// </summary>
            public Supervisor(string name, Func<bool> worker, int minDelayMs, int maxDelayMs)
            {
                _name = name;
                _worker = worker;
                _minDelayMs = Math.Max(50, minDelayMs);
                _maxDelayMs = Math.Max(_minDelayMs, maxDelayMs);
            }

            public void Start()
            {
                _stopping = false;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Name = "sup-" + _name;
                _thread.Start();
            }

            private void Loop()
            {
                int delay = _minDelayMs;
                while (!_stopping)
                {
                    DateTime startedAt = DateTime.UtcNow;
                    bool keepSupervising = true;
                    try
                    {
                        keepSupervising = _worker();
                    }
                    catch (Exception ex)
                    {
                        LastError = Log.Describe(ex);
                        Restarts++;
                        Log.Error(Cat.Fault, "worker '" + _name + "' stopped", ex);
                    }
                    if (_stopping || !keepSupervising) return;
                    if (DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(1)) delay = _minDelayMs;
                    Log.Warn(Cat.Fault, "restarting '" + _name + "' in " + delay + " ms");
                    int waited = 0;
                    while (waited < delay && !_stopping)
                    {
                        Thread.Sleep(50);
                        waited += 50;
                    }
                    delay = Math.Min(_maxDelayMs, delay * 2);
                }
            }

            public void Stop()
            {
                _stopping = true;
                Thread thread = _thread;
                if (thread != null) thread.Join(2000);
            }

            public void Dispose()
            {
                Stop();
            }
        }
    }
}
