using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Builder.Core.Http;

namespace Builder.Core.Java;

public sealed record JavaInstallation(string ExecutablePath, int MajorVersion, string VersionText, bool Managed);

public static partial class JavaVersionParser
{
    [GeneratedRegex("version \"(?<v>[^\"]+)\"")]
    private static partial Regex VersionPattern();

    /// <summary><c>java -version</c> 출력에서 주 버전을 얻는다.</summary>
    public static int? ParseMajorFromOutput(string output)
    {
        var match = VersionPattern().Match(output);
        return match.Success ? ParseMajor(match.Groups["v"].Value) : null;
    }

    /// <summary>"1.8.0_451" → 8, "25.0.4" → 25, "21" → 21, "17-ea" → 17</summary>
    public static int? ParseMajor(string version)
    {
        var parts = version.Split('.', '-', '_', '+');
        if (!int.TryParse(parts[0], out var first))
            return null;
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy))
            return legacy;
        return first;
    }
}

public static class JavaLocator
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>빌더 관리 런타임 → JAVA_HOME → PATH 순으로 찾고, 실행해서 버전을 확인한다.</summary>
    public static async Task<IReadOnlyList<JavaInstallation>> FindAllAsync(string runtimesDir, CancellationToken ct = default)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var found = new List<JavaInstallation>();

        foreach (var (path, managed) in Candidates(runtimesDir))
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (ArgumentException) { continue; }

            if (!File.Exists(full) || !seen.Add(full))
                continue;

            var installation = await ProbeAsync(full, managed, ct);
            if (installation is not null)
                found.Add(installation);
        }
        return found;
    }

    /// <summary>요구 버전 이상 중 가장 낮은 버전을 고른다. 같은 버전이면 빌더 관리 런타임을 우선한다.</summary>
    public static JavaInstallation? SelectFor(IEnumerable<JavaInstallation> installations, int requiredMajor) =>
        installations
            .Where(j => j.MajorVersion >= requiredMajor)
            .OrderBy(j => j.MajorVersion)
            .ThenByDescending(j => j.Managed)
            .FirstOrDefault();

    public static async Task<JavaInstallation?> ProbeAsync(string executable, bool managed, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(executable, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var stderr = process.StandardError.ReadToEndAsync(ct);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            var output = await stderr + "\n" + await stdout;
            var major = JavaVersionParser.ParseMajorFromOutput(output);
            if (major is null)
                return null;

            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            return new JavaInstallation(executable, major.Value, firstLine, managed);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Path, bool Managed)> Candidates(string runtimesDir)
    {
        if (Directory.Exists(runtimesDir))
        {
            foreach (var exe in Directory.EnumerateFiles(runtimesDir, ExecutableName, SearchOption.AllDirectories))
            {
                if (Path.GetFileName(Path.GetDirectoryName(exe)) == "bin")
                    yield return (exe, true);
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            yield return (Path.Combine(javaHome, "bin", ExecutableName), false);

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return (Path.Combine(dir.Trim('"'), ExecutableName), false);
    }
}

/// <summary>Eclipse Temurin JRE를 Adoptium API로 내려받아 빌더 런타임 폴더에 설치한다.</summary>
public sealed class AdoptiumClient(HttpClient http)
{
    public async Task<JavaInstallation> InstallJreAsync(
        int major, string runtimesDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var os = CurrentOs();
        var arch = CurrentArch();
        var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?os={os}&architecture={arch}&image_type=jre";
        var assets = await http.GetFromJsonAsync<List<Asset>>(url, BuilderHttp.Json, ct);
        var package = assets?.Select(a => a.Binary?.Package).FirstOrDefault(p => p is not null)
            ?? throw new InvalidOperationException($"Adoptium에서 Java {major} JRE({os}/{arch})를 찾을 수 없습니다.");

        Directory.CreateDirectory(runtimesDir);
        var archive = Path.Combine(runtimesDir, package.Name);
        var target = Path.Combine(runtimesDir, $"java-{major}");
        var staging = target + ".installing";

        try
        {
            await BuilderHttp.DownloadFileAsync(http, package.Link, archive, progress, new FileHash("sha256", package.Checksum), ct);

            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            if (package.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archive, staging);
            }
            else
            {
                await using var file = File.OpenRead(archive);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct);
            }

            // 기존 관리 런타임(같은 주 버전)은 새 버전으로 교체한다.
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (File.Exists(archive))
                File.Delete(archive);
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }

        var exe = Directory.EnumerateFiles(target, JavaLocator.ExecutableName, SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p)) == "bin")
            ?? throw new InvalidOperationException($"설치한 런타임에서 {JavaLocator.ExecutableName}을 찾을 수 없습니다: {target}");

        return await JavaLocator.ProbeAsync(exe, managed: true, ct)
            ?? throw new InvalidOperationException($"설치한 Java를 실행할 수 없습니다: {exe}");
    }

    private static string CurrentOs() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "mac"
        : throw new PlatformNotSupportedException("지원하지 않는 OS입니다.");

    private static string CurrentArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "x32",
        var other => throw new PlatformNotSupportedException($"지원하지 않는 CPU 아키텍처: {other}"),
    };

    private sealed record Asset(Binary? Binary);
    private sealed record Binary(Package? Package);
    private sealed record Package(string Name, string Link, string Checksum);
}
