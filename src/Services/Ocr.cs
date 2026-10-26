// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace DynamicIsland.Services;

/// <summary>Reconocimiento de texto con el motor de Windows (sin internet).</summary>
public static class OcrService
{
    /// <summary>Quita el canal alfa (el portapapeles de WPF a veces lo trae vacío).</summary>
    public static BitmapSource Opaque(BitmapSource src)
    {
        var b = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
        b.Freeze();
        return b;
    }

    public static byte[] ToPng(BitmapSource src)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <returns>Texto reconocido (puede ser vacío). null si Windows no tiene un idioma de OCR instalado.</returns>
    public static async Task<string?> RecognizeAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) return null;

        byte[] png = ToPng(image);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await WinBitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }
}
