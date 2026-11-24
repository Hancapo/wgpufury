using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WGpuFury;

/// <summary>An asynchronous wgpu operation completed with a non-success status.</summary>
public sealed class WGPUException(string operation, string status, string message)
    : Exception(message.Length == 0 ? $"{operation} failed: {status}" : $"{operation} failed: {status}: {message}")
{
    /// <summary>The operation that failed, e.g. <c>wgpuInstanceRequestAdapter</c>.</summary>
    public string Operation { get; } = operation;

    /// <summary>The status value reported to the callback.</summary>
    public string Status { get; } = status;
}

// Blocking wrappers over the callback-based entry points. Adapter and device requests use
// WGPUCallbackMode.AllowProcessEvents and call wgpuInstanceProcessEvents until the callback has run (they
// complete immediately in wgpu-native). Buffer maps use AllowSpontaneous, so the callback fires inside
// wgpuDevicePoll, which blocks until the GPU work the map waits on is done; this needs only the device, not
// the instance. The callbacks are static [UnmanagedCallersOnly] methods writing into a stack-allocated result:
// no delegates, no GC handles.

public unsafe partial struct WGPUInstance
{
    private struct AdapterResult
    {
        public int Done;
        public WGPURequestAdapterStatus Status;
        public WGPUAdapter Adapter;
        public string? Message;
    }

    /// <summary>
    /// Requests an adapter and waits for it (<c>wgpuInstanceRequestAdapter</c> + <c>wgpuInstanceProcessEvents</c>).
    /// </summary>
    /// <param name="options">Adapter options, or null for the defaults.</param>
    /// <exception cref="WGPUException">No adapter could be provided.</exception>
    public WGPUAdapter RequestAdapterSync(WGPURequestAdapterOptions* options = null)
    {
        AdapterResult result = default;
        var callbackInfo = new WGPURequestAdapterCallbackInfo
        {
            Mode = WGPUCallbackMode.AllowProcessEvents,
            Callback = &OnAdapter,
            Userdata1 = Unsafe.AsPointer(ref result),
        };
        WGPU.wgpuInstanceRequestAdapter(this, options, callbackInfo);
        while (Volatile.Read(ref result.Done) == 0)
        {
            ProcessEvents();
            Thread.Yield();
        }

        return result.Status == WGPURequestAdapterStatus.Success
            ? result.Adapter
            : throw new WGPUException(nameof(WGPU.wgpuInstanceRequestAdapter), result.Status.ToString(), result.Message ?? "");
    }

    /// <inheritdoc cref="RequestAdapterSync(WGPURequestAdapterOptions*)"/>
    public WGPUAdapter RequestAdapterSync(in WGPURequestAdapterOptions options)
    {
        fixed (WGPURequestAdapterOptions* p = &options)
        {
            return RequestAdapterSync(p);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAdapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref AdapterResult result = ref Unsafe.AsRef<AdapterResult>(userdata1);
        result.Status = status;
        result.Adapter = adapter;
        result.Message = message.ToString();
        Volatile.Write(ref result.Done, 1);
    }
}

public unsafe partial struct WGPUAdapter
{
    private struct DeviceResult
    {
        public int Done;
        public WGPURequestDeviceStatus Status;
        public WGPUDevice Device;
        public string? Message;
    }

    /// <summary>
    /// Requests a device and waits for it (<c>wgpuAdapterRequestDevice</c>).
    /// </summary>
    /// <param name="instance">The instance the adapter came from; polled until the callback runs.</param>
    /// <param name="descriptor">Device descriptor, or null for the defaults.</param>
    /// <exception cref="WGPUException">The device could not be created.</exception>
    public WGPUDevice RequestDeviceSync(WGPUInstance instance, WGPUDeviceDescriptor* descriptor = null)
    {
        DeviceResult result = default;
        var callbackInfo = new WGPURequestDeviceCallbackInfo
        {
            Mode = WGPUCallbackMode.AllowProcessEvents,
            Callback = &OnDevice,
            Userdata1 = Unsafe.AsPointer(ref result),
        };
        WGPU.wgpuAdapterRequestDevice(this, descriptor, callbackInfo);
        while (Volatile.Read(ref result.Done) == 0)
        {
            instance.ProcessEvents();
            Thread.Yield();
        }

        return result.Status == WGPURequestDeviceStatus.Success
            ? result.Device
            : throw new WGPUException(nameof(WGPU.wgpuAdapterRequestDevice), result.Status.ToString(), result.Message ?? "");
    }

