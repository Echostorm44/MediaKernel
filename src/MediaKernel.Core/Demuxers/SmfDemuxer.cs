// MediaKernel - Standard MIDI File (SMF) Demuxer
// Parses .mid, .kar, .rmi files containing MIDI data
// Reference: VLC's smf.c

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaKernel.Core.Demuxers;

/// <summary>
/// SMF file type.
/// </summary>
public enum SmfFileType : ushort
{
    /// <summary>Type 0: Single track containing all MIDI data.</summary>
    SingleTrack = 0,
    
    /// <summary>Type 1: Multiple tracks to be played simultaneously.</summary>
    MultiTrack = 1,
    
    /// <summary>Type 2: Multiple independent tracks (patterns).</summary>
    Pattern = 2
}

/// <summary>
/// MIDI event type classification.
/// </summary>
public enum MidiEventCategory
{
    /// <summary>Note on/off, key pressure.</summary>
    Note,
    
    /// <summary>Control change.</summary>
    Control,
    
    /// <summary>Program change.</summary>
    ProgramChange,
    
    /// <summary>Channel pressure (aftertouch).</summary>
    ChannelPressure,
    
    /// <summary>Pitch bend.</summary>
    PitchBend,
    
    /// <summary>System exclusive.</summary>
    SystemExclusive,
    
    /// <summary>Meta event.</summary>
    Meta
}

/// <summary>
/// MIDI meta event types.
/// </summary>
public enum MidiMetaEventType : byte
{
    SequenceNumber = 0x00,
    Text = 0x01,
    Copyright = 0x02,
    TrackName = 0x03,
    InstrumentName = 0x04,
    Lyric = 0x05,
    Marker = 0x06,
    CuePoint = 0x07,
    ProgramName = 0x08,
    DeviceName = 0x09,
    ChannelPrefix = 0x20,
    MidiPort = 0x21,
    EndOfTrack = 0x2F,
    Tempo = 0x51,
    SmpteOffset = 0x54,
    TimeSignature = 0x58,
    KeySignature = 0x59,
    SequencerSpecific = 0x7F
}

/// <summary>
/// Represents a single MIDI event.
/// </summary>
public readonly struct MidiEvent
{
    /// <summary>Absolute time in ticks (pulses) from start.</summary>
    public long AbsoluteTicks { get; init; }
    
    /// <summary>Time in microseconds from start.</summary>
    public long TimeMicroseconds { get; init; }
    
    /// <summary>Event category.</summary>
    public MidiEventCategory Category { get; init; }
    
    /// <summary>Status byte (event type and channel).</summary>
    public byte Status { get; init; }
    
    /// <summary>Channel (0-15) for channel messages.</summary>
    public int Channel => Status & 0x0F;
    
    /// <summary>First data byte (note number, controller number, etc.).</summary>
    public byte Data1 { get; init; }
    
    /// <summary>Second data byte (velocity, value, etc.).</summary>
    public byte Data2 { get; init; }
    
    /// <summary>Extended data for SysEx or Meta events.</summary>
    public byte[]? ExtendedData { get; init; }
    
    /// <summary>Meta event type (if Category is Meta).</summary>
    public MidiMetaEventType MetaType { get; init; }
    
    /// <summary>Track index this event belongs to.</summary>
    public int TrackIndex { get; init; }
}

/// <summary>
/// Track state during parsing.
/// </summary>
internal sealed class SmfTrackState
{
    public long StartOffset { get; set; }
    public int Length { get; set; }
    public int CurrentOffset { get; set; }
    public long NextEventTicks { get; set; }
    public byte RunningStatus { get; set; } = 0xF6; // Tuning calibration (no data bytes)
    public bool IsComplete => CurrentOffset >= Length;
}

/// <summary>
/// Time signature information from MIDI file.
/// </summary>
public readonly struct MidiTimeSignature
{
    public int Numerator { get; init; }
    public int DenominatorPower { get; init; }
    public int Denominator => 1 << DenominatorPower;
    public int MidiClocksPerClick { get; init; }
    public int ThirtySecondNotesPerBeat { get; init; }
    
    public static MidiTimeSignature Default => new()
    {
        Numerator = 4,
        DenominatorPower = 2, // 4 = 2^2
        MidiClocksPerClick = 24,
        ThirtySecondNotesPerBeat = 8
    };
}

