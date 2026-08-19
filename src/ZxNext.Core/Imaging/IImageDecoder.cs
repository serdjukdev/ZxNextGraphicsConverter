namespace ZxNext.Core.Imaging;

public record DecodedImage(int Width, int Height, byte[] Rgba32);

/// <summary>Decodes PNG/BMP/JPG files to RGBA32 pixel buffers. Implemented in the App layer (WPF's own codecs), kept behind this interface so Core stays WPF-free.</summary>
public interface IImageDecoder
{
    DecodedImage Decode(string filePath);
}
