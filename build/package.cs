#!/usr/bin/env dotnet
#:property PublishAot=false
#:property Nullable=enable
#:project DiskImage/DiskImage.csproj

// Builds ready-to-run AutoKey packages into ./artifacts. Works on Windows, macOS and Linux:
//
//   dotnet run build/package.cs                              all platforms
//   dotnet run build/package.cs -- osx-arm64 linux-x64       selected runtimes
//   dotnet run build/package.cs -- --version 1.2.0
//
// Output:
//   AutoKey-<version>-win-x64.exe          single self-contained executable
//   AutoKey-<version>-osx-<arch>.dmg       disk image with AutoKey.app (Apple Silicon: arm64, Intel: x64)
//   AutoKey-<version>-osx-<arch>.tar.gz    the same AutoKey.app as an archive
//   AutoKey-<version>-linux-<arch>.tar.gz  single executable + icon, menu entry and install.sh
//
// Archives are tar.gz so that executable permissions survive, whichever OS builds them. On macOS, the .dmg is made
// with Apple's hdiutil and the .app is ad-hoc signed as a whole; elsewhere the .dmg is written directly (an ISO 9660
// image with Rock Ridge extensions inside a compressed UDIF container). The executables inside the .app are signed
// either way, which Apple Silicon requires.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using AutoKey.Build.DiskImage;

const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
const UnixFileMode Regular = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

string root = FindRepositoryRoot();
string version = "1.0.0";
var runtimes = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--version" && i + 1 < args.Length)
    {
        version = args[++i];
    }
    else
    {
        runtimes.Add(args[i]);
    }
}

if (runtimes.Count == 0)
{
    runtimes.AddRange(["win-x64", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64"]);
}

string artifacts = Path.Combine(root, "artifacts");
Directory.CreateDirectory(artifacts);
var produced = new List<string>();

foreach (string runtime in runtimes)
{
    string work = Path.Combine(artifacts, "work", runtime);
    if (Directory.Exists(work))
    {
        Directory.Delete(work, recursive: true);
    }

    string publish = Path.Combine(work, "publish");
    bool mac = runtime.StartsWith("osx-", StringComparison.Ordinal);
    var publishArgs = new List<string>
    {
        "publish", Path.Combine(root, "src", "AutoKey", "AutoKey.csproj"),
        "--configuration", "Release", "--runtime", runtime, "--self-contained", "true", "--output", publish,
        $"-p:Version={version}", "-p:DebugType=none",
        "--disable-build-servers", // independent builds; a lingering build node can keep files locked
    };

    if (!mac)
    {
        // One executable; native libraries are unpacked next to each other on first start.
        publishArgs.AddRange(["-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true"]);
    }

    Console.WriteLine($"\n=== {runtime} ===");
    Run("dotnet", publishArgs);

    produced.AddRange(runtime switch
    {
        _ when runtime.StartsWith("win-", StringComparison.Ordinal) => [PackageWindows(publish, runtime)],
        _ when mac => PackageMac(publish, work, runtime),
        _ => [PackageLinux(publish, runtime)],
    });
}

Console.WriteLine("\nPackages:");
foreach (string package in produced)
{
    Console.WriteLine($"  {Path.GetRelativePath(root, package)}  ({new FileInfo(package).Length / 1048576.0:0.0} MB)");
}

string PackageWindows(string publish, string runtime)
{
    string destination = Path.Combine(artifacts, $"AutoKey-{version}-{runtime}.exe");
    File.Copy(Path.Combine(publish, "AutoKey.exe"), destination, overwrite: true);
    return destination;
}

string[] PackageMac(string publish, string work, string runtime)
{
    // The disk image shows AutoKey.app next to a shortcut to Applications, for drag-and-drop installation.
    string stage = Path.Combine(work, "dmg");
    string app = Path.Combine(stage, "AutoKey.app");
    string contents = Path.Combine(app, "Contents");
    CopyDirectory(publish, Path.Combine(contents, "MacOS"));
    Directory.CreateDirectory(Path.Combine(contents, "Resources"));
    File.Copy(Path.Combine(root, "build", "macos", "AutoKey.icns"), Path.Combine(contents, "Resources", "AutoKey.icns"));
    string plist = File.ReadAllText(Path.Combine(root, "build", "macos", "Info.plist")).Replace("@VERSION@", version);
    File.WriteAllText(Path.Combine(contents, "Info.plist"), plist);
    Func<string, bool> isExecutable = path =>
        Path.GetFileName(Path.GetDirectoryName(path)) == "MacOS" && (Path.GetExtension(path) is "" or ".dylib");

    string dmg = Path.Combine(artifacts, $"AutoKey-{version}-{runtime}.dmg");
    if (OperatingSystem.IsMacOS())
    {
        Run("codesign", ["--force", "--deep", "--sign", "-", app]);
        File.CreateSymbolicLink(Path.Combine(stage, "Applications"), "/Applications");

        // hdiutil occasionally fails with "Resource busy" on build machines; a retry usually succeeds.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Run("hdiutil", ["create", "-volname", "AutoKey", "-srcfolder", stage, "-fs", "HFS+", "-format", "UDZO", "-ov", dmg]);
                break;
            }
            catch (InvalidOperationException) when (attempt < 4)
            {
                Console.WriteLine($"hdiutil failed, retrying ({attempt}/3)…");
                Thread.Sleep(TimeSpan.FromSeconds(5 * attempt));
            }
        }
    }
    else
    {
        var iso = new IsoImageWriter();
        iso.AddDirectory(app, "AutoKey.app", isExecutable);
        iso.AddSymbolicLink("Applications", "/Applications");
        string isoPath = Path.Combine(work, "AutoKey.iso");
        using (FileStream stream = File.Create(isoPath))
        {
            iso.Write(stream, "AUTOKEY");
        }

        DmgWriter.Write(isoPath, dmg);
    }

    var entries = new List<(string Name, string? Source, bool Executable)>();
    AddDirectory(entries, app, "AutoKey.app", isExecutable);
    string archive = Path.Combine(artifacts, $"AutoKey-{version}-{runtime}.tar.gz");
    WriteTarGz(archive, entries);
    return [dmg, archive];
}

