using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FolderIconStudio.Services;

internal static class IcoEncoder
{
    private static readonly int[] Sizes = [256, 64, 48, 32, 16];

    public static void ConvertImageToIco(string sourcePath, string targetPath)
    {
        using var source = File.OpenRead(sourcePath);
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("图片没有可读取的画面。");
        var frame = decoder.Frames[0];

        var frames = new List<byte[]>(Sizes.Length);
        foreach (var size in Sizes) frames.Add(RenderPng(frame, size));

        using var output = File.Create(targetPath);
        using var writer = new BinaryWriter(output);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);

        var offset = 6 + (16 * frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var size = Sizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frameBytes in frames) writer.Write(frameBytes);
    }

    private static byte[] RenderPng(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var context = visual.RenderOpen())
        {
            var scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
            var width = Math.Max(1.0, source.PixelWidth * scale);
            var height = Math.Max(1.0, source.PixelHeight * scale);
            context.DrawImage(source, new Rect((size - width) / 2, (size - height) / 2, width, height));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
