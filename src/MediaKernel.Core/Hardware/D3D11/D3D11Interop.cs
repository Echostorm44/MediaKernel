using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.D3D11
{
    /// <summary>
    /// P/Invoke declarations for D3D11 video decoding COM interfaces.
    /// Uses raw vtable calls for maximum performance and AOT compatibility.
    /// </summary>
    public static unsafe partial class D3D11Interop
    {
        private const string D3D11Lib = "d3d11.dll";
        private const string DxgiLib = "dxgi.dll";

        // HRESULT constants
        public const int SOk = 0;
        public const int SFalse = 1;
        public const int EFail = unchecked((int)0x80004005);
        public const int EInvalidArg = unchecked((int)0x80070057);
        public const int ENotImpl = unchecked((int)0x80004001);
        public const int EOutOfMemory = unchecked((int)0x8007000E);

        // D3D11 feature levels
        public const uint FeatureLevel_11_1 = 0xb100;
        public const uint FeatureLevel_11_0 = 0xb000;
        public const uint FeatureLevel_10_1 = 0xa100;
        public const uint FeatureLevel_10_0 = 0xa000;

        // D3D11 driver types
        public const uint DriverTypeHardware = 1;
        public const uint DriverTypeWarp = 5;

        [LibraryImport(D3D11Lib)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        public static partial int D3D11CreateDevice(
            nint pAdapter,
            uint driverType,
            nint software,
            uint flags,
            uint* pFeatureLevels,
            uint featureLevels,
            uint sdkVersion,
            nint* ppDevice,
            uint* pFeatureLevel,
            nint* ppImmediateContext);

        [LibraryImport(DxgiLib)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        public static partial int CreateDXGIFactory1(
            Guid* riid,
            nint* ppFactory);

        /// <summary>
        /// IID for IDXGIFactory1.
        /// </summary>
        public static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

        /// <summary>
        /// IID for ID3D11VideoDevice.
        /// </summary>
        public static readonly Guid IidD3D11VideoDevice = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");

        /// <summary>
        /// IID for ID3D11VideoContext.
        /// </summary>
        public static readonly Guid IidD3D11VideoContext = new("61f21c45-3c0e-4a74-9cea-67100d9ad5e4");
    }

    /// <summary>
    /// Wrapper for ID3D11VideoDevice COM interface.
    /// </summary>
    public readonly unsafe struct D3D11VideoDeviceHandle
    {
        private readonly nint handle;

        public D3D11VideoDeviceHandle(nint ptr) => handle = ptr;

        public bool IsValid => handle != 0;
        public nint Handle => handle;

        // VTable indices for ID3D11VideoDevice (inherits from IUnknown)
        // IUnknown: 0=QueryInterface, 1=AddRef, 2=Release
        // ID3D11VideoDevice starts at index 3

        /// <summary>
        /// Creates a video decoder for the specified description.
        /// </summary>
        public int CreateVideoDecoder(
            D3D11VideoDecoderDesc* videoDesc,
            D3D11VideoDecoderConfig* config,
            nint* ppDecoder)
        {
            if (handle == 0) return D3D11Interop.EFail;

            // Get vtable pointer
            nint* vtable = *(nint**)handle;
            // CreateVideoDecoder is at index 3
            var func = (delegate* unmanaged[Stdcall]<nint, D3D11VideoDecoderDesc*, D3D11VideoDecoderConfig*, nint*, int>)vtable[3];
            return func(handle, videoDesc, config, ppDecoder);
        }

        /// <summary>
        /// Gets the number of decoder profiles supported.
        /// </summary>
        public uint GetVideoDecoderProfileCount()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            // GetVideoDecoderProfileCount is at index 4
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[4];
            return func(handle);
        }

        /// <summary>
        /// Gets a decoder profile by index.
        /// </summary>
        public int GetVideoDecoderProfile(uint index, Guid* pDecoderProfile)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // GetVideoDecoderProfile is at index 5
            var func = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, int>)vtable[5];
            return func(handle, index, pDecoderProfile);
        }

        /// <summary>
        /// Checks if a format is supported for a decoder profile.
        /// </summary>
        public int CheckVideoDecoderFormat(Guid* pDecoderProfile, DxgiFormat format, int* pSupported)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // CheckVideoDecoderFormat is at index 6
            var func = (delegate* unmanaged[Stdcall]<nint, Guid*, DxgiFormat, int*, int>)vtable[6];
            return func(handle, pDecoderProfile, format, pSupported);
        }

        /// <summary>
        /// Gets the number of configs for a decoder description.
        /// </summary>
        public int GetVideoDecoderConfigCount(D3D11VideoDecoderDesc* pDesc, uint* pCount)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // GetVideoDecoderConfigCount is at index 7
            var func = (delegate* unmanaged[Stdcall]<nint, D3D11VideoDecoderDesc*, uint*, int>)vtable[7];
            return func(handle, pDesc, pCount);
        }

        /// <summary>
        /// Gets a decoder config by index.
        /// </summary>
        public int GetVideoDecoderConfig(D3D11VideoDecoderDesc* pDesc, uint index, D3D11VideoDecoderConfig* pConfig)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // GetVideoDecoderConfig is at index 8
            var func = (delegate* unmanaged[Stdcall]<nint, D3D11VideoDecoderDesc*, uint, D3D11VideoDecoderConfig*, int>)vtable[8];
            return func(handle, pDesc, index, pConfig);
        }

        /// <summary>
        /// Creates a video decoder output view.
        /// </summary>
        public int CreateVideoDecoderOutputView(
            nint pResource,
            D3D11VideoDecoderOutputViewDesc* pDesc,
            nint* ppView)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // CreateVideoDecoderOutputView is at index 10
            var func = (delegate* unmanaged[Stdcall]<nint, nint, D3D11VideoDecoderOutputViewDesc*, nint*, int>)vtable[10];
            return func(handle, pResource, pDesc, ppView);
        }

        /// <summary>
        /// Releases the COM interface.
        /// </summary>
        public uint Release()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            return func(handle);
        }
    }

    /// <summary>
    /// D3D11_VIDEO_DECODER_OUTPUT_VIEW_DESC structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11VideoDecoderOutputViewDesc
    {
        public Guid DecodeProfile;
        public D3D11VdovDimension ViewDimension;
        public D3D11Tex2DVdov Texture2D;
    }

    public enum D3D11VdovDimension : uint
    {
        Unknown = 0,
        Texture2D = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11Tex2DVdov
    {
        public uint ArraySlice;
    }

    /// <summary>
    /// Wrapper for ID3D11VideoContext COM interface.
    /// </summary>
    public readonly unsafe struct D3D11VideoContextHandle
    {
        private readonly nint handle;

        public D3D11VideoContextHandle(nint ptr) => handle = ptr;

        public bool IsValid => handle != 0;
        public nint Handle => handle;

        // VTable indices for ID3D11VideoContext
        // Inherits from ID3D11DeviceChild (IUnknown + 3 methods)
        // IUnknown: 0-2, ID3D11DeviceChild: 3-5
        // ID3D11VideoContext starts at index 6

        /// <summary>
        /// Gets a decoder buffer for writing.
        /// </summary>
        public int GetDecoderBuffer(
            nint pDecoder,
            D3D11VideoDecoderBufferType bufferType,
            uint* pBufferSize,
            void** ppBuffer)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // GetDecoderBuffer is at index 6
            var func = (delegate* unmanaged[Stdcall]<nint, nint, D3D11VideoDecoderBufferType, uint*, void**, int>)vtable[6];
            return func(handle, pDecoder, bufferType, pBufferSize, ppBuffer);
        }

        /// <summary>
        /// Releases a decoder buffer.
        /// </summary>
        public int ReleaseDecoderBuffer(nint pDecoder, D3D11VideoDecoderBufferType bufferType)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // ReleaseDecoderBuffer is at index 7
            var func = (delegate* unmanaged[Stdcall]<nint, nint, D3D11VideoDecoderBufferType, int>)vtable[7];
            return func(handle, pDecoder, bufferType);
        }

        /// <summary>
        /// Begins a decode frame operation.
        /// </summary>
        public int DecoderBeginFrame(nint pDecoder, nint pOutputView, uint contentKeySize, void* pContentKey)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // DecoderBeginFrame is at index 8
            var func = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void*, int>)vtable[8];
            return func(handle, pDecoder, pOutputView, contentKeySize, pContentKey);
        }

        /// <summary>
        /// Ends a decode frame operation.
        /// </summary>
        public int DecoderEndFrame(nint pDecoder)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // DecoderEndFrame is at index 9
            var func = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[9];
            return func(handle, pDecoder);
        }

        /// <summary>
        /// Submits decoder buffers for processing.
        /// </summary>
        public int SubmitDecoderBuffers(nint pDecoder, uint numBuffers, D3D11VideoDecoderBufferDesc* pBufferDesc)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // SubmitDecoderBuffers is at index 10
            var func = (delegate* unmanaged[Stdcall]<nint, nint, uint, D3D11VideoDecoderBufferDesc*, int>)vtable[10];
            return func(handle, pDecoder, numBuffers, pBufferDesc);
        }

        /// <summary>
        /// Releases the COM interface.
        /// </summary>
        public uint Release()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            return func(handle);
        }
    }

    /// <summary>
    /// Wrapper for ID3D11VideoDecoder COM interface.
    /// </summary>
    public readonly unsafe struct D3D11VideoDecoderHandle
    {
        private readonly nint handle;

        public D3D11VideoDecoderHandle(nint ptr) => handle = ptr;

        public bool IsValid => handle != 0;
        public nint Handle => handle;

        /// <summary>
        /// Gets the decoder creation parameters.
        /// </summary>
        public int GetCreationParameters(D3D11VideoDecoderDesc* pVideoDesc, D3D11VideoDecoderConfig* pConfig)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // GetCreationParameters is at index 6
            var func = (delegate* unmanaged[Stdcall]<nint, D3D11VideoDecoderDesc*, D3D11VideoDecoderConfig*, int>)vtable[6];
            return func(handle, pVideoDesc, pConfig);
        }

        public uint Release()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            return func(handle);
        }

        public uint AddRef()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[1];
            return func(handle);
        }
    }

    /// <summary>
    /// Wrapper for ID3D11Texture2D COM interface.
    /// </summary>
    public readonly unsafe struct D3D11Texture2DHandle
    {
        private readonly nint handle;

        public D3D11Texture2DHandle(nint ptr) => handle = ptr;

        public bool IsValid => handle != 0;
        public nint Handle => handle;

        public int GetDesc(D3D11Texture2DDesc* pDesc)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // GetDesc is at index 10 (after IUnknown + ID3D11DeviceChild + ID3D11Resource)
            var func = (delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDesc*, void>)vtable[10];
            func(handle, pDesc);
            return D3D11Interop.SOk;
        }

        public uint Release()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            return func(handle);
        }

        public uint AddRef()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[1];
            return func(handle);
        }
    }

    /// <summary>
    /// Wrapper for ID3D11Device COM interface.
    /// </summary>
    public readonly unsafe struct D3D11DeviceHandle
    {
        private readonly nint handle;

        public D3D11DeviceHandle(nint ptr) => handle = ptr;

        public bool IsValid => handle != 0;
        public nint Handle => handle;

        /// <summary>
        /// Creates a 2D texture.
        /// </summary>
        public int CreateTexture2D(D3D11Texture2DDesc* pDesc, void* pInitialData, nint* ppTexture2D)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            // CreateTexture2D is at index 5
            var func = (delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDesc*, void*, nint*, int>)vtable[5];
            return func(handle, pDesc, pInitialData, ppTexture2D);
        }

        /// <summary>
        /// Queries for a specific interface.
        /// </summary>
        public int QueryInterface(Guid* riid, nint* ppvObject)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
            return func(handle, riid, ppvObject);
        }

        public uint Release()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            return func(handle);
        }
    }

    /// <summary>
    /// Wrapper for ID3D11DeviceContext COM interface.
    /// </summary>
    public readonly unsafe struct D3D11DeviceContextHandle
    {
        private readonly nint handle;

        public D3D11DeviceContextHandle(nint ptr) => handle = ptr;

        public bool IsValid => handle != 0;
        public nint Handle => handle;

        /// <summary>
        /// Queries for a specific interface.
        /// </summary>
        public int QueryInterface(Guid* riid, nint* ppvObject)
        {
            if (handle == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
            return func(handle, riid, ppvObject);
        }

        public uint Release()
        {
            if (handle == 0) return 0;

            nint* vtable = *(nint**)handle;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            return func(handle);
        }
    }
}
