// D3D11SurfaceAdapter - Zero-copy surface sharing for Windows D3D11 hardware decoding
// Enables decoded video frames to be rendered without CPU readback

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MediaKernel.Core.Hardware.D3D11
{
    /// <summary>
    /// Adapter for zero-copy sharing of D3D11 video decoder surfaces.
    /// Supports both same-device sharing (fastest) and cross-device via DXGI shared handles.
    /// </summary>
    public sealed unsafe class D3D11SurfaceAdapter : IHardwareSurfaceAdapter
    {
        private nint d3dDevice;
        private nint d3dContext;
        private nint dxgiDevice;
        private bool isInitialized;
        private bool isDisposed;

        // DXGI IIDs
        private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid IidDxgiResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
        private static readonly Guid IidDxgiResource1 = new("30961379-4609-4a41-998e-54fe567ee0c1");

        /// <inheritdoc/>
        public GpuPlatform Platform => GpuPlatform.D3D11;

        /// <inheritdoc/>
        public bool IsInitialized => isInitialized && !isDisposed;

        /// <summary>
        /// Initializes the adapter with an existing D3D11 device.
        /// </summary>
        /// <param name="device">ID3D11Device handle from the decoder factory.</param>
        /// <param name="context">ID3D11DeviceContext handle.</param>
        /// <returns>True if initialization succeeded.</returns>
        public bool Initialize(nint device, nint context)
        {
            if (device == 0 || context == 0)
                return false;

            d3dDevice = device;
            d3dContext = context;

            // Query for IDXGIDevice to enable cross-device sharing
            nint dxgiDevicePtr = 0;
            Guid iidDxgiDevice = IidDxgiDevice;
            int hr = QueryInterface(device, &iidDxgiDevice, &dxgiDevicePtr);
            if (hr >= 0)
            {
                dxgiDevice = dxgiDevicePtr;
            }

            isInitialized = true;
            return true;
        }

        /// <inheritdoc/>
        public bool TryGetSharedSurface(in HardwareDecodedFrame frame, out GpuSharedSurface sharedSurface)
        {
            sharedSurface = default;

            if (!isInitialized || isDisposed)
                return false;

            if (frame.SurfaceHandle == 0)
                return false;

            // For same-device sharing, we can use the texture directly
            // The TextureHandle points to ID3D11Texture2D
            // ArraySliceIndex identifies which slice in the texture array

            // For cross-device/cross-process sharing, we'd create a DXGI shared handle
            // But same-device is the most common case for our rendering scenario

            sharedSurface = new GpuSharedSurface(
                sharedHandle: 0, // Same-device doesn't need a shared handle
                width: frame.Width,
                height: frame.Height,
                format: frame.Format,
                platform: GpuPlatform.D3D11,
                textureHandle: frame.SurfaceHandle,
                arraySlice: frame.ArraySliceIndex,
                ownerAdapter: this,
                surfaceIndex: frame.ArraySliceIndex);

            return true;
        }

        /// <summary>
        /// Creates a cross-device shared handle for a texture.
        /// This is needed when the rendering device differs from the decoder device.
        /// </summary>
        /// <param name="textureHandle">ID3D11Texture2D handle.</param>
        /// <param name="sharedHandle">Output: DXGI shared handle.</param>
        /// <returns>True if the shared handle was created.</returns>
        public bool TryCreateSharedHandle(nint textureHandle, out nint sharedHandle)
        {
            sharedHandle = 0;

            if (textureHandle == 0)
                return false;

            // Query for IDXGIResource1 to get NT handle sharing
            nint dxgiResource1 = 0;
            Guid iid = IidDxgiResource1;
            int hr = QueryInterface(textureHandle, &iid, &dxgiResource1);

            if (hr >= 0 && dxgiResource1 != 0)
            {
                // Use CreateSharedHandle for NT handle (preferred for Windows 10+)
                hr = CreateSharedHandle(dxgiResource1, null, 0x80000000 /* GENERIC_ALL */, null, out sharedHandle);
                Release(dxgiResource1);

                if (hr >= 0)
                    return true;
            }

            // Fallback: Query for IDXGIResource for legacy shared handle
            nint dxgiResource = 0;
            iid = IidDxgiResource;
            hr = QueryInterface(textureHandle, &iid, &dxgiResource);

            if (hr >= 0 && dxgiResource != 0)
            {
                hr = GetSharedHandle(dxgiResource, out sharedHandle);
                Release(dxgiResource);

                return hr >= 0;
            }

            return false;
        }

        /// <summary>
        /// Opens a shared surface from another device by shared handle.
        /// </summary>
        /// <param name="sharedHandle">The DXGI shared handle.</param>
        /// <param name="textureHandle">Output: ID3D11Texture2D handle.</param>
        /// <returns>True if the texture was opened.</returns>
        public bool TryOpenSharedSurface(nint sharedHandle, out nint textureHandle)
        {
            textureHandle = 0;

            if (!isInitialized || d3dDevice == 0 || sharedHandle == 0)
                return false;

            Guid iidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D

            nint result = 0;
            int hr = OpenSharedResource(d3dDevice, sharedHandle, &iidTexture2D, &result);
            textureHandle = result;
            return hr >= 0;
        }

        /// <inheritdoc/>
        public void ReleaseSurface(int surfaceIndex)
        {
            // For D3D11, the surface pool manages surface lifetime
            // This is called when the GpuSharedSurface is disposed
            // The actual surface is still owned by the decoder's surface pool
        }

        /// <inheritdoc/>
        public nint GetRenderingContext() => d3dContext;

        /// <inheritdoc/>
        public nint GetDeviceHandle() => d3dDevice;

        /// <summary>
        /// Copies a decoder surface to a shared staging texture.
        /// Useful when direct sharing isn't possible.
        /// </summary>
        /// <param name="sourceTexture">Source decoder texture.</param>
        /// <param name="sourceSlice">Array slice in the source texture.</param>
        /// <param name="destTexture">Destination texture for rendering.</param>
        public void CopySubresource(nint sourceTexture, int sourceSlice, nint destTexture)
        {
            if (!isInitialized || d3dContext == 0)
                return;

            // Calculate subresource index: arraySlice * mipLevels + mipSlice
            // For video textures, mipLevels is typically 1
            uint srcSubresource = (uint)sourceSlice;
            uint dstSubresource = 0;

            CopySubresourceRegion(d3dContext, destTexture, dstSubresource, 0, 0, 0,
                sourceTexture, srcSubresource, null);
        }

        /// <summary>
        /// Creates a shared texture that can be used for cross-device rendering.
        /// </summary>
        public nint CreateSharedTexture(int width, int height, DxgiFormat format)
        {
            if (!isInitialized || d3dDevice == 0)
                return 0;

            D3D11Texture2DDesc desc = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDesc = DxgiSampleDesc.Default,
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.ShaderResource | D3D11BindFlags.RenderTarget,
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = D3D11ResourceMiscFlags.Shared | D3D11ResourceMiscFlags.SharedNtHandle,
            };

            nint texture = 0;
            int hr = CreateTexture2D(d3dDevice, &desc, null, &texture);

            return hr >= 0 ? texture : 0;
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;

            if (dxgiDevice != 0)
            {
                Release(dxgiDevice);
                dxgiDevice = 0;
            }

            // Don't release d3dDevice/d3dContext - they're owned by the decoder factory
            d3dDevice = 0;
            d3dContext = 0;
            isInitialized = false;
        }

        #region COM Interop Helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int QueryInterface(nint comObject, Guid* riid, nint* ppvObject)
        {
            if (comObject == 0) return D3D11Interop.EFail;
            nint* vtable = *(nint**)comObject;
            var func = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
            return func(comObject, riid, ppvObject);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Release(nint comObject)
        {
            if (comObject == 0) return 0;
            nint* vtable = *(nint**)comObject;
            var func = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            return func(comObject);
        }

        // IDXGIResource::GetSharedHandle (vtable index 8)
        private static int GetSharedHandle(nint dxgiResource, out nint sharedHandle)
        {
            sharedHandle = 0;
            if (dxgiResource == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)dxgiResource;
            var func = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[8];
            fixed (nint* pHandle = &sharedHandle)
            {
                return func(dxgiResource, pHandle);
            }
        }

        // IDXGIResource1::CreateSharedHandle (vtable index 9)
        private static int CreateSharedHandle(nint dxgiResource1, void* pAttributes, uint dwAccess, char* lpName, out nint sharedHandle)
        {
            sharedHandle = 0;
            if (dxgiResource1 == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)dxgiResource1;
            var func = (delegate* unmanaged[Stdcall]<nint, void*, uint, char*, nint*, int>)vtable[9];
            fixed (nint* pHandle = &sharedHandle)
            {
                return func(dxgiResource1, pAttributes, dwAccess, lpName, pHandle);
            }
        }

        // ID3D11Device::OpenSharedResource (vtable index 28)
        private static int OpenSharedResource(nint device, nint hResource, Guid* returnedInterface, nint* ppResource)
        {
            if (device == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)device;
            var func = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[28];
            return func(device, hResource, returnedInterface, ppResource);
        }

        // ID3D11Device::CreateTexture2D (vtable index 5)
        private static int CreateTexture2D(nint device, D3D11Texture2DDesc* pDesc, void* pInitialData, nint* ppTexture2D)
        {
            if (device == 0) return D3D11Interop.EFail;

            nint* vtable = *(nint**)device;
            var func = (delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDesc*, void*, nint*, int>)vtable[5];
            return func(device, pDesc, pInitialData, ppTexture2D);
        }

        // ID3D11DeviceContext::CopySubresourceRegion (vtable index 46)
        private static void CopySubresourceRegion(nint context, nint dstResource, uint dstSubresource,
            uint dstX, uint dstY, uint dstZ, nint srcResource, uint srcSubresource, void* pSrcBox)
        {
            if (context == 0) return;

            nint* vtable = *(nint**)context;
            var func = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, void*, void>)vtable[46];
            func(context, dstResource, dstSubresource, dstX, dstY, dstZ, srcResource, srcSubresource, pSrcBox);
        }

        #endregion
    }
}
