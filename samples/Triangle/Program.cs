// A spinning triangle in a plain Win32 window: surface creation from an HWND, swapchain configuration,
// a uniform buffer updated every frame, and presentation. Close the window or press Esc to quit.
//
//   dotnet run --project samples/Triangle
//   dotnet run --project samples/Triangle -- --frames 300     (quit by itself after 300 frames)
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WGpuFury;
using static Triangle.Win32;

namespace Triangle;

internal static unsafe class Program
{
    private static ReadOnlySpan<byte> Shader => """
        struct Uniforms { angle: f32, aspect: f32 }
        @group(0) @binding(0) var<uniform> u: Uniforms;

        struct VertexOut {
            @builtin(position) position: vec4<f32>,
            @location(0) color: vec3<f32>,
        }

        @vertex
        fn vs(@builtin(vertex_index) i: u32) -> VertexOut {
            let corners = array<vec2<f32>, 3>(vec2(0.0, 0.7), vec2(-0.6, -0.45), vec2(0.6, -0.45));
            let colors = array<vec3<f32>, 3>(vec3(1.0, 0.2, 0.3), vec3(0.2, 1.0, 0.4), vec3(0.3, 0.4, 1.0));
            let c = cos(u.angle);
            let s = sin(u.angle);
            let p = corners[i];
            var out: VertexOut;
            out.position = vec4<f32>((p.x * c - p.y * s) / u.aspect, p.x * s + p.y * c, 0.0, 1.0);
            out.color = colors[i];
            return out;
        }

        @fragment
        fn fs(in: VertexOut) -> @location(0) vec4<f32> {
            return vec4<f32>(in.color, 1.0);
        }
        """u8;

