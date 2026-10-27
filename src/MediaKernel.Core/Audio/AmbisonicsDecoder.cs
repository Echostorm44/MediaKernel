// MediaKernel - Ambisonics Decoder (B-format to speaker/binaural)
// Decodes spherical harmonic encoded audio to speaker or headphone output.
// Based on VLC's spatialaudio.cpp and libspatialaudio concepts.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Audio;

/// <summary>
/// Ambisonics order determines spatial resolution.
/// Higher orders provide better localization but require more channels.
/// </summary>
public enum AmbisonicsOrder
{
    /// <summary>First order: 4 channels (W, X, Y, Z). Most common in consumer content.</summary>
    First = 1,
    
    /// <summary>Second order: 9 channels. Better spatial resolution.</summary>
    Second = 2,
    
    /// <summary>Third order: 16 channels. Professional/VR applications.</summary>
    Third = 3
}

/// <summary>
/// B-format channel indices following ACN (Ambisonics Channel Number) ordering.
/// </summary>
public static class BFormatChannel
{
    // First order (4 channels)
    public const int W = 0;  // Omnidirectional (pressure)
    public const int Y = 1;  // Left-right (Y-axis velocity)
    public const int Z = 2;  // Up-down (Z-axis velocity)
    public const int X = 3;  // Front-back (X-axis velocity)
    
    // Second order (adds 5 more = 9 total)
    public const int V = 4;
    public const int T = 5;
    public const int R = 6;
    public const int S = 7;
    public const int U = 8;
    
    // Third order (adds 7 more = 16 total)
    public const int Q = 9;
    public const int O = 10;
    public const int M = 11;
    public const int K = 12;
    public const int L = 13;
    public const int N = 14;
    public const int P = 15;
    
    /// <summary>Returns channel count for given order.</summary>
    public static int ChannelCount(AmbisonicsOrder order) => ((int)order + 1) * ((int)order + 1);
}

/// <summary>
/// Speaker position in spherical coordinates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SpeakerPosition
{
    /// <summary>Azimuth angle in radians. 0 = front, π/2 = left, -π/2 = right.</summary>
    public readonly float Azimuth;
    
    /// <summary>Elevation angle in radians. 0 = horizontal, π/2 = above, -π/2 = below.</summary>
    public readonly float Elevation;
    
    /// <summary>Distance from listener in meters. Normalized to 1.0 for unit sphere.</summary>
    public readonly float Distance;
    
    public SpeakerPosition(float azimuth, float elevation, float distance = 1.0f)
    {
        Azimuth = azimuth;
        Elevation = elevation;
        Distance = distance;
    }
    
    /// <summary>Creates position from degrees.</summary>
    public static SpeakerPosition FromDegrees(float azimuthDegrees, float elevationDegrees, float distance = 1.0f)
        => new(azimuthDegrees * MathF.PI / 180f, elevationDegrees * MathF.PI / 180f, distance);
}

/// <summary>
/// Standard speaker layouts with pre-defined positions.
/// </summary>
public static class SpeakerLayouts
{
    /// <summary>Stereo: Left 30°, Right -30°</summary>
    public static readonly SpeakerPosition[] Stereo =
    [
        SpeakerPosition.FromDegrees(30, 0),   // Front Left
        SpeakerPosition.FromDegrees(-30, 0),  // Front Right
    ];
    
    /// <summary>Quadraphonic: FL 45°, FR -45°, BL 135°, BR -135°</summary>
    public static readonly SpeakerPosition[] Quad =
    [
        SpeakerPosition.FromDegrees(45, 0),    // Front Left
        SpeakerPosition.FromDegrees(-45, 0),   // Front Right
        SpeakerPosition.FromDegrees(135, 0),   // Back Left
        SpeakerPosition.FromDegrees(-135, 0),  // Back Right
    ];
    
    /// <summary>5.1 surround: FL 30°, FR -30°, C 0°, LFE 0°, BL 110°, BR -110°</summary>
    public static readonly SpeakerPosition[] Surround51 =
    [
        SpeakerPosition.FromDegrees(30, 0),    // Front Left
        SpeakerPosition.FromDegrees(-30, 0),   // Front Right
        SpeakerPosition.FromDegrees(0, 0),     // Center
        SpeakerPosition.FromDegrees(0, 0, 0.5f), // LFE (reduced distance = less directional)
        SpeakerPosition.FromDegrees(110, 0),   // Surround Left
        SpeakerPosition.FromDegrees(-110, 0),  // Surround Right
    ];
    
