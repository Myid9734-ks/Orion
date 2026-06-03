using Avalonia.Controls;
using Avalonia.Interactivity;
using OKXTradingBot.Core.Licensing;
using OKXTradingBot.UI.Services;

namespace OKXTradingBot.UI.Views;

public partial class LicenseErrorDialog : Window
{
    private readonly string _machineId;

    public LicenseErrorDialog(LicenseValidationResult result, string machineId, string licensePath)
    {
        InitializeComponent();
        _machineId = machineId;

        ReasonText.Text    = BuildReason(result, licensePath);
        MachineIdText.Text = machineId;
    }

    private static string BuildReason(LicenseValidationResult r, string licensePath) => r.Status switch
    {
        LicenseStatus.FileNotFound     => $"라이센스 파일(license.dat)을 찾을 수 없습니다.\n예상 위치: {licensePath}",
        LicenseStatus.Malformed        => "라이센스 파일이 손상되었거나 형식이 올바르지 않습니다." + FormatDetail(r),
        LicenseStatus.InvalidSignature => "라이센스 서명이 유효하지 않습니다.",
        LicenseStatus.MachineMismatch  => "이 라이센스는 다른 PC용으로 발급되었습니다." + FormatDetail(r),
        LicenseStatus.Expired          => "라이센스가 만료되었습니다." + FormatDetail(r),
        _                              => "알 수 없는 라이센스 오류." + FormatDetail(r),
    };

    private static string FormatDetail(LicenseValidationResult r)
        => string.IsNullOrEmpty(r.ErrorDetail) ? "" : $"\n({r.ErrorDetail})";

    private async void OnCopyMachineId(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(_machineId);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();
}
