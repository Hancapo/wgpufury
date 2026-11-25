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
    private const string ChildReportVariable = "WGPUFURY_DEVICE_REMOVAL_REPORT";
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

        if (Environment.GetEnvironmentVariable(ChildReportVariable) is { } reportFile)
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

        string report = Path.GetTempFileName();
        try
        {
            var psi = new ProcessStartInfo(Path.ChangeExtension(typeof(RobustnessTests).Assembly.Location, ".exe"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--filter");
            psi.ArgumentList.Add("FullyQualifiedName~" + nameof(RemovingTheD3D12DeviceFiresTheLostCallback));
            psi.Environment[ChildReportVariable] = report;

            using Process child = Process.Start(psi)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync();
            Task<string> stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                child.Kill(entireProcessTree: true);
                Assert.Fail("The device-removal child process hung:\n" + stdout.Result + stderr.Result);
            }

            string[] lines = File.ReadAllLines(report);
            foreach (string line in lines)
            {
                TestContext.WriteLine(line);
            }

            Assert.AreEqual(0, child.ExitCode, $"The device-removal child process failed:\n{string.Join('\n', lines)}\n{stdout.Result}{stderr.Result}");
            if (lines.FirstOrDefault() is { } first && first.StartsWith("skip: ", StringComparison.Ordinal))
            {
                Assert.Inconclusive(first);
            }
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

        using WGPUInstance instance = CreateD3D12Instance();
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
            report.Add($"adapter: {info.Device} (0x{vendorId:X4}:0x{deviceId:X4})");
            info.FreeMembers();

            WGPUDevice device = RequestDevice(instance, adapter);
            WGPUQueue queue = device.GetQueue();
            WGPUBuffer readback = device.CreateBuffer(new WGPUBufferDescriptor { Size = 256, Usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst });
            SubmitEmpty(device, queue);
            Assert.AreEqual(WGPUNativePollStatus.QueueEmpty, WGPU.wgpuDevicePoll(device, true, null, 0));
            Assert.IsFalse(s_lost.IsSet, "the device was lost before the removal");

            int removed = D3D12.RemoveDevices(vendorId, deviceId);
            Assert.AreNotEqual(0, removed, "found no Direct3D 12 device for the adapter");

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

    // ---------------------------------------------------------------- helpers

    private static WGPUInstance CreateD3D12Instance()
    {
        var extras = new WGPUInstanceExtras
        {
            Chain = new WGPUChainedStruct { SType = WGPUSType.InstanceExtras },
            Backends = WGPUInstanceBackend.DX12,
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