    /// <summary>7.1 surround: FL 30°, FR -30°, C 0°, LFE 0°, BL 145°, BR -145°, SL 110°, SR -110°</summary>
    public static readonly SpeakerPosition[] Surround71 =
    [
        SpeakerPosition.FromDegrees(30, 0),    // Front Left
        SpeakerPosition.FromDegrees(-30, 0),   // Front Right
        SpeakerPosition.FromDegrees(0, 0),     // Center
        SpeakerPosition.FromDegrees(0, 0, 0.5f), // LFE
        SpeakerPosition.FromDegrees(145, 0),   // Back Left
        SpeakerPosition.FromDegrees(-145, 0),  // Back Right
        SpeakerPosition.FromDegrees(110, 0),   // Side Left
        SpeakerPosition.FromDegrees(-110, 0),  // Side Right
    ];
    
    /// <summary>Binaural virtual speakers (cube arrangement for optimal HRTF)</summary>
    public static readonly SpeakerPosition[] BinauralVirtual =
    [
        SpeakerPosition.FromDegrees(45, 0),    // Front Left
        SpeakerPosition.FromDegrees(-45, 0),   // Front Right
        SpeakerPosition.FromDegrees(135, 0),   // Back Left
        SpeakerPosition.FromDegrees(-135, 0),  // Back Right
        SpeakerPosition.FromDegrees(45, 35),   // Top Front Left
        SpeakerPosition.FromDegrees(-45, 35),  // Top Front Right
        SpeakerPosition.FromDegrees(135, 35),  // Top Back Left
        SpeakerPosition.FromDegrees(-135, 35), // Top Back Right
    ];
}

/// <summary>
/// Ambisonics decoder configuration.
/// </summary>
public struct AmbisonicsConfig
{
    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate;
    
    /// <summary>Ambisonics order (1, 2, or 3).</summary>
    public AmbisonicsOrder Order;
    
    /// <summary>Block size for processing (default 1024 like VLC).</summary>
    public int BlockSize;
    
    /// <summary>Creates default config (48kHz, 1st order, 1024 block).</summary>
    public static AmbisonicsConfig Default => new()
    {
        SampleRate = 48000,
        Order = AmbisonicsOrder.First,
        BlockSize = 1024
    };
}

/// <summary>
/// Listener orientation in Euler angles.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ListenerOrientation
{
    /// <summary>Yaw (horizontal rotation) in radians. Positive = left.</summary>
    public float Yaw;
    
    /// <summary>Pitch (vertical tilt) in radians. Positive = up.</summary>
    public float Pitch;
    
    /// <summary>Roll (head tilt) in radians. Positive = right ear down.</summary>
    public float Roll;
    
    /// <summary>Zoom factor (0-1). Higher values focus sound toward front.</summary>
    public float Zoom;
    
    public static ListenerOrientation Default => new() { Yaw = 0, Pitch = 0, Roll = 0, Zoom = 0 };
}

/// <summary>
/// Decodes B-format Ambisonics audio to speaker or binaural output.
/// Supports 1st, 2nd, and 3rd order with rotation and zoom processing.
/// </summary>
public sealed class AmbisonicsDecoder
{
    private readonly AmbisonicsConfig config;
    private readonly int channelCount;
    private readonly float[] inputBuffer;
    private readonly float[] outputBuffer;
    private ListenerOrientation orientation;
    
    // Speaker decoding matrix: [outputChannel, inputChannel]
    private float[,] decodingMatrix;
    private SpeakerPosition[] speakerPositions;
    private int outputChannels;
    
    /// <summary>Gets the configured Ambisonics order.</summary>
    public AmbisonicsOrder Order => config.Order;
    
    /// <summary>Gets the number of input B-format channels.</summary>
    public int InputChannels => channelCount;
    
    /// <summary>Gets the number of output speaker channels.</summary>
    public int OutputChannels => outputChannels;
    
    /// <summary>Gets or sets the listener orientation for rotation processing.</summary>
    public ListenerOrientation Orientation
    {
        get => orientation;
        set
        {
            orientation = value;
            // Rotation is applied per-sample in Process(), no matrix update needed
        }
    }
    
