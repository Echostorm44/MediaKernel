# MediaKernel

A media engine for .NET written entirely in C# — a translation of FFmpeg's C into managed
code. No native binaries, nothing to ship per platform, nothing to P/Invoke into. It opens a
file, demuxes it, decodes it, and hands you frames and samples.

Three packages: **MediaKernel.Core** (demuxers, decoders, DSP, resampling),
**MediaKernel.Codecs** (picks a decoder for a track), **MediaKernel.Playback** (clock,
reordering, dropping, audio output, seeking). Targets `net10.0`, AOT- and trim-compatible.

---

**This is early alpha and not ready to be used or contributed to.** The public API is still
moving, parts of the engine are not published yet, and the project is not accepting issues or
pull requests. It is here so that the author's own applications can build against it. Pin an
exact version, or wait.

---

## Licence

**LGPL-2.1-or-later.** MediaKernel is a translation of FFmpeg's C, FFmpeg is
LGPL-2.1-or-later, and a translation is a derivative work — so this is inherited rather than
chosen, and it is the right outcome. See [NOTICE](NOTICE) for whose work this is built on.
