using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OKXTradingBot.Core.Licensing;

/// <summary>
/// 현재 PC의 고유 식별자(MachineId)를 읽는다.
///   Windows : wmic → PowerShell 순으로 CPU ProcessorId 읽기
///   macOS   : ioreg IOPlatformUUID
///   Linux   : /etc/machine-id
/// 빌드 시 -p:CpuId=<이 값> 으로 라이센스를 생성한다.
/// </summary>
public static class MachineIdProvider
{
    public static string Get()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetWindowsCpuId() ?? "UNKNOWN";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return GetMacOsUuid() ?? "UNKNOWN";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return GetLinuxMachineId() ?? "UNKNOWN";
        }
        catch { }
        return "UNKNOWN";
    }

    private static string? GetWindowsCpuId()
    {
        // 1차: wmic (Windows 10 이하)
        var result = RunProcess("wmic", "cpu get ProcessorId /value", 3000);
        if (result != null)
        {
            foreach (var line in result.Split('\n', '\r'))
            {
                if (line.StartsWith("ProcessorId=", StringComparison.OrdinalIgnoreCase))
                {
                    var id = line.Substring("ProcessorId=".Length).Trim();
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
        }

        // 2차: PowerShell (Windows 11 — wmic 제거됨)
        var ps = RunProcess("powershell",
            "-NoProfile -Command \"(Get-WmiObject Win32_Processor).ProcessorId\"", 5000);
        if (ps != null)
        {
            var id = ps.Trim();
            if (!string.IsNullOrEmpty(id)) return id;
        }

        return null;
    }

    private static string? GetMacOsUuid()
    {
        var output = RunProcess("ioreg", "-rd1 -c IOPlatformExpertDevice", 3000);
        if (output == null) return null;

        const string key = "\"IOPlatformUUID\"";
        var idx = output.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        var eq  = output.IndexOf('=', idx);
        if (eq  < 0) return null;
        var q1  = output.IndexOf('"', eq + 1);
        var q2  = output.IndexOf('"', q1 + 1);
        if (q1 < 0 || q2 < 0) return null;
        return output.Substring(q1 + 1, q2 - q1 - 1).Trim();
    }

    private static string? GetLinuxMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (File.Exists(path))
            {
                var v = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return null;
    }

    private static string? RunProcess(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return output;
        }
        catch { return null; }
    }
}
