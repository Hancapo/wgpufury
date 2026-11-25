# Maintaining WGpuFury

How the native library is built and patched, how the repository is laid out and tested, and how to move to a
new wgpu.

## Where wgpu 30 comes from

wgpu-native, which provides the C API, hasn't released a wgpu 30 build. The port is an open pull request,
[gfx-rs/wgpu-native#630](https://github.com/gfx-rs/wgpu-native/pull/630). WGpuFury builds that PR's head
commit from source, against wgpu's `v30.0.2` release, with patches on both:

- [`eng/native.lock.json`](../eng/native.lock.json) pins the two commits, the patch lists and the version string
  (`30.0.2.0`, which the library reports).
- [`eng/build-native.cs`](../eng/build-native.cs) checks wgpu-native out into `native/src` and wgpu into
  `native/wgpu`, and applies the patches. It points wgpu-native at the patched wgpu with a Cargo `[patch]`, runs
  `cargo build --release`, and copies the library and headers into `native/<rid>`.

### wgpu-native patches ([`eng/patches`](../eng/patches))

| Patch | What it does |
|---|---|
| 0001 build against wgpu v30.0.2 | The port pins a wgpu trunk commit from July 2026, without the 30.0.1 and 30.0.2 fixes; this pins the release and adapts four call sites. Goes away with a wgpu-native release. |
| 0002 implement futures and unimplemented functions | Implements everything the port left as an aborting stub: `WaitAny` and `ProcessEvents` with real callback modes, `GetLostFuture`, async pipeline creation, `wgpuGetProcAddress`, the instance-feature queries, `Read/WriteMappedRange`, `GetTextureBindingViewDimension`. Also: `GetMappedRange` returns NULL on error, an empty error-scope pop and the `Internal` filter work, and `wgpuCreateInstance` returns NULL for unsupported requirements. |
| 0003 report errors and device loss | wgpu errors from surfaces, polling, render bundles and device release were turned into a process abort; they are reported now. Registers wgpu's device-lost hook, which wgpu-native never did. Adds `WGPUNativePollStatus.Error`. |
| 0004 explain Direct3D 12 device removal | Adds the `GetDeviceRemovedReason` reason to the device-lost message. |
| 0005 accept unaligned chained structs | wgpu-native asserted the alignment of chained structs, which 32-bit .NET can't guarantee for structs with 64-bit fields; it now copies them with an unaligned read. |
| 0006 only warn about real device losses | Without a lost callback, every device release logged a "device lost" warning. |
| 0007 log panics before aborting | A panic hook sends the panic message to the log callback before the process aborts. |
| 0008 add Direct3D 12 DRED | `WGPUInstanceFlag.D3D12DRED`: turns on DRED and appends its breadcrumbs and page-fault data to the device-lost message. |

### wgpu patches ([`eng/patches/wgpu`](../eng/patches/wgpu))

All three bugs are still in wgpu trunk.

| Patch | What it does |
|---|---|
| 0001 report removed Direct3D 12 devices | A removed device reports every fence as complete (`UINT64_MAX`) and `Signal` keeps succeeding, so wgpu saw an idle queue and never noticed the removal. |
| 0002 align pipeline stream subobjects | Pipeline state stream subobjects were padded to 8 bytes; Direct3D 12 lays them out on pointer-size boundaries. In a 32-bit process, every render pipeline failed with `E_INVALIDARG`. |
| 0003 don't panic when pending writes can't start | `PendingWrites::activate` unwrapped `begin_encoding`, so the first queue write or `Present` on a removed device aborted the process. |

## Building

wgpu-native needs git, [rustup](https://rustup.rs) and libclang (for bindgen). The Rust version comes from
wgpu-native's `rust-toolchain.toml` and is installed automatically. On Windows, install
[LLVM](https://github.com/llvm/llvm-project/releases) in its default location (or set `LIBCLANG_PATH`) and
the MSVC build tools; on Linux, `clang-devel` or `libclang-dev`.

```sh
dotnet run eng/build-native.cs                     # wgpu-native for this machine
dotnet run eng/build-native.cs -- win-x64 win-x86  # or for these RIDs
dotnet build WGpuFury.slnx
dotnet test --solution WGpuFury.slnx
```

The first native build takes a few minutes. Later runs skip any `native/<rid>` that already matches the pinned
sources; `--force` rebuilds anyway. On an x64 Windows machine, win-x86 and win-arm64 cross-compile when the
matching MSVC libraries are installed. To run the tests as a 32-bit process, put an x86 .NET SDK first on
`PATH` (CI does this with `dotnet-install.ps1 -Architecture x86`).

The bindings are generated: after a header change, run `dotnet run --project eng/Generator`. CI fails if the
committed output is stale.

## Repository layout

```
eng/native.lock.json       wgpu-native and wgpu commits, patch lists, version, RID → Rust target
eng/patches/               patches on wgpu-native (wgpu/: on wgpu)
eng/build-native.cs        checks out and patches wgpu-native and wgpu, builds native/<rid>, checks patches
eng/Generator/             header parser and C# emitter (no dependencies, no libclang)
eng/run-abi-probe.ps1      compiles the C layout probe with MSVC → tests/abi/win-*.json
src/WGpuFury/              the library; Generated/ is generator output, the rest is hand-written
samples/                   Compute (any OS), Triangle (Windows)
tests/WGpuFury.Tests/      MSTest
```

## Tests

- **Struct layouts:** the generator writes [`tests/abi/layout_probe.c`](../tests/abi/layout_probe.c), which
  prints `sizeof` and every `offsetof` according to the C compiler, and `AbiTests` compares them with the C#
  layouts field by field. The win-x64 and win-x86 dumps are committed (`pwsh eng/run-abi-probe.ps1`). CI
  writes win-arm64's with MSVC, and Linux's and macOS's with `cc`. In CI, each job also checks that the tests
  ran as its RID, so the x86 job can't silently pass as a 64-bit process.
- **Exports and version:** every imported function must exist in the native library and resolve through
  `wgpuGetProcAddress`, and the library must report the version the bindings were generated for.
- **GPU behavior:** compute and render round trips, futures and callback modes, argument checks, and leaks. The
  test classes check `GetLiveObjects()` when they finish. Without an adapter, these tests are inconclusive.
- **Device loss** (`RobustnessTests`): removes the real Direct3D 12 device with `ID3D12Device5::RemoveDevice`,
  in a child test process, because Direct3D 12 shares one device per adapter across a process. Three variants:
  noticed by a poll, by a `WriteBuffer`, and by `Present`. The panic test deliberately aborts a child process
  too.
- **DRED:** `RemoveDevice` waits for the GPU to finish first, so it leaves no breadcrumbs. Patch 0008's Rust unit
  tests check the report against hand-made DRED data; run them with `cargo test --lib dred` in `native/src`
  (CI doesn't).
- **Packaging:** `dotnet pack` refuses `native/<rid>` directories built from different sources.

## CI

- [`ci.yml`](../.github/workflows/ci.yml) runs on pull requests and on pushes to `master`, except for changes
  that only touch documentation. It builds wgpu-native for all seven RIDs (cached by the lock file and patches),
  then tests on five and packs.
- [`upstream.yml`](../.github/workflows/upstream.yml) runs every Monday; see below.

## Updating to a new wgpu or wgpu-native

The Upstream workflow looks for the newest wgpu-native release for wgpu 30 or later (or else the head of #630)
and the newest wgpu release. If either moved past the lock file, it tries every patch on the new commits, and
the run fails so that GitHub notifies the owner. The job summary lists which patches apply, need a 3-way merge,
or conflict. The same check runs locally; leave out a commit to keep the pinned one:

```sh
dotnet run eng/build-native.cs -- --check-patches --check-source=<wgpu-native commit> --check-wgpu=<wgpu commit>
```

To update:

1. In `eng/native.lock.json`, change `source.commit` and `version`, and set `wgpuSource.commit` to the wgpu
   revision the new wgpu-native uses. Change `WGpuNativeVersion` in `Directory.Build.props` to match.
2. Rebase the patches: `dotnet run eng/build-native.cs -- --prepare` stops at the first one that no longer
   applies. Fix it in `native/src` (or `native/wgpu`), commit, and regenerate the patch file with
   `git format-patch`. Drop a patch once upstream includes the fix.
3. `dotnet run eng/build-native.cs`, then `dotnet run --project eng/Generator`. The parser fails loudly on header
   constructs it doesn't know, rather than mis-binding them.
4. Grep wgpu-native's `src/` for `unimplemented!()` and `todo!()`, and list any affected export in
   `eng/Generator/unimplemented.txt`, which marks it `[Obsolete]`.
5. `pwsh eng/run-abi-probe.ps1`, then `dotnet test`.
