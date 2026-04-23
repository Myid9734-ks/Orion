using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OKXTradingBot.Core.Licensing;
using OKXTradingBot.UI.Services;

namespace OKXTradingBot.UI.Views;

public partial class LicenseErrorDialog : Window
{
    private readonly string _machineId;

    /// <summary>
    /// 라이센스 등록에 성공했는지 여부. App.axaml.cs에서 MainWindow 전환 판단에 사용.
    /// </summary>
    public bool LicenseRegistered { get; private set; }

    public LicenseErrorDialog(LicenseValidationResult result, string machineId, string licensePath)
    {
        InitializeComponent();
        _machineId = machineId;

        ReasonText.Text    = BuildReason(result, licensePath);
        MachineIdText.Text = machineId;
    }

    private static string BuildReason(LicenseValidationResult r, string licensePath) => r.Status switch
    {
        LicenseStatus.FileNotFound      => $"라이센스 파일(license.dat)을 찾을 수 없습니다.\n예상 위치: {licensePath}",
        LicenseStatus.Malformed         => "라이센스 파일이 손상되었거나 형식이 올바르지 않습니다." + FormatDetail(r),
        LicenseStatus.InvalidSignature  => "라이센스 서명이 유효하지 않습니다. (위조되었거나 다른 배포용 라이센스)",
        LicenseStatus.MachineMismatch   => "이 라이센스는 다른 PC용으로 발급되었습니다." + FormatDetail(r),
        LicenseStatus.Expired           => "라이센스가 만료되었습니다." + FormatDetail(r),
        _                               => "알 수 없는 라이센스 오류." + FormatDetail(r),
    };

    private static string FormatDetail(LicenseValidationResult r)
        => string.IsNullOrEmpty(r.ErrorDetail) ? "" : $"\n({r.ErrorDetail})";

    private async void OnCopyMachineId(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_machineId);
            ShowStatus("✓ 머신 ID가 클립보드에 복사되었습니다.", isError: false);
        }
    }

    private void OnRegisterKey(object? sender, RoutedEventArgs e)
    {
        var text = KeyInput.Text ?? "";
        var result = LicenseGuard.TryRegisterFromText(text);
        HandleRegisterResult(result);
    }

    private async void OnSelectFile(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "라이센스 파일 선택",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("라이센스 파일") { Patterns = new[] { "*.dat", "*.txt" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            ShowStatus("파일 경로를 가져올 수 없습니다.", isError: true);
            return;
        }

        var result = LicenseGuard.TryRegisterFromFile(path);
        HandleRegisterResult(result);
    }

    private void HandleRegisterResult(LicenseValidationResult result)
    {
        if (result.IsValid)
        {
            LicenseRegistered = true;
            ShowStatus($"✓ 라이센스 등록 완료 (소유자: {result.Payload?.Owner}). 앱을 시작합니다.", isError: false);
            // 짧은 딜레이 없이 바로 닫기
            Close();
            return;
        }

        ShowStatus("등록 실패: " + BuildReason(result, LicenseGuard.LicensePath), isError: true);
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusBox.Background = SolidColorBrush.Parse(isError ? "#3E2A2A" : "#2A3E2A");
        StatusText.Foreground = SolidColorBrush.Parse(isError ? "#FFAAAA" : "#AAFFAA");
        StatusBox.IsVisible = true;
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();
}
