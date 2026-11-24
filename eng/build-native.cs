// Builds wgpu-native from source into ./native/<rid>/ (include/webgpu/*.h and lib/*), as pinned
// by eng/native.lock.json: a wgpu-native commit plus the patches in eng/patches.
//
//   dotnet run eng/build-native.cs                       # the RID of this machine
//   dotnet run eng/build-native.cs -- win-x64 win-arm64  # cross-compile (needs the MSVC/SDK bits)
//   dotnet run eng/build-native.cs -- --prepare          # only check out and patch native/src
//   dotnet run eng/build-native.cs -- --collect linux-x64  # only copy an existing cargo build
//   dotnet run eng/build-native.cs -- --force            # rebuild even if native/<rid> is current
//
// Needs git, rustup (the toolchain comes from wgpu-native's rust-toolchain.toml) and libclang
// for bindgen. On Windows, LIBCLANG_PATH defaults to the LLVM installer's location.
#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

string repoRoot = FindRepoRoot();
JsonObject lockFile = JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot, "eng", "native.lock.json")))!.AsObject();
string version = (string)lockFile["version"]!;
string repository = (string)lockFile["source"]!["repository"]!;
string commit = (string)lockFile["source"]!["commit"]!;
string[] patches = lockFile["patches"]!.AsArray().Select(p => Path.Combine(repoRoot, "eng", "patches", (string)p!)).ToArray();
JsonObject targets = lockFile["targets"]!.AsObject();

bool prepareOnly = args.Contains("--prepare");
bool collectOnly = args.Contains("--collect");
bool force = args.Contains("--force");
List<string> rids = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
if (rids.Count == 0 && !prepareOnly)
{
    rids.Add(HostRid());
}

foreach (string rid in rids)
{
    if (targets[rid] is null)
    {
        Console.Error.WriteLine($"Unknown RID '{rid}'. Known: {string.Join(", ", targets.Select(t => t.Key))}");
        return 1;
    }
}

// The stamp identifies the exact sources: commit + patch contents + version string.
string stamp = ComputeStamp();
string src = Path.Combine(repoRoot, "native", "src");

if (!collectOnly)
{
    List<string> todo = rids.Where(rid => force || !IsCurrent(rid)).ToList();
    foreach (string rid in rids.Except(todo))
    {
        Console.WriteLine($"{rid}: {version} ({stamp[..12]}) already built");
    }

    if (todo.Count == 0 && !prepareOnly)
    {
        return 0;
    }

    Prepare();
    if (prepareOnly)
    {
        return 0;
    }

    foreach (string rid in todo)
    {
        string triple = (string)targets[rid]!;
        string toolchain = RustToolchain();
        Run(src, "rustup", "toolchain", "install", toolchain, "--profile", "minimal", "--no-self-update");
        Run(src, "rustup", "target", "add", "--toolchain", toolchain, triple);
        var env = new Dictionary<string, string>
        {
            ["WGPU_NATIVE_VERSION"] = "v" + version,
        };
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("LIBCLANG_PATH") is null
            && File.Exists(@"C:\Program Files\LLVM\bin\libclang.dll"))
        {
            env["LIBCLANG_PATH"] = @"C:\Program Files\LLVM\bin";
        }

        if (triple.EndsWith("-apple-darwin", StringComparison.Ordinal))
        {
            env["MACOSX_DEPLOYMENT_TARGET"] = triple.StartsWith("x86_64", StringComparison.Ordinal) ? "10.13" : "11.0";
            env["RUSTFLAGS"] = "-C link-args=-Wl,-install_name,@rpath/libwgpu_native.dylib";
        }

        Console.WriteLine($"{rid}: cargo build --release --target {triple}");
        RunWith(src, "cargo", env, "build", "--release", "--target", triple);
        Collect(rid);
    }
}
else
{
    foreach (string rid in rids)
    {
        Collect(rid);
    }
}

return 0;

void Prepare()
{
    string stampFile = Path.Combine(src, ".wgpufury-stamp");
    if (File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == stamp)
    {
        return;
    }

    if (!Directory.Exists(Path.Combine(src, ".git")))
    {
        Directory.CreateDirectory(src);
        Run(src, "git", "init", "--quiet");
        Run(src, "git", "config", "core.autocrlf", "false");
        Run(src, "git", "remote", "add", "origin", repository);
    }

    Console.WriteLine($"source: {repository} @ {commit[..12]} + {patches.Length} patches");
    if (!RunOk(src, "git", "cat-file", "-e", commit + "^{commit}"))
    {
        Run(src, "git", "fetch", "--quiet", "--depth", "1", "origin", commit);
    }

    // Reset to the pinned commit (keeping cargo's target/ cache) and apply the patches on top.
    Run(src, "git", "checkout", "--quiet", "--force", "--detach", commit);
    Run(src, "git", "clean", "--quiet", "-fdx", "--exclude", "target");
    Run(src, "git", "submodule", "update", "--quiet", "--init", "--depth", "1", "ffi/webgpu-headers");
    Run(src, "git", ["-c", "user.name=WGpuFury", "-c", "user.email=wgpufury@users.noreply.github.com", "am", "--quiet", "--keep-cr", .. patches]);
    File.WriteAllText(stampFile, stamp);
}

