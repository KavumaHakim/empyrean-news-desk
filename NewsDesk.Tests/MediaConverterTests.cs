using System.Diagnostics;
using NewsDesk.Core.Data;
using NewsDesk.Core.Models;
using NewsDesk.Core.Services;

namespace NewsDesk.Tests;

public class EmbeddedJpegExtractorTests
{
    // Smallest structurally complete JPEG: SOI, a frame header, a scan with
    // entropy bytes (including stuffed FF00 and a restart marker), then EOI.
    private static byte[] Jpeg(int entropyBytes, byte[]? app1Payload = null)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (app1Payload != null)
        {
            int len = app1Payload.Length + 2;
            bytes.AddRange([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
            bytes.AddRange(app1Payload);
        }
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00]); // SOF0
        bytes.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);                   // SOS
        for (int i = 0; i < entropyBytes; i++) bytes.Add((byte)(i % 0xF0));
        bytes.AddRange([0xFF, 0x00, 0x12, 0xFF, 0xD3, 0x34]); // stuffing + restart marker inside the scan
        bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }

    [Fact]
    public void ExtractsLargestJpeg_FromRawLikeContainer()
    {
        var small = Jpeg(200);
        var large = Jpeg(5000);
        var container = new byte[] { 0x00, 0x01, 0x66, 0x74, 0x79, 0x70 }   // "ftyp"-style header noise
            .Concat(small).Concat(new byte[300]).Concat(large).Concat(new byte[] { 0xFF, 0xD8, 0x00 }) // trailing junk SOI
            .ToArray();

        var result = EmbeddedJpegExtractor.ExtractLargest(container);

        Assert.NotNull(result);
        Assert.Equal(large, result);
    }

    [Fact]
    public void NestedExifThumbnail_DoesNotTruncateThePreview()
    {
        // Camera previews carry a complete thumbnail JPEG inside their EXIF block
        var preview = Jpeg(4000, app1Payload: Jpeg(100));

        var result = EmbeddedJpegExtractor.ExtractLargest(new byte[64].Concat(preview).ToArray());

        Assert.Equal(preview, result);
    }

    [Fact]
    public void ReturnsNull_WhenThereIsNoCompleteJpeg()
    {
        Assert.Null(EmbeddedJpegExtractor.ExtractLargest([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x00]));
    }
}

