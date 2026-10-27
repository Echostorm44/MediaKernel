// Ported from FFmpeg's libavcodec/mjpeg.h - the marker bytes JPEG's segment structure is made
// of, and the predictors lossless JPEG uses.
//
// The whole enum is translated, not the subset this decoder handles. A marker it does not
// handle still has to be recognised to be skipped correctly, and a marker with no name is a
// number in a switch that nobody can check against the standard.

namespace MediaKernel.Core.Codecs.Mjpeg;

/// <summary><c>enum JpegMarker</c> (<c>mjpeg.h</c>) - the second byte of a JPEG marker.</summary>
/// <remarks>
/// A marker is <c>0xFF</c> followed by one of these. <c>0xFF 0x00</c> is not a marker: it is
/// how a literal <c>0xFF</c> is escaped inside entropy-coded data, which is what
/// <c>ff_mjpeg_unescape_sos</c> undoes. The range 0x02 to 0xBF is reserved.
/// </remarks>
public static class JpegMarker
{
    // --- start of frame ---------------------------------------------------------------------

    /// <summary><c>SOF0</c> - baseline.</summary>
    public const int Sof0 = 0xC0;

    /// <summary><c>SOF1</c> - extended sequential, Huffman.</summary>
    public const int Sof1 = 0xC1;

    /// <summary><c>SOF2</c> - progressive, Huffman.</summary>
    public const int Sof2 = 0xC2;

    /// <summary><c>SOF3</c> - lossless, Huffman.</summary>
    public const int Sof3 = 0xC3;

    /// <summary><c>SOF5</c> - differential sequential, Huffman.</summary>
    public const int Sof5 = 0xC5;

    /// <summary><c>SOF6</c> - differential progressive, Huffman.</summary>
    public const int Sof6 = 0xC6;

    /// <summary><c>SOF7</c> - differential lossless, Huffman.</summary>
    public const int Sof7 = 0xC7;

    /// <summary><c>JPG</c> - reserved for JPEG extension.</summary>
    public const int Jpg = 0xC8;

    /// <summary><c>SOF9</c> - extended sequential, arithmetic.</summary>
    public const int Sof9 = 0xC9;

    /// <summary><c>SOF10</c> - progressive, arithmetic.</summary>
    public const int Sof10 = 0xCA;

    /// <summary><c>SOF11</c> - lossless, arithmetic.</summary>
    public const int Sof11 = 0xCB;

    /// <summary><c>SOF13</c> - differential sequential, arithmetic.</summary>
    public const int Sof13 = 0xCD;

    /// <summary><c>SOF14</c> - differential progressive, arithmetic.</summary>
    public const int Sof14 = 0xCE;

    /// <summary><c>SOF15</c> - differential lossless, arithmetic.</summary>
    public const int Sof15 = 0xCF;

    /// <summary><c>DHT</c> - define Huffman tables.</summary>
    public const int Dht = 0xC4;

    /// <summary><c>DAC</c> - define arithmetic-coding conditioning.</summary>
    public const int Dac = 0xCC;

    // --- restart, with a modulo 8 count ------------------------------------------------------

    /// <summary><c>RST0</c>; <c>RST0</c> to <c>RST7</c> are 0xD0 to 0xD7.</summary>
    public const int Rst0 = 0xD0;

    /// <summary><c>RST7</c>.</summary>
    public const int Rst7 = 0xD7;

    // --- the rest -----------------------------------------------------------------------------

    /// <summary><c>SOI</c> - start of image.</summary>
    public const int Soi = 0xD8;

    /// <summary><c>EOI</c> - end of image.</summary>
    public const int Eoi = 0xD9;

    /// <summary><c>SOS</c> - start of scan.</summary>
    public const int Sos = 0xDA;

    /// <summary><c>DQT</c> - define quantisation tables.</summary>
    public const int Dqt = 0xDB;

    /// <summary><c>DNL</c> - define number of lines.</summary>
    public const int Dnl = 0xDC;

    /// <summary><c>DRI</c> - define restart interval.</summary>
    public const int Dri = 0xDD;

    /// <summary><c>DHP</c> - define hierarchical progression.</summary>
    public const int Dhp = 0xDE;

    /// <summary><c>EXP</c> - expand reference components.</summary>
    public const int Exp = 0xDF;

    /// <summary><c>APP0</c>; <c>APP0</c> to <c>APP15</c> are 0xE0 to 0xEF.</summary>
    public const int App0 = 0xE0;

    /// <summary><c>APP15</c>.</summary>
    public const int App15 = 0xEF;

    /// <summary><c>JPG0</c>; <c>JPG0</c> to <c>JPG13</c> fill 0xF0 to 0xFD, less SOF55 and LSE.</summary>
    public const int Jpg0 = 0xF0;

    /// <summary><c>JPG6</c>.</summary>
    public const int Jpg6 = 0xF6;

    /// <summary><c>SOF55</c> - JPEG-LS.</summary>
    public const int Sof55 = 0xF7;

    /// <summary><c>LSE</c> - JPEG-LS extension parameters.</summary>
    public const int Lse = 0xF8;

    /// <summary><c>JPG9</c>.</summary>
    public const int Jpg9 = 0xF9;

    /// <summary><c>JPG13</c>.</summary>
    public const int Jpg13 = 0xFD;

    /// <summary><c>COM</c> - comment.</summary>
    public const int Com = 0xFE;

    /// <summary><c>TEM</c> - temporary private use for arithmetic coding.</summary>
    public const int Tem = 0x01;
}

/// <summary><c>AVFieldOrder</c>, as far as Motion JPEG reads it.</summary>
public static class MjpegFieldOrder
{
    /// <summary><c>AV_FIELD_UNKNOWN</c>.</summary>
    public const int Unknown = 0;

    /// <summary><c>AV_FIELD_PROGRESSIVE</c>.</summary>
    public const int Progressive = 1;

    /// <summary><c>AV_FIELD_TT</c> - top coded first, top displayed first.</summary>
    public const int TopTop = 2;

    /// <summary><c>AV_FIELD_BB</c> - bottom coded first, bottom displayed first.</summary>
    public const int BottomBottom = 3;

    /// <summary><c>AV_FIELD_TB</c>.</summary>
    public const int TopBottom = 4;

    /// <summary><c>AV_FIELD_BT</c>.</summary>
    public const int BottomTop = 5;
}
