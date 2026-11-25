// Builds wgpu-native from source into ./native/<rid>/ (include/webgpu/*.h and lib/*), as pinned
// by eng/native.lock.json: a wgpu-native commit plus the patches in eng/patches, built against a
// wgpu commit plus the patches in eng/patches/wgpu. The sources go to native/src and native/wgpu.
//
//   dotnet run eng/build-native.cs                       # the RID of this machine
//   dotnet run eng/build-native.cs -- win-x64 win-arm64  # cross-compile (needs the MSVC/SDK bits)
//   dotnet run eng/build-native.cs -- --prepare          # only check out and patch native/src
//   dotnet run eng/build-native.cs -- --collect linux-x64  # only copy an existing cargo build
//   dotnet run eng/build-native.cs -- --force            # rebuild even if native/<rid> is current
//
// Upkeep: do the patches still apply to newer upstream commits? Each defaults to the pinned commit.
//   dotnet run eng/build-native.cs -- --check-patches --check-source=<wgpu-native commit> --check-wgpu=<wgpu commit>
//
// Needs git, rustup (the toolchain comes from wgpu-native's rust-toolchain.toml) and libclang
// for bindgen. On Windows, LIBCLANG_PATH defaults to the LLVM installer's location.
#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

string repoRoot = FindRepoRoot();
JsonObject lockFile = JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot, "eng", "native.lock.json")))!.AsObject();
string version = (string)lockFile["version"]!;
string repository = (string)lockFile["source"]!["repository"]!;
string commit = (string)lockFile["source"]!["commit"]!;
string[] patches = lockFile["patches"]!.AsArray().Select(p => Path.Combine(repoRoot, "eng", "patches", (string)p!)).ToArray();
JsonObject targets = lockFile["targets"]!.AsObject();
string wgpuRepository = (string)lockFile["wgpuSource"]!["repository"]!;
string wgpuCommit = (string)lockFile["wgpuSource"]!["commit"]!;
string[] wgpuPatches = lockFile["wgpuSource"]!["patches"]!.AsArray().Select(p => Path.Combine(repoRoot, "eng", "patches", (string)p!)).ToArray();

string? checkSource = Option("--check-source");
string? checkWgpu = Option("--check-wgpu");
if (args.Contains("--check-patches") || checkSource != null || checkWgpu != null)
{
    return CheckPatches(checkSource ?? commit, checkWgpu ?? wgpuCommit);
}

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
string wgpuSrc = Path.Combine(repoRoot, "native", "wgpu");

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

    Console.WriteLine($"wgpu-native: {repository} @ {commit[..12]} + {patches.Length} patches");
    Checkout(src, repository, commit, patches, submodule: "ffi/webgpu-headers");
    Console.WriteLine($"wgpu: {wgpuRepository} @ {wgpuCommit[..12]} + {wgpuPatches.Length} patches");
    Checkout(wgpuSrc, wgpuRepository, wgpuCommit, wgpuPatches, submodule: null);
    UsePatchedWgpu();
    File.WriteAllText(stampFile, stamp);
}

// Resets `dir` to the pinned commit (keeping cargo's target/ cache) and applies the patches on top.
void Checkout(string dir, string repo, string pinned, string[] patchFiles, string? submodule)
{
    if (!Directory.Exists(Path.Combine(dir, ".git")))
    {
        Directory.CreateDirectory(dir);
        Run(dir, "git", "init", "--quiet");
        Run(dir, "git", "config", "core.autocrlf", "false");
        Run(dir, "git", "remote", "add", "origin", repo);
    }
    else
    {
        Run(dir, "git", "remote", "set-url", "origin", repo);
    }

    if (!RunOk(dir, "git", "cat-file", "-e", pinned + "^{commit}"))
    {
        Run(dir, "git", "fetch", "--quiet", "--depth", "1", "origin", pinned);
    }

    Run(dir, "git", "checkout", "--quiet", "--force", "--detach", pinned);
    Run(dir, "git", "clean", "--quiet", "-fdx", "--exclude", "target");
    if (submodule != null)
    {
        Run(dir, "git", "submodule", "update", "--quiet", "--init", "--depth", "1", submodule);
    }

    if (patchFiles.Length > 0)
    {
        Run(dir, "git", ["-c", "user.name=WGpuFury", "-c", "user.email=wgpufury@users.noreply.github.com", "am", "--quiet", "--keep-cr", .. patchFiles]);
    }
}

