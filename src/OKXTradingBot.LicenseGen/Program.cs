using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OKXTradingBot.Core.Licensing;

namespace OKXTradingBot.LicenseGen;

/// <summary>
/// 판매자 전용 라이센스 생성 도구.
///
/// 사용법:
///   okxbot-licensegen --machine &lt;machineId&gt; --owner &lt;이름&gt; [--expires yyyy-MM-dd] [--key private.pem] [--out license.dat]
///
/// 기본값:
///   --key     ./keys/private.pem
///   --out     ./license.dat
///   --expires 없음 (영구)
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);
            if (opts is null) { PrintUsage(); return 1; }

            if (!File.Exists(opts.KeyPath))
            {
                Console.Error.WriteLine($"개인키 파일을 찾을 수 없습니다: {opts.KeyPath}");
                return 2;
            }

            var payload = new LicensePayload
            {
                MachineId = opts.MachineId,
                Owner     = opts.Owner,
                IssuedAt  = DateTime.UtcNow,
                ExpiresAt = opts.ExpiresAt,
            };

            var payloadJson  = JsonSerializer.SerializeToUtf8Bytes(payload);
            var payloadB64   = LicenseValidator.Base64UrlEncode(payloadJson);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(opts.KeyPath));
            var signature    = rsa.SignData(Encoding.UTF8.GetBytes(payloadB64), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var signatureB64 = LicenseValidator.Base64UrlEncode(signature);

            var licenseText = payloadB64 + "." + signatureB64;
            File.WriteAllText(opts.OutPath, licenseText);

            Console.WriteLine("라이센스 생성 완료");
            Console.WriteLine($"  대상 머신 : {opts.MachineId}");
            Console.WriteLine($"  소유자    : {opts.Owner}");
            Console.WriteLine($"  만료일    : {(opts.ExpiresAt?.ToString("yyyy-MM-dd") ?? "영구")}");
            Console.WriteLine($"  출력      : {Path.GetFullPath(opts.OutPath)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("오류: " + ex.Message);
            return 99;
        }
    }

    private sealed class Options
    {
        public string MachineId { get; set; } = "";
        public string Owner     { get; set; } = "";
        public DateTime? ExpiresAt { get; set; }
        public string KeyPath { get; set; } = Path.Combine("keys", "private.pem");
        public string OutPath { get; set; } = "license.dat";
    }

    private static Options? ParseArgs(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--machine": o.MachineId = args[++i]; break;
                case "--owner":   o.Owner     = args[++i]; break;
                case "--expires":
                    if (!DateTime.TryParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
                        return null;
                    o.ExpiresAt = DateTime.SpecifyKind(d.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc);
                    break;
                case "--key": o.KeyPath = args[++i]; break;
                case "--out": o.OutPath = args[++i]; break;
                default: return null;
            }
        }
        if (string.IsNullOrWhiteSpace(o.MachineId) || string.IsNullOrWhiteSpace(o.Owner))
            return null;
        return o;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("사용법:");
        Console.WriteLine("  okxbot-licensegen --machine <machineId> --owner <이름> [--expires yyyy-MM-dd] [--key private.pem] [--out license.dat]");
        Console.WriteLine();
        Console.WriteLine("예시:");
        Console.WriteLine("  okxbot-licensegen --machine A3F9-BC12-45D7-89AB --owner \"홍길동\" --expires 2027-12-31");
    }
}
