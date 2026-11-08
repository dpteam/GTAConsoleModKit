using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModKit.Core;

namespace ModKit.Web
{
    public sealed class HttpRequest
    {
        public string Method;
        public string RawUrl;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string ClientAddress;

        public string GetHeader(string name, string fallback)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : fallback;
        }

        public bool WantsJson
        {
            get
            {
                string accept = GetHeader("accept", "");
                return accept.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }

    public sealed class HttpResponse
    {
        public int Status = 200;
        public string StatusText = "OK";
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = new byte[0];
        public bool KeepAlive = true;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static HttpResponse Json(object payload, int status)
        {
            var response = new HttpResponse
            {
                Status = status,
                StatusText = HttpCore.StatusText(status),
                ContentType = "application/json; charset=utf-8",
                Body = Encoding.UTF8.GetBytes(ModKit.Core.Json.Stringify(payload, false))
            };
            response.Headers["Cache-Control"] = "no-store";
            return response;
        }

        public static HttpResponse Json(object payload)
        {
            return Json(payload, 200);
        }

        public static HttpResponse Error(int status, string message)
        {
            return Json(ModKit.Core.Json.O("ok", false, "error", message, "status", (long)status), status);
        }

        public static HttpResponse Text(string text, string contentType, int status)
        {
            return new HttpResponse
            {
                Status = status,
                StatusText = HttpCore.StatusText(status),
                ContentType = contentType,
                Body = Encoding.UTF8.GetBytes(text)
            };
        }

        public static HttpResponse Bytes(byte[] data, string contentType)
        {
            return new HttpResponse
            {
                ContentType = contentType,
                Body = data,
                Status = 200,
                StatusText = "OK"
            };
        }

        public static HttpResponse Redirect(string location)
        {
            var response = new HttpResponse { Status = 302, StatusText = "Found", Body = new byte[0] };
            response.Headers["Location"] = location;
            return response;
        }
    }

    /// <summary>
    /// Minimal HTTP/1.1 request parser and response writer over a plain socket.
    /// We do not use HttpListener: on Windows it needs an URL ACL (admin) for
    /// non-localhost prefixes and behaves differently on XP.
    /// </summary>
    public static class HttpCore
    {
        public const int MaxHeaderBytes = 32 * 1024;
        private const int MaxBodyBytes = 256 * 1024 * 1024;

        public static HttpRequest ReadRequest(Stream stream, string clientAddress)
        {
            var request = new HttpRequest { ClientAddress = clientAddress };
            string requestLine = ReadLine(stream, MaxHeaderBytes);
            if (requestLine == null) return null;
            if (requestLine.Length == 0) return null;

            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2) throw new InvalidDataException("malformed request line");
            request.Method = parts[0].ToUpperInvariant();
            request.RawUrl = parts[1];

            int totalHeader = 0;
            while (true)
            {
                string line = ReadLine(stream, MaxHeaderBytes);
                if (line == null) return null;
                if (line.Length == 0) break;
                totalHeader += line.Length;
                if (totalHeader > MaxHeaderBytes) throw new InvalidDataException("headers too large");
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                request.Headers[name] = value;
            }

            ParseUrl(request);

            string contentLengthText = request.GetHeader("content-length", "0");
            int contentLength;
            if (!int.TryParse(contentLengthText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out contentLength)) contentLength = 0;
            if (contentLength > MaxBodyBytes)
                throw new InvalidDataException("body too large (" + contentLength + " bytes)");

            if (contentLength > 0)
            {
                request.Body = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = stream.Read(request.Body, read, contentLength - read);
                    if (n <= 0) throw new InvalidDataException("truncated request body");
                    read += n;
                }
            }

            string connection = request.GetHeader("connection", "");
            bool keepAlive = !string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(connection, "keep-alive", StringComparison.OrdinalIgnoreCase)) keepAlive = true;
            request.Headers["x-keep-alive"] = keepAlive ? "1" : "0";
            return request;
        }

        private static void ParseUrl(HttpRequest request)
        {
            string url = request.RawUrl ?? "/";
            int question = url.IndexOf('?');
            string path = question < 0 ? url : url.Substring(0, question);
            string query = question < 0 ? "" : url.Substring(question + 1);
            request.Path = Uri.UnescapeDataString(path);
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string value = eq < 0 ? "" : pair.Substring(eq + 1);
                request.Query[Uri.UnescapeDataString(key.Replace("+", " "))] =
                    Uri.UnescapeDataString(value.Replace("+", " "));
            }
        }

        private static string ReadLine(Stream stream, int limit)
        {
            var buffer = new List<byte>(128);
            while (buffer.Count < limit)
            {
                int b = stream.ReadByte();
                if (b < 0) return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
                if (b == '\n')
                {
                    if (buffer.Count > 0 && buffer[buffer.Count - 1] == '\r') buffer.RemoveAt(buffer.Count - 1);
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }
                buffer.Add((byte)b);
            }
            throw new InvalidDataException("header line too long");
        }

        public static void WriteResponse(Stream stream, HttpResponse response, bool headOnly)
        {
            var head = new StringBuilder(512);
            head.Append("HTTP/1.1 ").Append(response.Status).Append(' ')
                .Append(string.IsNullOrEmpty(response.StatusText) ? StatusText(response.Status) : response.StatusText)
                .Append("\r\n");
            head.Append("Server: DPT.PSP-ModKit\r\n");
            head.Append("Date: ").Append(DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
            head.Append("Connection: ").Append(response.KeepAlive ? "keep-alive" : "close").Append("\r\n");
            head.Append("X-Content-Type-Options: nosniff\r\n");
            foreach (var header in response.Headers)
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            head.Append("\r\n");

            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (!headOnly && response.Body.Length > 0)
                stream.Write(response.Body, 0, response.Body.Length);
            stream.Flush();
        }

        public static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 302: return "Found";
                case 304: return "Not Modified";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status " + status;
            }
        }

        public static string ContentTypeFor(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".woff": return "font/woff";
                case ".woff2": return "font/woff2";
                case ".ttf": return "font/ttf";
                case ".txt": return "text/plain; charset=utf-8";
                case ".wav": return "audio/wav";
                case ".bin": return "application/octet-stream";
                default: return "application/octet-stream";
            }
        }

        public static string LocalAddressOf(Socket socket)
        {
            try
            {
                var endPoint = socket.RemoteEndPoint as IPEndPoint;
                return endPoint == null ? "?" : endPoint.Address.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}