public class MediaConverterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"newsdesk_convert_{Guid.NewGuid()}");

    public MediaConverterTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("clip.MTS", true)]
    [InlineData("clip.m2ts", true)]
    [InlineData("photo.CR3", true)]
    [InlineData("photo.nef", true)]
    [InlineData("clip.mp4", false)]
    [InlineData("photo.jpg", false)]
    public void KnowsWhichFilesNeedConversion(string file, bool expected)
    {
        Assert.Equal(expected, MediaConverter.NeedsConversion(file));
    }

    [Fact]
    public async Task NativeFiles_ArePassedThroughUntouched()
    {
        var mp4 = Path.Combine(_dir, "already.mp4");
        await File.WriteAllBytesAsync(mp4, [1, 2, 3]);

        var result = await new MediaConverter(Path.Combine(_dir, "cache")).EnsurePlayableAsync(mp4);

        Assert.Equal(mp4, result);
    }

    [Fact]
    public async Task RawStill_UsesEmbeddedPreview_WhenNoCodecIsAvailable()
    {
        var preview = new EmbeddedJpegExtractorTestsAccess().LargePreview();
        var cr3 = Path.Combine(_dir, "IMG_0001.CR3");
        await File.WriteAllBytesAsync(cr3, new byte[1024].Concat(preview).Concat(new byte[2048]).ToArray());

        var converter = new MediaConverter(Path.Combine(_dir, "cache")) { StillDecoder = (_, _) => false };
        var jpg = await converter.EnsurePlayableAsync(cr3);

        Assert.EndsWith(".jpg", jpg);
        Assert.Equal(preview, await File.ReadAllBytesAsync(jpg));
        Assert.True(new MediaItem { FilePath = jpg }.IsImage);

        // Second call is served from the cache
        Assert.Equal(jpg, await converter.EnsurePlayableAsync(cr3));
    }

    [Fact]
    public async Task CameraVideo_IsTranscodedToMp4_WhenFfmpegIsInstalled()
    {
        var ffmpeg = MediaConverter.FindFfmpeg();
        if (ffmpeg == null) return; // conversion needs ffmpeg; nothing to check on machines without it

        // A 2-second AVCHD-style transport stream: interlaced H.264 + AC-3 audio
        var mts = Path.Combine(_dir, "00001.MTS");
        var make = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList =
            {
                "-hide_banner", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-flags", "+ildct+ilme", "-c:a", "ac3", "-f", "mpegts", mts
            },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        })!;
        await make.StandardError.ReadToEndAsync();
        await make.WaitForExitAsync();
        Assert.True(File.Exists(mts));

        var reports = new List<string>();
        var mp4 = await new MediaConverter(Path.Combine(_dir, "cache"))
            .EnsurePlayableAsync(mts, new SyncProgress(reports.Add));

        Assert.EndsWith(".mp4", mp4);
        Assert.True(new FileInfo(mp4).Length > 10_000);
        Assert.False(new MediaItem { FilePath = mp4 }.IsImage);
        Assert.NotEmpty(reports);
    }

    private static async Task<int> RunFfmpegAsync(string ffmpeg, params string[] args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    [Fact]
    public async Task HealthCheck_FlagsVp9AndDamagedVideo_AndRepairMakesThemClean()
    {
        var ffmpeg = MediaConverter.FindFfmpeg();
        if (ffmpeg == null) return; // the check needs ffmpeg; nothing to verify without it

        var converter = new MediaConverter(Path.Combine(_dir, "cache"));

        // A healthy H.264 clip passes
        var good = Path.Combine(_dir, "good.mp4");
        await RunFfmpegAsync(ffmpeg, "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=4",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", good);
        Assert.Null(await converter.CheckVideoHealthAsync(good));

        // VP9 in an MP4 (like a phone/web download) is flagged by its format
        var vp9 = Path.Combine(_dir, "web.mp4");
        await RunFfmpegAsync(ffmpeg, "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=2",
            "-c:v", "libvpx-vp9", "-b:v", "300k", vp9);
        if (File.Exists(vp9)) Assert.Contains("VP9", await converter.CheckVideoHealthAsync(vp9));

        // Overwrite a stretch of the picture data with junk: a damaged file
        var damaged = Path.Combine(_dir, "damaged.mp4");
        await RunFfmpegAsync(ffmpeg, "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=6",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "250", "-movflags", "+faststart", damaged);
        var bytes = await File.ReadAllBytesAsync(damaged);
        var rng = new Random(7);
        for (int i = bytes.Length / 5; i < bytes.Length * 4 / 5; i += 97) bytes[i] = (byte)rng.Next(256);
        await File.WriteAllBytesAsync(damaged, bytes);
        Assert.Contains("damaged", await converter.CheckVideoHealthAsync(damaged));

        // The rebuilt copy is a clean H.264 file the check passes
        var repaired = await converter.RepairVideoAsync(damaged);
        Assert.EndsWith("_fixed.mp4", repaired);
        Assert.True(new FileInfo(repaired).Length > 1000);
        Assert.Null(await converter.CheckVideoHealthAsync(repaired));
    }

    [Fact]
    public async Task SameFileConvertedTwiceAtOnce_GivesOneCleanCopy()
    {
        var ffmpeg = MediaConverter.FindFfmpeg();
        if (ffmpeg == null) return;

        // Importing the same camera file twice in quick succession used to run two
        // conversions into one temporary file and scramble it
        var mts = Path.Combine(_dir, "00002.MTS");
        await RunFfmpegAsync(ffmpeg, "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-c:v", "libx264", "-c:a", "ac3", "-f", "mpegts", mts);

        var cache = Path.Combine(_dir, "cache");
        var first = new MediaConverter(cache).EnsurePlayableAsync(mts);
        var second = new MediaConverter(cache).EnsurePlayableAsync(mts);
        var paths = await Task.WhenAll(first, second);

        Assert.Equal(paths[0], paths[1]);
        Assert.Null(await new MediaConverter(cache).CheckVideoHealthAsync(paths[0]));
        Assert.Empty(Directory.GetFiles(cache, "*.part*"));
    }

    [Fact]
    public void OriginalPath_IsStoredWithTheMediaItem()
    {
        var dbPath = Path.Combine(_dir, "media.db");
        var db = new DatabaseService(dbPath);
        int id = db.AddMedia(new MediaItem
        {
            Title = "Speech",
            FilePath = @"C:\cache\speech.mp4",
            OriginalPath = @"E:\PRIVATE\AVCHD\BDMV\STREAM\00001.MTS"
        });

        Assert.Equal(@"E:\PRIVATE\AVCHD\BDMV\STREAM\00001.MTS", db.GetMediaById(id)!.OriginalPath);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; tests want the reports inline.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class EmbeddedJpegExtractorTestsAccess
    {
        // A preview big enough to pass the converter's "real picture" size check
        public byte[] LargePreview()
        {
            var bytes = new List<byte> { 0xFF, 0xD8 };
            bytes.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00]);
            bytes.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
            for (int i = 0; i < 80_000; i++) bytes.Add((byte)(i % 0xF0));
            bytes.AddRange([0xFF, 0xD9]);
            return bytes.ToArray();
        }
    }
}
