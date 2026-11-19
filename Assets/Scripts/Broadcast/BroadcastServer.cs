using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace VGuns.Broadcast
{
    public struct ServerCommand
    {
        public string Kind;   // "camera" | "rate"
        public string Arg;
    }

    /// <summary>
    /// Tiny HTTP server (raw TcpListener, works in Editor, PC and on Quest).
    ///   GET /                      viewer page
    ///   GET /stream.mjpg           live MJPEG video of the AI-directed camera
    ///   GET /frame.jpg             latest single frame
    ///   GET /api/state             JSON: director decision, probabilities, metrics, events
    ///   GET /api/camera?mode=fpv|tpv|cine|auto   viewer override
    ///   GET /api/rate?value=1|-1   rate the current camera choice
    ///   GET /api/sessions          list of telemetry sessions
    ///   GET /telemetry/&lt;session&gt;/&lt;file&gt;.csv   download a CSV
    /// All game-state work stays on Unity's main thread; this class only moves bytes.
    /// </summary>
    public class BroadcastServer
    {
        public int Port { get; private set; }
        public bool Running { get { return running; } }
        public int StreamClients { get { return Interlocked.CompareExchange(ref streamClients, 0, 0); } }
        public long FramesPublished { get { return Interlocked.Read(ref frameSeq); } }
        public string LastError { get; private set; }

        public volatile string StateJson = "{\"live\":false}";
        public volatile string IndexHtml = "<html><body>VGuns stream</body></html>";
        public readonly ConcurrentQueue<ServerCommand> Commands = new ConcurrentQueue<ServerCommand>();

        TcpListener listener;
        Thread acceptThread;
        volatile bool running;
        int streamClients;
        string telemetryRoot;

        readonly object frameLock = new object();
        byte[] latestJpeg;
        long frameSeq;

        readonly List<TcpClient> clients = new List<TcpClient>();

        public bool Start(int port, string telemetryRootPath)
        {
            telemetryRoot = telemetryRootPath;
            for (int p = port; p < port + 10; p++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Any, p);
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Start();
                    Port = p;
                    running = true;
                    acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "VGuns HTTP" };
                    acceptThread.Start();
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    try { if (listener != null) listener.Stop(); } catch { }
                    listener = null;
                }
            }
            Debug.LogError("[Broadcast] Could not open HTTP port " + port + ": " + LastError);
            return false;
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            lock (frameLock) Monitor.PulseAll(frameLock);
            lock (clients)
            {
                foreach (var c in clients) { try { c.Close(); } catch { } }
                clients.Clear();
            }
        }

        public void PublishFrame(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return;
            lock (frameLock)
            {
                latestJpeg = jpeg;
                frameSeq++;
                Monitor.PulseAll(frameLock);
            }
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (!running) break; Thread.Sleep(50); continue; }
                lock (clients) clients.Add(c);
                var t = new Thread(() => Handle(c)) { IsBackground = true, Name = "VGuns HTTP client" };
                t.Start();
            }
        }

        void Handle(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                string requestLine = ReadRequestHead(stream);
                if (requestLine == null) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0].ToUpperInvariant();
                string target = parts[1];
                string path = target, query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }
                path = Uri.UnescapeDataString(path);
                var args = ParseQuery(query);

                if (method == "OPTIONS") { Send(stream, 204, "text/plain", new byte[0]); return; }

                switch (path)
                {
                    case "/":
                    case "/index.html":
                        SendText(stream, 200, "text/html; charset=utf-8", IndexHtml);
                        return;
                    case "/stream.mjpg":
                        StreamMjpeg(stream);
                        return;
                    case "/frame.jpg":
                    {
                        byte[] f;
                        lock (frameLock) f = latestJpeg;
                        if (f == null) SendText(stream, 503, "text/plain", "no frame yet");
                        else Send(stream, 200, "image/jpeg", f);
                        return;
                    }
                    case "/api/state":
                        SendText(stream, 200, "application/json", StateJson);
                        return;
                    case "/api/camera":
                    {
                        string mode;
                        args.TryGetValue("mode", out mode);
                        Commands.Enqueue(new ServerCommand { Kind = "camera", Arg = mode ?? "auto" });
                        SendText(stream, 200, "application/json", "{\"ok\":true}");
                        return;
                    }
                    case "/api/rate":
                    {
                        string v;
                        args.TryGetValue("value", out v);
                        Commands.Enqueue(new ServerCommand { Kind = "rate", Arg = v ?? "1" });
                        SendText(stream, 200, "application/json", "{\"ok\":true}");
                        return;
                    }
                    case "/api/sessions":
                        SendText(stream, 200, "application/json", SessionsJson());
                        return;
                }

                if (path.StartsWith("/telemetry/", StringComparison.Ordinal))
                {
                    ServeTelemetryFile(stream, path.Substring("/telemetry/".Length));
                    return;
                }
                SendText(stream, 404, "text/plain", "not found");
            }
            catch (Exception)
            {
                // client went away - normal for streams
            }
            finally
            {
                lock (clients) clients.Remove(client);
                try { client.Close(); } catch { }
            }
        }

        static string ReadRequestHead(NetworkStream s)
        {
            var buf = new byte[8192];
            int len = 0;
            while (len < buf.Length)
            {
                int n = s.Read(buf, len, buf.Length - len);
                if (n <= 0) break;
                len += n;
                string sofar = Encoding.ASCII.GetString(buf, 0, len);
                if (sofar.Contains("\r\n\r\n") || sofar.Contains("\n\n"))
                {
                    int eol = sofar.IndexOf('\n');
                    return sofar.Substring(0, eol).TrimEnd('\r');
                }
            }
            if (len == 0) return null;
            string all = Encoding.ASCII.GetString(buf, 0, len);
            int e = all.IndexOf('\n');
            return e > 0 ? all.Substring(0, e).TrimEnd('\r') : all;
        }

        static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(q)) return d;
            foreach (var kv in q.Split('&'))
            {
                if (kv.Length == 0) continue;
                int i = kv.IndexOf('=');
                if (i < 0) d[Uri.UnescapeDataString(kv)] = "";
                else d[Uri.UnescapeDataString(kv.Substring(0, i))] = Uri.UnescapeDataString(kv.Substring(i + 1).Replace('+', ' '));
            }
            return d;
        }

        static string StatusText(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 503: return "Service Unavailable";
                default: return "OK";
            }
        }

        static void Send(NetworkStream s, int code, string contentType, byte[] body, string extraHeaders = "")
        {
            string head = "HTTP/1.1 " + code + " " + StatusText(code) + "\r\n" +
                          "Content-Type: " + contentType + "\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Access-Control-Allow-Origin: *\r\n" +
                          extraHeaders +
                          "Connection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            if (body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
        }

        static void SendText(NetworkStream s, int code, string contentType, string text)
        {
            Send(s, code, contentType, Encoding.UTF8.GetBytes(text ?? ""));
        }

        void StreamMjpeg(NetworkStream s)
        {
            s.WriteTimeout = 5000;
            const string boundary = "vgunsframe";
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: multipart/x-mixed-replace; boundary=" + boundary + "\r\n" +
                "Cache-Control: no-cache, no-store\r\n" +
                "Pragma: no-cache\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Connection: close\r\n\r\n");
            s.Write(head, 0, head.Length);

            Interlocked.Increment(ref streamClients);
            try
            {
                long seen = -1;
                while (running)
                {
                    byte[] frame;
                    lock (frameLock)
                    {
                        if (frameSeq == seen) Monitor.Wait(frameLock, 1000);
                        if (frameSeq == seen || latestJpeg == null) continue;
                        frame = latestJpeg;
                        seen = frameSeq;
                    }
                    var ph = Encoding.ASCII.GetBytes("--" + boundary + "\r\nContent-Type: image/jpeg\r\nContent-Length: " + frame.Length + "\r\n\r\n");
                    s.Write(ph, 0, ph.Length);
                    s.Write(frame, 0, frame.Length);
                    s.Write(new byte[] { 13, 10 }, 0, 2);
                    s.Flush();
                }
            }
            finally
            {
                Interlocked.Decrement(ref streamClients);
            }
        }

        string SessionsJson()
        {
            var sb = new StringBuilder("{\"root\":");
            sb.Append(Json.Str(telemetryRoot)).Append(",\"sessions\":[");
            try
            {
                if (Directory.Exists(telemetryRoot))
                {
                    var dirs = Directory.GetDirectories(telemetryRoot, "session_*");
                    Array.Sort(dirs);
                    Array.Reverse(dirs);
                    bool first = true;
                    int count = 0;
                    foreach (var d in dirs)
                    {
                        if (count++ >= 50) break;
                        if (!first) sb.Append(',');
                        first = false;
                        string name = Path.GetFileName(d);
                        sb.Append("{\"name\":").Append(Json.Str(name)).Append(",\"files\":[");
                        var files = Directory.GetFiles(d, "*.csv");
                        Array.Sort(files);
                        for (int i = 0; i < files.Length; i++)
                        {
                            if (i > 0) sb.Append(',');
                            var fi = new FileInfo(files[i]);
                            sb.Append("{\"name\":").Append(Json.Str(fi.Name)).Append(",\"bytes\":").Append(fi.Length).Append('}');
                        }
                        sb.Append("]}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append("],\"error\":").Append(Json.Str(ex.Message)).Append('}');
                return sb.ToString();
            }
            sb.Append("]}");
            return sb.ToString();
        }

        void ServeTelemetryFile(NetworkStream s, string rel)
        {
            // only <session_x>/<file>.csv or .json, no path tricks
            var parts = rel.Split('/');
            if (parts.Length != 2 || !parts[0].StartsWith("session_", StringComparison.Ordinal)
                || parts[0].Contains("..") || parts[1].Contains("..") || parts[1].Contains("\\") || parts[0].Contains("\\")
                || !(parts[1].EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                SendText(s, 403, "text/plain", "forbidden");
                return;
            }
            string full = Path.Combine(telemetryRoot, parts[0], parts[1]);
            if (!File.Exists(full)) { SendText(s, 404, "text/plain", "not found"); return; }

            byte[] data;
            // Files may still be open for writing by the recorder.
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                data = ms.ToArray();
            }
            Send(s, 200, "text/csv; charset=utf-8", data,
                "Content-Disposition: attachment; filename=\"" + parts[0] + "_" + parts[1] + "\"\r\n");
        }
    }

    public static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