/// <summary>
/// Key signature information from MIDI file.
/// </summary>
public readonly struct MidiKeySignature
{
    /// <summary>Number of sharps (positive) or flats (negative).</summary>
    public int SharpsFlats { get; init; }
    
    /// <summary>True for minor key, false for major.</summary>
    public bool IsMinor { get; init; }
    
    public string KeyName
    {
        get
        {
            string[] majorKeys = { "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#" };
            string[] minorKeys = { "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#", "G#", "D#", "A#" };
            int index = SharpsFlats + 7;
            if (index < 0 || index >= 15) return "Unknown";
            return (IsMinor ? minorKeys[index] : majorKeys[index]) + (IsMinor ? "m" : "");
        }
    }
}

/// <summary>
/// SMF header information.
/// </summary>
public readonly struct SmfHeader
{
    /// <summary>SMF file type (0, 1, or 2).</summary>
    public SmfFileType FileType { get; init; }
    
    /// <summary>Number of tracks.</summary>
    public int TrackCount { get; init; }
    
    /// <summary>Pulses per quarter note (PPQN) for metric timing.</summary>
    public int PulsesPerQuarterNote { get; init; }
    
    /// <summary>True if timing is SMPTE-based.</summary>
    public bool UseSmpte { get; init; }
    
    /// <summary>SMPTE frames per second (if UseSmpte is true).</summary>
    public int SmpteFramesPerSecond { get; init; }
    
    /// <summary>SMPTE ticks per frame (if UseSmpte is true).</summary>
    public int SmpteTicksPerFrame { get; init; }
}

/// <summary>
/// Parsed SMF file with all tracks and events.
/// </summary>
public sealed class SmfFile
{
    /// <summary>File header.</summary>
    public SmfHeader Header { get; init; }
    
    /// <summary>All MIDI events sorted by time.</summary>
    public IReadOnlyList<MidiEvent> Events { get; init; } = [];
    
    /// <summary>Track names indexed by track number.</summary>
    public IReadOnlyDictionary<int, string> TrackNames { get; init; } = new Dictionary<int, string>();
    
    /// <summary>Copyright notice.</summary>
    public string? Copyright { get; init; }
    
    /// <summary>Duration in microseconds.</summary>
    public long DurationMicroseconds { get; init; }
    
    /// <summary>Duration as TimeSpan.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(DurationMicroseconds * 10);
    
    /// <summary>Tempo events (tempo changes throughout the file).</summary>
    public IReadOnlyList<(long TickPosition, int MicrosecondsPerBeat)> TempoEvents { get; init; } = [];
    
    /// <summary>Time signature events.</summary>
    public IReadOnlyList<(long TickPosition, MidiTimeSignature Signature)> TimeSignatureEvents { get; init; } = [];
    
    /// <summary>Key signature events.</summary>
    public IReadOnlyList<(long TickPosition, MidiKeySignature Signature)> KeySignatureEvents { get; init; } = [];
    
    /// <summary>Text/lyric events.</summary>
    public IReadOnlyList<(long TickPosition, string Text)> TextEvents { get; init; } = [];
    
    /// <summary>Marker events.</summary>
    public IReadOnlyList<(long TickPosition, string Marker)> MarkerEvents { get; init; } = [];
}

