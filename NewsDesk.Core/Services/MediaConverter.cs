using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NewsDesk.Core.Services;

/// <summary>
/// Makes camera files playable on the broadcast canvas. Browsers (and WPF's
/// MediaElement) only handle a few formats, so camera video (AVCHD .mts/.m2ts,
/// .avi, .mkv …) is transcoded to H.264/AAC MP4 with ffmpeg, and raw stills
/// (.cr3, .cr2, .nef, .arw …) become a JPEG. Results are cached by source path
/// and timestamp, so importing the same file twice is instant.
/// </summary>
public class MediaConverter
{
    public static readonly string[] NativeVideoExtensions = [".mp4", ".webm", ".mov", ".m4v"];
    public static readonly string[] CameraVideoExtensions = [".mts", ".m2ts", ".ts", ".avi", ".mkv", ".mpg", ".mpeg", ".wmv", ".3gp", ".mxf", ".flv"];
    public static readonly string[] RawStillExtensions = [".cr3", ".cr2", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw", ".heic", ".heif", ".tif", ".tiff"];
    public static readonly string[] NativeAudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".ogg"];

    private readonly string _cacheDirectory;

    /// <summary>
    /// Optional platform decoder for raw stills (the WPF app supplies one backed by
    /// Windows' camera codecs). Returns true when it wrote a JPEG to the target path.
    /// </summary>
    public Func<string, string, bool>? StillDecoder { get; set; }

    public MediaConverter(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewsDesk", "Converted");
    }

    public static bool IsCameraVideo(string path) => CameraVideoExtensions.Contains(Extension(path));
    public static bool IsRawStill(string path) => RawStillExtensions.Contains(Extension(path));
    public static bool NeedsConversion(string path) => IsCameraVideo(path) || IsRawStill(path);

    /// <summary>File-dialog pattern for every format the app accepts.</summary>
    public static string DialogPattern(bool video, bool stills, bool audio = false)
    {
        var exts = new List<string>();
        if (video) exts.AddRange(NativeVideoExtensions.Concat(CameraVideoExtensions));
        if (stills) exts.AddRange(Models.MediaItem.ImageExtensions.Concat(RawStillExtensions));
        if (audio) exts.AddRange(NativeAudioExtensions);
        return string.Join(";", exts.Select(e => "*" + e));
    }

    private static string Extension(string path) => Path.GetExtension(path).ToLowerInvariant();

