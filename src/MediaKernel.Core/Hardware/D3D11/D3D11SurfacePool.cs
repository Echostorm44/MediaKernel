using System;
using System.Collections.Generic;
using System.Threading;

namespace MediaKernel.Core.Hardware.D3D11
{
    /// <summary>
    /// Represents a single surface in the D3D11 decoder surface pool.
    /// Reference-counted for proper resource management.
    /// </summary>
    public sealed unsafe class D3D11DecoderSurface : IDisposable
    {
        private readonly D3D11SurfacePool ownerPool;
        private readonly int surfaceIndex;
        private int referenceCount;
        private bool isDisposed;

        internal D3D11DecoderSurface(
            D3D11SurfacePool owner,
            int index,
            nint textureHandle,
            nint outputViewHandle,
            uint width,
            uint height,
            DxgiFormat format)
        {
            ownerPool = owner;
            surfaceIndex = index;
            TextureHandle = textureHandle;
            OutputViewHandle = outputViewHandle;
            Width = width;
            Height = height;
            Format = format;
            referenceCount = 0;
        }

        /// <summary>
        /// Handle to the ID3D11Texture2D.
        /// </summary>
        public nint TextureHandle { get; }

        /// <summary>
        /// Handle to the ID3D11VideoDecoderOutputView.
        /// </summary>
        public nint OutputViewHandle { get; }

        /// <summary>
        /// Surface width in pixels.
        /// </summary>
        public uint Width { get; }

        /// <summary>
        /// Surface height in pixels.
        /// </summary>
        public uint Height { get; }

        /// <summary>
        /// Surface format.
        /// </summary>
        public DxgiFormat Format { get; }

        /// <summary>
        /// Index within the texture array.
        /// </summary>
        public int Index => surfaceIndex;

        /// <summary>
        /// Whether this surface is currently in use.
        /// </summary>
        public bool IsInUse => Volatile.Read(ref referenceCount) > 0;

        /// <summary>
        /// Acquires a reference to this surface.
        /// </summary>
        internal void AddReference()
        {
            Interlocked.Increment(ref referenceCount);
        }

        /// <summary>
        /// Releases a reference to this surface.
        /// When the last reference is released, the surface is returned to the pool.
        /// </summary>
        public void Release()
        {
            int newCount = Interlocked.Decrement(ref referenceCount);
            if (newCount == 0)
            {
                ownerPool.ReturnSurface(this);
            }
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            // Release COM objects
            if (OutputViewHandle != 0)
            {
                var handle = new D3D11VideoDecoderOutputViewHandle(OutputViewHandle);
                handle.Release();
            }
        }
    }

