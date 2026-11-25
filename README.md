<img src="assets/icon.svg" width="64" height="64" alt="">

# WGpuFury

.NET 10 bindings for [wgpu](https://github.com/gfx-rs/wgpu) 30, built for Native AOT.

WGpuFury binds the C API of [wgpu-native](https://github.com/gfx-rs/wgpu-native), which is the standard
[`webgpu.h`](https://github.com/webgpu-native/webgpu-headers) plus the `wgpu.h` native extensions. The
bindings are generated from those headers and cover all of them:

- 262 functions
- 162 structs
- 93 enums and flag types
- 26 handle types

Every signature is blittable and the assembly is marked `[DisableRuntimeMarshalling]`, so calls compile to
direct P/Invokes. There is no reflection, no delegate marshalling and no trimming or AOT warning. You can also
link wgpu-native statically into a single-file Native AOT executable.

| | |
|---|---|
| wgpu | **30.0.2** (`wgpu-core`, `wgpu-hal`, `naga` at the `wgpu-v30.0.2` tag, plus three patches) |
| Native library | wgpu-native built from source: the wgpu 30 port plus eight patches (below); reports version `30.0.2.0` |
| Target | `net10.0`; Native AOT, trimming |
| Platforms | win-x64, win-arm64, win-x86, linux-x64, linux-arm64, osx-x64, osx-arm64, all built by [CI](.github/workflows/ci.yml). CI also runs the tests on win-x64, win-x86 (a 32-bit process), win-arm64, linux-x64 and osx-arm64. |

## Where wgpu 30 comes from

wgpu-native, which provides the C API, has not released a wgpu 30 build. Its port is an open pull request,
[gfx-rs/wgpu-native#630](https://github.com/gfx-rs/wgpu-native/pull/630). WGpuFury builds that PR's head
commit from source, with the patches in [`eng/patches`](eng/patches) on top:

1. [`0001-build-against-wgpu-v30.0.2.patch`](eng/patches/0001-build-against-wgpu-v30.0.2.patch): the PR pins
   a wgpu trunk commit from July 2026 (v30.0.0 plus 105 trunk commits, without the 30.0.1/30.0.2 fixes). The
   patch pins the `wgpu-v30.0.2` release tag instead, and adapts four call sites to it.
2. [`0002-implement-futures-and-unimplemented-functions.patch`](eng/patches/0002-implement-futures-and-unimplemented-functions.patch):
   implements every function the port still left as an aborting stub, plus a few related aborts
   ([details](#what-the-patches-implement)).
3. [`0003-report-errors-and-device-loss.patch`](eng/patches/0003-report-errors-and-device-loss.patch): errors
   that wgpu-native turned into a process abort (surfaces, polling, render bundles, device release) are
   reported instead, and the device-lost callback fires when wgpu loses the device by itself
   ([details](#errors-and-device-loss)).
4. [`0004-explain-d3d12-device-removal.patch`](eng/patches/0004-explain-d3d12-device-removal.patch): on
   Direct3D 12, the device-lost message says why the device was removed.
5. [`0005-accept-unaligned-chained-structs.patch`](eng/patches/0005-accept-unaligned-chained-structs.patch):
   wgpu-native aborted on a chained struct that isn't aligned for its type. 32-bit .NET can't guarantee
   8-byte alignment for structs with 64-bit fields such as `WGPUInstanceExtras`, so on win-x86
   `wgpuCreateInstance` with extras aborted about half the time. Chained structs are now copied with an
   unaligned read.
6. [`0006-only-warn-about-real-device-losses.patch`](eng/patches/0006-only-warn-about-real-device-losses.patch):
   without a lost callback, releasing or destroying a device logged a "device lost" warning. Only real
   losses do now.
7. [`0007-log-panics-before-aborting.patch`](eng/patches/0007-log-panics-before-aborting.patch): a panic
   aborts the process, and its message went only to stderr. With a log callback set, it is logged first.
8. [`0008-add-d3d12-dred.patch`](eng/patches/0008-add-d3d12-dred.patch): `WGPUInstanceFlag.D3D12DRED` turns on
   Direct3D 12 DRED, and the device-lost message then says what the GPU was doing
   ([details](#what-the-gpu-was-doing-dred)).

wgpu itself gets three patches:

- [`wgpu/0001-dx12-report-removed-devices.patch`](eng/patches/wgpu/0001-dx12-report-removed-devices.patch):
  on Direct3D 12, a removed device (driver crash, TDR, GPU reset) looked like an idle queue, so wgpu never
  noticed it ([details](#errors-and-device-loss)).
- [`wgpu/0002-dx12-align-pipeline-stream-subobjects.patch`](eng/patches/wgpu/0002-dx12-align-pipeline-stream-subobjects.patch):
  in a 32-bit process, every Direct3D 12 render pipeline failed with `E_INVALIDARG`. wgpu padded the pipeline
  state stream's subobjects to 8 bytes, but Direct3D 12 lays them out on pointer-size boundaries
  (`alignas(void*)` in `d3dx12.h`), which is 4 bytes there. Upstream wgpu still has this bug.
- [`wgpu/0003-dont-panic-when-pending-writes-cannot-start.patch`](eng/patches/wgpu/0003-dont-panic-when-pending-writes-cannot-start.patch):
  wgpu-core starts an internal command list for queue writes and surface-texture clears with an `unwrap()`.
  On a device the GPU had removed but wgpu hadn't noticed yet, that panicked and aborted the process, for
  example in `Present` or in a render loop's `WriteBuffer`. The error now loses the device and fires the
  device-lost callback instead.

The commits, the patch lists and the version string are pinned in [`eng/native.lock.json`](eng/native.lock.json).
[`eng/build-native.cs`](eng/build-native.cs) checks wgpu-native out into `native/src` and wgpu into
`native/wgpu`, applies the patches, points wgpu-native at the patched wgpu with a Cargo `[patch]`, runs
`cargo build --release` and copies the result into `native/<rid>`. When #630 merges and wgpu-native releases
wgpu 30, the pin can move to that release and patch 1 can go.

## A taste

```csharp
using WGpuFury;

// (inside an unsafe context)

using WGPUInstance instance = WGPU.wgpuCreateInstance(null);
using WGPUAdapter adapter = instance.RequestAdapterSync();
using WGPUDevice device = adapter.RequestDeviceSync(instance);
using WGPUQueue queue = device.GetQueue();

var wgsl = new WGPUShaderSourceWGSL { Code = WGPUStringView.FromPinned("""
    @group(0) @binding(0) var<storage, read_write> data: array<f32>;
    @compute @workgroup_size(64)
    fn main(@builtin(global_invocation_id) id: vec3<u32>) { data[id.x] *= 2.0; }
    """u8) };
using WGPUShaderModule module = device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });

using WGPUBuffer buffer = device.CreateBuffer(new WGPUBufferDescriptor
{
    Size = 4096,
    Usage = WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst,
});
queue.WriteBuffer(buffer, 0, myFloats.AsSpan());   // generic span overload
queue.Submit([commands]);                         // count + pointer pairs become spans
```

Full programs:
- [`samples/Compute`](samples/Compute/Program.cs): headless; doubles a million floats on the GPU.
- [`samples/Triangle`](samples/Triangle/Program.cs): a spinning triangle in a plain Win32 window; Windows only.

## How the C API maps to C#

The goal is that C examples and the `webgpu.h` docs translate line by line. Convenience is added only on top
of the raw API, never in place of it.

| C | C# |
|---|---|
| `WGPUDevice` (opaque pointer) | `readonly struct WGPUDevice` holding the handle, with instance methods for every `wgpuDevice*` function and `Dispose()` → `wgpuDeviceRelease`. The methods throw on null handles, missing descriptors and undefined enum values, where wgpu-native would abort ([details](#what-the-patches-implement)) |
| `wgpuDeviceCreateBuffer(device, &desc)` | `WGPU.wgpuDeviceCreateBuffer(device, &desc)` (raw), `device.CreateBuffer(&desc)` or `device.CreateBuffer(desc)` (`in` overload) |
| `struct WGPUBufferDescriptor` | `struct WGPUBufferDescriptor` with the same field order and PascalCase names (`nextInChain` → `NextInChain`) |
| `float transform[12]` member | `public fixed float Transform[12];` |
| `WGPU_BUFFER_DESCRIPTOR_INIT` | `new WGPUBufferDescriptor()`: the parameterless constructor applies the header's INIT defaults (e.g. `DepthSlice = WGPU_DEPTH_SLICE_UNDEFINED`, limits = `UNDEFINED`, chained `SType`s already set). `default(T)`, `stackalloc` and new arrays are zero-filled, as in C. |
| `WGPUTextureFormat_RGBA8Unorm` | `WGPUTextureFormat.RGBA8Unorm` (names that start with a digit get `_`: `WGPUTextureDimension._2D`) |
| `WGPUBufferUsage` flags (`uint64_t` constants) | `[Flags] enum WGPUBufferUsage : ulong` |
| `wgpu.h` enum extensions (`WGPUSType_DeviceExtras`, `WGPUSurfaceGetCurrentTextureStatus_Occluded`) | merged into the enum they extend: `WGPUSType.DeviceExtras` |
| `WGPUBool` | `WGPUBool`, implicitly convertible to and from `bool` |
| `WGPUStringView` | `WGPUStringView`: `FromPinned("main"u8)`, `NativeUtf8String`, `ToString()`, `Null` / `Empty` |
| callback typedefs | `delegate* unmanaged[Cdecl]<…>` fields, pointed at `[UnmanagedCallersOnly]` static methods |
| `size_t count, T const* items` | an extra overload taking `ReadOnlySpan<T>` |
| `void const* data, size_t size` | an extra generic overload taking `ReadOnlySpan<TData>` |
| `WGPUStringView` by value | extra overloads taking `ReadOnlySpan<byte>` (UTF-8) and `string?` |
| doxygen comments | XML docs, so the `webgpu.h` documentation shows up in IntelliSense |

### Hand-written helpers

Everything else in the API is generated. These were written by hand:

- `instance.RequestAdapterSync(...)`, `adapter.RequestDeviceSync(instance, ...)`, `buffer.MapSync(device, ...)`:
  blocking wrappers over the callback API. They use static `[UnmanagedCallersOnly]` callbacks and a result on
  the stack, so there are no delegates or GC handles. `MapSync` only needs the device: it maps with
  `AllowSpontaneous` and blocks in `wgpuDevicePoll`.
- `buffer.GetMappedRange<T>()` / `GetConstMappedRange<T>()` return typed spans over mapped memory.
- `WGPU.SetLogger(level, (lvl, msg) => ...)` routes wgpu-native's log output to a managed callback, including
  the message of a panic just before it aborts the process.
- `WGPU.NativeVersion` is the version reported by the loaded library; `WGPU.BindingsVersion` the one the
  bindings were generated for.
- `instance.GetLiveObjects()` is wgpu's own count of the objects created from an instance and not yet
  released, e.g. "2 buffers, 1 texture views". Handles are value types with no finalizer, so a missed
  `Dispose` leaks silently; release everything but the instance, then expect `Total == 0`. The test suite
  does this when each test class finishes.

### Futures and callbacks

All asynchronous functions return a real `WGPUFuture`, and callbacks honor their `WGPUCallbackMode`:

| Mode | The callback fires… |
|---|---|
| `WaitAnyOnly` | only inside `instance.WaitAny(...)` on that future |
| `AllowProcessEvents` | inside `WaitAny`, or `instance.ProcessEvents()` once the operation is complete |
| `AllowSpontaneous` | as soon as the operation completes, possibly inside another wgpu call (e.g. `wgpuDevicePoll`) |
| unset (`0`) | like `AllowSpontaneous`: wgpu-native's behavior before futures existed |

- `WaitAny` with `timeoutNS = 0` polls once. A timeout above zero blocks on the GPU rather than spinning, and
  requires the instance to be created with `WGPUInstanceFeatureName.TimedWaitAny`. Up to 64 futures per timed
  wait, or the `TimedWaitAnyMaxCount` the instance asked for.
- Releasing the instance fires its pending callbacks with `CallbackCancelled`.
- `device.GetLostFuture()` completes when the device is lost (`Unknown`), destroyed (`Destroyed`) or released,
  or when device creation fails (`FailedCreation`). A spontaneous lost callback for a real loss runs on a new
  thread, because wgpu detects the loss while holding its own locks.
- `Create{Compute,Render}PipelineAsync` report creation errors to their callback (`ValidationError` /
  `InternalError`), not to error scopes.
- Without a callback, uncaptured errors and device loss are logged (see `WGPU.SetLogger`) instead of aborting
  the process.

## What the patches implement

The wgpu 30 port already implements all `*SetLabel` functions, `wgpuShaderModuleGetCompilationInfo`,
`wgpuDeviceGetAdapterInfo`, `wgpuBufferGetMapState` and the WGSL language-feature queries, which wgpu-native
v29 still stubbed. Patch 2 implements the rest:

| Function | Implementation |
|---|---|
| `wgpuInstanceWaitAny`, `wgpuInstanceProcessEvents` | a future registry with the callback modes above |
| `wgpuDeviceGetLostFuture` | the device-lost callback as a future |
| `wgpuDeviceCreateComputePipelineAsync`, `wgpuDeviceCreateRenderPipelineAsync` | creates synchronously; the result goes to the callback per its mode |
| `wgpuGetProcAddress` | a name → function table that `build.rs` generates from both headers |
| `wgpuGetInstanceFeatures`, `wgpuHasInstanceFeature`, `wgpuSupportedInstanceFeaturesFreeMembers` | `TimedWaitAny`, `ShaderSourceSPIRV`, `MultipleDevicesPerAdapter` |
| `wgpuBufferReadMappedRange`, `wgpuBufferWriteMappedRange` | copies to and from the mapping; `Error` status when not mapped or out of range |
| `wgpuTextureGetTextureBindingViewDimension` | the `WGPUTextureBindingViewDimension` chained on the texture descriptor, else `Undefined` |

It also removes aborts that are reachable from valid or merely mistaken calls:

- `wgpuBufferGetMappedRange` / `GetConstMappedRange` return `NULL` on error, as `webgpu.h` specifies.
- Popping an empty error-scope stack reports `WGPUPopErrorScopeStatus.Error`.
- `WGPUErrorFilter.Internal` works, and internal errors are reported as `WGPUErrorType.Internal`, not as
  validation errors.
- `wgpuCreateInstance` returns null for unsupported required features or limits instead of panicking.
- The default uncaptured-error and device-lost handlers log instead of aborting.

wgpu-native still panics on a null handle, a missing descriptor or an undefined enum value, and the panic aborts
the process. The C# methods on handles check for these first and throw instead:

| Mistake | Exception |
|---|---|
| a method called on a null handle (`default(WGPUDevice).GetQueue()`) | `InvalidOperationException` |
| a null handle or descriptor where `webgpu.h` doesn't allow one (`device.CreateBuffer(null)`) | `ArgumentNullException` |
| an enum value that isn't defined (`device.PushErrorScope((WGPUErrorFilter)7)`) | `ArgumentOutOfRangeException` |

Parameters marked `WGPU_NULLABLE` and count + pointer arrays (which may be null when empty) aren't checked, and
neither are feature names, so that asking about an unknown feature keeps answering false. Each check is one
comparison. The raw `WGPU.wgpu*` functions don't check anything, as in C, and fields inside descriptors aren't
checked either.

A panic that still happens aborts the process. Once a logger is set (`WGPU.SetLogger`), it first receives the
panic message and location at error level, e.g. "wgpu-native panicked at src\lib.rs:3830:18: invalid error
filter" (patch 7); before, that went only to stderr. A logger that buffers must flush on every message to keep
it. [`eng/Generator/unimplemented.txt`](eng/Generator/unimplemented.txt) is empty,
so nothing is marked `[Obsolete("…aborts the process")]` any more. `tests/WGpuFury.Tests/FutureTests.cs`
exercises each of the above.

### Errors and device loss

Upstream wgpu-native aborts the process whenever wgpu returns an error from one of these calls. Patch 3 reports
the error instead:

| Call | Now |
|---|---|
| `wgpuSurfaceConfigure`, `wgpuSurfaceUnconfigure`, `wgpuDeviceCreateRenderBundleEncoder`, `wgpuRenderBundleEncoderFinish`, `wgpuRenderBundleEncoderSetImmediates` | an error on the device (error scopes, else the uncaptured-error callback) |
| `wgpuSurfaceGetCurrentTexture` | status `Error` and no texture; the error goes to the device the surface is configured with (logged if unconfigured) |
| `wgpuInstanceCreateSurface` | returns `NULL` and logs the error |
| `wgpuDevicePoll` | returns the new `WGPUNativePollStatus.Error`; a lost device fires the lost callback |
| `wgpuInstanceProcessEvents`, `wgpuInstancePollAllDevices`, releasing a device or surface texture | logs the error |

Patch 3 also registers wgpu's own device-lost hook, which wgpu-native never did. Before, a device that wgpu
lost by itself (a removed GPU, an unexpected backend error, running out of memory) reached the lost callback
only if a later call happened to fail with a device-lost error.

That still wasn't enough on Direct3D 12: after a removal, Direct3D reports every fence as complete
(`UINT64_MAX`) and `Signal` keeps succeeding, so wgpu saw an idle queue and never noticed. The wgpu patch
checks for that value and reports the device's removal reason (`GetDeviceRemovedReason`), which wgpu turns into
a lost device.

wgpu's own message for every backend failure is just "Device is lost". Patch 4 adds the Direct3D 12 removal
reason to the lost callback's message. After `RemoveDevice` in the test, it reads:

```
Device is lost: Direct3D 12 removed the device (DXGI_ERROR_DEVICE_REMOVED): The GPU device instance has been
suspended. Use GetDeviceRemovedReason to determine the appropriate action. (0x887A0005)
```

The possible reasons are `DXGI_ERROR_DEVICE_REMOVED`, `DEVICE_HUNG`, `DEVICE_RESET`, `DRIVER_INTERNAL_ERROR` and
`INVALID_CALL`.

#### What the GPU was doing: DRED

"Hung" says that the GPU stopped responding, not at what. Direct3D 12's Device Removed Extended Data (DRED)
records how far the GPU got through each command list, with the debug groups and pass labels around it, and the
address of a GPU page fault. Patch 8 adds `WGPUInstanceFlag.D3D12DRED` to turn it on:

```csharp
var extras = new WGPUInstanceExtras
{
    Chain = new WGPUChainedStruct { SType = WGPUSType.InstanceExtras },
    Flags = WGPUInstanceFlag.D3D12DRED,
};
using WGPUInstance instance = WGPU.wgpuCreateInstance(new WGPUInstanceDescriptor { NextInChain = &extras.Chain });
```

With `WGPUInstanceFlag.WithEnv`, setting `WGPU_D3D12_DRED=1` does the same, so a build can turn it on without
code changes. After a removal, the lost message ends with a DRED report such as:

```
DRED: command list "frame encoder" on queue (unnamed) stopped after 4 of 7 operations, at DrawIndexedInstanced
inside "shadow pass" > "cascade 2". GPU page fault at 0x1F2A0000, in "vertex buffer".
```

Label encoders, passes and debug groups to get those names. DRED applies to every Direct3D 12 device created
afterwards in the process and costs some GPU time, so it is meant for test builds; it needs Windows 10 2004 or
later, and is ignored elsewhere.

That report's format is what patch 8's unit tests check, by feeding the report hand-made DRED data. On a real
machine, only a genuine GPU hang or page fault leaves unfinished command lists. `RemoveDevice`, which the tests below use, first
waits for the GPU to finish, so there the report only says that every recorded command list had finished.

`tests/WGpuFury.Tests/RobustnessTests.cs` checks this end to end. It removes the real Direct3D 12 device under
wgpu with `ID3D12Device5::RemoveDevice` (in a child process, since that kills every device in the process),
then expects the lost callback, a logged `0x887A0005` (`DXGI_ERROR_DEVICE_REMOVED`) and no abort. Two variants
cover how an application first runs into the removal: a `WriteBuffer` right after it, and a loop that only
acquires and presents, where `Present` is what notices it. It also misuses surfaces: acquiring before
configuring, a zero-sized configuration, and a second swap chain on the same window, which wgpu rejects with a
validation error ("Invalid surface") without harming the first one.

Vulkan has no equivalent of `RemoveDevice`, so device loss there is checked only by reading the code: wgpu maps
`VK_ERROR_DEVICE_LOST` to the same lost-device error, which goes through the same device-lost hook.

## Native AOT and linking

```sh
# Default: wgpu_native.dll / .so / .dylib is copied next to the executable
dotnet publish -r win-x64 -p:PublishAot=true

# Static: wgpu-native is linked into the executable itself (one ~9 MB file, no native dependency)
dotnet publish -r win-x64 -p:PublishAot=true -p:WGpuFuryLinkage=Static -p:WGpuFuryStaticLibrary=path/to/wgpu_native.lib
```

The NuGet package ships the dynamic libraries under `runtimes/<rid>/native`. The static libraries are too large
to ship in it (about 65 MB each), so for static linking take `wgpu_native.lib` (Windows) or `libwgpu_native.a`
from `native/<rid>/lib` after `dotnet run eng/build-native.cs`, or from the `native-<rid>` artifact of a CI
run. Inside this repository the path is filled in automatically. Both modes are implemented in
[`WGpuFury.Native.targets`](src/WGpuFury/build/WGpuFury.Native.targets), which the package imports through
`buildTransitive`.

Static linking has been verified on win-x64; it only needs `userenv.lib` beyond Native AOT's defaults. The
Linux and macOS linker flags are the usual Rust staticlib requirements, but they have not been run yet.
[CI](.github/workflows/ci.yml) covers them.

## Repository layout

```
eng/native.lock.json       wgpu-native and wgpu commits, patch lists, version and RID → Rust target map
eng/patches/               patches applied on top of wgpu-native (wgpu/: on top of wgpu)
eng/build-native.cs        checks out + patches wgpu-native (native/src) and wgpu (native/wgpu), builds native/<rid>
eng/Generator/             header parser + C# emitter (no dependencies, no libclang)
eng/run-abi-probe.ps1      compiles the generated C layout probe with MSVC → tests/abi/win-*.json
src/WGpuFury/              the library; Generated/ is generator output, the rest is hand-written
samples/                   Compute (any OS), Triangle (Windows)
tests/WGpuFury.Tests/      ABI, defaults, GPU round-trip and futures tests (MSTest)
```

## Building

Building wgpu-native needs git, [rustup](https://rustup.rs) (the Rust version comes from wgpu-native's
`rust-toolchain.toml` and is installed automatically) and libclang for bindgen. On Windows that means
[LLVM](https://github.com/llvm/llvm-project/releases) in its default location (or `LIBCLANG_PATH`) and the
MSVC build tools; on Linux, `clang-devel` / `libclang-dev`.

```sh
dotnet run eng/build-native.cs           # wgpu-native for this machine (or list RIDs: -- win-x64 win-x86)
dotnet build WGpuFury.slnx
dotnet test --solution WGpuFury.slnx
dotnet run --project samples/Compute
```

The first native build takes a few minutes; later runs are skipped while `native/<rid>` matches the pinned
sources. Cross-compiling for win-x86 and win-arm64 on an x64 Windows machine works when the matching MSVC
libraries are installed.

Native AOT on Windows needs the MSVC build tools ("Desktop development with C++"). If publishing fails with
`'vswhere.exe' is not recognized`, add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH`.

## How correctness is checked

- **Struct layout**: the generator also writes [`tests/abi/layout_probe.c`](tests/abi/layout_probe.c), which
  prints `sizeof` and every `offsetof` according to the C compiler. `AbiTests` compares every named struct,
  field by field, with the C# layout for the current RID. The win-x64 and win-x86 dumps are committed; CI
  produces the win-arm64 one with MSVC and the Linux and macOS ones with `cc`. In CI, each job also checks
  that the tests really ran as its RID, so the x86 job can't silently pass as a 64-bit process.
- **Exports**: every imported function is looked up in the shipped native library, and resolved through
  `wgpuGetProcAddress`.
- **Behavior**: compute and offscreen-render round trips on a real device, and the futures tests above. These
  report inconclusive when no adapter is available.
- **Version**: the loaded library must report exactly the version the bindings were generated for.
- **Packaging**: `dotnet pack` refuses `native/<rid>` directories built from other sources than the others.

## Updating wgpu-native

The [Upstream](.github/workflows/upstream.yml) workflow checks every Monday whether wgpu-native or wgpu has
moved past `eng/native.lock.json`. For wgpu-native, it looks at a release for wgpu 30 or later, or else the head
of the wgpu 30 port (#630); for wgpu, at the newest release. If anything moved, it tries every patch on the new
commits and fails, so GitHub notifies you. The job summary has a table of which patches still apply, need a
3-way merge, or conflict. The same check runs locally, with either commit left out to keep the pinned one:

```sh
dotnet run eng/build-native.cs -- --check-patches --check-source=<wgpu-native commit> --check-wgpu=<wgpu commit>
```

To update:

1. Change `source.commit` (and `version`) in `eng/native.lock.json`, and `WGpuNativeVersion` in
   `Directory.Build.props`. Set `wgpuSource.commit` to the wgpu revision the new wgpu-native uses.
2. Rebase the patches: `dotnet run eng/build-native.cs -- --prepare` stops at the first one that no longer
   applies. Fix it in `native/src` (or `native/wgpu`), commit, and regenerate the patch file with
   `git format-patch`. Drop a patch once upstream includes the fix. Patch 1 only exists to build the port
   against wgpu v30.0.2, so it goes away with a wgpu-native release.
3. `dotnet run eng/build-native.cs`, then `dotnet run --project eng/Generator` to regenerate the bindings. The
   parser fails loudly on header constructs it doesn't know, rather than mis-binding them.
4. Grep wgpu-native's `src/` for `unimplemented!()` / `todo!()` and list any affected export in
   `eng/Generator/unimplemented.txt`.
5. `pwsh eng/run-abi-probe.ps1`, then `dotnet test`.

## License

WGpuFury is licensed under the [MIT License](LICENSE).

The generated code is derived from `webgpu.h` (BSD-3-Clause) and `wgpu.h` (MIT / Apache-2.0), and the package
ships wgpu-native and wgpu binaries (MIT / Apache-2.0); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