    /// <summary>
    /// Returns a path the canvas can play: the file itself when it is already a
    /// supported format, otherwise a cached converted copy.
    /// </summary>
    public async Task<string> EnsurePlayableAsync(string sourcePath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source file not found.", sourcePath);
        if (!NeedsConversion(sourcePath)) return sourcePath;

        Directory.CreateDirectory(_cacheDirectory);
        bool video = IsCameraVideo(sourcePath);
        var target = Path.Combine(_cacheDirectory, CacheName(sourcePath) + (video ? ".mp4" : ".jpg"));
        if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

        var partial = target + ".part" + (video ? ".mp4" : ".jpg");
        try
        {
            if (video) await TranscodeVideoAsync(sourcePath, partial, progress, ct);
            else await ConvertStillAsync(sourcePath, partial, ct);
            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>Readable name + hash of path, size and timestamp (a re-shot file with the same name re-converts).</summary>
    private static string CacheName(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var key = $"{Path.GetFullPath(sourcePath).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        var safe = string.Concat(Path.GetFileNameWithoutExtension(sourcePath).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        return $"{(safe.Length > 40 ? safe[..40] : safe)}_{hash}";
    }

    private async Task TranscodeVideoAsync(string source, string target, IProgress<string>? progress, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "ffmpeg is needed to convert camera video. Install it with: winget install Gyan.FFmpeg — then restart NEWS DESK.");

        // bwdif only touches frames flagged interlaced (1080i AVCHD), progressive footage passes through.
        // faststart puts the index first so the canvas can start and seek straight away.
        var args = new[]
        {
            "-hide_banner", "-nostdin", "-y", "-i", source,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", "bwdif=mode=send_frame:deint=interlaced,format=yuv420p",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2",
            "-movflags", "+faststart",
            "-progress", "pipe:1", "-nostats",
            target
        };

        var duration = await ProbeDurationAsync(ffmpeg, source, ct);
        var (exit, error) = await RunAsync(ffmpeg, args, line =>
        {
            // -progress emits out_time_ms=<microseconds>
            if (progress == null || duration <= 0 || !line.StartsWith("out_time_ms=")) return;
            if (long.TryParse(line["out_time_ms=".Length..], out var us))
                progress.Report($"{Math.Clamp(us / 1_000_000.0 / duration, 0, 1):P0}");
        }, ct);

        if (exit != 0 || !File.Exists(target))
            throw new InvalidOperationException($"ffmpeg could not convert '{Path.GetFileName(source)}': {LastLine(error)}");
    }

    private async Task ConvertStillAsync(string source, string target, CancellationToken ct)
    {
        // 1. Windows' own camera codecs (Raw Image Extension / HEIF) give the best result
        try
        {
            if (StillDecoder != null && StillDecoder(source, target) && File.Exists(target)) return;
        }
        catch
        {
            // Codec missing or unable to read this camera model: fall through
        }

        // 2. Every raw format carries a full-size JPEG preview from the camera
        var bytes = await File.ReadAllBytesAsync(source, ct);
        var jpeg = EmbeddedJpegExtractor.ExtractLargest(bytes);
        if (jpeg != null && jpeg.Length > 50_000)
        {
            await File.WriteAllBytesAsync(target, jpeg, ct);
            return;
        }

        // 3. Last resort: ffmpeg decodes some still formats (HEIC, TIFF)
        var ffmpeg = FindFfmpeg();
        if (ffmpeg != null)
        {
            var (exit, _) = await RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-i", source, "-frames:v", "1", "-q:v", "2", target], null, ct);
            if (exit == 0 && File.Exists(target)) return;
        }

        throw new InvalidOperationException(
            $"Could not read the picture in '{Path.GetFileName(source)}'. Install Microsoft's free 'Raw Image Extension' from the Microsoft Store, or export a JPEG from the camera software.");
    }

    private static async Task<double> ProbeDurationAsync(string ffmpeg, string source, CancellationToken ct)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffprobe)) return 0;
        var output = new StringBuilder();
        var (exit, _) = await RunAsync(ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", source],
            line => output.Append(line), ct);
        return exit == 0 && double.TryParse(output.ToString().Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>ffmpeg next to the app, then on PATH, then winget's install locations.</summary>
    public static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, exe) };

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), exe));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", exe));
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var pkg in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
                candidates.AddRange(Directory.EnumerateFiles(pkg, exe, SearchOption.AllDirectories));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string StdErr)> RunAsync(string exe, IEnumerable<string> args, Action<string>? onStdOut, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onStdOut?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (process.ExitCode, stderr.ToString());
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "unknown error";
}

/// <summary>
/// Finds the largest complete JPEG inside a file. Raw camera formats (CR3, CR2,
/// NEF, ARW, DNG …) embed a full-resolution JPEG preview alongside the sensor
/// data. JPEGs are walked marker by marker, so the small EXIF thumbnail nested
/// inside a preview's header doesn't cut the outer image short.
/// </summary>
public static class EmbeddedJpegExtractor
{
    public static byte[]? ExtractLargest(byte[] data)
    {
        int bestStart = -1, bestLength = 0;
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) continue;
            int end = FindJpegEnd(data, i);
            if (end < 0) continue;
            int length = end - i;
            if (length > bestLength)
            {
                bestStart = i;
                bestLength = length;
            }
            i = end - 1; // continue after this image
        }
        return bestStart < 0 ? null : data.AsSpan(bestStart, bestLength).ToArray();
    }

    /// <summary>Returns the index just past the EOI marker, or -1 when the stream isn't a whole JPEG.</summary>
    private static int FindJpegEnd(byte[] d, int start)
    {
        int p = start + 2;
        bool sawFrame = false;
        while (p + 1 < d.Length)
        {
            if (d[p] != 0xFF) return -1;
            byte marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; }              // fill byte
            if (marker == 0xD9) return sawFrame ? p + 2 : -1;    // EOI (may be the file's last bytes)
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) { p += 2; continue; }
            if (p + 3 >= d.Length) return -1;

            int segLength = (d[p + 2] << 8) | d[p + 3];
            if (segLength < 2 || p + 2 + segLength > d.Length) return -1;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) sawFrame = true;
            p += 2 + segLength;

            if (marker != 0xDA) continue;                        // not Start Of Scan

            // Entropy-coded data: runs until a marker other than stuffing (FF00) or restart (FFD0-D7)
            while (p + 1 < d.Length)
            {
                if (d[p] == 0xFF && d[p + 1] != 0x00 && d[p + 1] is not (>= 0xD0 and <= 0xD7)) break;
                p++;
            }
        }
        return -1;
    }
}
