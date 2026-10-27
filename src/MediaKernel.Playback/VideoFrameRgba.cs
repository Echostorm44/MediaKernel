using System.Runtime.InteropServices;
using MediaKernel.Core;
using MediaKernel.Core.Video;

namespace MediaKernel.Playback;

/// <summary>
/// Turns a decoded <see cref="VideoFrame"/> into the RGBA8 buffer a host draws.
/// </summary>
/// <remarks>
/// MediaKernel's converters all write BGRA, which is the order a Direct3D or Metal texture wants;
/// the hosts this hands pictures to want RGBA8. The two differ only in the red and blue
/// channels, so the conversion runs MediaKernel's own (SIMD) kernel and then swaps those two -
/// one pass over the frame, no second colour conversion. <see cref="SwapRedAndBlue"/> is the one
/// place to change if the backend turns out to want BGRA after all: a swapped frame is obvious on
/// anything with sky or skin in it, and the gate for this is a human looking at a picture.
/// </remarks>
public static class VideoFrameRgba
{
    /// <summary>Whether the upload path wants RGBA (true) or the BGRA our kernels emit (false).</summary>
    private const bool SwapRedAndBlue = true;

    /// <summary>
    /// Converts <paramref name="frame"/> into <paramref name="destination"/>, which is resized
    /// when it is not already the right length.
    /// </summary>
    /// <returns>The RGBA8 bytes, tightly packed, <c>width * height * 4</c> long.</returns>
    public static byte[] Convert(ref VideoFrame frame, byte[]? destination)
    {
        int width = frame.Width;
        int height = frame.Height;
        int stride = width * 4;
        int length = stride * height;

        byte[] output = destination is not null && destination.Length == length
            ? destination
            : new byte[length];

        // Rec.601 below standard definition and Rec.709 above it, which is what the two
        // standards themselves say and what every player assumes in the absence of the
        // colour-description fields. VideoFrame does not carry them, so the height decides.
        var matrix = height > 576 ? ColorMatrix.Bt709 : ColorMatrix.Bt601;

        switch (frame.Format)
        {
            case PixelFormat.Yuv420P:
                YuvToRgbConverter.ConvertI420ToBgra(
                    frame.YPlane.Span, frame.UPlane.Span, frame.VPlane.Span,
                    width, height, frame.YStride, ChromaStride(ref frame),
                    output, stride, in matrix);
                break;

            case PixelFormat.Yuv422P:
                YuvToRgbConverter.ConvertI422ToBgra(
                    frame.YPlane.Span, frame.UPlane.Span, frame.VPlane.Span,
                    width, height, frame.YStride, ChromaStride(ref frame),
                    output, stride, in matrix);
                break;

            case PixelFormat.Yuv420P10:
                YuvToRgbConverter.ConvertI420P10ToBgra(
                    frame.YPlane.Span, frame.UPlane.Span, frame.VPlane.Span,
                    width, height, frame.YStride, ChromaStride(ref frame),
                    output, stride, in matrix);
                break;

            case PixelFormat.Nv12:
                YuvToRgbConverter.ConvertNv12ToBgra(
                    frame.YPlane.Span, frame.UPlane.Span,
                    width, height, frame.YStride, frame.UStride,
                    output, stride, in matrix);
                break;

            case PixelFormat.Gray8:
                YuvToRgbConverter.ConvertYToBgra(
                    frame.YPlane.Span, width, height, frame.YStride, output, stride);
                break;

            case PixelFormat.Bgra:
                CopyPacked(frame.YPlane.Span, frame.YStride, output, stride, height);
                break;

            default:
                // Not a silent black window: a format with no arm here is a decoder this app
                // cannot show yet, and saying which one is the whole point of a sanity check.
                throw new NotSupportedException(
                    $"This player cannot display {frame.Format} yet. Add an arm to "
                  + $"{nameof(VideoFrameRgba)}.{nameof(Convert)} that converts it to RGBA.");
        }

        if (SwapRedAndBlue)
        {
            SwapChannels(output);
        }

        return output;
    }

    /// <summary>
    /// The stride shared by the two chroma planes. MediaKernel's converters take one figure for
    /// both, which every decoder here satisfies; a frame that did not would be converted with
    /// the wrong stride on one plane and look sheared, so it is refused instead.
    /// </summary>
    private static int ChromaStride(ref VideoFrame frame)
    {
        if (frame.UStride != frame.VStride)
        {
            throw new NotSupportedException(
                $"The U and V planes have different strides ({frame.UStride} and {frame.VStride}); "
              + "MediaKernel's converters take one stride for both.");
        }

        return frame.UStride;
    }

    /// <summary>Copies a packed frame row by row, dropping any stride padding.</summary>
    private static void CopyPacked(ReadOnlySpan<byte> source, int sourceStride,
                                   Span<byte> destination, int destinationStride, int height)
    {
        for (int y = 0; y < height; y++)
        {
            source.Slice(y * sourceStride, destinationStride)
                  .CopyTo(destination.Slice(y * destinationStride, destinationStride));
        }
    }

    /// <summary>
    /// Swaps the first and third byte of every pixel: BGRA becomes RGBA. Done a pixel at a time
    /// as a 32-bit word, which is cheap enough beside the colour conversion itself.
    /// </summary>
    private static void SwapChannels(Span<byte> pixels)
    {
        var words = MemoryMarshal.Cast<byte, uint>(pixels);

        for (int i = 0; i < words.Length; i++)
        {
            uint p = words[i];
            words[i] = (p & 0xFF00FF00u) | ((p & 0x000000FFu) << 16) | ((p & 0x00FF0000u) >> 16);
        }
    }
}
