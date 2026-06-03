using System.Reflection;
using OKXTradingBot.Core.Licensing;

namespace OKXTradingBot.UI.Services;

/// <summary>
/// 앱 시작 시 라이센스를 검증한다.
///
/// license.dat 탐색 순서:
///   1. 앱 실행 폴더 (빌드 시 자동 생성된 번들 라이센스)
///   2. %UserProfile%/.okxtradingbot/license.dat (수동 설치)
///
/// 공개키: 어셈블리 내장 리소스 (OKXTradingBot.UI.Assets.public.pem)
/// </summary>
public static class LicenseGuard
{
    private static readonly string _userLicensePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".okxtradingbot", "license.dat");

    /// <summary>실제로 로드된 license.dat 경로 (앱 폴더 우선).</summary>
    public static string LicensePath
    {
        get
        {
            var appDir = AppContext.BaseDirectory;
            var appLicense = Path.Combine(appDir, "license.dat");
            if (File.Exists(appLicense)) return appLicense;
            return _userLicensePath;
        }
    }

    public static LicenseValidationResult Check()
    {
        var machineId = MachineIdProvider.Get();

        var path = LicensePath;
        if (!File.Exists(path))
            return new LicenseValidationResult { Status = LicenseStatus.FileNotFound, ErrorDetail = path };

        string licenseText;
        try
        {
            licenseText = File.ReadAllText(path);
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
        var dir = Path.GetDirectoryName(_userLicensePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_userLicensePath, text);
    }
}
