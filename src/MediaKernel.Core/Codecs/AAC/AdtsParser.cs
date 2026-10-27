using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.AAC;

/// <summary>
/// Parser for ADTS (Audio Data Transport Stream) headers.
/// ADTS is the common framing format for AAC in streaming and file containers.
/// </summary>
public static class AdtsParser
{
    /// <summary>
    /// Checks if the data starts with a valid ADTS sync word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasSyncWord(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return false;
        
        // Sync word: 0xFFF (12 bits)
        return data[0] == 0xFF && (data[1] & 0xF0) == 0xF0;
    }

    /// <summary>
    /// Parses an ADTS frame header.
    /// </summary>
    /// <param name="data">Input data (at least 7 bytes).</param>
    /// <param name="header">Parsed header on success.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out AdtsHeader header)
    {
        header = default;
        
        if (data.Length < 7)
            return false;
        
        // Check sync word (12 bits: 0xFFF)
        if (data[0] != 0xFF || (data[1] & 0xF0) != 0xF0)
            return false;
        
        // Byte 1: sync(4) | ID(1) | layer(2) | protection_absent(1)
        bool isMpeg2 = (data[1] & 0x08) != 0;  // ID: 0=MPEG-4, 1=MPEG-2
        int layer = (data[1] >> 1) & 0x03;      // Should be 0
        bool protectionAbsent = (data[1] & 0x01) != 0;
        
        if (layer != 0)
            return false;  // Invalid layer for AAC
        
        // Byte 2: profile(2) | sampling_frequency_index(4) | private_bit(1) | channel_config(1 of 3)
        int profile = (data[2] >> 6) & 0x03;
        int sampleRateIndex = (data[2] >> 2) & 0x0F;
        int channelConfigHigh = data[2] & 0x01;
        
        // Byte 3: channel_config(2 of 3) | original_copy(1) | home(1) | copyright_id(1) | copyright_start(1) | frame_length(2 of 13)
        int channelConfigMid = (data[3] >> 6) & 0x03;
        int frameLengthHigh = data[3] & 0x03;
        
        int channelConfig = (channelConfigHigh << 2) | channelConfigMid;
        
        // Byte 4: frame_length(8 of 13)
        int frameLengthMid = data[4];
        
        // Byte 5: frame_length(3 of 13) | buffer_fullness(5 of 11)
        int frameLengthLow = (data[5] >> 5) & 0x07;
        int bufferFullnessHigh = data[5] & 0x1F;
        
        // Byte 6: buffer_fullness(6 of 11) | raw_data_blocks(2)
        int bufferFullnessLow = (data[6] >> 2) & 0x3F;
        int rawDataBlocks = data[6] & 0x03;
        
        int frameLength = (frameLengthHigh << 11) | (frameLengthMid << 3) | frameLengthLow;
        int bufferFullness = (bufferFullnessHigh << 6) | bufferFullnessLow;
        
        // Validate
        if (frameLength < 7 || sampleRateIndex > 12)
            return false;
        
        header = new AdtsHeader
        {
            IsMpeg2 = isMpeg2,
            Profile = (AudioObjectType)(profile + 1),  // ADTS profile is 0-based, AOT is 1-based
            SampleRateIndex = sampleRateIndex,
            ChannelConfiguration = (ChannelConfiguration)channelConfig,
            FrameLength = frameLength,
            BufferFullness = bufferFullness,
            RawDataBlocksInFrame = rawDataBlocks,
            HasCrc = !protectionAbsent,
        };
        
        return header.IsValid;
    }

    /// <summary>
    /// Parses Audio Specific Config from raw bytes (e.g., from MP4 ESDS box).
    /// </summary>
    /// <param name="data">AudioSpecificConfig data (typically 2-5 bytes).</param>
    /// <returns>Parsed configuration or null on failure.</returns>
    public static AudioSpecificConfig? ParseAudioSpecificConfig(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return null;
        
        var config = new AudioSpecificConfig();
        int bitPos = 0;
        
        // audioObjectType (5 bits)
        int aot = ReadBits(data, ref bitPos, 5);
        if (aot == 31)  // Escape value
        {
            aot = 32 + ReadBits(data, ref bitPos, 6);
        }
        config.ObjectType = (AudioObjectType)aot;
        
        // samplingFrequencyIndex (4 bits)
        int sfIndex = ReadBits(data, ref bitPos, 4);
        config.SampleRateIndex = sfIndex;
        
        if (sfIndex == 0x0F)  // Escape value
        {
            // samplingFrequency (24 bits)
            config.SampleRate = ReadBits(data, ref bitPos, 24);
        }
        else
        {
            config.SampleRate = AacSampleRates.FromIndex(sfIndex);
        }
        
        // channelConfiguration (4 bits)
        int channelConfig = ReadBits(data, ref bitPos, 4);
        config.ChannelConfiguration = (ChannelConfiguration)channelConfig;
        
        // Explicit hierarchical signalling: the object type IS the extension, and the core type
        // follows the extension sample rate. This is FFmpeg's ff_mpeg4audio_get_config_gb, down
        // to the W6132 Annex check that keeps MP3onMP4 out of the PS branch.
        if (config.ObjectType == AudioObjectType.SpectralBandReplication ||
            (config.ObjectType == AudioObjectType.ParametricStereo &&
             !((PeekBits(data, bitPos, 3) & 0x03) != 0 && (PeekBits(data, bitPos, 9) & 0x3F) == 0)))
        {
            if (config.ObjectType == AudioObjectType.ParametricStereo)
                config.PsMode = 1;

            config.HasSbr = true;
            config.SbrMode = 1;
            config.ExtensionObjectType = AudioObjectType.SpectralBandReplication;

            // extensionSamplingFrequencyIndex (4 bits)
            int extSfIndex = ReadBits(data, ref bitPos, 4);
            config.ExtensionSampleRate = extSfIndex == 0x0F
                ? ReadBits(data, ref bitPos, 24)
                : AacSampleRates.FromIndex(extSfIndex);

            // The audio object type that follows is the CORE one - LC for every HE-AAC stream in
            // use. FFmpeg assigns it to object_type, and so does this: the GASpecificConfig that
            // follows belongs to the core type, and leaving ObjectType at 5 skipped it.
            int coreAot = ReadBits(data, ref bitPos, 5);
            if (coreAot == 31)
                coreAot = 32 + ReadBits(data, ref bitPos, 6);
            config.ObjectType = (AudioObjectType)coreAot;
        }
        else
        {
            config.ExtensionObjectType = AudioObjectType.Null;
            config.ExtensionSampleRate = 0;
        }

        // ER AAC LD is only defined for sampling indices 3..7 (48 kHz down to 16 kHz). FFmpeg
        // rejects the config outright rather than decode with a null band table, and so does
        // this: the 512- and 480-point band tables do not exist outside that range, so the
        // alternative is a decoder reading scalefactor bands out of nothing.
        if (config.ObjectType == AudioObjectType.ErrorResilientAacLd &&
            (config.SampleRateIndex < 3 || config.SampleRateIndex > 7))
            return null;

        // USAC reads its own configuration, starting right here. Everything after the
        // thirteen-bit prefix is UsacConfig, including a sampling frequency index of its own
        // that supersedes the four-bit one above, so there is nothing further for this parser
        // to do but record the position.
        if (config.ObjectType == AudioObjectType.Usac)
        {
            config.RawConfig = data.ToArray();
            config.SpecificConfigBitOffset = bitPos;
            return config;
        }

        // Parse GASpecificConfig for AAC-LC, AAC-Main, AAC-LTP
        if (config.ObjectType == AudioObjectType.ErrorResilientAacEld)
        {
            if (!ParseEldSpecificConfig(data, ref bitPos, config))
                return null;
        }
        else if (IsGaSpecificConfig(config.ObjectType))
        {
            if (!ParseGaSpecificConfig(data, ref bitPos, config))
                return null;
        }

        // Backward-compatible signalling: SBR and PS announced by sync words after the core
        // configuration, which is how the CT_DecoderCheck streams and most broadcast HE-AAC
        // declare themselves.
        //
        // Not for the low-delay types. ELD carries its own extension list, which the parser
        // above has already walked to its terminator, and an ELDEXT payload is not a 0x2b7 sync
        // word; LD has no backward-compatible SBR signalling at all. Scanning either for one
        // would be reading whatever follows the config as though it announced a tool.
        if (config.ExtensionObjectType != AudioObjectType.SpectralBandReplication &&
            config.ObjectType != AudioObjectType.ErrorResilientAacLd &&
            config.ObjectType != AudioObjectType.ErrorResilientAacEld)
            ReadBackwardCompatibleExtension(data, ref bitPos, config);

        // PS requires SBR, and implicit PS is limited to the HE-AACv2 profile: AAC-LC with one
        // channel or an unspecified channel configuration.
        if (config.SbrMode == 0) config.PsMode = 0;
        if ((config.PsMode == -1 && config.ObjectType != AudioObjectType.AacLowComplexity) ||
            (config.ChannelCount & ~0x01) != 0)
            config.PsMode = 0;

        config.HasSbr = config.SbrMode != 0;
        config.HasParametricStereo = config.PsMode == 1;

        return config;
    }

    /// <summary>
    /// The <c>0x2b7</c> / <c>0x548</c> sync extensions, which announce SBR and PS after a core
    /// configuration that did not mention them.
    /// </summary>
    private static void ReadBackwardCompatibleExtension(
        ReadOnlySpan<byte> data, ref int bitPos, AudioSpecificConfig config)
    {
        int totalBits = data.Length * 8;

        while (totalBits - bitPos > 15)
        {
            if (PeekBits(data, bitPos, 11) != 0x2b7)
            {
                bitPos++;   // the C steps one bit at a time looking for the sync word
                continue;
            }

            bitPos += 11;
            int extAot = ReadBits(data, ref bitPos, 5);
            if (extAot == 31)
                extAot = 32 + ReadBits(data, ref bitPos, 6);
            config.ExtensionObjectType = (AudioObjectType)extAot;

            if (config.ExtensionObjectType == AudioObjectType.SpectralBandReplication)
            {
                config.SbrMode = ReadBits(data, ref bitPos, 1);
                if (config.SbrMode == 1)
                {
                    int extSfIndex = ReadBits(data, ref bitPos, 4);
                    config.ExtensionSampleRate = extSfIndex == 0x0F
                        ? ReadBits(data, ref bitPos, 24)
                        : AacSampleRates.FromIndex(extSfIndex);

                    // An extension rate equal to the core rate is not SBR signalling at all;
                    // FFmpeg puts sbr back to "unknown" rather than trusting it.
                    if (config.ExtensionSampleRate == config.SampleRate)
                        config.SbrMode = -1;
                }
            }

            if (totalBits - bitPos > 11 && ReadBits(data, ref bitPos, 11) == 0x548)
                config.PsMode = ReadBits(data, ref bitPos, 1);

            break;
        }
    }

    /// <summary>Reads without consuming, for the two lookaheads the C does with show_bits.</summary>
    private static int PeekBits(ReadOnlySpan<byte> data, int bitPos, int count)
    {
        int scratch = bitPos;
        return ReadBits(data, ref scratch, count);
    }

    /// <summary>
    /// Finds the next ADTS sync word in the data.
    /// </summary>
    /// <param name="data">Data to search.</param>
    /// <param name="offset">Starting offset.</param>
    /// <returns>Offset of sync word, or -1 if not found.</returns>
    public static int FindSyncWord(ReadOnlySpan<byte> data, int offset = 0)
    {
        for (int i = offset; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xF0) == 0xF0)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Extracts all ADTS frames from a buffer.
    /// </summary>
    /// <param name="data">Input buffer.</param>
    /// <param name="frameCallback">Callback for each frame (header, payload).</param>
    /// <returns>Number of frames extracted.</returns>
    public static int ExtractFrames(ReadOnlySpan<byte> data, Action<AdtsHeader, ReadOnlySpan<byte>> frameCallback)
    {
        int frameCount = 0;
        int offset = 0;
        
        while (offset < data.Length - 7)
        {
            int syncPos = FindSyncWord(data, offset);
            if (syncPos < 0)
                break;
            
            if (!TryParseHeader(data.Slice(syncPos), out var header))
            {
                offset = syncPos + 1;
                continue;
            }
            
            if (syncPos + header.FrameLength > data.Length)
                break;  // Incomplete frame
            
            var payload = data.Slice(syncPos + header.HeaderSize, header.PayloadSize);
            frameCallback(header, payload);
            
            frameCount++;
            offset = syncPos + header.FrameLength;
        }
        
        return frameCount;
    }

    /// <summary>
    /// Creates an AudioSpecificConfig from an ADTS header.
    /// </summary>
    public static AudioSpecificConfig FromAdtsHeader(in AdtsHeader header)
    {
        return new AudioSpecificConfig
        {
            ObjectType = header.Profile,
            SampleRateIndex = header.SampleRateIndex,
            SampleRate = header.SampleRate,
            ChannelConfiguration = header.ChannelConfiguration,
            FrameLength = 1024,  // Standard AAC frame length
        };
    }

    #region Private Helpers

    private static bool IsGaSpecificConfig(AudioObjectType aot)
    {
        return aot switch
        {
            AudioObjectType.AacMain => true,
            AudioObjectType.AacLowComplexity => true,
            AudioObjectType.AacScalableSampleRate => true,
            AudioObjectType.AacLongTermPrediction => true,
            AudioObjectType.AacScalable => true,
            AudioObjectType.TwinVq => true,
            AudioObjectType.ErrorResilientAacLc => true,
            AudioObjectType.ErrorResilientAacLtp => true,
            AudioObjectType.ErrorResilientAacScalable => true,
            AudioObjectType.ErrorResilientTwinVq => true,
            AudioObjectType.ErrorResilientBsac => true,
            AudioObjectType.ErrorResilientAacLd => true,
            _ => false
        };
    }

    /// <summary>
    /// <c>decode_ga_specific_config</c>. Returns false when the configuration names a tool this
    /// decoder does not implement; that is an unsupported stream rather than a malformed one,
    /// but either way it must not be decoded, so both come back the same way.
    /// </summary>
    private static bool ParseGaSpecificConfig(ReadOnlySpan<byte> data, ref int bitPos, AudioSpecificConfig config)
    {
        if (bitPos + 3 > data.Length * 8)
            return true;

        // frameLengthFlag (1 bit)
        int frameLengthFlag = ReadBits(data, ref bitPos, 1);
        config.FrameLength = frameLengthFlag == 0 ? 1024 : 960;

        if (frameLengthFlag != 0 && config.SbrMode == 1)
        {
            // FFmpeg reports "SBR with 960 frame length" as missing and carries on with SBR off.
            config.SbrMode = 0;
            config.PsMode = 0;
        }

        // dependsOnCoreCoder (1 bit)
        int dependsOnCoreCoder = ReadBits(data, ref bitPos, 1);
        config.DependsOnCoreCoder = dependsOnCoreCoder != 0;

        if (config.DependsOnCoreCoder)
        {
            // coreCoderDelay (14 bits)
            ReadBits(data, ref bitPos, 14);
        }

        // extensionFlag (1 bit)
        int extensionFlag = ReadBits(data, ref bitPos, 1);

        if (config.ObjectType == AudioObjectType.AacScalable ||
            config.ObjectType == AudioObjectType.ErrorResilientAacScalable)
            ReadBits(data, ref bitPos, 3);   // layerNr

        if (config.ChannelConfiguration == ChannelConfiguration.AotSpecificConfig)
        {
            // PCE follows in GASpecificConfig - parse it
            config.Pce = ParseProgramConfigElement(data, ref bitPos);
        }

        // The error-resilient object types carry more configuration after the extension flag,
        // and reading none of it leaves the bit position short by up to six bits. That never
        // showed up before because nothing downstream of a GA config read any further. LD does.
        if (extensionFlag != 0)
        {
            switch (config.ObjectType)
            {
                case AudioObjectType.ErrorResilientBsac:
                    ReadBits(data, ref bitPos, 5);    // numOfSubFrame
                    ReadBits(data, ref bitPos, 11);   // layer_length
                    break;

                case AudioObjectType.ErrorResilientAacLc:
                case AudioObjectType.ErrorResilientAacLtp:
                case AudioObjectType.ErrorResilientAacScalable:
                case AudioObjectType.ErrorResilientAacLd:
                    // aacSectionDataResilienceFlag, aacScalefactorDataResilienceFlag,
                    // aacSpectralDataResilienceFlag. Any of them changes how the section and
                    // scalefactor data is coded, which this decoder does not implement - so a
                    // stream that sets one is refused rather than decoded as if it had not.
                    if (ReadBits(data, ref bitPos, 3) != 0)
                        return false;
                    break;
            }

            ReadBits(data, ref bitPos, 1);   // extensionFlag3, reserved
        }

        switch (config.ObjectType)
        {
            case AudioObjectType.ErrorResilientAacLc:
            case AudioObjectType.ErrorResilientAacLtp:
            case AudioObjectType.ErrorResilientAacScalable:
            case AudioObjectType.ErrorResilientAacLd:
                // epConfig 1 and 2 wrap the payload in an error-protection layer, so what
                // follows is not the raw_data_block this decoder expects.
                if (ReadBits(data, ref bitPos, 2) != 0)
                    return false;
                break;
        }

        return true;
    }

    /// <summary>
    /// <c>decode_eld_specific_config</c>. ELD's configuration is not a GA configuration: no
    /// dependsOnCoreCoder, no extension flag and no PCE - the channel configuration alone names
    /// the elements - but it does carry a length-prefixed extension list which has to be walked
    /// to its terminator before epConfig can be read.
    /// </summary>
    private static bool ParseEldSpecificConfig(ReadOnlySpan<byte> data, ref int bitPos, AudioSpecificConfig config)
    {
        const int EldExtTerm = 0;
        int totalBits = data.Length * 8;

        // ELD never carries SBR or PS by the backward-compatible route; the only SBR it can have
        // is the ldSbrPresentFlag below, and that is refused.
        config.SbrMode = 0;
        config.PsMode = 0;
        config.HasSbr = false;
        config.HasParametricStereo = false;

        if (bitPos + 8 > totalBits)
            return false;

        int frameLengthFlag = ReadBits(data, ref bitPos, 1);
        config.FrameLength = frameLengthFlag == 0 ? 1024 : 960;

        if (ReadBits(data, ref bitPos, 3) != 0)
            return false;   // AAC data resilience, as above

        if (ReadBits(data, ref bitPos, 1) != 0)
            return false;   // ldSbrPresentFlag: Low Delay SBR, which FFmpeg does not implement

        // eldExtType(4), eldExtLen(4) [+8] [+16], then that many bytes, until eldExtType is 0.
        while (true)
        {
            if (bitPos + 4 > totalBits)
                return false;
            if (ReadBits(data, ref bitPos, 4) == EldExtTerm)
                break;

            if (bitPos + 4 > totalBits)
                return false;
            int len = ReadBits(data, ref bitPos, 4);
            if (len == 15)
            {
                if (bitPos + 8 > totalBits)
                    return false;
                len += ReadBits(data, ref bitPos, 8);
            }
            if (len == 15 + 255)
            {
                if (bitPos + 16 > totalBits)
                    return false;
                len += ReadBits(data, ref bitPos, 16);
            }

            // The +4 is the next eldExtType, which the loop must still be able to read.
            if (totalBits - bitPos < len * 8 + 4)
                return false;
            bitPos += len * 8;
        }

        if (bitPos + 2 > totalBits)
            return false;
        if (ReadBits(data, ref bitPos, 2) != 0)
            return false;   // epConfig, as above

        return true;
    }

    /// <summary>
    /// Parses a Program Config Element from the bitstream.
    /// Port of FFmpeg's decode_pce() from aacdec.c:811-870.
    /// </summary>
    internal static ProgramConfigElement? ParseProgramConfigElement(ReadOnlySpan<byte> data, ref int bitPos)
    {
        int startBitPos = bitPos;

        // element_instance_tag (4 bits)
        ReadBits(data, ref bitPos, 4);
        
        // object_type (2 bits)
        ReadBits(data, ref bitPos, 2);
        
        // sampling_frequency_index (4 bits)
        ReadBits(data, ref bitPos, 4);
        
        int numFront = ReadBits(data, ref bitPos, 4);
        int numSide = ReadBits(data, ref bitPos, 4);
        int numBack = ReadBits(data, ref bitPos, 4);
        int numLfe = ReadBits(data, ref bitPos, 2);
        int numAssocData = ReadBits(data, ref bitPos, 3);
        int numCc = ReadBits(data, ref bitPos, 4);
        
        // mono_mixdown
        if (ReadBits(data, ref bitPos, 1) != 0)
            ReadBits(data, ref bitPos, 4);
        
        // stereo_mixdown
        if (ReadBits(data, ref bitPos, 1) != 0)
            ReadBits(data, ref bitPos, 4);
        
        // matrix_mixdown
        if (ReadBits(data, ref bitPos, 1) != 0)
            ReadBits(data, ref bitPos, 3);
        
        // Build element list: front, side, back, LFE
        var elements = new List<PceElementDescriptor>();
        
        // Front channels
        for (int i = 0; i < numFront; i++)
        {
            bool isCpe = ReadBits(data, ref bitPos, 1) != 0;
            int tag = ReadBits(data, ref bitPos, 4);
            elements.Add(new PceElementDescriptor(
                isCpe ? SyntaxElementType.ChannelPairElement : SyntaxElementType.SingleChannelElement,
                tag));
        }
        
        // Side channels
        for (int i = 0; i < numSide; i++)
        {
            bool isCpe = ReadBits(data, ref bitPos, 1) != 0;
            int tag = ReadBits(data, ref bitPos, 4);
            elements.Add(new PceElementDescriptor(
                isCpe ? SyntaxElementType.ChannelPairElement : SyntaxElementType.SingleChannelElement,
                tag));
        }
        
        // Back channels
        for (int i = 0; i < numBack; i++)
        {
            bool isCpe = ReadBits(data, ref bitPos, 1) != 0;
            int tag = ReadBits(data, ref bitPos, 4);
            elements.Add(new PceElementDescriptor(
                isCpe ? SyntaxElementType.ChannelPairElement : SyntaxElementType.SingleChannelElement,
                tag));
        }
        
        // LFE channels
        for (int i = 0; i < numLfe; i++)
        {
            int tag = ReadBits(data, ref bitPos, 4);
            elements.Add(new PceElementDescriptor(SyntaxElementType.LowFrequencyEffectsElement, tag));
        }
        
        // Skip assoc_data tags
        for (int i = 0; i < numAssocData; i++)
            ReadBits(data, ref bitPos, 4);
        
        // Skip CC element tags
        for (int i = 0; i < numCc; i++)
        {
            ReadBits(data, ref bitPos, 1); // ind_sw
            ReadBits(data, ref bitPos, 4); // tag
        }
        
        // Byte-align relative to start of PCE
        int alignment = (startBitPos - bitPos) & 7;
        if (alignment != 0)
            bitPos += alignment;
        
        // Comment field
        if (bitPos + 8 <= data.Length * 8)
        {
            int commentLen = ReadBits(data, ref bitPos, 8);
            bitPos += commentLen * 8;
        }
        
        return new ProgramConfigElement
        {
            NumFront = numFront,
            NumSide = numSide,
            NumBack = numBack,
            NumLfe = numLfe,
            NumAssocData = numAssocData,
            NumCc = numCc,
            Elements = elements.ToArray()
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadBits(ReadOnlySpan<byte> data, ref int bitPos, int numBits)
    {
        int value = 0;
        for (int i = 0; i < numBits; i++)
        {
            int byteIdx = bitPos / 8;
            int bitIdx = 7 - (bitPos % 8);
            
            if (byteIdx >= data.Length)
                return 0;
            
            value = (value << 1) | ((data[byteIdx] >> bitIdx) & 1);
            bitPos++;
        }
        return value;
    }

    #endregion
}
