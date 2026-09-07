using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Overtake.SimHub.Plugin.Live
{
    /// <summary>
    /// Minimal embedded web server (Rota B) for the live race UI. Serves the
    /// broadcast page over HTTP and pushes JSON snapshots over WebSocket.
    ///
    /// Uses a raw <see cref="TcpListener"/> + hand-rolled WebSocket framing on
    /// purpose: it avoids the HttpListener URL-ACL / admin requirement on Windows,
    /// binds cleanly to loopback, and ships with zero extra dependencies (works in
    /// the SimHub .NET Framework 4.8 host).
    ///
    /// Read-only: it never touches capture/parse/export. Safe to enable/disable.
    /// </summary>
    internal sealed class RaceWebServer : IDisposable
    {
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private readonly object _clientsLock = new object();
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private byte[] _indexHtml;
        private byte[] _logoPng;
        private string _lastJson = "{\"ok\":false}";
        private int _port;
        private bool _lan;

        private const string WsMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const string IndexResource = "Overtake.SimHub.Plugin.Assets.race-ui.html";
        private const string LogoResource = "Overtake.SimHub.Plugin.Assets.overtake-icon.png";

        public int Port { get { return _port; } }
        public bool Running { get { return _running; } }
        public int ClientCount { get { lock (_clientsLock) { return _clients.Count; } } }

        public string Url
        {
            get { return string.Format("http://{0}:{1}", _lan ? LocalIPv4() : "localhost", _port); }
        }

        public void Start(int port, bool allowLan)
        {
            Stop();
            _port = port <= 0 ? 8088 : port;
            _lan = allowLan;
            _indexHtml = LoadIndexHtml();
            _logoPng = LoadResource(LogoResource);

            IPAddress bind = allowLan ? IPAddress.Any : IPAddress.Loopback;
            _listener = new TcpListener(bind, _port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "OvertakeRaceWeb" };
            _acceptThread.Start();
            global::SimHub.Logging.Current.Info(
                string.Format("[Overtake] Race UI web server listening at {0}", Url));
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            lock (_clientsLock)
            {
                foreach (var c in _clients) { try { c.Close(); } catch { } }
                _clients.Clear();
            }
            _listener = null;
        }

        public void Dispose() { Stop(); }

        /// <summary>Push a JSON snapshot to all connected WebSocket clients.</summary>
        public void Publish(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            _lastJson = json;
            if (!_running) return;
            byte[] frame = EncodeTextFrame(json);
            List<TcpClient> snapshot;
            lock (_clientsLock) { snapshot = new List<TcpClient>(_clients); }
            foreach (var c in snapshot)
            {
                try
                {
                    NetworkStream s = c.GetStream();
                    lock (c) { s.Write(frame, 0, frame.Length); }
                }
                catch { Remove(c); }
            }
        }

        private void Remove(TcpClient c)
        {
            lock (_clientsLock) { _clients.Remove(c); }
            try { c.Close(); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
                catch
                {
                    if (_running) Thread.Sleep(50);
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string request = ReadHttpRequest(stream);
                if (request == null) { client.Close(); return; }

                string key = HeaderValue(request, "Sec-WebSocket-Key");
                bool isWs = request.IndexOf("upgrade: websocket", StringComparison.OrdinalIgnoreCase) >= 0
                    && !string.IsNullOrEmpty(key);

                if (isWs)
                {
                    string accept = ComputeAccept(key);
                    string resp = "HTTP/1.1 101 Switching Protocols\r\n"
                        + "Upgrade: websocket\r\n"
                        + "Connection: Upgrade\r\n"
                        + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                    byte[] rb = Encoding.ASCII.GetBytes(resp);
                    stream.Write(rb, 0, rb.Length);

                    lock (_clientsLock) { _clients.Add(client); }
                    try { byte[] f = EncodeTextFrame(_lastJson); lock (client) { stream.Write(f, 0, f.Length); } }
                    catch { }

                    ReadUntilClose(stream);
                    Remove(client);
                }
                else
                {
                    ServeStatic(stream, request);
                    client.Close();
                }
            }
            catch { try { client.Close(); } catch { } }
        }

        private void ServeStatic(NetworkStream stream, string request)
        {
            string path = "/";
            string firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            string[] parts = firstLine.Split(' ');
            if (parts.Length >= 2) path = parts[1];

            if (path == "/" || path.StartsWith("/index") || path.StartsWith("/?"))
                WriteHttp(stream, "200 OK", "text/html; charset=utf-8", _indexHtml);
            else if (path.StartsWith("/logo.png") && _logoPng != null)
                WriteHttp(stream, "200 OK", "image/png", _logoPng);
            else if (path.StartsWith("/snapshot"))
                WriteHttp(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_lastJson));
            else
                WriteHttp(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.ASCII.GetBytes("Not found"));
        }

        private static void WriteHttp(NetworkStream stream, string status, string contentType, byte[] body)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
        }

        private static string ReadHttpRequest(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[1024];
            try { stream.ReadTimeout = 5000; } catch { }
            int total = 0;
            while (total < 16384)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); } catch { return null; }
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                total += n;
                if (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string HeaderValue(string req, string name)
        {
            foreach (var line in req.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                int idx = line.IndexOf(':');
                if (idx > 0 && line.Substring(0, idx).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(idx + 1).Trim();
            }
            return null;
        }

        private static string ComputeAccept(string key)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WsMagic));
                return Convert.ToBase64String(hash);
            }
        }

        private static byte[] EncodeTextFrame(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            int len = payload.Length;
            byte[] header;
            if (len <= 125)
            {
                header = new byte[2];
                header[1] = (byte)len;
            }
            else if (len <= 65535)
            {
                header = new byte[4];
                header[1] = 126;
                header[2] = (byte)((len >> 8) & 0xFF);
                header[3] = (byte)(len & 0xFF);
            }
            else
            {
                header = new byte[10];
                header[1] = 127;
                long l = len;
                for (int i = 0; i < 8; i++) header[9 - i] = (byte)((l >> (8 * i)) & 0xFF);
            }
            header[0] = 0x81; // FIN + text opcode
            var frame = new byte[header.Length + len];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            Buffer.BlockCopy(payload, 0, frame, header.Length, len);
            return frame;
        }

        private static void ReadUntilClose(NetworkStream stream)
        {
            var buf = new byte[1024];
            try { stream.ReadTimeout = Timeout.Infinite; } catch { }
            while (true)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); } catch { return; }
                if (n <= 0) return;
                if ((buf[0] & 0x0F) == 0x8) return; // close opcode
            }
        }

        private static byte[] LoadIndexHtml()
        {
            byte[] data = LoadResource(IndexResource);
            return data ?? Encoding.UTF8.GetBytes("<html><body>race-ui.html missing from assembly</body></html>");
        }

        private static byte[] LoadResource(string resourceName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string LocalIPv4()
        {
            try
            {
                foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip.ToString();
            }
            catch { }
            return "localhost";
        }
    }
}
