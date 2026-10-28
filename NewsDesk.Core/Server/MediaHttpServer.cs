using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NewsDesk.Core.Server;

public class MediaHttpServer : IDisposable
{
    private HttpListener? _listener;
    private readonly string _assetsDirectory;
    private readonly ConcurrentDictionary<int, string> _approvedMedia = new();
    private CancellationTokenSource? _cts;
    private int _port;

    public int Port => _port;
    public string BaseUrl => $"http://localhost:{_port}/";

    public MediaHttpServer(string assetsDirectory, int preferredPort = 8089)
    {
        _assetsDirectory = Path.GetFullPath(assetsDirectory);
        _port = preferredPort;
    }

    public void RegisterApprovedMedia(int id, string filePath)
    {
        if (File.Exists(filePath))
        {
            _approvedMedia[id] = Path.GetFullPath(filePath);
        }
    }

    public void UnregisterMedia(int id)
    {
        _approvedMedia.TryRemove(id, out _);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        int attempts = 0;
        bool started = false;

        while (!started && attempts < 10)
        {
            try
            {
                _listener = new HttpListener();
                // "localhost" prefix works without admin URL ACLs; 127.0.0.1 does not
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
                started = true;
            }
            catch (HttpListenerException)
            {
                _listener?.Close();
                _port++;
                attempts++;
            }
        }

        if (!started)
        {
            throw new InvalidOperationException("Could not find an available loopback port for MediaHttpServer.");
        }

        Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context), token);
            }
            catch (HttpListenerException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                // Continue loop
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var rawPath = context.Request.Url?.AbsolutePath ?? "/";
            var decodedPath = Uri.UnescapeDataString(rawPath);

            // Add standard security headers
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            response.Headers.Add("X-Frame-Options", "SAMEORIGIN");
            response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.Headers.Add("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Range, Content-Type");
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }

            if (decodedPath.StartsWith("/media/"))
            {
                await HandleMediaRequest(context, decodedPath);
                return;
            }

            // Static asset request
            await HandleAssetRequest(context, decodedPath);
        }
        catch (Exception ex)
        {
            try
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                var bytes = Encoding.UTF8.GetBytes($"500 Server Error: {ex.Message}");
                response.ContentType = "text/plain";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
                response.Close();
            }
            catch
            {
                // Ignore downstream socket failures
            }
        }
    }

    private async Task HandleMediaRequest(HttpListenerContext context, string path)
    {
        var response = context.Response;

        // Path format: /media/{id}
        var match = Regex.Match(path, @"^/media/(\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int mediaId))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            response.Close();
            return;
        }

        if (!_approvedMedia.TryGetValue(mediaId, out var fullPath) || !File.Exists(fullPath))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
            return;
        }

        await StreamFileWithRangeSupport(context, fullPath, GetMimeType(fullPath));
    }

    private async Task HandleAssetRequest(HttpListenerContext context, string path)
    {
        var response = context.Response;
        if (path == "/" || string.IsNullOrEmpty(path))
        {
            path = "/index.html";
        }

        // Prevent directory traversal
        var cleanRelPath = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var candidatePath = Path.GetFullPath(Path.Combine(_assetsDirectory, cleanRelPath));

        if (!candidatePath.StartsWith(_assetsDirectory, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidatePath))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            var bytes = Encoding.UTF8.GetBytes("404 Not Found");
            response.ContentType = "text/plain";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
            return;
        }

        var mimeType = GetMimeType(candidatePath);
        await StreamFileWithRangeSupport(context, candidatePath, mimeType);
    }

    private static async Task StreamFileWithRangeSupport(HttpListenerContext context, string filePath, string mimeType)
    {
        var request = context.Request;
        var response = context.Response;

        var fileInfo = new FileInfo(filePath);
        long totalLength = fileInfo.Length;

        response.ContentType = mimeType;
        response.Headers.Add("Accept-Ranges", "bytes");

        var rangeHeader = request.Headers["Range"];
        if (string.IsNullOrEmpty(rangeHeader))
        {
            // Full file response
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentLength64 = totalLength;

            if (request.HttpMethod == "HEAD")
            {
                response.Close();
                return;
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await fs.CopyToAsync(response.OutputStream);
            response.Close();
            return;
        }

        // Byte Range parsing: Range: bytes=start-end | bytes=start- | bytes=-suffix
        var rangeMatch = Regex.Match(rangeHeader, @"^bytes=(\d*)-(\d*)");
        string startGroup = rangeMatch.Success ? rangeMatch.Groups[1].Value : "";
        string endGroup = rangeMatch.Success ? rangeMatch.Groups[2].Value : "";

        static void SendRangeNotSatisfiable(HttpListenerResponse resp, long length)
        {
            resp.StatusCode = 416; // Range Not Satisfiable
            resp.Headers.Add("Content-Range", $"bytes */{length}");
            resp.Close();
        }

        if (!rangeMatch.Success || (startGroup.Length == 0 && endGroup.Length == 0) || totalLength == 0)
        {
            SendRangeNotSatisfiable(response, totalLength);
            return;
        }

        long start;
        long end;

        if (startGroup.Length == 0)
        {
            // Suffix range: last N bytes
            if (!long.TryParse(endGroup, out long suffixLength) || suffixLength <= 0)
            {
                SendRangeNotSatisfiable(response, totalLength);
                return;
            }

            start = Math.Max(0, totalLength - suffixLength);
            end = totalLength - 1;
        }
        else
        {
            if (!long.TryParse(startGroup, out start) || start >= totalLength)
            {
                SendRangeNotSatisfiable(response, totalLength);
                return;
            }

            // An open or overshooting end is clamped to the last byte (RFC 7233)
            end = totalLength - 1;
            if (endGroup.Length > 0)
            {
                if (!long.TryParse(endGroup, out long requestedEnd))
                {
                    SendRangeNotSatisfiable(response, totalLength);
                    return;
                }
                end = Math.Min(requestedEnd, totalLength - 1);
            }

            if (end < start)
            {
                SendRangeNotSatisfiable(response, totalLength);
                return;
            }
        }

        long count = end - start + 1;
        response.StatusCode = (int)HttpStatusCode.PartialContent;
        response.Headers.Add("Content-Range", $"bytes {start}-{end}/{totalLength}");
        response.ContentLength64 = count;

        if (request.HttpMethod == "HEAD")
        {
            response.Close();
            return;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(start, SeekOrigin.Begin);

        byte[] buffer = new byte[64 * 1024];
        long bytesRemaining = count;

        while (bytesRemaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead));
            if (read <= 0) break;

            await response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
            bytesRemaining -= read;
        }

        response.Close();
    }

    public static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".m4v" => "video/x-m4v",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream"
        };
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