void Collect(string rid)
{
    string triple = (string)targets[rid]!;
    string release = Path.Combine(src, "target", triple, "release");
    string[] files = triple switch
    {
        _ when triple.Contains("windows") => ["wgpu_native.dll", "wgpu_native.dll.lib", "wgpu_native.lib", "wgpu_native.pdb"],
        _ when triple.Contains("apple") => ["libwgpu_native.dylib", "libwgpu_native.a"],
        _ => ["libwgpu_native.so", "libwgpu_native.a"],
    };

    string dest = Path.Combine(repoRoot, "native", rid);
    if (Directory.Exists(dest))
    {
        Directory.Delete(dest, recursive: true);
    }

    string lib = Path.Combine(dest, "lib");
    string include = Path.Combine(dest, "include", "webgpu");
    Directory.CreateDirectory(lib);
    Directory.CreateDirectory(include);
    foreach (string file in files)
    {
        File.Copy(Path.Combine(release, file), Path.Combine(lib, file));
    }

    File.Copy(Path.Combine(src, "ffi", "webgpu-headers", "webgpu.h"), Path.Combine(include, "webgpu.h"));
    File.Copy(Path.Combine(src, "ffi", "wgpu.h"), Path.Combine(include, "wgpu.h"));
    File.WriteAllText(Path.Combine(dest, ".version"), $"{version} {stamp}");
    Console.WriteLine($"{rid}: {Path.GetRelativePath(repoRoot, dest)} ({string.Join(", ", files)})");
}

// The toolchain wgpu-native pins in rust-toolchain.toml, installed explicitly because not every rustup
// version installs it on first use.
string RustToolchain()
{
    string toml = File.ReadAllText(Path.Combine(src, "rust-toolchain.toml"));
    int key = toml.IndexOf("channel", StringComparison.Ordinal);
    int open = toml.IndexOf('"', key) + 1;
    return toml[open..toml.IndexOf('"', open)];
}

bool IsCurrent(string rid)
{
    string file = Path.Combine(repoRoot, "native", rid, ".version");
    return File.Exists(file) && File.ReadAllText(file).Trim() == $"{version} {stamp}";
}

string ComputeStamp()
{
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    sha.AppendData(Encoding.UTF8.GetBytes($"{version}\n{repository}\n{commit}\n"));
    foreach (string patch in patches)
    {
        // Line endings must not change the stamp (or the patch) across checkouts.
        sha.AppendData(Encoding.UTF8.GetBytes(File.ReadAllText(patch).ReplaceLineEndings("\n")));
    }

    return Convert.ToHexStringLower(sha.GetHashAndReset());
}

static void Run(string cwd, string file, params string[] arguments) => RunWith(cwd, file, new Dictionary<string, string>(), arguments);

static void RunWith(string cwd, string file, Dictionary<string, string> env, params string[] arguments)
{
    if (Start(cwd, file, env, arguments, quiet: false) is int code and not 0)
    {
        throw new InvalidOperationException($"'{file} {string.Join(' ', arguments)}' exited with {code}");
    }
}

static bool RunOk(string cwd, string file, params string[] arguments) =>
    Start(cwd, file, new Dictionary<string, string>(), arguments, quiet: true) == 0;

static int Start(string cwd, string file, Dictionary<string, string> env, string[] arguments, bool quiet)
{
    var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardError = quiet, RedirectStandardOutput = quiet };
    foreach (string a in arguments)
    {
        psi.ArgumentList.Add(a);
    }

    foreach ((string key, string value) in env)
    {
        psi.Environment[key] = value;
    }

    using Process process = Process.Start(psi)!;
    if (quiet)
    {
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
    }

    process.WaitForExit();
    return process.ExitCode;
}

static string HostRid()
{
    string arch = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        var a => throw new PlatformNotSupportedException(a.ToString()),
    };
    return OperatingSystem.IsWindows() ? "win-" + arch : OperatingSystem.IsMacOS() ? "osx-" + arch : "linux-" + arch;
}

static string FindRepoRoot()
{
    for (string? dir = Directory.GetCurrentDirectory(); dir != null; dir = Path.GetDirectoryName(dir))
    {
        if (File.Exists(Path.Combine(dir, "eng", "native.lock.json")))
        {
            return dir;
        }
    }

    throw new InvalidOperationException("Run from inside the WGpuFury repository.");
}
