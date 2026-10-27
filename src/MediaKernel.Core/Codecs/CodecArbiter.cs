using System;
using System.Collections.Generic;
using MediaKernel.Core.Hardware;
using MediaKernel.Core.Hardware.D3D11;

namespace MediaKernel.Core.Codecs
{
    /// <summary>
    /// Decoder capability scores for selection priority.
    /// </summary>
    public static class DecoderScores
    {
        /// <summary>Hardware decoder available and supported.</summary>
        public const int HardwareAccelerated = 100;

        /// <summary>Software decoder available.</summary>
        public const int SoftwareDecoder = 10;

        /// <summary>Codec not supported.</summary>
        public const int Unsupported = 0;
    }

    /// <summary>
    /// Represents the capabilities of a specific decoder.
    /// </summary>
    public sealed class DecoderCapability
    {
        /// <summary>Name of the decoder.</summary>
        public required string DecoderName { get; init; }

        /// <summary>Codec type this decoder handles.</summary>
        public required HardwareCodecType CodecType { get; init; }

        /// <summary>True if this is a hardware-accelerated decoder.</summary>
        public required bool IsHardwareAccelerated { get; init; }

        /// <summary>Priority score (higher = preferred).</summary>
        public required int Score { get; init; }

        /// <summary>Maximum supported width.</summary>
        public required uint MaxWidth { get; init; }

        /// <summary>Maximum supported height.</summary>
        public required uint MaxHeight { get; init; }

        /// <summary>Maximum supported bit depth.</summary>
        public required int MaxBitDepth { get; init; }

        /// <summary>Supported output formats.</summary>
        public required HardwarePixelFormat[] SupportedFormats { get; init; }

        /// <summary>Profile name (e.g., "H.264 VLD No FGT").</summary>
        public string ProfileName { get; init; } = "";

        /// <summary>
        /// Checks if this decoder can handle the given video parameters.
        /// </summary>
        public bool CanDecode(uint width, uint height, int bitDepth)
        {
            return width <= MaxWidth &&
                   height <= MaxHeight &&
                   bitDepth <= MaxBitDepth;
        }
    }

    /// <summary>
    /// Result of decoder selection.
    /// </summary>
    public readonly struct DecoderSelection
    {
        public DecoderSelection(DecoderCapability? capability, bool success, string? errorMessage = null)
        {
            Capability = capability;
            Success = success;
            ErrorMessage = errorMessage;
        }

        /// <summary>Selected decoder capability.</summary>
        public DecoderCapability? Capability { get; }

        /// <summary>True if selection succeeded.</summary>
        public bool Success { get; }

        /// <summary>Error message if selection failed.</summary>
        public string? ErrorMessage { get; }

        public static DecoderSelection Failed(string message) => new(null, false, message);
        public static DecoderSelection Ok(DecoderCapability capability) => new(capability, true);
    }

    /// <summary>
    /// The Codec Arbiter selects the best decoder for a given codec and video parameters.
    /// It probes hardware capabilities and falls back to software when needed.
    /// </summary>
    public sealed class CodecArbiter : IDisposable
    {
        private readonly List<DecoderCapability> capabilities = [];
        private D3D11DecoderFactory? d3d11Factory;
        private bool isInitialized;
        private bool isDisposed;

        /// <summary>
        /// Gets all discovered decoder capabilities.
        /// </summary>
        public IReadOnlyList<DecoderCapability> Capabilities => capabilities;

        /// <summary>
        /// Gets whether hardware acceleration is available.
        /// </summary>
        public bool HasHardwareAcceleration => d3d11Factory?.IsInitialized ?? false;

        /// <summary>
        /// Initializes the arbiter, probing all available decoders.
        /// </summary>
        public void Initialize()
        {
            if (isInitialized) return;

            // Probe D3D11VA on Windows
            if (OperatingSystem.IsWindows())
            {
                ProbeD3D11Decoders();
            }

            // TODO: Probe VA-API on Linux
            // TODO: Probe VideoToolbox on macOS

            // Register software decoders (when available)
            RegisterSoftwareDecoders();

            isInitialized = true;
        }

        private void ProbeD3D11Decoders()
        {
            try
            {
                d3d11Factory = new D3D11DecoderFactory();
                if (!d3d11Factory.Initialize())
                {
                    d3d11Factory.Dispose();
                    d3d11Factory = null;
                    return;
                }

                var hwCapabilities = d3d11Factory.GetCapabilities();
                foreach (var hwCap in hwCapabilities)
                {
                    capabilities.Add(new DecoderCapability
                    {
                        DecoderName = $"D3D11VA {hwCap.ProfileName}",
                        CodecType = hwCap.CodecType,
                        IsHardwareAccelerated = true,
                        Score = DecoderScores.HardwareAccelerated,
                        MaxWidth = hwCap.MaxWidth,
                        MaxHeight = hwCap.MaxHeight,
                        MaxBitDepth = hwCap.MaxBitDepth,
                        SupportedFormats = hwCap.SupportedFormats,
                        ProfileName = hwCap.ProfileName,
                    });
                }
            }
            catch
            {
                d3d11Factory?.Dispose();
                d3d11Factory = null;
            }
        }

