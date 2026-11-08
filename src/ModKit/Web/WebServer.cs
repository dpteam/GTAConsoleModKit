using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ModKit.Core;

namespace ModKit.Web
{
    /// <summary>
    /// Own HTTP/1.1 server over TcpListener.
    ///
    /// Why not HttpListener: it requires an URL ACL (admin) for any prefix that is
    /// not localhost, and on XP the behaviour differs. A socket server needs no
    /// rights at all, which matters for a portable modding kit.
    /// </summary>
    public sealed class WebServer : IDisposable
    {
        private readonly ModKitContext _context;
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _stopping;
        private int _connections;
        private long _requests;
        private long _errors;
        private readonly object _connectionSync = new object();
        private readonly List<Socket> _openSockets = new List<Socket>();

        public int Port { get; private set; }
        public string BindAddress { get; private set; }
        public int ConnectionLimit = 64;

        public long Requests { get { return Interlocked.Read(ref _requests); } }
        public long Errors { get { return Interlocked.Read(ref _errors); } }
        public int ActiveConnections { get { lock (_connectionSync) return _connections; } }

        /// <summary>True while the accept loop is running (the supervisor watches this).</summary>
        public bool IsAlive
        {
            get { return !_stopping && _thread != null && _thread.IsAlive; }
        }

        /// <summary>Re-creates the accept loop after a failure, keeping port and bind.</summary>
        public void Restart()
        {
            if (_stopping) return;
            if (_thread != null && _thread.IsAlive) return;
            Log.Warn(Cat.Net, "restarting web listener on " + BindAddress + ":" + Port);
            try
            {
                if (_listener != null)
                {
                    try { _listener.Stop(); } catch (Exception) { }
                    _listener = null;
                }
                IPAddress address = BindAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(BindAddress);
                _listener = new TcpListener(address, Port);
                _listener.Start(64);
                _thread = new Thread(AcceptLoop);
                _thread.IsBackground = true;
                _thread.Name = "http-accept";
                _thread.Start();
            }
            catch (Exception ex)
            {
                Log.Error(Cat.Net, "cannot restart web listener", ex);
            }
        }

        public WebServer(ModKitContext context)
        {
            _context = context;
        }

        /// <summary>Starts listening; tries the configured port and then the fallbacks.</summary>
        public void Start(int port, string bind, int[] fallbacks)
        {
            BindAddress = string.IsNullOrEmpty(bind) ? "127.0.0.1" : bind;
            IPAddress address = BindAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(BindAddress);

            var candidates = new List<int> { port };
            if (fallbacks != null) candidates.AddRange(fallbacks);

            Exception last = null;
            foreach (int candidate in candidates)
            {
                try
                {
                    var listener = new TcpListener(address, candidate);
                    listener.Start(64);
                    _listener = listener;
                    Port = candidate;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Warn(Cat.Net, "port " + candidate + " unavailable (" + Log.Describe(ex) + ")");
                }
            }

            if (_listener == null)
                throw new InvalidOperationException("no free port for the web UI", last);

            if (Port != port)
                Log.Warn(Cat.Net, "web UI fell back to port " + Port);

            _thread = new Thread(AcceptLoop);
            _thread.IsBackground = true;
            _thread.Name = "http-accept";
            _thread.Start();

            Log.Info(Cat.Net, "web UI listening on http://" + BindAddress + ":" + Port + "/");
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                Socket socket = null;
                try
                {
                    socket = _listener.AcceptSocket();
                }
                catch (Exception ex)
                {
                    if (_stopping) return;
                    Log.Warn(Cat.Net, "accept failed: " + Log.Describe(ex));
                    Thread.Sleep(200);
                    continue;
                }

                int active;
                lock (_connectionSync)
                {
                    active = _connections;
                    if (active >= ConnectionLimit)
                    {
                        try { socket.Close(); } catch (Exception) { }
                        Interlocked.Increment(ref _errors);
                        continue;
                    }
                    _connections++;
                    _openSockets.Add(socket);
                }

                var thread = new Thread(delegate() { Serve(socket); });
                thread.IsBackground = true;
                thread.Name = "http-conn";
                thread.Start();
            }
        }

