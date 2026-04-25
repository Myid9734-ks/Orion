using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OKXTradingBot.Core.Licensing;

/// <summary>
/// 현재 PC의 고유 식별자(MachineId)를 생성한다.
/// OS 별 하드웨어/설치 단위 고정 ID 사용 → 네트워크 어댑터 변경에 영향받지 않음.
///   macOS  : IOPlatformUUID (ioreg)
///   Windows: HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid
///   Linux  : /etc/machine-id (또는 /var/lib/dbus/machine-id)
/// OS 재설치 전엔 동일 PC에서 항상 같은 값.
/// </summary>
public static class MachineIdProvider
{
    public static string Get()
    {
        var raw = GetRawHardwareId() ?? FallbackId();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex  = Convert.ToHexString(hash)[..16];
        return $"{hex[..4]}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}";
    }

    private static string? GetRawHardwareId()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return GetMacOsUuid();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetWindowsMachineGuid();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return GetLinuxMachineId();
        }
        catch { }
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

        // "IOPlatformUUID" = "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX"
        const string key = "\"IOPlatformUUID\"";
        var idx = output.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        var eq = output.IndexOf('=', idx);
        if (eq < 0) return null;
        var q1 = output.IndexOf('"', eq + 1);
        var q2 = output.IndexOf('"', q1 + 1);
        if (q1 < 0 || q2 < 0) return null;
        return output.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static string? GetWindowsMachineGuid()
    {
#pragma warning disable CA1416
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
#pragma warning restore CA1416
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

    private static string FallbackId()
        => Environment.MachineName + "|" + Environment.OSVersion.Platform;
}