    /// <summary>
    /// Creates an Ambisonics decoder with the specified configuration and speaker layout.
    /// </summary>
    public AmbisonicsDecoder(AmbisonicsConfig config, ReadOnlySpan<SpeakerPosition> speakers)
    {
        this.config = config;
        this.channelCount = BFormatChannel.ChannelCount(config.Order);
        this.inputBuffer = new float[channelCount * config.BlockSize];
        this.outputChannels = speakers.Length;
        this.outputBuffer = new float[outputChannels * config.BlockSize];
        this.orientation = ListenerOrientation.Default;
        
        ConfigureSpeakers(speakers);
    }
    
    /// <summary>
    /// Creates an Ambisonics decoder with default config and 5.1 speaker layout.
    /// </summary>
    public AmbisonicsDecoder() : this(AmbisonicsConfig.Default, SpeakerLayouts.Surround51)
    {
    }
    
    /// <summary>
    /// Configures the decoder for a new speaker layout.
    /// </summary>
    public void ConfigureSpeakers(ReadOnlySpan<SpeakerPosition> speakers)
    {
        speakerPositions = speakers.ToArray();
        outputChannels = speakers.Length;
        decodingMatrix = new float[outputChannels, channelCount];
        
        // Calculate spherical harmonic coefficients for each speaker
        for (int spk = 0; spk < outputChannels; spk++)
        {
            var pos = speakerPositions[spk];
            CalculateDecodingCoefficients(spk, pos);
        }
    }
    
