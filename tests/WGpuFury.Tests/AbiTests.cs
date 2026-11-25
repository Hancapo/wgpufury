using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WGpuFury.Tests;

/// <summary>
/// Checks the generated C# structs against the C compiler's view of the headers
/// (tests/abi/&lt;rid&gt;.json, produced by eng/run-abi-probe.ps1), and the imports against the native exports.
/// </summary>
[TestClass]
public sealed class AbiTests
{
    /// <summary>Set by CI to the RID each leg is meant to test.</summary>
    private static readonly string? s_expectedRid = Environment.GetEnvironmentVariable("WGPUFURY_EXPECTED_RID");

    [TestMethod]
    public void TestsRunAsTheExpectedRid()
    {
        if (s_expectedRid == null)
        {
            Assert.Inconclusive("WGPUFURY_EXPECTED_RID is not set (only CI sets it).");
        }

        Assert.AreEqual(s_expectedRid, RuntimeInformation.RuntimeIdentifier);
    }

    [TestMethod]
    public void StructLayoutsMatchC()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "abi", RuntimeInformation.RuntimeIdentifier + ".json");
        if (!File.Exists(path))
        {
            // In CI every leg produces or has a dump, so a missing one is a failure there.
            string message = $"No C layout dump for {RuntimeInformation.RuntimeIdentifier}; run eng/run-abi-probe.ps1 on that platform.";
            if (s_expectedRid != null)
            {
                Assert.Fail(message);
            }

            Assert.Inconclusive(message);
        }

        using JsonDocument c = JsonDocument.Parse(File.ReadAllText(path));
        var managed = ManagedLayouts.All();
        var mismatches = new List<string>();
        foreach (var (name, size, fields) in managed)
        {
            if (!c.RootElement.TryGetProperty(name, out JsonElement expected))
            {
                mismatches.Add($"{name}: missing from the C dump");
                continue;
            }

            uint cSize = expected.GetProperty("size").GetUInt32();
            if (cSize != size)
            {
                mismatches.Add($"{name}: sizeof C={cSize} C#={size}");
            }

            JsonElement cFields = expected.GetProperty("fields");
            foreach (var (field, offset) in fields)
            {
                uint cOffset = cFields.GetProperty(field).GetUInt32();
                if (cOffset != offset)
                {
                    mismatches.Add($"{name}.{field}: offsetof C={cOffset} C#={offset}");
                }
            }

            if (cFields.EnumerateObject().Count() != fields.Length)
            {
                mismatches.Add($"{name}: C has {cFields.EnumerateObject().Count()} fields, C# has {fields.Length}");
            }
        }

        Assert.AreEqual(c.RootElement.EnumerateObject().Count(), managed.Count, "struct count");
        Assert.IsEmpty(mismatches, string.Join(Environment.NewLine, mismatches));
    }

    [TestMethod]
    public void EveryImportIsExportedByTheNativeLibrary()
    {
        nint lib = NativeLibrary.Load(WGPU.LibraryName, typeof(WGPU).Assembly, DllImportSearchPath.AssemblyDirectory);
        var missing = NativeExports.Functions.Where(f => !NativeLibrary.TryGetExport(lib, f, out _)).ToList();
        Assert.IsEmpty(missing, "Missing exports: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void NativeVersionMatchesBindings()
    {
        Assert.AreEqual(Version.Parse(WGPU.BindingsVersion), WGPU.NativeVersion);
    }

    [TestMethod]
    public void AllTypesAreBlittable()
    {
        // DisableRuntimeMarshalling requires every P/Invoke signature to be unmanaged; check the public structs too.
        var notUnmanaged = typeof(WGPU).Assembly.GetExportedTypes()
            .Where(t => t.IsValueType && !t.IsEnum && t.Name.StartsWith("WGPU", StringComparison.Ordinal) && !t.IsByRefLike)
            .Where(t => !IsUnmanaged(t))
            .Select(t => t.Name)
            .ToList();
        Assert.IsEmpty(notUnmanaged, string.Join(", ", notUnmanaged));
    }

    private static bool IsUnmanaged(Type t) =>
        t.IsPrimitive || t.IsPointer || t.IsEnum || t.IsFunctionPointer || t == typeof(nint) || t == typeof(nuint)
        || (t.IsValueType && t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(f => IsUnmanaged(f.FieldType)));
}
