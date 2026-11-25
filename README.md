<img src="assets/icon.svg" width="64" height="64" alt="">

# WGpuFury

.NET 10 bindings for [wgpu](https://github.com/gfx-rs/wgpu) 30, made for Native AOT.

WGpuFury covers the whole C API of [wgpu-native](https://github.com/gfx-rs/wgpu-native), that is, the standard
[`webgpu.h`](https://github.com/webgpu-native/webgpu-headers) and the `wgpu.h` extensions. The bindings are
generated from the headers, so C examples and the `webgpu.h` documentation translate line by line.

- **Native AOT and trimming, no warnings.** Every call is a blittable P/Invoke: no reflection, no delegate
  marshalling, no GC handles. wgpu-native can be linked statically into a single-file executable.
- **The whole API:** 262 functions, 162 structs, 93 enums and 26 handle types, with the header's documentation
  as IntelliSense.
- **Mistakes throw instead of killing the process.** Plain wgpu-native aborts on a null handle or a bad enum
  value, and on several runtime errors. Here, mistakes throw .NET exceptions and runtime errors reach the
  error callbacks. A lost GPU (a driver crash, a TDR) fires the device-lost callback, with the reason.
- **wgpu 30.0.2,** even though wgpu-native hasn't released wgpu 30 yet. It is built from source, with fixes
  (see [Maintaining](docs/MAINTAINING.md)).

## Getting started

WGpuFury isn't on NuGet yet. Build the package from this repository; [Building](docs/MAINTAINING.md#building)
lists what that needs (git, rustup, LLVM, and on Windows the MSVC build tools):

```sh
dotnet run eng/build-native.cs       # build wgpu-native for this machine (a few minutes, once)
dotnet pack src/WGpuFury -c Release -o path/to/packages
```

That package contains wgpu-native for the platforms you built. The
[CI runs](https://github.com/Hancapo/wgpufury/actions/workflows/ci.yml) produce one for all seven, as their
`packages` artifact. Reference it from a project with unsafe code enabled:

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <RestoreAdditionalProjectSources>path/to/packages</RestoreAdditionalProjectSources>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="WGpuFury" Version="30.0.2" />
</ItemGroup>
```

## Example

Doubling numbers on the GPU:

```csharp
using WGpuFury;

unsafe
{
    using WGPUInstance instance = WGPU.wgpuCreateInstance(null);
    using WGPUAdapter adapter = instance.RequestAdapterSync();
    using WGPUDevice device = adapter.RequestDeviceSync(instance);
    using WGPUQueue queue = device.GetQueue();

    // The WGSL source is chained onto the descriptor, as in C.
    var wgsl = new WGPUShaderSourceWGSL { Code = WGPUStringView.FromPinned("""
        @group(0) @binding(0) var<storage, read_write> data: array<f32>;
        @compute @workgroup_size(64)
        fn main(@builtin(global_invocation_id) id: vec3<u32>) { data[id.x] *= 2.0; }
        """u8) };
    using WGPUShaderModule module = device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });
    using WGPUComputePipeline pipeline = device.CreateComputePipeline(new WGPUComputePipelineDescriptor
    {
        Compute = new WGPUComputeState { Module = module, EntryPoint = WGPUStringView.FromPinned("main"u8) },
    });

    float[] values = [.. Enumerable.Range(0, 64).Select(i => (float)i)];
    const ulong size = 64 * sizeof(float);
    using WGPUBuffer data = device.CreateBuffer(new WGPUBufferDescriptor { Size = size, Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst });
    using WGPUBuffer readback = device.CreateBuffer(new WGPUBufferDescriptor { Size = size, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
    queue.WriteBuffer(data, 0, values.AsSpan());

    using WGPUBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
    var entry = new WGPUBindGroupEntry { Binding = 0, Buffer = data, Size = size };
    using WGPUBindGroup bindGroup = device.CreateBindGroup(new WGPUBindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry });

    using WGPUCommandEncoder encoder = device.CreateCommandEncoder();
    using (WGPUComputePassEncoder pass = encoder.BeginComputePass())
    {
        pass.SetPipeline(pipeline);
        pass.SetBindGroup(0, bindGroup, []);
        pass.DispatchWorkgroups(1, 1, 1);
        pass.End();
    }

    encoder.CopyBufferToBuffer(data, 0, readback, 0, size);
    using WGPUCommandBuffer commands = encoder.Finish();
    queue.Submit([commands]);

    readback.MapSync(device, WGPUMapMode.Read, 0, (nuint)size);
    Console.WriteLine(string.Join(", ", readback.GetConstMappedRange<float>(0, 8).ToArray())); // 0, 2, 4, 6, ...
    readback.Unmap();
}
```

More complete programs: [`samples/Compute`](samples/Compute/Program.cs) (headless, any OS) and
[`samples/Triangle`](samples/Triangle/Program.cs) (a spinning triangle in a Win32 window).

## How the C API looks in C#

| C | C# |
|---|---|
| `WGPUDevice` | `readonly struct WGPUDevice` holding the pointer. `Dispose()` calls `wgpuDeviceRelease`; handles are values, so dispose each reference once, not every copy. |
| `wgpuDeviceCreateBuffer(device, &desc)` | `device.CreateBuffer(desc)` (an `in` overload) or `device.CreateBuffer(&desc)`; the raw `WGPU.wgpuDeviceCreateBuffer(device, &desc)` is there too |
| `struct WGPUBufferDescriptor` | a struct with the same fields in PascalCase (`nextInChain` → `NextInChain`) |
| `WGPU_BUFFER_DESCRIPTOR_INIT` | `new WGPUBufferDescriptor()`: the parameterless constructor applies the header's defaults. `default(T)` is zero-filled, as in C. |
| `WGPUTextureFormat_RGBA8Unorm` | `WGPUTextureFormat.RGBA8Unorm` (a leading digit gets `_`: `WGPUTextureDimension._2D`) |
| flag constants (`WGPUBufferUsage_Storage`) | `[Flags] enum WGPUBufferUsage : ulong` |
| `wgpu.h` additions to an enum (`WGPUSType_InstanceExtras`) | members of that enum (`WGPUSType.InstanceExtras`) |
| `WGPUStringView` | `WGPUStringView`: `FromPinned("main"u8)`, `ToString()`, and `string` / UTF-8 span overloads |
| `size_t count, T const* items` | an extra overload taking a span |
| callbacks | `delegate* unmanaged[Cdecl]<…>` fields, pointed at static `[UnmanagedCallersOnly]` methods |

Hand-written helpers on top:

- `instance.RequestAdapterSync()`, `adapter.RequestDeviceSync(instance)` and `buffer.MapSync(device, …)` wait for
  their callbacks, without delegates or GC handles.
- `buffer.GetMappedRange<T>()` and `GetConstMappedRange<T>()` return typed spans over the mapping.
- `WGPU.SetLogger(WGPULogLevel.Warn, (level, message) => …)` receives wgpu's log.
- `instance.GetLiveObjects()` counts the objects not yet released, for example "2 buffers, 1 texture views".
  Handles have no finalizer, so a missed `Dispose` leaks silently; call it after releasing everything else and
  expect `Total == 0`.
- `WGPU.NativeVersion` is the version of the loaded wgpu-native, and `WGPU.BindingsVersion` the version the
  bindings were generated for.

### Callbacks and futures

Every asynchronous call returns a `WGPUFuture`, and its callback fires according to its mode:

| Mode | The callback fires… |
|---|---|
| `WaitAnyOnly` | only in `instance.WaitAny(...)` for that future |
| `AllowProcessEvents` | in `WaitAny`, or in `instance.ProcessEvents()` once the work is done |
| `AllowSpontaneous` | as soon as the work is done, possibly inside another wgpu call or on another thread |

A `WaitAny` with a timeout blocks on the GPU instead of spinning. It needs the instance to be created with
`WGPUInstanceFeatureName.TimedWaitAny`. Releasing the instance fires its pending callbacks with
`CallbackCancelled`.

## Errors

**Mistakes in your calls throw.** The methods on handles check their arguments, where wgpu-native would abort the
process:

| Mistake | Exception |
|---|---|
| calling a method on a null handle (`default(WGPUDevice).GetQueue()`) | `InvalidOperationException` |
| a null handle or descriptor where `webgpu.h` requires one (`device.CreateBuffer(null)`) | `ArgumentNullException` |
| an undefined enum value (`device.PushErrorScope((WGPUErrorFilter)7)`) | `ArgumentOutOfRangeException` |

Each check costs one comparison. The raw `WGPU.wgpu*` functions don't check anything, as in C, and neither
are fields inside descriptors.

**WebGPU errors go to error scopes or the uncaptured-error callback,** as `webgpu.h` specifies. This includes
the surface, polling and render-bundle errors that plain wgpu-native turns into an abort.
`wgpuSurfaceGetCurrentTexture` returns status `Error`, and `wgpuDevicePoll` returns `WGPUNativePollStatus.Error`.

**If wgpu-native still panics,** for example on a missing callback or a bad descriptor field, the process aborts.
With a logger set, the message and location reach it first:

```
wgpu-native panicked at src\lib.rs:3830:18: invalid error filter
```

A logger that buffers must flush on every message, or this line is lost with the process.

## When the GPU goes away

A driver crash, a TDR or a GPU reset fires the device-lost callback with reason `Unknown`, whichever call first
runs into it: a poll, a queue write or a `Present`. Calls on the lost device return errors from then on, and
releasing it is safe. On Direct3D 12 the message says why:

```
Device is lost: Direct3D 12 removed the device (DXGI_ERROR_DEVICE_REMOVED): The GPU device instance has been
suspended. Use GetDeviceRemovedReason to determine the appropriate action. (0x887A0005)
```

The reason is one of `DXGI_ERROR_DEVICE_REMOVED`, `DEVICE_HUNG`, `DEVICE_RESET`, `DRIVER_INTERNAL_ERROR` or
`INVALID_CALL`.

To find out what the GPU was doing, turn on Direct3D 12's Device Removed Extended Data (DRED) when you create
the instance:

```csharp
var extras = new WGPUInstanceExtras
{
    Chain = new WGPUChainedStruct { SType = WGPUSType.InstanceExtras },
    Flags = WGPUInstanceFlag.D3D12DRED,
};
using WGPUInstance instance = WGPU.wgpuCreateInstance(new WGPUInstanceDescriptor { NextInChain = &extras.Chain });
```

The lost message then ends with which command list stopped, at which operation, and inside which debug groups
and passes. Label them to get the names. For example:

```
DRED: command list "frame encoder" on queue (unnamed) stopped after 4 of 7 operations, at DrawIndexedInstanced
inside "shadow pass" > "cascade 2". GPU page fault at 0x1F2A0000, in "vertex buffer".
```

DRED costs some GPU time and applies to the whole process, so it is meant for test builds. With
`WGPUInstanceFlag.WithEnv`, setting `WGPU_D3D12_DRED=1` turns it on without code changes. It needs Windows 10
2004 or later and is ignored elsewhere.

The tests remove the real Direct3D 12 device to check all of this. Breadcrumbs only appear after a genuine hang
or page fault, so so far they have been tested only with hand-made data. On Vulkan, device loss goes through the
same path, but it can't be triggered in a test.

## Native AOT

```sh
# wgpu_native.dll / .so / .dylib next to the executable
dotnet publish -r win-x64 -p:PublishAot=true

# wgpu-native linked into the executable: one file of about 9 MB
dotnet publish -r win-x64 -p:PublishAot=true -p:WGpuFuryLinkage=Static -p:WGpuFuryStaticLibrary=path/to/wgpu_native.lib
```

The package contains the dynamic libraries. The static ones are about 65 MB each, so they aren't in it: take
`wgpu_native.lib` (Windows) or `libwgpu_native.a` from `native/<rid>/lib` after building, or from a CI run's
`native-<rid>` artifact. CI builds the statically linked Compute sample on win-x64, win-arm64, linux-x64 and
osx-arm64, and runs it on linux-x64.

On Windows, Native AOT needs the MSVC build tools. If publishing fails with `'vswhere.exe' is not recognized`,
add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH`.

## Platforms

| | Built | Tested in CI |
|---|---|---|
| win-x64, win-x86, win-arm64 | yes | yes (Direct3D 12 on WARP, Windows' software renderer) |
| linux-x64 | yes | yes (Vulkan on Mesa's lavapipe) |
| osx-arm64 | yes | yes (Metal) |
| linux-arm64, osx-x64 | yes | no |

On every tested platform, the C# struct layouts are also compared with the C compiler's, field by field.

## Contributing

[docs/MAINTAINING.md](docs/MAINTAINING.md) covers how wgpu-native is built and patched, the repository layout,
the tests, and updating to a new wgpu.

## License

[MIT](LICENSE). The bindings are derived from `webgpu.h` (BSD-3-Clause) and `wgpu.h`, and the package contains
wgpu-native and wgpu (MIT / Apache-2.0); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
