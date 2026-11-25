using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WGpuFury.Tests;

/// <summary>
/// Failures that wgpu-native used to turn into a process abort (surface, poll and render bundle errors), and
/// device loss that wgpu detects itself, which used to reach no callback. See
/// eng/patches/0003-report-errors-and-device-loss.patch. Direct3D 12 only: the tests are inconclusive elsewhere.
/// </summary>
[TestClass]
public sealed unsafe class RobustnessTests
{
    private const string ChildReportVariable = "WGPUFURY_CHILD_TEST_REPORT";
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);

    private static readonly ManualResetEventSlim s_lost = new();
    private static int s_lostCalls;
    private static WGPUDeviceLostReason s_lostReason;
    private static string? s_lostMessage;
    private static int s_uncapturedErrors;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Reset()
    {
        s_lost.Reset();
        s_lostCalls = 0;
        s_lostMessage = null;
        s_uncapturedErrors = 0;
    }

    // ---------------------------------------------------------------- device removal

    /// <summary>
    /// Removes the Direct3D 12 device under wgpu (as a driver crash or TDR would) and checks that the device-lost
    /// callback fires and that the calls after it, including release, don't abort. Direct3D 12 devices are
    /// per-adapter singletons, so removing one kills every device in the process: the removal runs in a child
    /// test process.
    /// </summary>
    [TestMethod]
    public void RemovingTheD3D12DeviceFiresTheLostCallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Direct3D 12 only.");
        }

        if (ChildReport() is { } reportFile)
        {
            var lines = new List<string>();
            try
            {
                RemoveTheDeviceInThisProcess(lines);
            }
            finally
            {
                File.WriteAllLines(reportFile, lines);
            }

            return;
        }

        (int exitCode, string[] report, string output) = RunInChildProcess(nameof(RemovingTheD3D12DeviceFiresTheLostCallback));
        Assert.AreEqual(0, exitCode, $"The device-removal child process failed:\n{string.Join('\n', report)}\n{output}");
        if (report.FirstOrDefault() is { } first && first.StartsWith("skip: ", StringComparison.Ordinal))
        {
            Assert.Inconclusive(first);
        }
    }

    /// <summary>
    /// A panic in wgpu-native (here a deliberate one: an invalid error filter) aborts the process, and Rust only
    /// reports it on stderr. It now reaches the log callback first. The abort kills the process, so this runs in a
    /// child test process.
    /// </summary>
    [TestMethod]
    public void PanicsReachTheLoggerBeforeTheProcessAborts()
    {
        if (ChildReport() is { } reportFile)
        {
            // Written line by line: nothing buffered survives the abort.
            WGPU.SetLogger(WGPULogLevel.Error, (level, message) => File.AppendAllText(reportFile, $"{level}: {message}\n"));
            using WGPUInstance instance = WGPU.wgpuCreateInstance(null);
            if (!TryRequestAdapter(instance, out WGPUAdapter adapter, out string? why))
            {
                File.AppendAllText(reportFile, $"skip: {why}\n");
                return;
            }

            using (adapter)
            using (WGPUDevice device = adapter.RequestDeviceSync(instance))
            {
                // The raw function: device.PushErrorScope checks the value and throws instead.
                WGPU.wgpuDevicePushErrorScope(device, (WGPUErrorFilter)0x7777);
            }

            File.AppendAllText(reportFile, "the process didn't abort\n");
            return;
        }

        (int exitCode, string[] report, string output) = RunInChildProcess(nameof(PanicsReachTheLoggerBeforeTheProcessAborts));
        string text = string.Join('\n', report);
        if (text.StartsWith("skip: ", StringComparison.Ordinal))
        {
            Assert.Inconclusive(text);
        }

        Assert.AreNotEqual(0, exitCode, $"The child process should have aborted:\n{text}\n{output}");
        StringAssert.Contains(text, "Error: wgpu-native panicked at", $"the panic didn't reach the logger:\n{output}");
        StringAssert.Contains(text, "invalid error filter");
        StringAssert.Contains(text, "The process will abort.");
    }

    /// <summary>
    /// The removal test notices the loss through a poll. A render loop may notice it in <c>Present</c> first, which
    /// must fire the lost callback just the same. Here nothing but acquire and present runs after the removal.
    /// </summary>
    [TestMethod]
    public void RemovalNoticedByPresentFiresTheLostCallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Uses a Win32 window and Direct3D 12.");
        }

        if (ChildReport() is { } reportFile)
        {
            var lines = new List<string>();
            try
            {
                RemoveTheDeviceThenPresent(lines);
            }
            finally
            {
                File.WriteAllLines(reportFile, lines);
            }

            return;
        }

        (int exitCode, string[] report, string output) = RunInChildProcess(nameof(RemovalNoticedByPresentFiresTheLostCallback));
        Assert.AreEqual(0, exitCode, $"The child process failed:\n{string.Join('\n', report)}\n{output}");
        if (report.FirstOrDefault() is { } first && first.StartsWith("skip: ", StringComparison.Ordinal))
        {
            Assert.Inconclusive(first);
        }
    }

    private static void RemoveTheDeviceThenPresent(List<string> report)
    {
        using WGPUInstance instance = CreateD3D12Instance();
        if (!TryRequestAdapter(instance, out WGPUAdapter adapter, out string? why))
        {
            report.Add("skip: " + why);
            return;
        }

        using (adapter)
        using (var window = new HiddenWindow())
        {
            var info = new WGPUAdapterInfo();
            adapter.GetInfo(&info);
            (uint vendorId, uint deviceId) = (info.VendorID, info.DeviceID);
            info.FreeMembers();

            WGPUDevice device = RequestDevice(instance, adapter);
            WGPUSurface surface = CreateSurface(instance, window);
            var capabilities = new WGPUSurfaceCapabilities();
            Assert.AreEqual(WGPUStatus.Success, surface.GetCapabilities(adapter, &capabilities));
            WGPUTextureFormat format = capabilities.Formats[0];
            capabilities.FreeMembers();
            surface.Configure(new WGPUSurfaceConfiguration { Device = device, Format = format, Width = 320, Height = 240 });
            Present(surface);

            Assert.AreNotEqual(0, D3D12.RemoveDevices(vendorId, deviceId), "found no Direct3D 12 device for the adapter");

            var steps = new List<string>();
            for (int i = 0; i < 20 && !s_lost.IsSet; i++)
            {
                var texture = new WGPUSurfaceTexture();
                surface.GetCurrentTexture(&texture);
                steps.Add($"acquire {texture.Status}");
                if (!texture.Texture.IsNull)
                {
                    steps.Add($"present {surface.Present()}");
                    texture.Texture.Dispose();
                }

                s_lost.Wait(50);
            }

            report.Add("after the removal: " + string.Join(", ", steps));
            Assert.IsTrue(s_lost.Wait(TimeSpan.FromSeconds(5)), "presenting never fired the lost callback");
            report.Add($"lost callback: {s_lostReason}: {s_lostMessage}");
            Assert.AreEqual(WGPUDeviceLostReason.Unknown, s_lostReason);
            StringAssert.Contains(s_lostMessage, "DXGI_ERROR_DEVICE_REMOVED");

            surface.Unconfigure();
            surface.Dispose();
            device.Dispose();
        }
    }

    /// <summary>The report file to write, when this process is a child started by <see cref="RunInChildProcess"/>.</summary>
    private static string? ChildReport() => Environment.GetEnvironmentVariable(ChildReportVariable);

    /// <summary>
    /// Runs one test of this class in a new test process, for tests that remove the Direct3D 12 device or abort,
    /// which would take the other tests down with them. The child writes its findings to a report file.
    /// </summary>
    private (int ExitCode, string[] Report, string Output) RunInChildProcess(string testName)
    {
        string report = Path.GetTempFileName();
        try
        {
            // The test project's executable: WGpuFury.Tests.exe on Windows, WGpuFury.Tests elsewhere.
            string executable = Path.ChangeExtension(typeof(RobustnessTests).Assembly.Location, OperatingSystem.IsWindows() ? ".exe" : null);
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--filter");
            psi.ArgumentList.Add($"FullyQualifiedName~{nameof(RobustnessTests)}.{testName}");
            psi.Environment[ChildReportVariable] = report;

            using Process child = Process.Start(psi)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync();
            Task<string> stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                child.Kill(entireProcessTree: true);
                Assert.Fail($"The {testName} child process hung:\n{stdout.Result}{stderr.Result}");
            }

            string[] lines = File.ReadAllLines(report);
            foreach (string line in lines)
            {
                TestContext.WriteLine(line);
            }

            return (child.ExitCode, lines, stdout.Result + stderr.Result);
        }
        finally
        {
            File.Delete(report);
        }
    }

    private static void RemoveTheDeviceInThisProcess(List<string> report)
    {
        var log = new List<string>();
        WGPU.SetLogger(WGPULogLevel.Warn, (level, message) =>
        {
            lock (log)
            {
                log.Add($"{level}: {message}");
            }
        });

        // DRED, so the lost message also says what the GPU was doing (patch 0008).
        using WGPUInstance instance = CreateD3D12Instance(WGPUInstanceFlag.D3D12DRED);
        if (!TryRequestAdapter(instance, out WGPUAdapter adapter, out string? why))
        {
            report.Add("skip: " + why);
            return;
        }

        using (adapter)
        {
            var info = new WGPUAdapterInfo();
            adapter.GetInfo(&info);
            (uint vendorId, uint deviceId) = (info.VendorID, info.DeviceID);
            report.Add($"adapter: {info.Device} (0x{vendorId:X4}:0x{deviceId:X4}, {info.AdapterType})");
            info.FreeMembers();

            WGPUDevice device = RequestDevice(instance, adapter);
            WGPUQueue queue = device.GetQueue();
            WGPUBuffer readback = device.CreateBuffer(new WGPUBufferDescriptor { Size = 256, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
            SubmitEmpty(device, queue);
            Assert.AreEqual(WGPUNativePollStatus.QueueEmpty, WGPU.wgpuDevicePoll(device, true, null, 0));
            Assert.IsFalse(s_lost.IsSet, "the device was lost before the removal");

            int removed = D3D12.RemoveDevices(vendorId, deviceId);
            Assert.AreNotEqual(0, removed, "found no Direct3D 12 device for the adapter");

            // What a render loop does first after a removal it hasn't noticed yet: update a buffer. wgpu-core
            // panicked starting its internal command list on a removed device (eng/patches/wgpu/0003).
            queue.WriteBuffer(readback, 0, new uint[] { 1, 2, 3, 4 }.AsSpan());

            // wgpu notices on its next call into Direct3D 12 that reports the removal (signaling a fence).
            var polls = new List<WGPUNativePollStatus>();
            for (int i = 0; i < 20 && !s_lost.IsSet; i++)
            {
                SubmitEmpty(device, queue);
                polls.Add(WGPU.wgpuDevicePoll(device, true, null, 100_000_000));
                s_lost.Wait(50);
            }

            report.Add("polls after the removal: " + string.Join(", ", polls));
            lock (log)
            {
                report.AddRange(log.Select(l => "log " + l));
                log.Clear();
            }

            Assert.IsTrue(s_lost.Wait(TimeSpan.FromSeconds(5)), "the lost callback never fired");
            report.Add($"lost callback: {s_lostReason}: {s_lostMessage}");
            Assert.AreEqual(1, s_lostCalls);
            Assert.AreEqual(WGPUDeviceLostReason.Unknown, s_lostReason);
            StringAssert.Contains(s_lostMessage, "DXGI_ERROR_DEVICE_REMOVED", "the lost message doesn't say why");
            StringAssert.Contains(s_lostMessage, "0x887A0005");
            // RemoveDevice waits for the GPU to finish first, so DRED has no unfinished command list to describe
            // here: those only exist after a real hang or page fault. eng/patches/0008's unit tests cover them.
            StringAssert.Contains(s_lostMessage, "DRED:", "the lost message has no DRED report");

            // A lost device still accepts calls (they do nothing), and releasing it doesn't abort.
            device.CreateBuffer(new WGPUBufferDescriptor { Size = 256, Usage = WGPUBufferUsage.CopyDst }).Dispose();
            WGPUException mapError = Assert.ThrowsExactly<WGPUException>(() => readback.MapSync(device, WGPUMapMode.Read, 0, 256));
            report.Add("MapSync on the lost device: " + mapError.Message);
            readback.Dispose();
            SubmitEmpty(device, queue);
            report.Add("poll on the lost device: " + WGPU.wgpuDevicePoll(device, true, null, 0));
            queue.Dispose();
            device.Dispose();
            Assert.AreEqual(1, s_lostCalls, "the lost callback fired twice");
        }

        lock (log)
        {
            report.AddRange(log.Select(l => "log " + l));
            Assert.IsTrue(report.Any(l => l.StartsWith("log ", StringComparison.Ordinal) && l.Contains("0x887A0005", StringComparison.OrdinalIgnoreCase)),
                "wgpu didn't log DXGI_ERROR_DEVICE_REMOVED");
        }

        WGPU.SetLogger(WGPULogLevel.Off, null);
    }

    // ---------------------------------------------------------------- surfaces

    [TestMethod]
    public void SurfaceMisuseIsReportedInsteadOfAborting()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Uses a Win32 window and Direct3D 12.");
        }

        using WGPUInstance instance = CreateD3D12Instance();
        if (!TryRequestAdapter(instance, out WGPUAdapter adapter, out string? why))
        {
            Assert.Inconclusive(why!);
        }

        using (adapter)
        using (var window = new HiddenWindow())
        {
            using WGPUDevice device = RequestDevice(instance, adapter);
            using WGPUSurface surface = CreateSurface(instance, window);
            Assert.IsFalse(surface.IsNull);

            var capabilities = new WGPUSurfaceCapabilities();
            Assert.AreEqual(WGPUStatus.Success, surface.GetCapabilities(adapter, &capabilities));
            WGPUTextureFormat format = capabilities.Formats[0];
            capabilities.FreeMembers();
            var config = new WGPUSurfaceConfiguration { Device = device, Format = format, Width = 320, Height = 240 };

            // Acquiring before configuring was a panic; it now fails with status Error.
            var texture = new WGPUSurfaceTexture();
            surface.GetCurrentTexture(&texture);
            Assert.AreEqual(WGPUSurfaceGetCurrentTextureStatus.Error, texture.Status);
            Assert.IsTrue(texture.Texture.IsNull);

            // An invalid configuration was a panic; it is now a validation error on the device.
            device.PushErrorScope(WGPUErrorFilter.Validation);
            surface.Configure(config with { Width = 0 });
            (WGPUErrorType type, string message) = PopErrorScope(device);
            Assert.AreEqual(WGPUErrorType.Validation, type, message);
            TestContext.WriteLine("zero-width configure: " + message);

            surface.Configure(config);
            Present(surface);

            // A second surface on the same window: DXGI allows one flip-model swap chain per window. Whatever
            // wgpu makes of it, it must come back as an error (or work), never abort or lose the first device.
            using (WGPUSurface second = CreateSurface(instance, window))
            using (WGPUDevice otherDevice = RequestDevice(instance, adapter))
            {
                otherDevice.PushErrorScope(WGPUErrorFilter.Internal);
                otherDevice.PushErrorScope(WGPUErrorFilter.OutOfMemory);
                otherDevice.PushErrorScope(WGPUErrorFilter.Validation);
                second.Configure(config with { Device = otherDevice });
                var errors = new[] { PopErrorScope(otherDevice), PopErrorScope(otherDevice), PopErrorScope(otherDevice) }
                    .Where(e => e.Type != WGPUErrorType.NoError)
                    .ToArray();
                foreach ((WGPUErrorType t, string m) in errors)
                {
                    TestContext.WriteLine($"second swap chain on the window: {t}: {m}");
                }

                var secondTexture = new WGPUSurfaceTexture();
                second.GetCurrentTexture(&secondTexture);
                TestContext.WriteLine($"second surface acquire: {secondTexture.Status}");
                if (!secondTexture.Texture.IsNull)
                {
                    secondTexture.Texture.Dispose();
                }

                second.Unconfigure();
            }

            Assert.AreEqual(0, s_lostCalls, $"a device was lost: {s_lostMessage}");
            Present(surface); // the first surface is unaffected
            surface.Unconfigure();

            // Unconfigured again: Error, not a panic.
            surface.GetCurrentTexture(&texture);
            Assert.AreEqual(WGPUSurfaceGetCurrentTextureStatus.Error, texture.Status);
        }

        Assert.AreEqual(0, s_uncapturedErrors, "errors reached the uncaptured-error callback");
    }

    // ---------------------------------------------------------------- render bundles

    [TestMethod]
    public void RenderBundleErrorsAreValidationErrors()
    {
        using WGPUInstance instance = WGPU.wgpuCreateInstance(null);
        if (!TryRequestAdapter(instance, out WGPUAdapter adapter, out string? why))
        {
            Assert.Inconclusive(why!);
        }

        using (adapter)
        {
            using WGPUDevice device = RequestDevice(instance, adapter);

            // No attachments at all was a panic in wgpuDeviceCreateRenderBundleEncoder.
            device.PushErrorScope(WGPUErrorFilter.Validation);
            using WGPURenderBundleEncoder encoder = device.CreateRenderBundleEncoder(new WGPURenderBundleEncoderDescriptor { SampleCount = 1 });
            using WGPURenderBundle bundle = encoder.Finish();
            (WGPUErrorType type, string message) = PopErrorScope(device);
            Assert.AreEqual(WGPUErrorType.Validation, type, message);
            TestContext.WriteLine(message);
        }

        Assert.AreEqual(0, s_uncapturedErrors);
    }

    // ---------------------------------------------------------------- 32-bit and alignment

    /// <summary>
    /// .NET on 32-bit Windows puts structs with 64-bit fields on 4-byte boundaries, and wgpu-native aborted on a
    /// chained struct that isn't aligned for its type. Placing one off its alignment on purpose tests that on
    /// every architecture.
    /// </summary>
    [TestMethod]
    public void MisalignedChainedStructsAreAccepted()
    {
        byte* block = (byte*)NativeMemory.AlignedAlloc((nuint)(sizeof(WGPUInstanceExtras) + 8), 8);
        try
        {
            var extras = (WGPUInstanceExtras*)(block + 4);
            *extras = new WGPUInstanceExtras
            {
                Chain = new WGPUChainedStruct { SType = WGPUSType.InstanceExtras },
                Backends = WGPUInstanceBackend.All,
            };
            Assert.AreNotEqual(0, (nint)extras % 8, "the struct must be misaligned for this test");

            using WGPUInstance instance = WGPU.wgpuCreateInstance(new WGPUInstanceDescriptor { NextInChain = &extras->Chain });
            Assert.IsFalse(instance.IsNull);
        }
        finally
        {
            NativeMemory.AlignedFree(block);
        }
    }

    /// <summary>
    /// wgpu padded Direct3D 12 pipeline state streams to 8 bytes instead of pointer size, so in a 32-bit process
    /// every render pipeline failed with E_INVALIDARG. The other GPU tests use the default backend, which can be
    /// Vulkan; this one always uses Direct3D 12.
    /// </summary>
    [TestMethod]
    public void D3D12RenderPipelinesCanBeCreated()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Direct3D 12 only.");
        }

        using WGPUInstance instance = CreateD3D12Instance();
        if (!TryRequestAdapter(instance, out WGPUAdapter adapter, out string? why))
        {
            Assert.Inconclusive(why!);
        }

        using (adapter)
        {
            using WGPUDevice device = RequestDevice(instance, adapter);
            ReadOnlySpan<byte> shader = """
                @vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
                    return vec4<f32>(f32(i) - 1.0, 0.0, 0.0, 1.0);
                }
                @fragment fn fs() -> @location(0) vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }
                """u8;
            var wgsl = new WGPUShaderSourceWGSL { Code = WGPUStringView.FromPinned(shader) };
            using WGPUShaderModule module = device.CreateShaderModule(new WGPUShaderModuleDescriptor { NextInChain = &wgsl.Chain });
            var colorTarget = new WGPUColorTargetState { Format = WGPUTextureFormat.RGBA8Unorm };
            var fragment = new WGPUFragmentState { Module = module, EntryPoint = WGPUStringView.FromPinned("fs"u8), TargetCount = 1, Targets = &colorTarget };

            device.PushErrorScope(WGPUErrorFilter.Internal);
            device.PushErrorScope(WGPUErrorFilter.Validation);
            using WGPURenderPipeline pipeline = device.CreateRenderPipeline(new WGPURenderPipelineDescriptor
            {
                Vertex = new WGPUVertexState { Module = module, EntryPoint = WGPUStringView.FromPinned("vs"u8) },
                Fragment = &fragment,
            });
            (WGPUErrorType validation, string validationMessage) = PopErrorScope(device);
            (WGPUErrorType internalError, string internalMessage) = PopErrorScope(device);
            Assert.AreEqual(WGPUErrorType.NoError, validation, validationMessage);
            Assert.AreEqual(WGPUErrorType.NoError, internalError, internalMessage);
        }

        Assert.AreEqual(0, s_uncapturedErrors);
    }

    /// <summary>Without a lost callback, releasing a device used to log a "device lost" warning.</summary>
    [TestMethod]
    public void ReleasingADeviceLogsNoLossWarning()
    {
        var log = new List<string>();
        WGPU.SetLogger(WGPULogLevel.Warn, (level, message) =>
        {
            lock (log)
            {
                log.Add($"{level}: {message}");
            }
        });
        try
        {
            using WGPUInstance instance = WGPU.wgpuCreateInstance(null);
            if (!TryRequestAdapter(instance, out WGPUAdapter adapter, out string? why))
            {
                Assert.Inconclusive(why!);
            }

            using (adapter)
            {
                WGPUDevice device = adapter.RequestDeviceSync(instance);
                device.Destroy();
                device.Dispose();
            }
        }
        finally
        {
            WGPU.SetLogger(WGPULogLevel.Off, null);
        }

        lock (log)
        {
            Assert.IsFalse(log.Any(l => l.Contains("device lost", StringComparison.OrdinalIgnoreCase)), string.Join('\n', log));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static WGPUInstance CreateD3D12Instance(WGPUInstanceFlag flags = WGPUInstanceFlag.Empty)
    {
        var extras = new WGPUInstanceExtras
        {
            Chain = new WGPUChainedStruct { SType = WGPUSType.InstanceExtras },
            Backends = WGPUInstanceBackend.DX12,
            Flags = flags,
        };
        return WGPU.wgpuCreateInstance(new WGPUInstanceDescriptor { NextInChain = &extras.Chain });
    }

    private static bool TryRequestAdapter(WGPUInstance instance, out WGPUAdapter adapter, out string? why)
    {
        try
        {
            adapter = instance.RequestAdapterSync();
            why = null;
            return true;
        }
        catch (WGPUException e)
        {
            adapter = default;
            why = "No adapter: " + e.Message;
            return false;
        }
    }

    private static WGPUDevice RequestDevice(WGPUInstance instance, WGPUAdapter adapter) =>
        adapter.RequestDeviceSync(instance, new WGPUDeviceDescriptor
        {
            DeviceLostCallbackInfo = new WGPUDeviceLostCallbackInfo { Mode = WGPUCallbackMode.AllowSpontaneous, Callback = &OnDeviceLost },
            UncapturedErrorCallbackInfo = new WGPUUncapturedErrorCallbackInfo { Callback = &OnUncapturedError },
        });

    private static WGPUSurface CreateSurface(WGPUInstance instance, HiddenWindow window)
    {
        var source = new WGPUSurfaceSourceWindowsHWND { Hwnd = (void*)window.Handle, Hinstance = (void*)HiddenWindow.GetModuleHandleW(null) };
        var descriptor = new WGPUSurfaceDescriptor { NextInChain = &source.Chain };
        return instance.CreateSurface(&descriptor);
    }

    private static void Present(WGPUSurface surface)
    {
        var texture = new WGPUSurfaceTexture();
        surface.GetCurrentTexture(&texture);
        Assert.IsTrue(texture.Status is WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal or WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal, texture.Status.ToString());
        Assert.IsFalse(texture.Texture.IsNull);
        surface.Present();
        texture.Texture.Dispose();
    }

    private static void SubmitEmpty(WGPUDevice device, WGPUQueue queue)
    {
        using WGPUCommandEncoder encoder = device.CreateCommandEncoder();
        WGPUCommandBuffer commands = encoder.Finish();
        queue.Submit(1, &commands);
        commands.Dispose();
    }

    private struct ScopeResult
    {
        public int Calls;
        public WGPUErrorType Type;
        public string? Message;
    }

    private static (WGPUErrorType Type, string Message) PopErrorScope(WGPUDevice device)
    {
        var result = new ScopeResult();
        device.PopErrorScope(new WGPUPopErrorScopeCallbackInfo
        {
            Mode = WGPUCallbackMode.AllowSpontaneous,
            Callback = &OnPopErrorScope,
            Userdata1 = Unsafe.AsPointer(ref result),
        });
        Assert.AreEqual(1, result.Calls);
        return (result.Type, result.Message ?? "");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPopErrorScope(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2)
    {
        ref ScopeResult result = ref Unsafe.AsRef<ScopeResult>(userdata1);
        result.Calls++;
        result.Type = type;
        result.Message = message.ToString();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLost(WGPUDevice* device, WGPUDeviceLostReason reason, WGPUStringView message, void* userdata1, void* userdata2)
    {
        // Devices released by the tests report Destroyed; only real losses count.
        if (reason == WGPUDeviceLostReason.Destroyed)
        {
            return;
        }

        s_lostReason = reason;
        s_lostMessage = message.ToString();
        Interlocked.Increment(ref s_lostCalls);
        s_lost.Set();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedError(WGPUDevice* device, WGPUErrorType type, WGPUStringView message, void* userdata1, void* userdata2)
    {
        Interlocked.Increment(ref s_uncapturedErrors);
        Console.Error.WriteLine($"{type}: {message}");
    }

    /// <summary>A never-shown Win32 window to put swap chains on.</summary>
    private sealed class HiddenWindow : IDisposable
    {
        public HiddenWindow()
        {
            fixed (char* cls = "STATIC")
            fixed (char* title = "WGpuFury surface test")
            {
                Handle = CreateWindowExW(0, cls, title, 0x00CF0000 /* WS_OVERLAPPEDWINDOW */, 0, 0, 320, 240, 0, 0, GetModuleHandleW(null), null);
            }

            Assert.AreNotEqual(0, Handle, "CreateWindowExW failed");
        }

        public nint Handle { get; }

        public void Dispose() => DestroyWindow(Handle);

        [DllImport("user32.dll")]
        private static extern nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, void* param);

        [DllImport("user32.dll")]
        private static extern int DestroyWindow(nint window);

        [DllImport("kernel32.dll")]
        public static extern nint GetModuleHandleW(char* moduleName);
    }

    /// <summary>Just enough DXGI and Direct3D 12 to remove the device wgpu uses.</summary>
    private static class D3D12
    {
        private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IidD3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
        private static readonly Guid IidD3D12Device5 = new("8b4f173b-2fea-4b80-8f58-4307191ab95d");
        private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
        private const int FeatureLevel11_0 = 0xb000;

        // Vtable slots, from the declaration order in dxgi.h and d3d12.h.
        private const int ReleaseSlot = 2;
        private const int QueryInterfaceSlot = 0;
        private const int EnumAdapters1Slot = 12;            // IDXGIFactory1
        private const int GetDesc1Slot = 10;                 // IDXGIAdapter1
        private const int GetDeviceRemovedReasonSlot = 37;   // ID3D12Device
        private const int RemoveDeviceSlot = 58;             // ID3D12Device5

        /// <summary>
        /// Removes the Direct3D 12 device of every adapter with these PCI ids. D3D12CreateDevice returns the
        /// process's existing device for an adapter, which is the one wgpu created.
        /// </summary>
        public static int RemoveDevices(uint vendorId, uint deviceId)
        {
            void* factory;
            Guid iid = IidDxgiFactory1;
            Check(CreateDXGIFactory1(&iid, &factory), "CreateDXGIFactory1");
            int removed = 0;
            byte* desc = stackalloc byte[512]; // DXGI_ADAPTER_DESC1: WCHAR[128], then VendorId, DeviceId, ...
            try
            {
                for (uint i = 0; ; i++)
                {
                    void* adapter;
                    int hr = Call(factory, EnumAdapters1Slot, i, &adapter);
                    if (hr == DxgiErrorNotFound)
                    {
                        break;
                    }

                    Check(hr, "IDXGIFactory1::EnumAdapters1");
                    try
                    {
                        Check(Call(adapter, GetDesc1Slot, desc), "IDXGIAdapter1::GetDesc1");
                        if (*(uint*)(desc + 256) != vendorId || *(uint*)(desc + 260) != deviceId)
                        {
                            continue;
                        }

                        Remove(adapter);
                        removed++;
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
            }
            finally
            {
                Release(factory);
            }

            return removed;
        }

        private static void Remove(void* adapter)
        {
            void* device;
            Guid iid = IidD3D12Device;
            Check(D3D12CreateDevice(adapter, FeatureLevel11_0, &iid, &device), "D3D12CreateDevice");
            try
            {
                Check(Call(device, GetDeviceRemovedReasonSlot), "the device was already removed");
                void* device5;
                Guid iid5 = IidD3D12Device5;
                Check(Call(device, QueryInterfaceSlot, &iid5, &device5), "QueryInterface(ID3D12Device5)");
                ((delegate* unmanaged[Stdcall]<void*, void>)(*(void***)device5)[RemoveDeviceSlot])(device5);
                Release(device5);
                Assert.AreEqual(DxgiErrorDeviceRemoved, Call(device, GetDeviceRemovedReasonSlot), "ID3D12Device5::RemoveDevice didn't remove the device");
            }
            finally
            {
                Release(device);
            }
        }

        private static int Call(void* self, int slot) =>
            ((delegate* unmanaged[Stdcall]<void*, int>)(*(void***)self)[slot])(self);

        private static int Call(void* self, int slot, void* arg) =>
            ((delegate* unmanaged[Stdcall]<void*, void*, int>)(*(void***)self)[slot])(self, arg);

        private static int Call(void* self, int slot, uint arg1, void* arg2) =>
            ((delegate* unmanaged[Stdcall]<void*, uint, void*, int>)(*(void***)self)[slot])(self, arg1, arg2);

        private static int Call(void* self, int slot, void* arg1, void* arg2) =>
            ((delegate* unmanaged[Stdcall]<void*, void*, void*, int>)(*(void***)self)[slot])(self, arg1, arg2);

        private static void Release(void* self) =>
            ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)self)[ReleaseSlot])(self);

        private static void Check(int hr, string what)
        {
            if (hr < 0)
            {
                throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
            }
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(Guid* riid, void** factory);

        [DllImport("d3d12.dll")]
        private static extern int D3D12CreateDevice(void* adapter, int minimumFeatureLevel, Guid* riid, void** device);
    }
}
