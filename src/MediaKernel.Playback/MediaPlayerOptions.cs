using MediaKernel.Core.Audio;

namespace MediaKernel.Playback;

/// <summary>
/// What a <see cref="MediaPlayer"/> is opened with. Every value has a default that plays a file
/// correctly; nothing here has to be set.
/// </summary>
public sealed class MediaPlayerOptions
{
    /// <summary>Playback volume, 0 to 1.</summary>
    public float Volume { get; init; } = 1.0f;

    /// <summary>
    /// Decode and show the pictures, but play no sound and open no device.
    /// </summary>
    /// <remarks>
    /// The clock is then driven by the first picture rather than by the sound, which is what a
    /// test harness wants and what a machine with no audio device gets. The audio track is still
    /// decoded and discarded: the channels are bounded, so a track nobody reads stops the decode
    /// loop as soon as it fills.
    /// </remarks>
    public bool Silent { get; init; }

    /// <summary>
    /// The device to play through. Null uses the platform's, which today means WASAPI on
    /// Windows and silence elsewhere.
    /// </summary>
    /// <remarks>
    /// A caller that supplies one keeps it: the player does not dispose a device it did not
    /// open, because a host that mixes several players through one device would lose it on the
    /// first one closed.
    /// </remarks>
    public IAudioOutput? AudioOutput { get; init; }

    /// <summary>
    /// How many decoded pictures are held back so they can be shown in timestamp order.
    /// </summary>
    /// <remarks>
    /// A player should not need this: a decoder hands out pictures in the order they are
    /// displayed. It is here because one did not - MPEG-2 through this project's own pipeline
    /// handed out decode order, each I or P picture three slots before its place - and because a
    /// buffer is the only thing that can put that right from outside. Four is one more than the
    /// reordering depth of a stream with two B pictures between references, which is the common
    /// case; raising it costs memory and latency, and lowering it to 1 turns the reordering off.
    /// <para>
    /// It does not hide the fault: <see cref="MediaPlayer.FramesOutOfOrder"/> counts every
    /// picture that arrived out of order, so a host can report it.
    /// </para>
    /// </remarks>
    public int ReorderDepth { get; init; } = 4;
}
