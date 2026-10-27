using System;
using System.Collections.Generic;

namespace MediaKernel.Core.Hardware.D3D11
{
    /// <summary>
    /// Factory for creating D3D11 video decoders.
    /// Handles device creation and capability enumeration.
    /// </summary>
    public sealed unsafe class D3D11DecoderFactory : IDisposable
    {
        private D3D11DeviceHandle device;
        private D3D11DeviceContextHandle deviceContext;
        private D3D11VideoDeviceHandle videoDevice;
        private bool isInitialized;
        private bool isDisposed;
        private readonly List<HardwareDecoderCapabilities> cachedCapabilities = new();

        public D3D11DecoderFactory()
        {
        }

        /// <summary>
        /// Initializes the factory with an existing D3D11 device.
        /// </summary>
        public D3D11DecoderFactory(nint existingDevice, nint existingContext)
        {
            device = new D3D11DeviceHandle(existingDevice);
            deviceContext = new D3D11DeviceContextHandle(existingContext);
        }

        public bool IsInitialized => isInitialized;

        /// <summary>
        /// Initializes the factory, creating a D3D11 device if needed.
        /// </summary>
        public bool Initialize()
        {
            if (isInitialized) return true;

            // If no device provided, create one
            if (!device.IsValid)
            {
                if (!CreateDevice())
                {
                    return false;
                }
            }

            // Get ID3D11VideoDevice interface
            var videoDeviceIid = D3D11Interop.IidD3D11VideoDevice;
            nint videoDevicePtr;
            int hr = device.QueryInterface(&videoDeviceIid, &videoDevicePtr);
            if (hr < 0)
            {
                return false;
            }
            videoDevice = new D3D11VideoDeviceHandle(videoDevicePtr);

            // Enumerate capabilities
            EnumerateCapabilities();

            isInitialized = true;
            return true;
        }

        private bool CreateDevice()
        {
            uint featureLevels = D3D11Interop.FeatureLevel_11_0;
            nint devicePtr = 0;
            nint contextPtr = 0;
            uint actualFeatureLevel;

            // Try hardware device first
            int hr = D3D11Interop.D3D11CreateDevice(
                0,                              // Default adapter
                D3D11Interop.DriverTypeHardware,
                0,                              // No software module
                0,                              // No flags (no debug)
                &featureLevels,
                1,
                7,                              // SDK version (D3D11_SDK_VERSION)
                &devicePtr,
                &actualFeatureLevel,
                &contextPtr);

            if (hr < 0)
            {
                // Try WARP (software) device as fallback
                hr = D3D11Interop.D3D11CreateDevice(
                    0,
                    D3D11Interop.DriverTypeWarp,
                    0,
                    0,
                    &featureLevels,
                    1,
                    7,
                    &devicePtr,
                    &actualFeatureLevel,
                    &contextPtr);

                if (hr < 0)
                {
                    return false;
                }
            }

            device = new D3D11DeviceHandle(devicePtr);
            deviceContext = new D3D11DeviceContextHandle(contextPtr);
            return true;
        }

        private void EnumerateCapabilities()
        {
            cachedCapabilities.Clear();

            uint profileCount = videoDevice.GetVideoDecoderProfileCount();

            for (uint i = 0; i < profileCount; i++)
            {
                Guid profileGuid;
                int hr = videoDevice.GetVideoDecoderProfile(i, &profileGuid);
                if (hr < 0) continue;

                var capability = CreateCapabilityFromProfile(profileGuid);
                if (capability is not null)
                {
                    cachedCapabilities.Add(capability);
                }
            }
        }

        private HardwareDecoderCapabilities? CreateCapabilityFromProfile(Guid profileGuid)
        {
            // H.264 profiles
            if (profileGuid == D3D11VideoDecoderProfiles.H264VldNoFgt)
            {
                return new HardwareDecoderCapabilities
                {
                    CodecType = HardwareCodecType.H264,
                    MaxWidth = 4096,
                    MaxHeight = 2160,
                    MaxBitDepth = 8,
                    SupportedFormats = GetSupportedFormats(profileGuid),
                    ProfileName = "H.264 VLD No FGT",
                };
            }

            if (profileGuid == D3D11VideoDecoderProfiles.H264VldFgt)
            {
                return new HardwareDecoderCapabilities
                {
                    CodecType = HardwareCodecType.H264,
                    MaxWidth = 4096,
                    MaxHeight = 2160,
                    MaxBitDepth = 8,
                    SupportedFormats = GetSupportedFormats(profileGuid),
                    ProfileName = "H.264 VLD with FGT",
                };
            }

            // HEVC profiles
            if (profileGuid == D3D11VideoDecoderProfiles.HevcVldMain)
            {
                return new HardwareDecoderCapabilities
                {
                    CodecType = HardwareCodecType.Hevc,
                    MaxWidth = 8192,
                    MaxHeight = 4320,
                    MaxBitDepth = 8,
                    SupportedFormats = GetSupportedFormats(profileGuid),
                    ProfileName = "HEVC Main",
                };
            }

            if (profileGuid == D3D11VideoDecoderProfiles.HevcVldMain10)
            {
                return new HardwareDecoderCapabilities
                {
                    CodecType = HardwareCodecType.Hevc,
                    MaxWidth = 8192,
                    MaxHeight = 4320,
                    MaxBitDepth = 10,
                    SupportedFormats = GetSupportedFormats(profileGuid),
                    ProfileName = "HEVC Main 10",
                };
            }

            // VP9 profiles
            if (profileGuid == D3D11VideoDecoderProfiles.Vp9VldProfile0)
            {
                return new HardwareDecoderCapabilities
                {
                    CodecType = HardwareCodecType.Vp9,
                    MaxWidth = 8192,
                    MaxHeight = 4320,
                    MaxBitDepth = 8,
                    SupportedFormats = GetSupportedFormats(profileGuid),
                    ProfileName = "VP9 Profile 0",
                };
            }

            if (profileGuid == D3D11VideoDecoderProfiles.Vp9VldProfile210bit)
            {
                return new HardwareDecoderCapabilities
                {
                    CodecType = HardwareCodecType.Vp9,
                    MaxWidth = 8192,
                    MaxHeight = 4320,
                    MaxBitDepth = 10,
                    SupportedFormats = GetSupportedFormats(profileGuid),
                    ProfileName = "VP9 Profile 2 (10-bit)",
                };
            }

            // AV1 profiles
            if (profileGuid == D3D11VideoDecoderProfiles.Av1VldProfile0)
            {
                return new HardwareDecoderCapabilities
                {
                    CodecType = HardwareCodecType.Av1,
                    MaxWidth = 8192,
                    MaxHeight = 4320,
                    MaxBitDepth = 8,
                    SupportedFormats = GetSupportedFormats(profileGuid),
                    ProfileName = "AV1 Profile 0",
                };
            }

            if (profileGuid == D3D11VideoDecoderProfiles.Av1VldProfile210bit)
            {
                return new HardwareDecoderCapabilities
                {
                    CodecType = HardwareCodecType.Av1,
                    MaxWidth = 8192,
                    MaxHeight = 4320,
                    MaxBitDepth = 10,
                    SupportedFormats = GetSupportedFormats(profileGuid),
                    ProfileName = "AV1 Profile 2 (10-bit)",
                };
            }

            return null;
        }