string PackageLinux(string publish, string runtime)
{
    var entries = new List<(string Name, string? Source, bool Executable)>
    {
        ("AutoKey/", null, true),
        ("AutoKey/AutoKey", Path.Combine(publish, "AutoKey"), true),
        ("AutoKey/install.sh", Path.Combine(root, "build", "linux", "install.sh"), true),
        ("AutoKey/autokey.desktop", Path.Combine(root, "build", "linux", "autokey.desktop"), false),
        ("AutoKey/autokey.png", Path.Combine(root, "src", "AutoKey", "Assets", "AutoKey.png"), false),
    };
    string destination = Path.Combine(artifacts, $"AutoKey-{version}-{runtime}.tar.gz");
    WriteTarGz(destination, entries);
    return destination;
}

static void AddDirectory(List<(string Name, string? Source, bool Executable)> entries, string directory, string name, Func<string, bool> isExecutable)
{
    entries.Add((name + "/", null, true));
    foreach (string file in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
    {
        entries.Add(($"{name}/{Path.GetFileName(file)}", file, isExecutable(file)));
    }

    foreach (string child in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
    {
        AddDirectory(entries, child, $"{name}/{Path.GetFileName(child)}", isExecutable);
    }
}

static void WriteTarGz(string destination, List<(string Name, string? Source, bool Executable)> entries)
{
    using FileStream file = File.Create(destination);
    using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
    using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    foreach (var (name, source, executable) in entries)
    {
        if (source is null)
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name) { Mode = Executable, ModificationTime = now });
            continue;
        }

        using FileStream data = File.OpenRead(source);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            Mode = executable ? Executable : Regular,
            ModificationTime = now,
            DataStream = data,
        });
    }
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.GetFiles(source))
    {
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    foreach (string child in Directory.GetDirectories(source))
    {
        CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }
}

static void Run(string program, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(program) { UseShellExecute = false };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {program}.");
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{program} failed with exit code {process.ExitCode}.");
    }
}

static string FindRepositoryRoot()
{
    for (string? directory = Environment.CurrentDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
    {
        if (File.Exists(Path.Combine(directory, "AutoKey.slnx")))
        {
            return directory;
        }
    }

    throw new InvalidOperationException("Run this from inside the AutoKey repository.");
}
