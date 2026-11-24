# Third-party notices

WGpuFury's generated sources are derived from, and its packages redistribute binaries of, the projects below.

## wgpu-native

https://github.com/gfx-rs/wgpu-native (`wgpu.h`, `wgpu_native` binaries), built from source at the commit pinned in
`eng/native.lock.json` (the head of gfx-rs/wgpu-native#630) with the patches in `eng/patches`, which are
distributed under the same terms.
Licensed under either of Apache License 2.0 or MIT, at your option.
The binaries statically include wgpu, naga and other Rust crates under their respective (MIT / Apache-2.0 compatible) licenses.

## webgpu-headers

https://github.com/webgpu-native/webgpu-headers (`webgpu.h`, `webgpu.yml`).
Copyright 2019-2023 WebGPU-Native developers. SPDX-License-Identifier: BSD-3-Clause.