    private static bool s_resized;
    private static bool s_quit;

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This sample opens a Win32 window; run it on Windows.");
            return 1;
        }

        int maxFrames = args is ["--frames", var n] ? int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue;
        nint hinstance = GetModuleHandleW(null);
        nint hwnd = CreateMainWindow(hinstance);

        using WGPUInstance instance = WGPU.wgpuCreateInstance(null);
        var hwndSource = new WGPUSurfaceSourceWindowsHWND { Hinstance = (void*)hinstance, Hwnd = (void*)hwnd };
        using WGPUSurface surface = instance.CreateSurface(new WGPUSurfaceDescriptor { NextInChain = &hwndSource.Chain });

        using WGPUAdapter adapter = instance.RequestAdapterSync(new WGPURequestAdapterOptions
        {
            CompatibleSurface = surface,
            PowerPreference = WGPUPowerPreference.HighPerformance,
        });
        var info = new WGPUAdapterInfo();
        adapter.GetInfo(&info);
        string adapterName = $"{info.Device} ({info.BackendType})";
        info.FreeMembers();

        var deviceDescriptor = new WGPUDeviceDescriptor
        {
            UncapturedErrorCallbackInfo = new WGPUUncapturedErrorCallbackInfo { Callback = &OnUncapturedError },
        };
        using WGPUDevice device = adapter.RequestDeviceSync(instance, deviceDescriptor);
        using WGPUQueue queue = device.GetQueue();

        // Pick the surface's preferred format and alpha mode.
        var caps = new WGPUSurfaceCapabilities();
        surface.GetCapabilities(adapter, &caps);
        WGPUTextureFormat format = caps.Formats[0];
        WGPUCompositeAlphaMode alphaMode = caps.AlphaModes[0];
        caps.FreeMembers();

        var config = new WGPUSurfaceConfiguration
        {
            Device = device,
            Format = format,
            Usage = WGPUTextureUsage.RenderAttachment,
            AlphaMode = alphaMode,
            PresentMode = WGPUPresentMode.Fifo,
        };
        Resize(surface, ref config, hwnd);

        var wgsl = new WGPUShaderSourceWGSL { Code = WGPUStringView.FromPinned(Shader) };
        using WGPUShaderModule module = device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });

        var colorTarget = new WGPUColorTargetState { Format = format };
        var fragment = new WGPUFragmentState { Module = module, EntryPoint = WGPUStringView.FromPinned("fs"u8), TargetCount = 1, Targets = &colorTarget };
        using WGPURenderPipeline pipeline = device.CreateRenderPipeline(new WGPURenderPipelineDescriptor
        {
            Label = WGPUStringView.FromPinned("triangle"u8),
            Vertex = new WGPUVertexState { Module = module, EntryPoint = WGPUStringView.FromPinned("vs"u8) },
            Fragment = &fragment,
        });

        using WGPUBuffer uniforms = device.CreateBuffer(new WGPUBufferDescriptor { Size = 16, Usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst });
        using WGPUBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        var entry = new WGPUBindGroupEntry { Binding = 0, Buffer = uniforms };
        using WGPUBindGroup bindGroup = device.CreateBindGroup(new WGPUBindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry });

        Console.WriteLine($"Rendering on {adapterName}, {format}, {config.Width}x{config.Height}");
        var clock = Stopwatch.StartNew();
        int frames = 0;
        MSG msg;
        while (!s_quit && frames < maxFrames)
        {
            while (PeekMessageW(&msg, 0, 0, 0, PM_REMOVE) != 0)
            {
                if (msg.message == WM_QUIT)
                {
                    s_quit = true;
                }

                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }

            if (s_quit)
            {
                break;
            }

            if (s_resized)
            {
                s_resized = false;
                Resize(surface, ref config, hwnd);
            }

            if (config.Width == 0 || config.Height == 0)
            {
                Thread.Sleep(16); // minimized
                continue;
            }

            var surfaceTexture = new WGPUSurfaceTexture();
            surface.GetCurrentTexture(&surfaceTexture);
            switch (surfaceTexture.Status)
            {
                case WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal:
                case WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal:
                    break;
                case WGPUSurfaceGetCurrentTextureStatus.Timeout:
                case WGPUSurfaceGetCurrentTextureStatus.Outdated:
                case WGPUSurfaceGetCurrentTextureStatus.Lost:
                case WGPUSurfaceGetCurrentTextureStatus.Occluded:
                    surfaceTexture.Texture.Dispose();
                    Resize(surface, ref config, hwnd);
                    continue;
                default:
                    Console.Error.WriteLine($"GetCurrentTexture failed: {surfaceTexture.Status}");
                    return 1;
            }

            ReadOnlySpan<float> uniformData = [(float)clock.Elapsed.TotalSeconds, config.Width / (float)config.Height, 0, 0];
            queue.WriteBuffer(uniforms, 0, uniformData);

            using WGPUTexture texture = surfaceTexture.Texture;
            using WGPUTextureView view = texture.CreateView();
            using WGPUCommandEncoder encoder = device.CreateCommandEncoder();
            var attachment = new WGPURenderPassColorAttachment
            {
                View = view,
                LoadOp = WGPULoadOp.Clear,
                StoreOp = WGPUStoreOp.Store,
                ClearValue = new WGPUColor { R = 0.05, G = 0.05, B = 0.08, A = 1 },
            };
            using (WGPURenderPassEncoder pass = encoder.BeginRenderPass(new WGPURenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment }))
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, bindGroup, []);
                pass.Draw(3, 1, 0, 0);
                pass.End();
            }

            using WGPUCommandBuffer commands = encoder.Finish();
            queue.Submit([commands]);
            surface.Present();
            frames++;
        }

        Console.WriteLine($"{frames} frames in {clock.Elapsed.TotalSeconds:F1}s ({frames / clock.Elapsed.TotalSeconds:F0} fps)");
        surface.Unconfigure();
        DestroyWindow(hwnd);
        return 0;
    }

    private static void Resize(WGPUSurface surface, ref WGPUSurfaceConfiguration config, nint hwnd)
    {
        RECT rect;
        GetClientRect(hwnd, &rect);
        config.Width = (uint)Math.Max(0, rect.right - rect.left);
        config.Height = (uint)Math.Max(0, rect.bottom - rect.top);
        if (config.Width > 0 && config.Height > 0)
        {
            surface.Configure(config);
        }
    }

    private static nint CreateMainWindow(nint hinstance)
    {
        fixed (char* className = "WGpuFuryTriangle")
        fixed (char* title = "WGpuFury - Triangle")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = &WndProc,
                hInstance = hinstance,
                hCursor = LoadCursorW(0, IDC_ARROW),
                lpszClassName = className,
            };
            RegisterClassExW(&wc);
            return CreateWindowExW(0, className, title, WS_OVERLAPPEDWINDOW | WS_VISIBLE, CW_USEDEFAULT, CW_USEDEFAULT, 960, 640, 0, 0, hinstance, null);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                s_resized = true;
                return 0;
            case WM_KEYDOWN when wParam == VK_ESCAPE:
            case WM_DESTROY:
                s_quit = true;
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedError(WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2) =>
        Console.Error.WriteLine($"Uncaptured {type} error: {message}");
}
