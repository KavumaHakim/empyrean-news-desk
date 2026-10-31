using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NewsDesk.App.Services;

/// <summary>
/// Decodes raw camera stills with Windows' installed codecs (Raw Image Extension,
/// HEIF) and writes a broadcast-sized JPEG, turned upright from the camera's
/// orientation tag so portrait shots don't appear sideways.
/// </summary>
public static class WicStillDecoder
{
    private const int MaxWidth = 3840;

    public static bool TryConvert(string source, string target)
    {
        BitmapSource frame;
        BitmapMetadata? metadata;
        using (var stream = File.OpenRead(source))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return false;
            frame = decoder.Frames[0];
            metadata = frame.Metadata as BitmapMetadata;
        }

        if (frame.PixelWidth < 64) return false; // codec handed back a thumbnail only

        BitmapSource output = frame;
        if (frame.PixelWidth > MaxWidth)
        {
            double scale = (double)MaxWidth / frame.PixelWidth;
            output = new TransformedBitmap(output, new ScaleTransform(scale, scale));
        }

        var rotation = OrientationToRotation(ReadOrientation(metadata));
        if (rotation != 0) output = new TransformedBitmap(output, new RotateTransform(rotation));

        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var outStream = File.Create(target);
        encoder.Save(outStream);
        return true;
    }

    /// <summary>EXIF orientation (1 = upright, 3 = 180°, 6 = 90° CW, 8 = 90° CCW).</summary>
    private static int ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata == null) return 1;
        foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                var value = metadata.GetQuery(query);
                if (value != null) return Convert.ToInt32(value);
            }
            catch
            {
                // Query not supported by this container format
            }
        }
        return 1;
    }

    private static double OrientationToRotation(int orientation) => orientation switch
    {
        3 => 180,
        6 => 90,
        8 => 270,
        _ => 0
    };
}
