using System.Reflection;
using OKXTradingBot.Core.Licensing;

namespace OKXTradingBot.UI.Services;

/// <summary>
/// 앱 시작 시 라이센스를 검증한다.
/// license.dat 위치: %UserProfile%/.okxtradingbot/license.dat (Windows, macOS, Linux 공통)
/// 공개키: 어셈블리 내장 리소스 (OKXTradingBot.UI.Assets.public.pem)
/// </summary>
public static class LicenseGuard
{
    public static string LicensePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".okxtradingbot", "license.dat");

    public static LicenseValidationResult Check()
    {
        var machineId = MachineIdProvider.Get();

        if (!File.Exists(LicensePath))
            return new LicenseValidationResult { Status = LicenseStatus.FileNotFound, ErrorDetail = LicensePath };

        string licenseText;
        try
        {
            licenseText = File.ReadAllText(LicensePath);
        }
        catch (Exception ex)
        {
            return new LicenseValidationResult { Status = LicenseStatus.Malformed, ErrorDetail = "읽기 실패: " + ex.Message };
        }

        var publicKeyPem = LoadEmbeddedPublicKey();
        return LicenseValidator.Validate(licenseText, publicKeyPem, machineId, DateTime.UtcNow);
    }

    private static string LoadEmbeddedPublicKey()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("public.pem", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("내장 공개키(public.pem)를 찾을 수 없습니다.");

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string GetCurrentMachineId() => MachineIdProvider.Get();

    /// <summary>
    /// 사용자가 입력한 라이센스 키 문자열을 검증하고, 유효하면 기본 경로에 저장한다.
    /// </summary>
    public static LicenseValidationResult TryRegisterFromText(string keyText)
    {
        if (string.IsNullOrWhiteSpace(keyText))
            return new LicenseValidationResult { Status = LicenseStatus.Malformed, ErrorDetail = "키가 비어있습니다" };

        var trimmed = keyText.Trim();
        var publicKeyPem = LoadEmbeddedPublicKey();
        var machineId = MachineIdProvider.Get();
        var result = LicenseValidator.Validate(trimmed, publicKeyPem, machineId, DateTime.UtcNow);
        if (!result.IsValid) return result;

        SaveLicenseText(trimmed);
        return result;
    }

    /// <summary>
    /// 외부 파일(license.dat)을 선택했을 때 검증 후 기본 경로로 복사한다.
    /// </summary>
    public static LicenseValidationResult TryRegisterFromFile(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            return new LicenseValidationResult { Status = LicenseStatus.FileNotFound, ErrorDetail = sourcePath };

        string text;
        try { text = File.ReadAllText(sourcePath); }
        catch (Exception ex)
        {
            return new LicenseValidationResult { Status = LicenseStatus.Malformed, ErrorDetail = "읽기 실패: " + ex.Message };
        }

        return TryRegisterFromText(text);
    }

    private static void SaveLicenseText(string text)
    {
        var dir = Path.GetDirectoryName(LicensePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(LicensePath, text);
    }
}
