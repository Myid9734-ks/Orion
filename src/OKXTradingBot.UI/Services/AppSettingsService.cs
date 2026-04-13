using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OKXTradingBot.UI.Services;

// ── 탭 하나의 저장 설정 ───────────────────────────────────────────────
public class SymbolTabSettings
{
    public string        Symbol             { get; set; } = "BTC-USDT-SWAP";
    public decimal       TotalBudget        { get; set; } = 100m;
    public int           Leverage           { get; set; } = 10;
    public string        MarginMode         { get; set; } = "Cross";
    public int           MartinCount        { get; set; } = 9;
    public decimal       MartinGap          { get; set; } = 0.5m;
    public decimal       TargetProfit       { get; set; } = 0.5m;
    public List<decimal> MartinGapSteps     { get; set; } = new();
    public List<decimal> TargetProfitSteps  { get; set; } = new();
    public bool          StopLossEnabled    { get; set; } = false;
    public decimal       StopLossPercent    { get; set; } = 3.0m;
}

// ── 전체 앱 설정 (복호화된 형태) ─────────────────────────────────────
public class AppSettings
{
    // 전역 API 인증
    public string  ApiKey                 { get; set; } = "";
    public string  ApiSecret              { get; set; } = "";
    public string  Passphrase             { get; set; } = "";

    // GPT
    public string  GptApiKey              { get; set; } = "";
    public string  GptModel               { get; set; } = "gpt-5.4-mini";
    public int     GptCandleCount         { get; set; } = 30;
    public int     GptConfidenceThreshold { get; set; } = 60;

    // Telegram
    public string  TelegramBotToken       { get; set; } = "";
    public string  TelegramChatId         { get; set; } = "";
    public bool    TelegramEnabled        { get; set; } = false;

    // 알림 항목
    public bool    NotifyBotStartStop     { get; set; } = true;
    public bool    NotifyEntry            { get; set; } = true;
    public bool    NotifyMartin           { get; set; } = true;
    public bool    NotifyTakeProfit       { get; set; } = true;
    public bool    NotifyStopLoss         { get; set; } = true;
    public bool    NotifyError            { get; set; } = true;

    // 수신 제한 시간
    public bool    QuietHoursEnabled      { get; set; } = false;
    public string  QuietStart             { get; set; } = "23:00";
    public string  QuietEnd               { get; set; } = "07:00";

    // 심볼 탭 목록 (각 탭마다 독립 설정)
    public List<SymbolTabSettings> Tabs   { get; set; } = new();
}

// ── 파일에 저장되는 암호화된 형태 ────────────────────────────────────
internal class EncryptedSettings
{
    // 암호화 필드
    public string  ApiKey                 { get; set; } = "";
    public string  ApiSecret              { get; set; } = "";
    public string  Passphrase             { get; set; } = "";
    public string  GptApiKey              { get; set; } = "";
    public string  TelegramBotToken       { get; set; } = "";

    // 평문 필드
    public string  GptModel               { get; set; } = "";
    public int     GptCandleCount         { get; set; } = 30;
    public int     GptConfidenceThreshold { get; set; } = 60;
    public string  TelegramChatId         { get; set; } = "";
    public bool    TelegramEnabled        { get; set; } = false;
    public bool    NotifyBotStartStop     { get; set; } = true;
    public bool    NotifyEntry            { get; set; } = true;
    public bool    NotifyMartin           { get; set; } = true;
    public bool    NotifyTakeProfit       { get; set; } = true;
    public bool    NotifyStopLoss         { get; set; } = true;
    public bool    NotifyError            { get; set; } = true;
    public bool    QuietHoursEnabled      { get; set; } = false;
    public string  QuietStart             { get; set; } = "23:00";
    public string  QuietEnd               { get; set; } = "07:00";

    // 탭 설정 목록 (평문 JSON)
    public List<SymbolTabSettings> Tabs   { get; set; } = new();
}

public class AppSettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".okxtradingbot", "settings.enc.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly byte[] _key = DeriveKey();
    private static readonly byte[] _iv  = new byte[16];

    private static byte[] DeriveKey()
    {
        var seed = Environment.UserName + Environment.MachineName + "OKXBot_v1";
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
    }

    private static string Encrypt(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV  = _iv;
        var enc    = aes.CreateEncryptor();
        var bytes  = Encoding.UTF8.GetBytes(plain);
        var cipher = enc.TransformFinalBlock(bytes, 0, bytes.Length);
        return Convert.ToBase64String(cipher);
    }

    private static string Decrypt(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV  = _iv;
            var dec   = aes.CreateDecryptor();
            var bytes = Convert.FromBase64String(cipher);
            var plain = dec.TransformFinalBlock(bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return ""; }
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return DefaultSettings();
            var json = File.ReadAllText(FilePath);
            var enc  = JsonSerializer.Deserialize<EncryptedSettings>(json) ?? new EncryptedSettings();

            var result = new AppSettings
            {
                ApiKey                 = Decrypt(enc.ApiKey),
                ApiSecret              = Decrypt(enc.ApiSecret),
                Passphrase             = Decrypt(enc.Passphrase),
                GptApiKey              = Decrypt(enc.GptApiKey),
                TelegramBotToken       = Decrypt(enc.TelegramBotToken),
                GptModel               = enc.GptModel,
                GptCandleCount         = enc.GptCandleCount,
                GptConfidenceThreshold = enc.GptConfidenceThreshold,
                TelegramChatId         = enc.TelegramChatId,
                TelegramEnabled        = enc.TelegramEnabled,
                NotifyBotStartStop     = enc.NotifyBotStartStop,
                NotifyEntry            = enc.NotifyEntry,
                NotifyMartin           = enc.NotifyMartin,
                NotifyTakeProfit       = enc.NotifyTakeProfit,
                NotifyStopLoss         = enc.NotifyStopLoss,
                NotifyError            = enc.NotifyError,
                QuietHoursEnabled      = enc.QuietHoursEnabled,
                QuietStart             = enc.QuietStart,
                QuietEnd               = enc.QuietEnd,
                Tabs                   = enc.Tabs.Count > 0 ? enc.Tabs : new List<SymbolTabSettings> { new() },
            };
            return result;
        }
        catch { return DefaultSettings(); }
    }

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var enc = new EncryptedSettings
        {
            ApiKey                 = Encrypt(s.ApiKey),
            ApiSecret              = Encrypt(s.ApiSecret),
            Passphrase             = Encrypt(s.Passphrase),
            GptApiKey              = Encrypt(s.GptApiKey),
            TelegramBotToken       = Encrypt(s.TelegramBotToken),
            GptModel               = s.GptModel,
            GptCandleCount         = s.GptCandleCount,
            GptConfidenceThreshold = s.GptConfidenceThreshold,
            TelegramChatId         = s.TelegramChatId,
            TelegramEnabled        = s.TelegramEnabled,
            NotifyBotStartStop     = s.NotifyBotStartStop,
            NotifyEntry            = s.NotifyEntry,
            NotifyMartin           = s.NotifyMartin,
            NotifyTakeProfit       = s.NotifyTakeProfit,
            NotifyStopLoss         = s.NotifyStopLoss,
            NotifyError            = s.NotifyError,
            QuietHoursEnabled      = s.QuietHoursEnabled,
            QuietStart             = s.QuietStart,
            QuietEnd               = s.QuietEnd,
            Tabs                   = s.Tabs,
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(enc, JsonOpts));
    }

    private static AppSettings DefaultSettings() => new()
    {
        Tabs = new List<SymbolTabSettings> { new() }
    };
}
