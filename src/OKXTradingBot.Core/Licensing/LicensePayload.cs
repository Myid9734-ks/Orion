namespace OKXTradingBot.Core.Licensing;

/// <summary>
/// 라이센스 파일에 포함되는 실데이터(서명 대상).
/// </summary>
public sealed class LicensePayload
{
    public string MachineId { get; set; } = "";
    public string Owner     { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }  // null = 영구 라이센스

    public bool IsExpired(DateTime now) => ExpiresAt.HasValue && now > ExpiresAt.Value;
}