    /// <summary>
    /// Calculates spherical harmonic decoding coefficients for a speaker position.
    /// Uses SN3D (semi-normalized) normalization with ACN channel ordering.
    /// </summary>
    private void CalculateDecodingCoefficients(int speakerIndex, SpeakerPosition pos)
    {
        float az = pos.Azimuth;
        float el = pos.Elevation;
        float cosEl = MathF.Cos(el);
        float sinEl = MathF.Sin(el);
        float cosAz = MathF.Cos(az);
        float sinAz = MathF.Sin(az);
        
        // Distance attenuation (inverse square, normalized to 1.0 at unit distance)
        float distGain = 1.0f / MathF.Max(pos.Distance, 0.1f);
        
        // First order (4 channels) - always present
        // W (omnidirectional) - weight of 0.7071 (1/sqrt(2)) for energy normalization
        decodingMatrix[speakerIndex, BFormatChannel.W] = 0.7071f * distGain;
        
        // X, Y, Z (figure-8 patterns)
        decodingMatrix[speakerIndex, BFormatChannel.Y] = sinAz * cosEl * distGain;
        decodingMatrix[speakerIndex, BFormatChannel.Z] = sinEl * distGain;
        decodingMatrix[speakerIndex, BFormatChannel.X] = cosAz * cosEl * distGain;
        
        if (config.Order >= AmbisonicsOrder.Second)
        {
            // Second order (adds 5 channels)
            float cos2Az = MathF.Cos(2 * az);
            float sin2Az = MathF.Sin(2 * az);
            float cosEl2 = cosEl * cosEl;
            float sinElCosEl = sinEl * cosEl;
            
            // V, T, R, S, U
            decodingMatrix[speakerIndex, BFormatChannel.V] = MathF.Sqrt(3f / 4f) * sin2Az * cosEl2 * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.T] = MathF.Sqrt(3f / 4f) * sinAz * sinElCosEl * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.R] = 0.5f * (3 * sinEl * sinEl - 1) * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.S] = MathF.Sqrt(3f / 4f) * cosAz * sinElCosEl * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.U] = MathF.Sqrt(3f / 4f) * cos2Az * cosEl2 * distGain;
        }
        
        if (config.Order >= AmbisonicsOrder.Third)
        {
            // Third order (adds 7 channels)
            float cos3Az = MathF.Cos(3 * az);
            float sin3Az = MathF.Sin(3 * az);
            float cosEl3 = cosEl * cosEl * cosEl;
            float sinEl2 = sinEl * sinEl;
            
            // Q, O, M, K, L, N, P
            decodingMatrix[speakerIndex, BFormatChannel.Q] = MathF.Sqrt(5f / 8f) * sin3Az * cosEl3 * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.O] = MathF.Sqrt(15f / 8f) * MathF.Sin(2 * az) * sinEl * cosEl * cosEl * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.M] = MathF.Sqrt(3f / 8f) * sinAz * cosEl * (5 * sinEl2 - 1) * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.K] = 0.5f * sinEl * (5 * sinEl2 - 3) * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.L] = MathF.Sqrt(3f / 8f) * cosAz * cosEl * (5 * sinEl2 - 1) * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.N] = MathF.Sqrt(15f / 8f) * MathF.Cos(2 * az) * sinEl * cosEl * cosEl * distGain;
            decodingMatrix[speakerIndex, BFormatChannel.P] = MathF.Sqrt(5f / 8f) * cos3Az * cosEl3 * distGain;
        }
    }
    
    /// <summary>
    /// Decodes B-format input to speaker output.
    /// </summary>
    /// <param name="input">Interleaved B-format input samples (channelCount channels).</param>
    /// <param name="output">Interleaved speaker output samples (outputChannels channels).</param>
    public void Decode(ReadOnlySpan<float> input, Span<float> output)
    {
        int frames = input.Length / channelCount;
        int requiredOutput = frames * outputChannels;
        
        if (output.Length < requiredOutput)
            return;
        
        // Apply rotation if orientation is non-zero
        bool needsRotation = MathF.Abs(orientation.Yaw) > 0.001f ||
                            MathF.Abs(orientation.Pitch) > 0.001f ||
                            MathF.Abs(orientation.Roll) > 0.001f;
        
        // Process each frame
        for (int frame = 0; frame < frames; frame++)
        {
            int inOffset = frame * channelCount;
            int outOffset = frame * outputChannels;
            
            // Read B-format channels (with optional rotation)
            Span<float> bFormat = stackalloc float[channelCount];
            for (int ch = 0; ch < channelCount; ch++)
                bFormat[ch] = input[inOffset + ch];
            
            if (needsRotation)
                RotateBFormat(bFormat);
            
            // Apply zoom if enabled
            if (orientation.Zoom > 0.01f)
                ApplyZoom(bFormat);
            
            // Decode to speakers using matrix multiplication
            for (int spk = 0; spk < outputChannels; spk++)
            {
                float sum = 0;
                for (int ch = 0; ch < channelCount; ch++)
                    sum += decodingMatrix[spk, ch] * bFormat[ch];
                
                output[outOffset + spk] = sum;
            }
        }
    }
    
    /// <summary>
    /// Rotates B-format channels based on current orientation.
    /// Only modifies first-order channels (W, X, Y, Z) - higher orders need full SH rotation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RotateBFormat(Span<float> bFormat)
    {
        // For first order, we can use simple 3D rotation
        // W is omnidirectional, doesn't need rotation
        float x = bFormat[BFormatChannel.X];
        float y = bFormat[BFormatChannel.Y];
        float z = bFormat[BFormatChannel.Z];
        
        // Rotate around Z-axis (yaw)
        float cosYaw = MathF.Cos(orientation.Yaw);
        float sinYaw = MathF.Sin(orientation.Yaw);
        float x1 = x * cosYaw - y * sinYaw;
        float y1 = x * sinYaw + y * cosYaw;
        
        // Rotate around Y-axis (pitch)
        float cosPitch = MathF.Cos(orientation.Pitch);
        float sinPitch = MathF.Sin(orientation.Pitch);
        float x2 = x1 * cosPitch + z * sinPitch;
        float z1 = -x1 * sinPitch + z * cosPitch;
        
        // Rotate around X-axis (roll)
        float cosRoll = MathF.Cos(orientation.Roll);
        float sinRoll = MathF.Sin(orientation.Roll);
        float y2 = y1 * cosRoll - z1 * sinRoll;
        float z2 = y1 * sinRoll + z1 * cosRoll;
        
        bFormat[BFormatChannel.X] = x2;
        bFormat[BFormatChannel.Y] = y2;
        bFormat[BFormatChannel.Z] = z2;
        
        // Note: Higher order channels require full spherical harmonic rotation matrices
        // which is significantly more complex. For simplicity, we only rotate 1st order.
    }
    
    /// <summary>
    /// Applies zoom effect by focusing sound toward front direction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyZoom(Span<float> bFormat)
    {
        // Zoom increases W (omni) while decreasing directional channels
        // This focuses the sound field toward the front
        float zoom = Math.Clamp(orientation.Zoom, 0, 1);
        float omniBoost = 1 + zoom * 0.5f;
        float directionalReduce = 1 - zoom * 0.3f;
        
        bFormat[BFormatChannel.W] *= omniBoost;
        
        for (int ch = 1; ch < channelCount; ch++)
            bFormat[ch] *= directionalReduce;
    }
    
    /// <summary>
    /// Decodes B-format to binaural stereo output for headphones.
    /// Uses virtual speaker approach with the HeadphoneVirtualizer.
    /// </summary>
    /// <param name="input">Interleaved B-format input samples.</param>
    /// <param name="output">Stereo output samples (L, R interleaved).</param>
    /// <param name="virtualizer">Headphone virtualizer for spatialization.</param>
    public void DecodeToBinaural(ReadOnlySpan<float> input, Span<float> output, HeadphoneVirtualizer virtualizer)
    {
        int frames = input.Length / channelCount;
        int requiredOutput = frames * 2;
        
        if (output.Length < requiredOutput)
            return;
        
        // First decode to virtual speaker array
        var virtualLayout = SpeakerLayouts.BinauralVirtual;
        ConfigureSpeakers(virtualLayout);
        
        Span<float> virtualSpeakers = stackalloc float[frames * virtualLayout.Length];
        Decode(input, virtualSpeakers);
        
        // Then binauralize the virtual speakers to stereo
        // For simplicity, we use a basic panning approach here
        // A full implementation would use proper HRTF convolution
        for (int frame = 0; frame < frames; frame++)
        {
            int vOffset = frame * virtualLayout.Length;
            int outOffset = frame * 2;
            
            float left = 0, right = 0;
            
            for (int spk = 0; spk < virtualLayout.Length; spk++)
            {
                float sample = virtualSpeakers[vOffset + spk];
                var pos = virtualLayout[spk];
                
                // Simple amplitude panning based on azimuth
                // Left ear favors left speakers, right ear favors right
                float pan = MathF.Sin(pos.Azimuth) * 0.5f + 0.5f; // 0=right, 1=left
                left += sample * pan;
                right += sample * (1 - pan);
            }
            
            // Normalize by number of speakers to prevent clipping
            float norm = 1.0f / MathF.Sqrt(virtualLayout.Length);
            output[outOffset] = left * norm;
            output[outOffset + 1] = right * norm;
        }
    }
    
    /// <summary>
    /// Decodes B-format to binaural stereo with simple HRTF approximation.
    /// Uses interaural time and level differences for spatialization.
    /// </summary>
    public void DecodeToBinauralSimple(ReadOnlySpan<float> input, Span<float> output)
    {
        int frames = input.Length / channelCount;
        int requiredOutput = frames * 2;
        
        if (output.Length < requiredOutput)
            return;
        
        // For 1st order, we can use a simple approach:
        // Left ear = W + 0.7*Y - 0.5*X
        // Right ear = W - 0.7*Y - 0.5*X
        // This approximates basic HRTF response
        
        bool hasRotation = MathF.Abs(orientation.Yaw) > 0.001f ||
                          MathF.Abs(orientation.Pitch) > 0.001f;
        
        for (int frame = 0; frame < frames; frame++)
        {
            int inOffset = frame * channelCount;
            int outOffset = frame * 2;
            
            float w = input[inOffset + BFormatChannel.W];
            float y = input[inOffset + BFormatChannel.Y];
            float z = input[inOffset + BFormatChannel.Z];
            float x = input[inOffset + BFormatChannel.X];
            
            // Apply yaw rotation (most important for head tracking)
            if (hasRotation)
            {
                float cosYaw = MathF.Cos(orientation.Yaw);
                float sinYaw = MathF.Sin(orientation.Yaw);
                float newX = x * cosYaw - y * sinYaw;
                float newY = x * sinYaw + y * cosYaw;
                x = newX;
                y = newY;
            }
            
            // Approximate binaural decode
            // These coefficients approximate ITD/ILD for average head size
            float left = w * 0.7071f + y * 0.5f + x * 0.3f + z * 0.2f;
            float right = w * 0.7071f - y * 0.5f + x * 0.3f + z * 0.2f;
            
            output[outOffset] = left;
            output[outOffset + 1] = right;
        }
    }
    
    /// <summary>
    /// Resets decoder state (clears any internal buffers).
    /// </summary>
    public void Reset()
    {
        Array.Clear(inputBuffer);
        Array.Clear(outputBuffer);
        orientation = ListenerOrientation.Default;
    }
}
