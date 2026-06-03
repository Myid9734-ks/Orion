using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OKXTradingBot.Core.Licensing;

/// <summary>
/// 현재 PC의 고유 식별자(MachineId)를 읽는다.
///   Windows : wmic cpu get ProcessorId  → BFEBFBFF000A0655 형태
///   macOS   : ioreg IOPlatformUUID       → XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX 형태
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
        var psi = new ProcessStartInfo("wmic", "cpu get ProcessorId /value")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);

        foreach (var line in output.Split('\n', '\r'))
        {
            if (line.StartsWith("ProcessorId=", StringComparison.OrdinalIgnoreCase))
                return line.Substring("ProcessorId=".Length).Trim();
        }
        return null;
    }

    private static string? GetMacOsUuid()
    {
        var psi = new ProcessStartInfo("ioreg", "-rd1 -c IOPlatformExpertDevice")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);

        const string key = "\"IOPlatformUUID\"";
        var idx = output.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        var eq = output.IndexOf('=', idx);
        if (eq < 0) return null;
        var q1 = output.IndexOf('"', eq + 1);
        var q2 = output.IndexOf('"', q1 + 1);
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
}