/// <summary>
/// Demuxer for Standard MIDI Files (.mid, .kar, .rmi).
/// </summary>
public sealed class SmfDemuxer : IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private bool disposed;
    
    // Default tempo: 120 BPM = 500,000 microseconds per beat
    private const int DefaultMicrosecondsPerBeat = 500_000;
    private const int MinTempoBeatsPerMinute = 20;
    private const int MaxTempoBeatsPerMinute = 250;
    
    public SmfDemuxer(Stream stream, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.leaveOpen = leaveOpen;
    }
    
    /// <summary>
    /// Attempts to parse the SMF header.
    /// </summary>
    public bool TryParseHeader(out SmfHeader header)
    {
        header = default;
        
        Span<byte> headerBytes = stackalloc byte[14];
        if (stream.Read(headerBytes) < 14)
            return false;
        
        // Check for RIFF MIDI header
        if (headerBytes[..4].SequenceEqual("RIFF"u8))
        {
            if (!TrySkipRiffMidiHeader())
                return false;
            
            if (stream.Read(headerBytes) < 14)
                return false;
        }
        
        // Check MThd magic
        if (!headerBytes[..4].SequenceEqual("MThd"u8))
            return false;
        
        // Header length must be 6
        int headerLength = BinaryPrimitives.ReadInt32BigEndian(headerBytes[4..]);
        if (headerLength != 6)
            return false;
        
        ushort fileType = BinaryPrimitives.ReadUInt16BigEndian(headerBytes[8..]);
        ushort trackCount = BinaryPrimitives.ReadUInt16BigEndian(headerBytes[10..]);
        ushort division = BinaryPrimitives.ReadUInt16BigEndian(headerBytes[12..]);
        
        // Validate file type
        if (fileType > 2)
            return false;
        
        // Type 0 must have exactly 1 track
        if (fileType == 0 && trackCount != 1)
            return false;
        
        header = new SmfHeader
        {
            FileType = (SmfFileType)fileType,
            TrackCount = trackCount,
            UseSmpte = (division & 0x8000) != 0,
            PulsesPerQuarterNote = (division & 0x8000) == 0 ? division : 0,
            SmpteFramesPerSecond = (division & 0x8000) != 0 ? -(sbyte)(division >> 8) : 0,
            SmpteTicksPerFrame = (division & 0x8000) != 0 ? division & 0xFF : 0
        };
        
        return true;
    }
    
    /// <summary>
    /// Parses the entire SMF file.
    /// </summary>
    public SmfFile? Parse()
    {
        stream.Seek(0, SeekOrigin.Begin);
        
        if (!TryParseHeader(out var header))
            return null;
        
        // We don't support SMPTE timing currently
        if (header.UseSmpte)
            return null;
        
        var trackStates = new SmfTrackState[header.TrackCount];
        var allEvents = new List<MidiEvent>(1024);
        var trackNames = new Dictionary<int, string>();
        var tempoEvents = new List<(long, int)>();
        var timeSignatureEvents = new List<(long, MidiTimeSignature)>();
        var keySignatureEvents = new List<(long, MidiKeySignature)>();
        var textEvents = new List<(long, string)>();
        var markerEvents = new List<(long, string)>();
        string? copyright = null;
        
        // Initialize default tempo
        tempoEvents.Add((0, DefaultMicrosecondsPerBeat));
        
        // Read all track chunks
        for (int trackIndex = 0; trackIndex < header.TrackCount; trackIndex++)
        {
            var trackState = ReadTrackChunk(trackIndex);
            if (trackState == null)
                return null;
            
            trackStates[trackIndex] = trackState;
        }
        
        // Parse all events from all tracks
        for (int trackIndex = 0; trackIndex < header.TrackCount; trackIndex++)
        {
            var trackState = trackStates[trackIndex];
            stream.Seek(trackState.StartOffset, SeekOrigin.Begin);
            
            long currentTicks = 0;
            
            while (!trackState.IsComplete)
            {
                int deltaTime = ReadVariableLengthValue();
                if (deltaTime < 0)
                    break;
                
                currentTicks += deltaTime;
                trackState.CurrentOffset = (int)(stream.Position - trackState.StartOffset);
                
                var eventResult = ParseEvent(trackState, trackIndex, currentTicks);
                if (eventResult == null)
                    break;
                
                var midiEvent = eventResult.Value;
                
                // Handle meta events for metadata
                if (midiEvent.Category == MidiEventCategory.Meta)
                {
                    switch (midiEvent.MetaType)
                    {
                        case MidiMetaEventType.TrackName when midiEvent.ExtendedData != null:
                            trackNames[trackIndex] = Encoding.UTF8.GetString(midiEvent.ExtendedData);
                            break;
                            
                        case MidiMetaEventType.Copyright when midiEvent.ExtendedData != null:
                            copyright = Encoding.UTF8.GetString(midiEvent.ExtendedData);
                            break;
                            
                        case MidiMetaEventType.Tempo when midiEvent.ExtendedData?.Length == 3:
                            int microsecondsPerBeat = (midiEvent.ExtendedData[0] << 16) |
                                                      (midiEvent.ExtendedData[1] << 8) |
                                                       midiEvent.ExtendedData[2];
                            // Clamp to valid tempo range
                            int bpm = 60_000_000 / Math.Max(microsecondsPerBeat, 1);
                            bpm = Math.Clamp(bpm, MinTempoBeatsPerMinute, MaxTempoBeatsPerMinute);
                            microsecondsPerBeat = 60_000_000 / bpm;
                            tempoEvents.Add((currentTicks, microsecondsPerBeat));
                            break;
                            
                        case MidiMetaEventType.TimeSignature when midiEvent.ExtendedData?.Length == 4:
                            timeSignatureEvents.Add((currentTicks, new MidiTimeSignature
                            {
                                Numerator = midiEvent.ExtendedData[0],
                                DenominatorPower = midiEvent.ExtendedData[1],
                                MidiClocksPerClick = midiEvent.ExtendedData[2],
                                ThirtySecondNotesPerBeat = midiEvent.ExtendedData[3]
                            }));
                            break;
                            
                        case MidiMetaEventType.KeySignature when midiEvent.ExtendedData?.Length == 2:
                            keySignatureEvents.Add((currentTicks, new MidiKeySignature
                            {
                                SharpsFlats = (sbyte)midiEvent.ExtendedData[0],
                                IsMinor = midiEvent.ExtendedData[1] != 0
                            }));
                            break;
                            
                        case MidiMetaEventType.Text or MidiMetaEventType.Lyric when midiEvent.ExtendedData != null:
                            textEvents.Add((currentTicks, Encoding.UTF8.GetString(midiEvent.ExtendedData)));
                            break;
                            
                        case MidiMetaEventType.Marker when midiEvent.ExtendedData != null:
                            markerEvents.Add((currentTicks, Encoding.UTF8.GetString(midiEvent.ExtendedData)));
                            break;
                            
                        case MidiMetaEventType.EndOfTrack:
                            trackState.CurrentOffset = trackState.Length; // Mark complete
                            break;
                    }
                }
                
                allEvents.Add(midiEvent);
                trackState.CurrentOffset = (int)(stream.Position - trackState.StartOffset);
            }
        }
        
        // Sort events by time
        allEvents.Sort((a, b) => a.AbsoluteTicks.CompareTo(b.AbsoluteTicks));
        
        // Calculate times in microseconds using tempo map
        var eventsWithTimes = ConvertTicksToMicroseconds(allEvents, tempoEvents, header.PulsesPerQuarterNote);
        
        long durationMicroseconds = eventsWithTimes.Count > 0 
            ? eventsWithTimes[^1].TimeMicroseconds 
            : 0;
        
        return new SmfFile
        {
            Header = header,
            Events = eventsWithTimes,
            TrackNames = trackNames,
            Copyright = copyright,
            DurationMicroseconds = durationMicroseconds,
            TempoEvents = tempoEvents,
            TimeSignatureEvents = timeSignatureEvents,
            KeySignatureEvents = keySignatureEvents,
            TextEvents = textEvents,
            MarkerEvents = markerEvents
        };
    }
    
    private List<MidiEvent> ConvertTicksToMicroseconds(
        List<MidiEvent> events, 
        List<(long TickPosition, int MicrosecondsPerBeat)> tempoEvents,
        int ppqn)
    {
        var result = new List<MidiEvent>(events.Count);
        
        int tempoIndex = 0;
        long lastTick = 0;
        long currentMicroseconds = 0;
        int currentMicrosecondsPerBeat = DefaultMicrosecondsPerBeat;
        
        foreach (var evt in events)
        {
            // Update tempo if we've passed a tempo change
            while (tempoIndex < tempoEvents.Count - 1 && 
                   tempoEvents[tempoIndex + 1].TickPosition <= evt.AbsoluteTicks)
            {
                // Accumulate time up to tempo change
                long ticksToChange = tempoEvents[tempoIndex + 1].TickPosition - lastTick;
                currentMicroseconds += TicksToMicroseconds(ticksToChange, currentMicrosecondsPerBeat, ppqn);
                lastTick = tempoEvents[tempoIndex + 1].TickPosition;
                tempoIndex++;
                currentMicrosecondsPerBeat = tempoEvents[tempoIndex].MicrosecondsPerBeat;
            }
            
            // Calculate time for this event
            long tickDelta = evt.AbsoluteTicks - lastTick;
            long eventMicroseconds = currentMicroseconds + TicksToMicroseconds(tickDelta, currentMicrosecondsPerBeat, ppqn);
            
            result.Add(new MidiEvent
            {
                AbsoluteTicks = evt.AbsoluteTicks,
                TimeMicroseconds = eventMicroseconds,
                Category = evt.Category,
                Status = evt.Status,
                Data1 = evt.Data1,
                Data2 = evt.Data2,
                ExtendedData = evt.ExtendedData,
                MetaType = evt.MetaType,
                TrackIndex = evt.TrackIndex
            });
        }
        
        return result;
    }
    
    private static long TicksToMicroseconds(long ticks, int microsecondsPerBeat, int ppqn)
    {
        return (ticks * microsecondsPerBeat) / ppqn;
    }
    
    private SmfTrackState? ReadTrackChunk(int trackIndex)
    {
        Span<byte> chunkHeader = stackalloc byte[8];
        
        // Seek past previous tracks if needed (handled by caller tracking position)
        while (true)
        {
            if (stream.Read(chunkHeader) < 8)
                return null;
            
            if (chunkHeader[..4].SequenceEqual("MTrk"u8))
                break;
            
            // Skip unknown chunk
            int chunkLength = BinaryPrimitives.ReadInt32BigEndian(chunkHeader[4..]);
            stream.Seek(chunkLength, SeekOrigin.Current);
        }
        
        int trackLength = BinaryPrimitives.ReadInt32BigEndian(chunkHeader[4..]);
        
        return new SmfTrackState
        {
            StartOffset = stream.Position,
            Length = trackLength,
            CurrentOffset = 0,
            NextEventTicks = 0,
            RunningStatus = 0xF6
        };
    }
    
    private MidiEvent? ParseEvent(SmfTrackState trackState, int trackIndex, long absoluteTicks)
    {
        int firstByte = stream.ReadByte();
        if (firstByte < 0)
            return null;
        
        byte status = (byte)((firstByte & 0x80) != 0 ? firstByte : trackState.RunningStatus);
        byte firstData = (byte)((firstByte & 0x80) != 0 ? 0 : firstByte);
        bool usedRunningStatus = (firstByte & 0x80) == 0;
        
        int dataLength;
        MidiEventCategory category;
        MidiMetaEventType metaType = 0;
        byte[]? extendedData = null;
        
        switch (status & 0xF0)
        {
            case 0x80: // Note Off
            case 0x90: // Note On
            case 0xA0: // Polyphonic Key Pressure
                category = MidiEventCategory.Note;
                dataLength = 2;
                break;
                
            case 0xB0: // Control Change
                category = MidiEventCategory.Control;
                dataLength = 2;
                break;
                
            case 0xC0: // Program Change
                category = MidiEventCategory.ProgramChange;
                dataLength = 1;
                break;
                
            case 0xD0: // Channel Pressure
                category = MidiEventCategory.ChannelPressure;
                dataLength = 1;
                break;
                
            case 0xE0: // Pitch Bend
                category = MidiEventCategory.PitchBend;
                dataLength = 2;
                break;
                
            case 0xF0: // System messages
                switch (status)
                {
                    case 0xF0: // SysEx Start
                    case 0xF7: // SysEx Continuation
                        category = MidiEventCategory.SystemExclusive;
                        int sysexLength = ReadVariableLengthValue();
                        if (sysexLength < 0)
                            return null;
                        extendedData = new byte[sysexLength + 1];
                        extendedData[0] = status;
                        if (sysexLength > 0 && stream.Read(extendedData.AsSpan(1)) < sysexLength)
                            return null;
                        dataLength = 0;
                        break;
                        
                    case 0xFF: // Meta Event
                        category = MidiEventCategory.Meta;
                        int metaTypeByte = stream.ReadByte();
                        if (metaTypeByte < 0)
                            return null;
                        metaType = (MidiMetaEventType)metaTypeByte;
                        int metaLength = ReadVariableLengthValue();
                        if (metaLength < 0)
                            return null;
                        if (metaLength > 0)
                        {
                            extendedData = new byte[metaLength];
                            if (stream.Read(extendedData) < metaLength)
                                return null;
                        }
                        dataLength = 0;
                        break;
                        
                    case 0xF1: // MIDI Time Code Quarter Frame
                    case 0xF3: // Song Select
                        category = MidiEventCategory.SystemExclusive;
                        dataLength = 1;
                        break;
                        
                    case 0xF2: // Song Position Pointer
                        category = MidiEventCategory.SystemExclusive;
                        dataLength = 2;
                        break;
                        
                    default: // F4, F5, F6, F8-FE (real-time, etc.)
                        category = MidiEventCategory.SystemExclusive;
                        dataLength = 0;
                        break;
                }
                break;
                
            default:
                return null;
        }
        
        byte data1 = 0, data2 = 0;
        
        if (dataLength >= 1)
        {
            if (usedRunningStatus)
            {
                data1 = firstData;
            }
            else
            {
                int d1 = stream.ReadByte();
                if (d1 < 0)
                    return null;
                data1 = (byte)d1;
            }
        }
        
        if (dataLength >= 2)
        {
            int d2 = stream.ReadByte();
            if (d2 < 0)
                return null;
            data2 = (byte)d2;
        }
        
        // Update running status (only for channel messages)
        if (status < 0xF8)
            trackState.RunningStatus = status;
        
        return new MidiEvent
        {
            AbsoluteTicks = absoluteTicks,
            TimeMicroseconds = 0, // Filled in later
            Category = category,
            Status = status,
            Data1 = data1,
            Data2 = data2,
            ExtendedData = extendedData,
            MetaType = metaType,
            TrackIndex = trackIndex
        };
    }
    
    private int ReadVariableLengthValue()
    {
        int value = 0;
        
        for (int i = 0; i < 4; i++)
        {
            int b = stream.ReadByte();
            if (b < 0)
                return -1;
            
            value = (value << 7) | (b & 0x7F);
            
            if ((b & 0x80) == 0)
                return value;
        }
        
        return -1; // More than 4 bytes - invalid
    }
    
    private bool TrySkipRiffMidiHeader()
    {
        // Already read "RIFF", now read length
        Span<byte> buffer = stackalloc byte[8];
        
        // Read RIFF length
        if (stream.Read(buffer[..4]) < 4)
            return false;
        
        int riffLength = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        
        // Read format type (should be "RMID")
        if (stream.Read(buffer[..4]) < 4)
            return false;
        
        if (!buffer[..4].SequenceEqual("RMID"u8))
            return false;
        
        // Find the "data" chunk
        int remaining = riffLength - 4; // Already read "RMID"
        
        while (remaining >= 8)
        {
            if (stream.Read(buffer) < 8)
                return false;
            
            remaining -= 8;
            int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
            
            if (buffer[..4].SequenceEqual("data"u8))
                return true; // Found it, stream is positioned at SMF header
            
            if (chunkLength > remaining)
                return false;
            
            stream.Seek(chunkLength, SeekOrigin.Current);
            remaining -= chunkLength;
        }
        
        return false;
    }
    
    /// <summary>
    /// Gets events within a time range in microseconds.
    /// </summary>
    public static IEnumerable<MidiEvent> GetEventsInRange(SmfFile file, long startMicroseconds, long endMicroseconds)
    {
        foreach (var evt in file.Events)
        {
            if (evt.TimeMicroseconds >= startMicroseconds && evt.TimeMicroseconds < endMicroseconds)
                yield return evt;
            else if (evt.TimeMicroseconds >= endMicroseconds)
                break;
        }
    }
    
    /// <summary>
    /// Gets note on events (for analysis/playback).
    /// </summary>
    public static IEnumerable<MidiEvent> GetNoteOnEvents(SmfFile file)
    {
        foreach (var evt in file.Events)
        {
            if (evt.Category == MidiEventCategory.Note && 
                (evt.Status & 0xF0) == 0x90 && 
                evt.Data2 > 0) // Note on with velocity > 0
            {
                yield return evt;
            }
        }
    }
    
    /// <summary>
    /// Gets all control change events for a specific controller number.
    /// </summary>
    public static IEnumerable<MidiEvent> GetControlChanges(SmfFile file, int controllerNumber)
    {
        foreach (var evt in file.Events)
        {
            if (evt.Category == MidiEventCategory.Control && evt.Data1 == controllerNumber)
                yield return evt;
        }
    }
    
    public void Dispose()
    {
        if (!disposed)
        {
            if (!leaveOpen)
                stream.Dispose();
            disposed = true;
        }
    }
}

