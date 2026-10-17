using System.Runtime.InteropServices;

namespace Builder.Core.Hardware;

public sealed record HardwareInfo(
    int LogicalProcessors,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    string OsDescription)
{
    public double TotalMemoryGb => TotalMemoryBytes / 1024d / 1024 / 1024;
    public double AvailableMemoryGb => AvailableMemoryBytes / 1024d / 1024 / 1024;
}

public static class HardwareProbe
{
    public static HardwareInfo Detect()
    {
        var (total, available) = ReadMemory();
        return new HardwareInfo(Environment.ProcessorCount, total, available, RuntimeInformation.OSDescription);
    }

    private static (long Total, long Available) ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return ((long)status.TotalPhys, (long)status.AvailPhys);
        }
        else if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            var values = ParseMemInfo(File.ReadAllLines("/proc/meminfo"));
            if (values.TryGetValue("MemTotal", out var total) && values.TryGetValue("MemAvailable", out var available))
                return (total, available);
        }

        // 측정 실패 시: 전체 메모리만 알 수 있으므로 여유 메모리도 같은 값으로 둔다.
        var fallback = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (fallback, fallback);
    }

    /// <summary>/proc/meminfo의 "Key:   1234 kB" 줄을 바이트 단위로 변환한다.</summary>
    internal static Dictionary<string, long> ParseMemInfo(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], out var value))
            {
                var multiplier = parts.Length > 1 && parts[1].Equals("kB", StringComparison.OrdinalIgnoreCase) ? 1024 : 1;
                result[line[..colon]] = value * multiplier;
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