        private void Serve(Socket socket)
        {
            string client = HttpCore.LocalAddressOf(socket);
            try
            {
                socket.NoDelay = true;
                socket.ReceiveTimeout = 30000;
                socket.SendTimeout = 60000;
                using (var stream = new NetworkStream(socket, false))
                {
                    while (!_stopping)
                    {
                        HttpRequest request;
                        try
                        {
                            request = HttpCore.ReadRequest(stream, client);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _errors);
                            // a browser closing the tab mid-request is not an error
                            if (ex is IOException || ex is SocketException)
                                Log.Debug(Cat.Http, "connection from " + client + " closed early: " + Log.Describe(ex));
                            else
                                Log.Warn(Cat.Http, "bad request from " + client + ": " + Log.Describe(ex));
                            break;
                        }
                        if (request == null) break;

                        Interlocked.Increment(ref _requests);
                        HttpResponse response;
                        try
                        {
                            response = Route(request);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _errors);
                            Log.Error(Cat.Http, "handler crashed for " + request.Method + " " + request.Path, ex);
                            response = HttpResponse.Error(500, Log.Describe(ex));
                        }

                        bool keepAlive = request.GetHeader("x-keep-alive", "1") == "1";
                        response.KeepAlive = keepAlive;
                        if (request.Method == "HEAD") response.KeepAlive = false;
                        HttpCore.WriteResponse(stream, response, request.Method == "HEAD");
                        if (!response.KeepAlive) break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(Cat.Http, "connection " + client + " ended: " + Log.Describe(ex));
            }
            finally
            {
                lock (_connectionSync)
                {
                    _connections--;
                    _openSockets.Remove(socket);
                }
                try { socket.Close(); } catch (Exception) { }
            }
        }

        private HttpResponse Route(HttpRequest request)
        {
            if (request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                return WebApi.Handle(_context, this, request);

            if (request.Method != "GET" && request.Method != "HEAD")
                return HttpResponse.Error(405, "only GET is allowed for static files");

            string path = request.Path;
            if (path == "/" || path.Length == 0) path = "/index.html";

            // cache buster: strip ?v= handled by query parsing already
            string relative = path.TrimStart('/');
            string full = Util.SafeCombine(_context.PublicDirectory, relative);
            if (full == null) return HttpResponse.Error(403, "path traversal denied");
            if (!File.Exists(full))
            {
                // single page fallback for unknown routes
                string index = Path.Combine(_context.PublicDirectory, "index.html");
                if (File.Exists(index) && !path.Contains("."))
                    return ServeFile(index, request);
                return HttpResponse.Error(404, "not found: " + path);
            }
            return ServeFile(full, request);
        }

        private HttpResponse ServeFile(string fullPath, HttpRequest request)
        {
            try
            {
                string extension = Path.GetExtension(fullPath).ToLowerInvariant();
                bool volatileFile = extension == ".html" || extension == ".js" || extension == ".css";

                // ETag from size + timestamp: after "build.cmd" the browser gets the new CSS
                // on the next reload instead of running a 10-minute-old copy from its cache.
                var info = new FileInfo(fullPath);
                string etag = "\"" + info.Length.ToString("x") + "-" + info.LastWriteTimeUtc.Ticks.ToString("x") + "\"";
                string ifNoneMatch = request.GetHeader("If-None-Match", null);
                if (volatileFile && ifNoneMatch != null && ifNoneMatch.Trim() == etag)
                {
                    var notModified = new HttpResponse
                    {
                        Status = 304,
                        StatusText = "Not Modified",
                        ContentType = HttpCore.ContentTypeFor(fullPath)
                    };
                    notModified.Headers["ETag"] = etag;
                    notModified.Headers["Cache-Control"] = "no-cache";
                    return notModified;
                }

                byte[] data = File.ReadAllBytes(fullPath);
                var response = HttpResponse.Bytes(data, HttpCore.ContentTypeFor(fullPath));
                if (volatileFile)
                {
                    response.Headers["Cache-Control"] = "no-cache";
                    response.Headers["ETag"] = etag;
                }
                else
                {
                    response.Headers["Cache-Control"] = "public, max-age=600";
                }
                return response;
            }
            catch (Exception ex)
            {
                Log.Warn(Cat.Http, "cannot serve " + fullPath + ": " + Log.Describe(ex));
                return HttpResponse.Error(500, "cannot read file");
            }
        }

        public void Stop()
        {
            _stopping = true;
            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }
            lock (_connectionSync)
            {
                foreach (Socket socket in _openSockets)
                {
                    try { socket.Close(); } catch (Exception) { }
                }
                _openSockets.Clear();
            }
            if (_thread != null) _thread.Join(1500);
            Log.Info(Cat.Net, "web UI stopped");
        }

        public void Dispose()
        {
            Stop();
        }

        public string BaseUrl
        {
            get
            {
                string host = BindAddress == "0.0.0.0" ? "127.0.0.1" : BindAddress;
                return "http://" + host + ":" + Port + "/";
            }
        }
    }
}