/// <summary>
/// MIDI note number utilities.
/// </summary>
public static class MidiNoteUtils
{
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    
    /// <summary>
    /// Gets the note name (e.g., "C4", "A#3") for a MIDI note number.
    /// </summary>
    public static string GetNoteName(int noteNumber)
    {
        if (noteNumber < 0 || noteNumber > 127)
            return "?";
        
        int octave = (noteNumber / 12) - 1;
        int noteIndex = noteNumber % 12;
        return $"{NoteNames[noteIndex]}{octave}";
    }
    
    /// <summary>
    /// Gets the frequency in Hz for a MIDI note number (A4 = 440 Hz).
    /// </summary>
    public static double GetFrequency(int noteNumber, double a4Frequency = 440.0)
    {
        return a4Frequency * Math.Pow(2.0, (noteNumber - 69) / 12.0);
    }
    
    /// <summary>
    /// Gets the MIDI note number for a note name.
    /// </summary>
    public static int? GetNoteNumber(string noteName)
    {
        if (string.IsNullOrEmpty(noteName) || noteName.Length < 2)
            return null;
        
        int i = 0;
        string note = noteName[i].ToString().ToUpperInvariant();
        i++;
        
        if (i < noteName.Length && (noteName[i] == '#' || noteName[i] == 'b'))
        {
            if (noteName[i] == '#')
                note += "#";
            else
                note = GetFlatEquivalent(note);
            i++;
        }
        
        if (!int.TryParse(noteName[i..], out int octave))
            return null;
        
        int noteIndex = Array.IndexOf(NoteNames, note);
        if (noteIndex < 0)
            return null;
        
        return (octave + 1) * 12 + noteIndex;
    }
    
    private static string GetFlatEquivalent(string note)
    {
        return note switch
        {
            "D" => "C#",
            "E" => "D#",
            "G" => "F#",
            "A" => "G#",
            "B" => "A#",
            _ => note
        };
    }
    
    /// <summary>
    /// Standard MIDI controller numbers.
    /// </summary>
    public static class Controllers
    {
        public const int BankSelect = 0;
        public const int ModulationWheel = 1;
        public const int BreathController = 2;
        public const int FootController = 4;
        public const int PortamentoTime = 5;
        public const int DataEntry = 6;
        public const int Volume = 7;
        public const int Balance = 8;
        public const int Pan = 10;
        public const int Expression = 11;
        public const int SustainPedal = 64;
        public const int PortamentoSwitch = 65;
        public const int SostenutoPedal = 66;
        public const int SoftPedal = 67;
        public const int LegatoSwitch = 68;
        public const int AllSoundOff = 120;
        public const int ResetAllControllers = 121;
        public const int LocalControl = 122;
        public const int AllNotesOff = 123;
    }
}
