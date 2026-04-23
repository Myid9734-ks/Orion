using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace OKXTradingBot.Core.Licensing;

/// <summary>
/// 현재 PC의 고유 식별자(MachineId)를 생성한다.
/// MAC 주소 + 호스트명을 SHA256으로 해시하여 16자리 그룹으로 포맷한다.
/// 동일 PC에서는 항상 동일한 값이 나오며, 서로 다른 PC에서는 서로 다른 값이 나온다.
/// </summary>
public static class MachineIdProvider
{
    public static string Get()
    {
        var macs = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && n.OperationalStatus == OperationalStatus.Up
                     && n.GetPhysicalAddress().GetAddressBytes().Length > 0)
            .Select(n => n.GetPhysicalAddress().ToString())
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        if (macs.Count == 0)
        {
            // Fallback: 업 상태 필터 없이 다시 시도
            macs = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.GetPhysicalAddress().GetAddressBytes().Length > 0)
                .Select(n => n.GetPhysicalAddress().ToString())
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();
        }

        var raw = string.Join("|", macs) + "|" + Environment.MachineName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

        // 32자리 hex를 4-4-4-4 형태로 포맷 (앞 16바이트만 사용)
        var hex = Convert.ToHexString(hash)[..16];
        return $"{hex[..4]}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}";
    }
}