        private void RegisterSoftwareDecoders()
        {
            // Software decoders would be registered here when implemented
            // For now, we only have hardware decoders
        }

        /// <summary>
        /// Selects the best decoder for the given codec and video parameters.
        /// </summary>
        /// <param name="codecType">The codec type to decode.</param>
        /// <param name="width">Video width in pixels.</param>
        /// <param name="height">Video height in pixels.</param>
        /// <param name="bitDepth">Bit depth (8, 10, 12).</param>
        /// <param name="preferHardware">If true, prefer hardware decoders.</param>
        /// <returns>The selected decoder, or a failure result.</returns>
        public DecoderSelection SelectDecoder(
            HardwareCodecType codecType,
            uint width,
            uint height,
            int bitDepth = 8,
            bool preferHardware = true)
        {
            if (!isInitialized)
            {
                Initialize();
            }

            // Find all capable decoders
            var candidates = new List<DecoderCapability>();
            foreach (var cap in capabilities)
            {
                if (cap.CodecType == codecType && cap.CanDecode(width, height, bitDepth))
                {
                    candidates.Add(cap);
                }
            }

            if (candidates.Count == 0)
            {
                return DecoderSelection.Failed($"No decoder available for {codecType} at {width}x{height} {bitDepth}-bit");
            }

            // Sort by score (highest first), then by hardware preference
            candidates.Sort((a, b) =>
            {
                // If preferring hardware, boost hardware scores in comparison
                int scoreA = a.Score;
                int scoreB = b.Score;

                if (preferHardware)
                {
                    if (a.IsHardwareAccelerated && !b.IsHardwareAccelerated)
                        return -1;
                    if (!a.IsHardwareAccelerated && b.IsHardwareAccelerated)
                        return 1;
                }

                return scoreB.CompareTo(scoreA);
            });

            return DecoderSelection.Ok(candidates[0]);
        }

        /// <summary>
        /// Creates a D3D11 video decoder for the given parameters.
        /// Returns null if not available or not on Windows.
        /// </summary>
        public D3D11VideoDecoder? CreateD3D11Decoder(
            HardwareCodecType codecType,
            uint width,
            uint height,
            int bitDepth = 8)
        {
            if (d3d11Factory is null || !d3d11Factory.IsInitialized)
            {
                return null;
            }

            return d3d11Factory.CreateDecoder(codecType, width, height, bitDepth);
        }

        /// <summary>
        /// Gets a summary of available decoders.
        /// </summary>
        public string GetCapabilitySummary()
        {
            if (!isInitialized)
            {
                Initialize();
            }

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("Decoder Capabilities:");
            builder.AppendLine("----------------------");

            if (capabilities.Count == 0)
            {
                builder.AppendLine("  No decoders available");
                return builder.ToString();
            }

            var groupedByCodec = new Dictionary<HardwareCodecType, List<DecoderCapability>>();
            foreach (var cap in capabilities)
            {
                if (!groupedByCodec.TryGetValue(cap.CodecType, out var list))
                {
                    list = [];
                    groupedByCodec[cap.CodecType] = list;
                }
                list.Add(cap);
            }

            foreach (var (codec, decoders) in groupedByCodec)
            {
                builder.AppendLine($"  {codec}:");
                foreach (var dec in decoders)
                {
                    string type = dec.IsHardwareAccelerated ? "HW" : "SW";
                    builder.AppendLine($"    [{type}] {dec.DecoderName} - {dec.MaxWidth}x{dec.MaxHeight} {dec.MaxBitDepth}-bit (score: {dec.Score})");
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Checks if a specific codec is supported at all.
        /// </summary>
        public bool IsCodecSupported(HardwareCodecType codecType)
        {
            if (!isInitialized)
            {
                Initialize();
            }

            foreach (var cap in capabilities)
            {
                if (cap.CodecType == codecType)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if hardware acceleration is available for a codec.
        /// </summary>
        public bool HasHardwareSupport(HardwareCodecType codecType)
        {
            if (!isInitialized)
            {
                Initialize();
            }

            foreach (var cap in capabilities)
            {
                if (cap.CodecType == codecType && cap.IsHardwareAccelerated)
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            d3d11Factory?.Dispose();
        }
    }
}
