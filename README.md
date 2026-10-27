# MediaKernel

A media engine for .NET written entirely in C#. No native binaries, no `ffmpeg.dll`, no
`libvlc`, nothing to ship alongside your application and nothing to go wrong per platform.
It opens a file, demuxes it, decodes it and hands you frames and samples.

It is a **translation of FFmpeg**, not a wrapper around it and not a reimplementation from the
specifications. The decoders were written by reading FFmpeg's C and writing the equivalent C#,
keeping FFmpeg's structures and control flow, so that the output can be compared against
FFmpeg's and be expected to match exactly.

> **Alpha.** The public API is still moving and the version numbers say so. It is used in
> anger by the author's own applications, which is why it exists, but pin an exact version.

## Install

```
dotnet add package MediaKernel.Core
dotnet add package MediaKernel.Codecs
dotnet add package MediaKernel.Playback
```

- **MediaKernel.Core** — demuxers, decoders, DSP, resampling. This is the engine.
- **MediaKernel.Codecs** — picks a decoder for a track. Reference it unless you are wiring
  decoders up yourself.
- **MediaKernel.Playback** — the part between a decoder and a window: the clock, reordering,
  dropping, audio output, seeking.

Targets `net10.0`. AOT- and trim-compatible.

## Decode a file

```csharp
using MediaKernel.Core;

using var pipeline = DecodePipeline.Open("episode.mkv");
var run = pipeline.RunAsync(CancellationToken.None);

while (pipeline.VideoFrames.TryRead(out var frame))
{
    // frame.Width, frame.Height, planar YUV in frame's buffers
    frame.Dispose();
}
```

## Play one

`MediaPlayer` is the part every application would otherwise write for itself. It runs the
decode and the audio device on their own threads, keeps the picture in step with the sound,
puts frames back in display order and drops what is too late to be worth showing. Your UI
thread does nothing but draw.

```csharp
using MediaKernel.Playback;

using var player = MediaPlayer.Open("episode.mkv");
player.SetDisplaySize(1280, 720);   // frames are scaled before handover, not after

// from your render loop, on whatever thread draws:
player.Tick();
player.TryTakePicture((pixels, width, height, pts) =>
{
    // pixels is RGBA8 over the player's own buffer, valid for this call only.
    // Copy it into your texture here.
});

player.SeekTo(TimeSpan.FromMinutes(10));
player.Volume = 0.5f;
```

Open it with `new MediaPlayerOptions { Silent = true }` to decode and present with no audio
device at all — which is also what a machine with no output gets, rather than a refusal.

## What it reads

**Containers** — MP4/MOV, Matroska/WebM, AVI, MPEG program and transport streams, FLV, Ogg,
ASF/WMV, WAV, AIFF, CAF, FLAC, and raw elementary streams.

**Video** — H.264, HEVC, VP8, VP9, MPEG-1, MPEG-2, MPEG-4 Part 2, VC-1/WMV3, MJPEG, Cinepak
and several older formats.

**Audio** — AAC (including HE-AAC and USAC), MP3, AC-3 and E-AC-3, DTS, FLAC, Opus, Vorbis,
WMA, ALAC, TrueHD/MLP, Speex, G.711, G.726, GSM, ADPCM in its many variants, and PCM.

**Encoders** — MP3 and FLAC, byte-exact against their reference encoders, plus WAV.

Correctness is held against FFmpeg: decoded output is compared frame by frame with what
`ffmpeg` produces from the same input, and a codec is only called done where that comparison
passes.

## Why

The author writes applications that show and play media. The usual answer is to ship LibVLC
or an FFmpeg build, which means native binaries per platform, a slow cold start, and a large
dependency to debug through. MediaKernel is the other answer: managed code you can step into,
that AOT-compiles into your binary and starts immediately.

## Licence

**LGPL-2.1-or-later.** MediaKernel is a translation of FFmpeg's C, FFmpeg is LGPL-2.1-or-later,
and a translation is a derivative work — so this is not a choice so much as an inheritance, and
the right one. You can use it in a closed-source application; see [NOTICE](NOTICE) for what
that asks of you and for who else's work is in here.

## Contributing

Issues and pull requests are welcome. One rule above the others: **anything translated from
another project must say so, in the file, naming the file it came from.** A missing or wrong
attribution is a more serious bug than a decoding fault.
