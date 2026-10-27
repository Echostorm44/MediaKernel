namespace MediaKernel.Codecs;

/// <summary>
/// Selects the optimal decoder based on stream metadata and hardware capabilities.
/// Uses a scoring system to prefer hardware acceleration when available.
/// </summary>
public sealed class CodecArbiter
{
    private readonly IDecoderModule[] availableModules;

    public CodecArbiter(IDecoderModule[] modules)
    {
        availableModules = modules;
    }

    /// <summary>
    /// Selects the best decoder for the given codec request.
    /// </summary>
    /// <param name="request">The codec requirements.</param>
    /// <returns>The best available decoder.</returns>
    /// <exception cref="NotSupportedException">Thrown when no suitable decoder is found.</exception>
    public IDecoderModule SelectBestDecoder(CodecRequest request)
    {
        IDecoderModule? bestModule = null;
        int highestScore = 0;

        foreach (var module in availableModules)
        {
            int currentScore = EvaluateModule(module, request);

            if (currentScore > highestScore)
            {
                highestScore = currentScore;
                bestModule = module;
            }
        }

        return bestModule ?? throw new NotSupportedException(
            $"No suitable decoder found for codec '{request.CodecName}' " +
            $"at {request.Width}x{request.Height} {request.BitDepth}-bit.");
    }

    /// <summary>
    /// Evaluates a decoder module against the codec request.
    /// </summary>
    private static int EvaluateModule(IDecoderModule module, CodecRequest request)
    {
        if (!module.CanHandle(request.CodecName))
            return (int)DecoderScore.Unsupported;

        if (module is IHardwareDecoder hardware)
        {
            // Check hardware-specific limits
            if (request.Width > hardware.MaxSupportedWidth || 
                request.Height > hardware.MaxSupportedHeight)
            {
                // Fallback to software for frames exceeding hardware limits
                return (int)DecoderScore.Software;
            }

            if (request.BitDepth > 8 && !hardware.SupportsHighBitDepth)
            {
                // Hardware doesn't support HDR content
                return (int)DecoderScore.Software;
            }

            // Hardware decoding is preferred for power efficiency,
            // even on battery power (dedicated silicon uses less watts than CPU)
            return (int)DecoderScore.Hardware;
        }

        return (int)DecoderScore.Software;
    }

    /// <summary>
    /// Attempts to get a decoder, returning null instead of throwing if none found.
    /// </summary>
    public IDecoderModule? TrySelectDecoder(CodecRequest request)
    {
        try
        {
            return SelectBestDecoder(request);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
