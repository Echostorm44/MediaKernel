using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Hardware.D3D11
{
    /// <summary>
    /// Result of a hardware decode operation.
    /// </summary>
    public readonly struct D3D11DecodeResult
    {
        public D3D11DecodeResult(bool success, D3D11DecoderSurface? surface, int hResult = 0)
        {
            Success = success;
            Surface = surface;
            HResult = hResult;
        }

        public bool Success { get; }
        public D3D11DecoderSurface? Surface { get; }
        public int HResult { get; }

        public static D3D11DecodeResult Failure(int hResult) => new(false, null, hResult);
        public static D3D11DecodeResult Ok(D3D11DecoderSurface surface) => new(true, surface, 0);
    }

    /// <summary>
    /// D3D11VA hardware video decoder.
    /// Implements the ID3D11VideoDecoder workflow for H.264, HEVC, VP9, AV1.
    /// </summary>
    public sealed unsafe class D3D11VideoDecoder : IDisposable
    {
        private readonly D3D11DeviceHandle device;
        private readonly D3D11DeviceContextHandle deviceContext;
        private D3D11VideoDeviceHandle videoDevice;
        private D3D11VideoContextHandle videoContext;
        private D3D11VideoDecoderHandle videoDecoder;
        private D3D11SurfacePool? surfacePool;

        private readonly Guid decoderProfileGuid;
        private readonly uint width;
        private readonly uint height;
        private readonly DxgiFormat outputFormat;
        private readonly int surfaceCount;

        private bool isInitialized;
        private bool isDisposed;

        public D3D11VideoDecoder(
            D3D11DeviceHandle deviceHandle,
            D3D11DeviceContextHandle contextHandle,
            Guid decoderProfile,
            uint videoWidth,
            uint videoHeight,
            DxgiFormat format,
            int requiredSurfaces = 16)
        {
            device = deviceHandle;
            deviceContext = contextHandle;
            decoderProfileGuid = decoderProfile;
            width = videoWidth;
            height = videoHeight;
            outputFormat = format;
            surfaceCount = requiredSurfaces;
        }

        public bool IsInitialized => isInitialized;
        public uint Width => width;
        public uint Height => height;
        public HardwarePixelFormat PixelFormat => FormatConverter.ToHardwareFormat(outputFormat);
        public nint VideoDecoderHandle => videoDecoder.Handle;

        /// <summary>
        /// Initializes the hardware decoder.
        /// </summary>
        public bool Initialize()
        {
            if (isInitialized) return true;

            // Query for ID3D11VideoDevice from device
            var videoDeviceIid = D3D11Interop.IidD3D11VideoDevice;
            nint videoDevicePtr;
            int hr = device.QueryInterface(&videoDeviceIid, &videoDevicePtr);
            if (hr < 0)
            {
                return false;
            }
            videoDevice = new D3D11VideoDeviceHandle(videoDevicePtr);

            // Query for ID3D11VideoContext from context
            var videoContextIid = D3D11Interop.IidD3D11VideoContext;
            nint videoContextPtr;
            hr = deviceContext.QueryInterface(&videoContextIid, &videoContextPtr);
            if (hr < 0)
            {
                videoDevice.Release();
                return false;
            }
            videoContext = new D3D11VideoContextHandle(videoContextPtr);

            // Check format support
            int isSupported;
            fixed (Guid* profilePtr = &decoderProfileGuid)
            {
                hr = videoDevice.CheckVideoDecoderFormat(profilePtr, outputFormat, &isSupported);
            }

            if (hr < 0 || isSupported == 0)
            {
                videoContext.Release();
                videoDevice.Release();
                return false;
            }

            // Get decoder config
            var decoderDesc = new D3D11VideoDecoderDesc
            {
                Guid = decoderProfileGuid,
                SampleWidth = width,
                SampleHeight = height,
                OutputFormat = outputFormat,
            };

            uint configCount;
            hr = videoDevice.GetVideoDecoderConfigCount(&decoderDesc, &configCount);
            if (hr < 0 || configCount == 0)
            {
                videoContext.Release();
                videoDevice.Release();
                return false;
            }

            // Find best config (prefer ConfigBitstreamRaw = 1)
            D3D11VideoDecoderConfig? selectedConfig = null;
            int bestScore = -1;

            for (uint i = 0; i < configCount; i++)
            {
                D3D11VideoDecoderConfig config;
                hr = videoDevice.GetVideoDecoderConfig(&decoderDesc, i, &config);
                if (hr < 0) continue;

                // Score based on ConfigBitstreamRaw
                // 1 = short slice mode (preferred)
                // 2 = long slice mode (H.264 supports both)
                int score = config.ConfigBitstreamRaw switch
                {
                    1 => 2,  // Best: short format
                    2 => 1,  // OK: long format
                    _ => 0,
                };

                if (score > bestScore)
                {
                    bestScore = score;
                    selectedConfig = config;
                }
            }

            if (selectedConfig is null)
            {
                videoContext.Release();
                videoDevice.Release();
                return false;
            }

            // Create video decoder
            nint decoderPtr;
            var config2 = selectedConfig.Value;
            hr = videoDevice.CreateVideoDecoder(&decoderDesc, &config2, &decoderPtr);
            if (hr < 0)
            {
                videoContext.Release();
                videoDevice.Release();
                return false;
            }
            videoDecoder = new D3D11VideoDecoderHandle(decoderPtr);

            // Create surface pool
            surfacePool = new D3D11SurfacePool(
                device,
                videoDevice,
                decoderProfileGuid,
                width,
                height,
                outputFormat,
                surfaceCount);

            if (!surfacePool.Initialize())
            {
                videoDecoder.Release();
                videoContext.Release();
                videoDevice.Release();
                return false;
            }

            isInitialized = true;
            return true;
        }

        /// <summary>
        /// Decodes a single frame using the provided bitstream and picture parameters.
        /// </summary>
        public D3D11DecodeResult DecodeFrame(
            ReadOnlySpan<byte> bitstream,
            ReadOnlySpan<byte> pictureParameters,
            ReadOnlySpan<byte> sliceControl,
            ReadOnlySpan<byte> quantizationMatrix = default)
        {
            if (!isInitialized)
            {
                return D3D11DecodeResult.Failure(D3D11Interop.EFail);
            }

            // Acquire output surface
            if (!surfacePool.TryAcquireSurface(out var surface))
            {
                return D3D11DecodeResult.Failure(D3D11Interop.EOutOfMemory);
            }

            int hr;

            // Begin frame
            hr = videoContext.DecoderBeginFrame(videoDecoder.Handle, surface!.OutputViewHandle, 0, null);
            if (hr < 0)
            {
                surface.Release();
                return D3D11DecodeResult.Failure(hr);
            }

            var buffers = new List<D3D11VideoDecoderBufferDesc>(4);

            // Fill picture parameters buffer
            if (!pictureParameters.IsEmpty)
            {
                hr = FillDecoderBuffer(D3D11VideoDecoderBufferType.PictureParameters, pictureParameters, ref buffers);
                if (hr < 0)
                {
                    videoContext.DecoderEndFrame(videoDecoder.Handle);
                    surface.Release();
                    return D3D11DecodeResult.Failure(hr);
                }
            }

            // Fill quantization matrix buffer
            if (!quantizationMatrix.IsEmpty)
            {
                hr = FillDecoderBuffer(D3D11VideoDecoderBufferType.InverseQuantizationMatrix, quantizationMatrix, ref buffers);
                if (hr < 0)
                {
                    videoContext.DecoderEndFrame(videoDecoder.Handle);
                    surface.Release();
                    return D3D11DecodeResult.Failure(hr);
                }
            }

            // Fill slice control buffer
            if (!sliceControl.IsEmpty)
            {
                hr = FillDecoderBuffer(D3D11VideoDecoderBufferType.SliceControl, sliceControl, ref buffers);
                if (hr < 0)
                {
                    videoContext.DecoderEndFrame(videoDecoder.Handle);
                    surface.Release();
                    return D3D11DecodeResult.Failure(hr);
                }
            }

            // Fill bitstream buffer
            if (!bitstream.IsEmpty)
            {
                hr = FillDecoderBuffer(D3D11VideoDecoderBufferType.Bitstream, bitstream, ref buffers);
                if (hr < 0)
                {
                    videoContext.DecoderEndFrame(videoDecoder.Handle);
                    surface.Release();
                    return D3D11DecodeResult.Failure(hr);
                }
            }

            // Submit buffers
            if (buffers.Count > 0)
            {
                Span<D3D11VideoDecoderBufferDesc> bufferSpan = stackalloc D3D11VideoDecoderBufferDesc[buffers.Count];
                for (int i = 0; i < buffers.Count; i++)
                {
                    bufferSpan[i] = buffers[i];
                }

                fixed (D3D11VideoDecoderBufferDesc* pBuffers = bufferSpan)
                {
                    hr = videoContext.SubmitDecoderBuffers(videoDecoder.Handle, (uint)buffers.Count, pBuffers);
                }

                if (hr < 0)
                {
                    videoContext.DecoderEndFrame(videoDecoder.Handle);
                    surface.Release();
                    return D3D11DecodeResult.Failure(hr);
                }
            }

            // End frame
            hr = videoContext.DecoderEndFrame(videoDecoder.Handle);
            if (hr < 0)
            {
                surface.Release();
                return D3D11DecodeResult.Failure(hr);
            }

            return D3D11DecodeResult.Ok(surface);
        }

        private int FillDecoderBuffer(
            D3D11VideoDecoderBufferType bufferType,
            ReadOnlySpan<byte> data,
            ref List<D3D11VideoDecoderBufferDesc> buffers)
        {
            uint bufferSize;
            void* bufferPtr;

            int hr = videoContext.GetDecoderBuffer(videoDecoder.Handle, bufferType, &bufferSize, &bufferPtr);
            if (hr < 0)
            {
                return hr;
            }

            if (bufferSize < (uint)data.Length)
            {
                videoContext.ReleaseDecoderBuffer(videoDecoder.Handle, bufferType);
                return D3D11Interop.EOutOfMemory;
            }

            // Copy data to buffer
            fixed (byte* srcPtr = data)
            {
                Unsafe.CopyBlock(bufferPtr, srcPtr, (uint)data.Length);
            }

            hr = videoContext.ReleaseDecoderBuffer(videoDecoder.Handle, bufferType);
            if (hr < 0)
            {
                return hr;
            }

            buffers.Add(new D3D11VideoDecoderBufferDesc
            {
                BufferType = bufferType,
                BufferIndex = 0,
                DataOffset = 0,
                DataSize = (uint)data.Length,
            });

            return 0;
        }

        /// <summary>
        /// Gets the surface pool for this decoder.
        /// </summary>
        public D3D11SurfacePool GetSurfacePool() => surfacePool;

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            surfacePool?.Dispose();
            videoDecoder.Release();
            videoContext.Release();
            videoDevice.Release();
        }
    }
}
