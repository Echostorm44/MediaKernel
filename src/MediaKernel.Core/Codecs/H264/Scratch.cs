// Licensed to the MediaKernel contributors. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.H264;

/// <summary>
/// Fixed-size stack scratch buffers that do not defeat tiered compilation.
/// </summary>
/// <remarks>
/// <para>
/// A method containing <c>stackalloc</c> emits <c>localloc</c>, and the runtime excludes any such
/// method from tiered compilation: it is compiled once with full optimisation, never reaches Tier1,
/// and so never gets Dynamic PGO or the hot/cold block ordering that comes with it. One
/// <c>stackalloc</c> line is enough to cost a method all of it, and twenty-two methods in this
/// decoder - about 44% of decode-thread time - were in that state.
/// </para>
/// <para>
/// An inline array is a struct local, so it gives the same stack storage with no <c>localloc</c>.
/// Declared <c>= default</c> it is also zeroed per call exactly as <c>stackalloc</c> was, which is
/// what makes the substitution behaviour-preserving rather than merely equivalent-looking:
/// </para>
/// <code>
/// Scratch.Of16&lt;int&gt; coefficientStorage = default;
/// Span&lt;int&gt; coefficients = coefficientStorage;
/// </code>
/// <para>
/// Sizes here are the ones the H.264 decoder actually asks for. Add another only when a call site
/// needs it; an unused one costs nothing but a name.
/// </para>
/// </remarks>
internal static class Scratch
{
    [InlineArray(2)] internal struct Of2<T> { private T _element0; }

    [InlineArray(4)] internal struct Of4<T> { private T _element0; }

    [InlineArray(8)] internal struct Of8<T> { private T _element0; }

    [InlineArray(9)] internal struct Of9<T> { private T _element0; }

    [InlineArray(16)] internal struct Of16<T> { private T _element0; }

    [InlineArray(17)] internal struct Of17<T> { private T _element0; }

    [InlineArray(25)] internal struct Of25<T> { private T _element0; }

    [InlineArray(64)] internal struct Of64<T> { private T _element0; }

    /// <summary>Chroma prediction: 8x8 for 4:2:0, 8x16 for 4:2:2.</summary>
    [InlineArray(128)] internal struct Of128<T> { private T _element0; }

    [InlineArray(256)] internal struct Of256<T> { private T _element0; }

    /// <summary>A 16x16 partition's two-pass interpolation window: 16 * (16 + 5).</summary>
    [InlineArray(336)] internal struct Of336<T> { private T _element0; }
}
