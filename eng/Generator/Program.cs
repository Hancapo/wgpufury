using WGpuFury.Generator;

// Usage: dotnet run --project eng/Generator [-- <include dir>]
// Reads webgpu.h + wgpu.h and regenerates src/WGpuFury/Generated, the tests' layout probe and the C ABI probe.
string root = FindRepoRoot();
string include = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Directory.EnumerateDirectories(Path.Combine(root, "native"))
        .Select(d => Path.Combine(d, "include", "webgpu"))
        .FirstOrDefault(Directory.Exists)
      ?? throw new DirectoryNotFoundException("No headers found. Run `dotnet run eng/build-native.cs` first.");

string version = (string)System.Text.Json.Nodes.JsonNode.Parse(
    File.ReadAllText(Path.Combine(root, "eng", "native.lock.json")))!["version"]!;
var model = new HeaderModel();
var parser = new HeaderParser(model);
parser.Parse(Path.Combine(include, "webgpu.h"));
parser.Parse(Path.Combine(include, "wgpu.h"));

string[] unimplemented = File.ReadAllLines(Path.Combine(root, "eng", "Generator", "unimplemented.txt"))
    .Select(l => l.Trim())
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .ToArray();
var emitter = new Emitter(model, version, unimplemented);
string generated = Path.Combine(root, "src", "WGpuFury", "Generated");
Directory.CreateDirectory(generated);
Write(Path.Combine(generated, "Enums.g.cs"), emitter.EmitEnums());
Write(Path.Combine(generated, "Structs.g.cs"), emitter.EmitStructs());
Write(Path.Combine(generated, "Handles.g.cs"), emitter.EmitHandles());
Write(Path.Combine(generated, "StructMethods.g.cs"), emitter.EmitStructMethods());
Write(Path.Combine(generated, "WGPU.g.cs"), emitter.EmitFunctions());
Write(Path.Combine(root, "tests", "WGpuFury.Tests", "Generated", "ManagedLayouts.g.cs"), emitter.EmitLayoutProbeCs());
Write(Path.Combine(root, "tests", "abi", "layout_probe.c"), emitter.EmitLayoutProbeC());
Console.WriteLine($"Generated from {include} ({version}): {emitter.Summary()}");
return 0;

static void Write(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
    Console.WriteLine($"  wrote {path}");
}

static string FindRepoRoot()
{
    for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
    {
        if (File.Exists(Path.Combine(dir, "eng", "native.lock.json")))
        {
            return dir;
        }
    }

    throw new InvalidOperationException("Could not locate the repository root.");
}