// Points every wgpu crate in wgpu-native's Cargo.lock at the patched checkout in native/wgpu, with a
// Cargo [patch]. All of them, not only the patched ones, so that no crate is built from two sources.
void UsePatchedWgpu()
{
    string lockText = File.ReadAllText(Path.Combine(src, "Cargo.lock"));
    var fromWgpu = new Regex(@"name = ""([^""]+)""\r?\nversion = ""[^""]+""\r?\nsource = ""git\+" + Regex.Escape(wgpuRepository) + "[?#]");
    string[] used = fromWgpu.Matches(lockText).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
    if (used.Length == 0)
    {
        throw new InvalidOperationException($"wgpu-native's Cargo.lock has no crates from {wgpuRepository}.");
    }

    var packageName = new Regex(@"^\[package\][^\[]*?^name\s*=\s*""([^""]+)""", RegexOptions.Multiline);
    var crateDirs = new Dictionary<string, string>();
    foreach (string manifest in Directory.EnumerateFiles(wgpuSrc, "Cargo.toml", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(wgpuSrc, manifest).Replace('\\', '/');
        if (!relative.StartsWith(".git/", StringComparison.Ordinal) && !relative.StartsWith("target/", StringComparison.Ordinal)
            && packageName.Match(File.ReadAllText(manifest)) is { Success: true } name)
        {
            crateDirs.TryAdd(name.Groups[1].Value, Path.GetDirectoryName(relative)!.Replace('\\', '/'));
        }
    }

    var section = new StringBuilder();
    section.Append("\n# Added by eng/build-native.cs: build against the patched wgpu in native/wgpu.\n");
    section.Append($"[patch.\"{wgpuRepository}\"]\n");
    foreach (string crate in used)
    {
        if (!crateDirs.TryGetValue(crate, out string? dir))
        {
            throw new InvalidOperationException($"The wgpu checkout has no crate '{crate}'.");
        }

        section.Append($"{crate} = {{ path = \"../wgpu/{dir}\" }}\n");
    }

    File.AppendAllText(Path.Combine(src, "Cargo.toml"), section.ToString());
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
    sha.AppendData(Encoding.UTF8.GetBytes($"{version}\n{repository}\n{commit}\n{wgpuRepository}\n{wgpuCommit}\n"));
    foreach (string patch in patches.Concat(wgpuPatches))
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

// `--name=value`, or null.
string? Option(string name) =>
    args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

// Applies the patches, one by one with 3-way merges, onto other commits (a new wgpu-native or wgpu release, say) in
// throwaway checkouts under native/upstream-check. Prints a Markdown report, also written to the job summary in
// GitHub Actions. Returns 0 if every patch still applies. A patch that doesn't apply is skipped, so the later ones
// are still tried; one that needed it fails too.
int CheckPatches(string sourceTarget, string wgpuTarget)
{
    string root = Path.Combine(repoRoot, "native", "upstream-check");
    var report = new StringBuilder("| Repository | Commit | Patch | Result |\n|---|---|---|---|\n");
    bool sourceOk = CheckOnto(Path.Combine(root, "wgpu-native"), repository, commit, sourceTarget, patches, report);
    bool wgpuOk = CheckOnto(Path.Combine(root, "wgpu"), wgpuRepository, wgpuCommit, wgpuTarget, wgpuPatches, report);
    report.Append($"\nThe checkouts are in `native/upstream-check`. To rebase a patch, fix it there, commit, and regenerate it with `git format-patch`.\n");

    Console.WriteLine(report);
    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
    {
        File.AppendAllText(summary, report.ToString());
    }

    return sourceOk && wgpuOk ? 0 : 1;
}

bool CheckOnto(string dir, string repo, string pinned, string target, string[] patchFiles, StringBuilder report)
{
    Checkout(dir, repo, target, [], submodule: null);
    // The patches were made against the pinned commit: its blobs let git reconstruct their base for 3-way merges.
    if (!RunOk(dir, "git", "cat-file", "-e", pinned + "^{commit}"))
    {
        Run(dir, "git", "fetch", "--quiet", "--depth", "1", "origin", pinned);
    }

    string name = Path.GetFileNameWithoutExtension(repo);
    string shortTarget = target.Length > 12 ? target[..12] : target;
    bool allApply = true;
    foreach (string patch in patchFiles)
    {
        (int code, string output) = Capture(dir, "git", "-c", "user.name=WGpuFury", "-c", "user.email=wgpufury@users.noreply.github.com", "am", "--3way", "--keep-cr", patch);
        string result;
        if (code == 0)
        {
            result = output.Contains("3-way merge", StringComparison.Ordinal) ? "applies with a 3-way merge" : "applies";
        }
        else
        {
            allApply = false;
            string[] conflicts = output.Split('\n')
                .Where(l => l.StartsWith("CONFLICT", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
                .Select(l => l.Trim().Replace("|", "\\|", StringComparison.Ordinal))
                .Take(3)
                .ToArray();
            result = "**doesn't apply**" + (conflicts.Length > 0 ? ": " + string.Join("; ", conflicts) : "");
            RunOk(dir, "git", "am", "--abort");
        }

        report.Append($"| {name} | `{shortTarget}`{(target == pinned ? " (pinned)" : "")} | {Path.GetRelativePath(Path.Combine(repoRoot, "eng", "patches"), patch).Replace('\\', '/')} | {result} |\n");
    }

    return allApply;
}

static (int Code, string Output) Capture(string cwd, string file, params string[] arguments)
{
    var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (string a in arguments)
    {
        psi.ArgumentList.Add(a);
    }

    using Process process = Process.Start(psi)!;
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    string output = process.StandardOutput.ReadToEnd() + stderr.Result;
    process.WaitForExit();
    return (process.ExitCode, output);
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