    /// <inheritdoc cref="RequestDeviceSync(WGPUInstance, WGPUDeviceDescriptor*)"/>
    public WGPUDevice RequestDeviceSync(WGPUInstance instance, in WGPUDeviceDescriptor descriptor)
    {
        fixed (WGPUDeviceDescriptor* p = &descriptor)
        {
            return RequestDeviceSync(instance, p);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDevice(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref DeviceResult result = ref Unsafe.AsRef<DeviceResult>(userdata1);
        result.Status = status;
        result.Device = device;
        result.Message = message.ToString();
        Volatile.Write(ref result.Done, 1);
    }
}

public unsafe partial struct WGPUBuffer
{
    private struct MapResult
    {
        public int Done;
        public WGPUMapAsyncStatus Status;
        public string? Message;
    }

    /// <summary>
    /// Maps the buffer and blocks until the mapping is ready (<c>wgpuBufferMapAsync</c> + <c>wgpuDevicePoll</c>).
    /// </summary>
    /// <param name="device">The device that owns the buffer; it is polled until the callback runs.</param>
    /// <param name="mode">Read or write.</param>
    /// <param name="offset">Byte offset; a multiple of 8.</param>
    /// <param name="size">Byte size; a multiple of 4, or <see cref="WGPU.WGPU_WHOLE_MAP_SIZE"/>.</param>
    /// <exception cref="WGPUException">The mapping failed.</exception>
    public void MapSync(WGPUDevice device, WGPUMapMode mode, nuint offset, nuint size)
    {
        MapResult result = default;
        var callbackInfo = new WGPUBufferMapCallbackInfo
        {
            Mode = WGPUCallbackMode.AllowSpontaneous,
            Callback = &OnMapped,
            Userdata1 = Unsafe.AsPointer(ref result),
        };
        WGPU.wgpuBufferMapAsync(this, mode, offset, size, callbackInfo);
        while (Volatile.Read(ref result.Done) == 0)
        {
            WGPU.wgpuDevicePoll(device, true, null, 0);
        }

        if (result.Status != WGPUMapAsyncStatus.Success)
        {
            throw new WGPUException(nameof(WGPU.wgpuBufferMapAsync), result.Status.ToString(), result.Message ?? "");
        }
    }

    /// <summary>A writable view of the mapped range (<c>wgpuBufferGetMappedRange</c>), in elements of <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The range is not mapped.</exception>
    public Span<T> GetMappedRange<T>(nuint byteOffset, int count) where T : unmanaged
    {
        void* p = WGPU.wgpuBufferGetMappedRange(this, byteOffset, (nuint)count * (nuint)sizeof(T));
        return p != null ? new Span<T>(p, count) : throw new InvalidOperationException("The buffer range is not mapped for writing.");
    }

    /// <summary>A read-only view of the mapped range (<c>wgpuBufferGetConstMappedRange</c>), in elements of <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The range is not mapped.</exception>
    public ReadOnlySpan<T> GetConstMappedRange<T>(nuint byteOffset, int count) where T : unmanaged
    {
        void* p = WGPU.wgpuBufferGetConstMappedRange(this, byteOffset, (nuint)count * (nuint)sizeof(T));
        return p != null ? new ReadOnlySpan<T>(p, count) : throw new InvalidOperationException("The buffer range is not mapped.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMapped(WGPUMapAsyncStatus status, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref MapResult result = ref Unsafe.AsRef<MapResult>(userdata1);
        result.Status = status;
        result.Message = message.ToString();
        Volatile.Write(ref result.Done, 1);
    }
}
