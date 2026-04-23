using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OKXTradingBot.Core.Licensing;

public enum LicenseStatus
{
    Valid,
    FileNotFound,
    Malformed,
    InvalidSignature,
    MachineMismatch,
    Expired,
}

public sealed class LicenseValidationResult
{
    public LicenseStatus Status { get; init; }
    public LicensePayload? Payload { get; init; }
    public string? ErrorDetail { get; init; }

    public bool IsValid => Status == LicenseStatus.Valid;

    public static LicenseValidationResult Ok(LicensePayload p)    => new() { Status = LicenseStatus.Valid, Payload = p };
    public static LicenseValidationResult Err(LicenseStatus s, string? d = null) => new() { Status = s, ErrorDetail = d };
}

/// <summary>
/// license.dat를 공개키(PEM)로 검증한다.
///
/// 파일 포맷: "&lt;payload-base64url&gt;.&lt;signature-base64url&gt;" (JWT 유사)
///   - payload: LicensePayload의 UTF-8 JSON
///   - signature: RSA-SHA256(payload-base64url 문자열)
/// </summary>
public static class LicenseValidator
{
    public static LicenseValidationResult Validate(string licenseFileText, string publicKeyPem, string currentMachineId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(licenseFileText))
            return LicenseValidationResult.Err(LicenseStatus.Malformed, "빈 라이센스");

        var parts = licenseFileText.Trim().Split('.');
        if (parts.Length != 2)
            return LicenseValidationResult.Err(LicenseStatus.Malformed, "포맷 불일치");

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes   = Base64UrlDecode(parts[0]);
            signatureBytes = Base64UrlDecode(parts[1]);
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Err(LicenseStatus.Malformed, "Base64 디코드 실패: " + ex.Message);
        }

        // 서명 검증: "payload-base64url" 문자열의 UTF-8 바이트에 대해
        bool verified;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var signedData = Encoding.UTF8.GetBytes(parts[0]);
            verified = rsa.VerifyData(signedData, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Err(LicenseStatus.InvalidSignature, "검증 예외: " + ex.Message);
        }

        if (!verified)
            return LicenseValidationResult.Err(LicenseStatus.InvalidSignature, "서명 불일치");

        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes);
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Err(LicenseStatus.Malformed, "JSON 파싱 실패: " + ex.Message);
        }
        if (payload is null)
            return LicenseValidationResult.Err(LicenseStatus.Malformed, "페이로드 null");

        if (!string.Equals(payload.MachineId, currentMachineId, StringComparison.OrdinalIgnoreCase))
            return LicenseValidationResult.Err(LicenseStatus.MachineMismatch,
                $"라이센스 대상: {payload.MachineId}, 현재: {currentMachineId}");

        if (payload.IsExpired(now))
            return LicenseValidationResult.Err(LicenseStatus.Expired,
                $"만료일: {payload.ExpiresAt:yyyy-MM-dd}");

        return LicenseValidationResult.Ok(payload);
    }

    public static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
        }
        return Convert.FromBase64String(s);
    }
}
