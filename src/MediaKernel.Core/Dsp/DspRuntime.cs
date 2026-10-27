// Licensed to the MediaKernel contributors. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace MediaKernel.Core.Dsp;

/// <summary>
/// Runtime detection of CPU SIMD capabilities. Queried once at startup;
/// the JIT eliminates dead branches when the ISA check is a static readonly bool.
/// </summary>
public static class DspRuntime
{
    /// <summary>Baseline x86 SIMD (128-bit integer/float).</summary>
    public static readonly bool HasSse2 = Sse2.IsSupported;

    /// <summary>SSE4.1 adds blend, round, insert/extract, and widening multiply.</summary>
    public static readonly bool HasSse41 = Sse41.IsSupported;

    /// <summary>AVX2 provides 256-bit integer SIMD — the primary fast path.</summary>
    public static readonly bool HasAvx2 = Avx2.IsSupported;

    /// <summary>AVX-512 BW/F provides 512-bit SIMD for server/workstation CPUs.</summary>
    public static readonly bool HasAvx512 = Avx512BW.IsSupported;

    /// <summary>ARM NEON 128-bit SIMD (Apple Silicon, Snapdragon, Graviton).</summary>
    public static readonly bool HasNeon = AdvSimd.IsSupported;

    /// <summary>ARM NEON with 64-bit specific extensions.</summary>
    public static readonly bool HasNeonArm64 = AdvSimd.Arm64.IsSupported;

    /// <summary>The maximum usable SIMD register width in bytes.</summary>
    public static readonly int MaxSimdWidth =
        HasAvx512 ? 64 :
        HasAvx2 ? 32 :
        HasSse2 || HasNeon ? 16 :
        0;

    /// <summary>Human-readable tier name for diagnostics.</summary>
    public static string SimdTierName =>
        HasAvx512 ? "AVX-512" :
        HasAvx2 ? "AVX2" :
        HasSse41 ? "SSE4.1" :
        HasSse2 ? "SSE2" :
        HasNeonArm64 ? "NEON (Arm64)" :
        HasNeon ? "NEON" :
        "Scalar";

    /// <summary>
    /// Returns a summary string for CLI/diagnostic output.
    /// </summary>
    public static string GetCapabilitySummary()
    {
        return $"SIMD: {SimdTierName} ({MaxSimdWidth * 8}-bit registers)";
    }
}