    /// <summary>
    /// Wrapper for ID3D11VideoDecoderOutputView.
    /// </summary>
    public readonly unsafe struct D3D11VideoDecoderOutputViewHandle
    {
        private readonly nint handle;

        public D3D11VideoDecoderOutputViewHandle(nint ptr) => handle = ptr;

        public bool IsValid => handle != 0;
        public nint Handle => handle;

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
    /// Manages a pool of D3D11 textures for video decoding.
    /// Uses a texture array with individual output views per slice.
    /// </summary>
    public sealed unsafe class D3D11SurfacePool : IDisposable
    {
        private readonly D3D11DeviceHandle deviceHandle;
        private readonly D3D11VideoDeviceHandle videoDeviceHandle;
        private readonly Guid decoderProfileGuid;
        private readonly uint width;
        private readonly uint height;
        private readonly DxgiFormat format;

        private D3D11Texture2DHandle textureArrayHandle;
        private readonly D3D11DecoderSurface[] surfaces;
        private readonly Stack<int> availableIndices;
        private readonly SemaphoreSlim poolSemaphore;
        private readonly object poolLock = new();
        private bool isDisposed;

        public D3D11SurfacePool(
            D3D11DeviceHandle device,
            D3D11VideoDeviceHandle videoDevice,
            Guid decoderProfile,
            uint surfaceWidth,
            uint surfaceHeight,
            DxgiFormat surfaceFormat,
            int surfaceCount)
        {
            deviceHandle = device;
            videoDeviceHandle = videoDevice;
            decoderProfileGuid = decoderProfile;
            width = surfaceWidth;
            height = surfaceHeight;
            format = surfaceFormat;

            surfaces = new D3D11DecoderSurface[surfaceCount];
            availableIndices = new Stack<int>(surfaceCount);
            poolSemaphore = new SemaphoreSlim(surfaceCount, surfaceCount);
        }

        /// <summary>
        /// Number of surfaces in the pool.
        /// </summary>
        public int SurfaceCount => surfaces.Length;

        /// <summary>
        /// Number of surfaces currently available.
        /// </summary>
        public int AvailableCount => poolSemaphore.CurrentCount;

        /// <summary>
        /// Surface width.
        /// </summary>
        public uint Width => width;

        /// <summary>
        /// Surface height.
        /// </summary>
        public uint Height => height;

        /// <summary>
        /// Surface format.
        /// </summary>
        public HardwarePixelFormat Format => FormatConverter.ToHardwareFormat(format);

        /// <summary>
        /// Initializes the surface pool by creating the texture array and output views.
        /// </summary>
        public bool Initialize()
        {
            // Create texture array
            var textureDesc = new D3D11Texture2DDesc
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = (uint)surfaces.Length,
                Format = format,
                SampleDesc = DxgiSampleDesc.Default,
                Usage = D3D11Usage.Default,
                BindFlags = D3D11BindFlags.Decoder,
                CpuAccessFlags = D3D11CpuAccessFlags.None,
                MiscFlags = 0,
            };

            nint texturePtr;
            int hr = deviceHandle.CreateTexture2D(&textureDesc, null, &texturePtr);
            if (hr < 0)
            {
                return false;
            }

            textureArrayHandle = new D3D11Texture2DHandle(texturePtr);

            // Create output views for each array slice
            for (int i = 0; i < surfaces.Length; i++)
            {
                var viewDesc = new D3D11VideoDecoderOutputViewDesc
                {
                    DecodeProfile = decoderProfileGuid,
                    ViewDimension = D3D11VdovDimension.Texture2D,
                    Texture2D = new D3D11Tex2DVdov { ArraySlice = (uint)i },
                };

                nint outputViewPtr;
                hr = videoDeviceHandle.CreateVideoDecoderOutputView(texturePtr, &viewDesc, &outputViewPtr);
                if (hr < 0)
                {
                    // Clean up and fail
                    Dispose();
                    return false;
                }

                surfaces[i] = new D3D11DecoderSurface(
                    this,
                    i,
                    texturePtr,
                    outputViewPtr,
                    width,
                    height,
                    format);

                availableIndices.Push(i);
            }

            return true;
        }

        /// <summary>
        /// Acquires a surface from the pool.
        /// Blocks if no surfaces are available.
        /// </summary>
        public D3D11DecoderSurface AcquireSurface(CancellationToken cancellation = default)
        {
            poolSemaphore.Wait(cancellation);

            lock (poolLock)
            {
                if (availableIndices.Count == 0)
                {
                    throw new InvalidOperationException("Surface pool is exhausted despite semaphore.");
                }

                int index = availableIndices.Pop();
                var surface = surfaces[index];
                surface.AddReference();
                return surface;
            }
        }

        /// <summary>
        /// Tries to acquire a surface without blocking.
        /// </summary>
        public bool TryAcquireSurface(out D3D11DecoderSurface? surface)
        {
            surface = null;

            if (!poolSemaphore.Wait(0))
            {
                return false;
            }

            lock (poolLock)
            {
                if (availableIndices.Count == 0)
                {
                    poolSemaphore.Release();
                    return false;
                }

                int index = availableIndices.Pop();
                surface = surfaces[index];
                surface.AddReference();
                return true;
            }
        }

        /// <summary>
        /// Returns a surface to the pool.
        /// </summary>
        internal void ReturnSurface(D3D11DecoderSurface surface)
        {
            lock (poolLock)
            {
                availableIndices.Push(surface.Index);
            }

            poolSemaphore.Release();
        }

        /// <summary>
        /// Gets a surface by index without acquiring it.
        /// </summary>
        public D3D11DecoderSurface GetSurfaceByIndex(int index)
        {
            if (index < 0 || index >= surfaces.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            return surfaces[index];
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            // Dispose all surfaces
            foreach (var surface in surfaces)
            {
                surface?.Dispose();
            }

            // Release texture array
            textureArrayHandle.Release();

            poolSemaphore.Dispose();
        }
    }
}
