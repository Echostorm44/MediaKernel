// aacpsdata.c - the Parametric Stereo Huffman codes and the two band-mapping tables.
//
// Generated from ffmpeg-master/libavcodec/aacpsdata.c by parsing its text, not by retyping it.
// Each code is a canonical Huffman code given as (symbol, length) pairs in FFmpeg's order, which
// is the form SbrHuffmanTable already takes - PS and SBR use the same construction, so the SBR
// table class is reused rather than duplicated.

using System;
using MediaKernel.Core.Codecs.AAC.Sbr;

namespace MediaKernel.Core.Codecs.AAC.Ps;

/// <summary>The ten PS Huffman codes and the sub-subband to parameter-band maps.</summary>
internal static class PsData
{
    /// <summary>IID, frequency delta, fine quantisation: 61 codes, symbol bias -30.</summary>
    public static readonly SbrHuffmanTable IidDf1 = new(
        offset: -30,
        symbols: [
            28, 32, 29, 31, 27, 33, 26, 34, 25, 35, 24, 36, 37, 40, 19, 41, 22, 38, 9, 51, 11,
            49, 13, 47, 16, 18, 42, 44, 12, 48, 4, 5, 2, 3, 15, 21, 39, 45, 8, 52, 6, 7, 55,
            56, 53, 54, 17, 43, 59, 60, 57, 58, 0, 1, 10, 50, 14, 46, 20, 23, 30],
        lengths: [
            4, 4, 3, 3, 5, 5, 6, 6, 7, 7, 8, 8, 9, 11, 12, 12, 10, 10, 17, 17, 17, 17, 16, 16,
            14, 13, 13, 14, 17, 17, 18, 18, 18, 18, 15, 11, 11, 15, 18, 18, 18, 18, 18, 18, 18,
            18, 14, 14, 18, 18, 18, 18, 18, 18, 18, 18, 16, 16, 12, 10, 1]);
    /// <summary>IID, time delta, fine quantisation: 61 codes, symbol bias -30.</summary>
    public static readonly SbrHuffmanTable IidDt1 = new(
        offset: -30,
        symbols: [
            31, 26, 34, 27, 33, 35, 24, 36, 39, 41, 9, 10, 48, 49, 17, 23, 37, 43, 11, 12, 4,
            56, 2, 3, 59, 60, 57, 58, 0, 1, 5, 55, 6, 54, 13, 15, 20, 40, 22, 38, 45, 47, 7,
            53, 18, 42, 16, 44, 8, 52, 14, 46, 50, 51, 19, 21, 25, 28, 32, 29, 30],
        lengths: [
            2, 7, 7, 6, 6, 8, 9, 9, 11, 12, 15, 15, 15, 15, 13, 10, 10, 13, 15, 15, 16, 16, 16,
            16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 15, 14, 12, 12, 11, 11, 14, 15, 16, 16,
            13, 13, 14, 14, 16, 16, 15, 15, 16, 16, 13, 12, 9, 5, 5, 3, 1]);
    /// <summary>IID, frequency delta, default quantisation: 29 codes, symbol bias -14.</summary>
    public static readonly SbrHuffmanTable IidDf0 = new(
        offset: -14,
        symbols: [
            14, 15, 13, 16, 12, 17, 11, 10, 18, 19, 9, 20, 8, 7, 21, 22, 6, 23, 24, 5, 25, 4,
            3, 0, 1, 2, 26, 27, 28],
        lengths: [
            1, 3, 3, 4, 4, 5, 5, 6, 6, 6, 7, 8, 9, 10, 11, 13, 13, 14, 14, 15, 15, 16, 17, 17,
            17, 17, 17, 18, 18]);
    /// <summary>IID, time delta, default quantisation: 29 codes, symbol bias -14.</summary>
    public static readonly SbrHuffmanTable IidDt0 = new(
        offset: -14,
        symbols: [
            14, 13, 15, 12, 16, 11, 17, 10, 18, 9, 19, 8, 20, 21, 7, 22, 6, 23, 0, 1, 2, 3, 4,
            5, 24, 25, 26, 27, 28],
        lengths: [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17, 17, 19, 19, 19, 19, 20, 20,
            20, 20, 20, 20, 20, 20]);
    /// <summary>ICC, frequency delta: 15 codes, symbol bias -7.</summary>
    public static readonly SbrHuffmanTable IccDf = new(
        offset: -7,
        symbols: [
            7, 8, 6, 9, 5, 10, 4, 11, 12, 3, 13, 2, 14, 1, 0],
        lengths: [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 14]);
    /// <summary>ICC, time delta: 15 codes, symbol bias -7.</summary>
    public static readonly SbrHuffmanTable IccDt = new(
        offset: -7,
        symbols: [
            7, 8, 6, 9, 5, 10, 4, 11, 3, 12, 2, 13, 1, 0, 14],
        lengths: [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 14]);
    /// <summary>IPD, frequency delta: 8 codes, symbol bias 0.</summary>
    public static readonly SbrHuffmanTable IpdDf = new(
        offset: 0,
        symbols: [
            1, 4, 5, 3, 6, 2, 7, 0],
        lengths: [
            3, 4, 4, 4, 4, 4, 4, 1]);
    /// <summary>IPD, time delta: 8 codes, symbol bias 0.</summary>
    public static readonly SbrHuffmanTable IpdDt = new(
        offset: 0,
        symbols: [
            5, 4, 3, 2, 6, 1, 7, 0],
        lengths: [
            4, 5, 5, 4, 4, 3, 3, 1]);
    /// <summary>OPD, frequency delta: 8 codes, symbol bias 0.</summary>
    public static readonly SbrHuffmanTable OpdDf = new(
        offset: 0,
        symbols: [
            7, 1, 3, 6, 2, 5, 4, 0],
        lengths: [
            3, 3, 4, 4, 4, 5, 5, 1]);
    /// <summary>OPD, time delta: 8 codes, symbol bias 0.</summary>
    public static readonly SbrHuffmanTable OpdDt = new(
        offset: 0,
        symbols: [
            5, 2, 6, 4, 3, 1, 7, 0],
        lengths: [
            4, 4, 4, 5, 5, 3, 3, 1]);

    /// <summary>The ten codes in <c>vlc_ps</c> order, so the parser can index them as the C does.</summary>
    public static readonly SbrHuffmanTable[] Tables =
        [IidDf1, IidDt1, IidDf0, IidDt0, IccDf, IccDt, IpdDf, IpdDt, OpdDf, OpdDt];

    /// <summary>Table 8.48: sub-subband k to parameter band i, 20-band mode.</summary>
    public static ReadOnlySpan<sbyte> KToI20 =>
    [
        1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 14, 15, 15, 15, 16, 16, 16, 16,
        17, 17, 17, 17, 17, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 19, 19, 19,
        19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19,
        19, 19
    ];

    /// <summary>Table 8.49: sub-subband k to parameter band i, 34-band mode.</summary>
    public static ReadOnlySpan<sbyte> KToI34 =>
    [
        0, 1, 2, 3, 4, 5, 6, 6, 7, 2, 1, 0, 10, 10, 4, 5, 6, 7, 8, 9, 10, 11, 12, 9, 14, 11,
        12, 13, 14, 15, 16, 13, 16, 17, 18, 19, 20, 21, 22, 22, 23, 23, 24, 24, 25, 25, 26, 26,
        27, 27, 27, 28, 28, 28, 29, 29, 29, 30, 30, 30, 31, 31, 31, 31, 32, 32, 32, 32, 33, 33,
        33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33, 33
    ];
}