        private HardwarePixelFormat[] GetSupportedFormats(Guid profileGuid)
        {
            var formats = new List<HardwarePixelFormat>();
            DxgiFormat[] candidateFormats =
            [
                DxgiFormat.Nv12,
                DxgiFormat.P010,
                DxgiFormat.P016,
                DxgiFormat.Ayuv,
                DxgiFormat.Y410,
            ];

            foreach (var format in candidateFormats)
            {
                int isSupported;
                int hr = videoDevice.CheckVideoDecoderFormat(&profileGuid, format, &isSupported);
                if (hr >= 0 && isSupported != 0)
                {
                    formats.Add(FormatConverter.ToHardwareFormat(format));
                }
            }

            return [.. formats];
        }

        /// <summary>
        /// Gets capabilities for all supported decoders.
        /// </summary>
        public IReadOnlyList<HardwareDecoderCapabilities> GetCapabilities()
        {
            return cachedCapabilities;
        }

        /// <summary>
        /// Checks if a specific codec is supported.
        /// </summary>
        public bool IsCodecSupported(HardwareCodecType codec)
        {
            foreach (var cap in cachedCapabilities)
            {
                if (cap.CodecType == codec)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Creates a decoder for the specified codec and dimensions.
        /// </summary>
        public D3D11VideoDecoder? CreateDecoder(
            HardwareCodecType codec,
            uint width,
            uint height,
            int bitDepth = 8,
            int chromaFormat = 1)
        {
            if (!isInitialized) return null;

            // Find appropriate profile
            Guid profileGuid = codec switch
            {
                HardwareCodecType.H264 => D3D11VideoDecoderProfiles.GetH264Profile(77, bitDepth),
                HardwareCodecType.Hevc => D3D11VideoDecoderProfiles.GetHevcProfile(1, bitDepth, chromaFormat),
                HardwareCodecType.Vp9 => bitDepth == 10 ? D3D11VideoDecoderProfiles.Vp9VldProfile210bit : D3D11VideoDecoderProfiles.Vp9VldProfile0,
                HardwareCodecType.Av1 => bitDepth == 10 ? D3D11VideoDecoderProfiles.Av1VldProfile210bit : D3D11VideoDecoderProfiles.Av1VldProfile0,
                _ => Guid.Empty,
            };

            if (profileGuid == Guid.Empty)
            {
                return null;
            }

            // Determine output format
            DxgiFormat format = FormatConverter.GetPreferredFormat(bitDepth);

            // Verify support
            int isSupported;
            int hr = videoDevice.CheckVideoDecoderFormat(&profileGuid, format, &isSupported);
            if (hr < 0 || isSupported == 0)
            {
                return null;
            }

            // Calculate surface count (reference frames + output + margin)
            int surfaceCount = codec switch
            {
                HardwareCodecType.H264 => 16 + 4,      // H.264 can have up to 16 reference frames
                HardwareCodecType.Hevc => 16 + 4,      // HEVC similar to H.264
                HardwareCodecType.Vp9 => 8 + 4,        // VP9 uses up to 8 references
                HardwareCodecType.Av1 => 8 + 4,        // AV1 uses up to 8 references
                _ => 20,
            };

            var decoder = new D3D11VideoDecoder(
                device,
                deviceContext,
                profileGuid,
                width,
                height,
                format,
                surfaceCount);

            if (!decoder.Initialize())
            {
                decoder.Dispose();
                return null;
            }

            return decoder;
        }

        /// <summary>
        /// Gets the underlying D3D11 device.
        /// </summary>
        public nint GetDevice() => device.Handle;

        /// <summary>
        /// Gets the underlying D3D11 device context.
        /// </summary>
        public nint GetDeviceContext() => deviceContext.Handle;

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            videoDevice.Release();
            deviceContext.Release();
            device.Release();
        }
    }
}
