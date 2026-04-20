namespace OKXTradingBot.Infrastructure.Persistence;

/// <summary>
/// 매매 로그를 날짜별 텍스트 파일로 저장
/// 경로: ~/.okxtradingbot/logs/YYYYMMDD_SYMBOL_m단계_세션ID.log
/// </summary>
public class LogFileService
{
    private readonly string _symbol;
    private readonly string _sessionTag;
    private readonly string _logDir;
    private readonly object _lock = new();

    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".okxtradingbot", "logs");

    /// <param name="symbol">OKX 심볼 (BTC-USDT-SWAP 등)</param>
    /// <param name="martinCount">마틴 단계수 — 탭 구분용 (파일명에 포함)</param>
    public LogFileService(string symbol, int martinCount = 0)
    {
        // 파일명에 사용할 심볼 정리 (BTC-USDT-SWAP → BTC)
        _symbol = symbol
            .Replace("-USDT-SWAP", "")
            .Replace("-USDT-PERP", "")
            .Replace("-USDC-SWAP", "")
            .Replace("-", "_");

        // 세션 태그: m단계_시작시각(HHmmss) → 같은 심볼 여러 탭 구분
        var startedAt = DateTime.Now.ToString("HHmmss");
        _sessionTag = martinCount > 0 ? $"m{martinCount}_{startedAt}" : startedAt;

        _logDir = BaseDir;
        Directory.CreateDirectory(_logDir);
    }

    /// <summary>로그 한 줄 저장 (날짜가 바뀌면 자동으로 새 파일)</summary>
    public void Write(string message)
    {
        try
        {
            var today    = DateTime.Now.ToString("yyyyMMdd");
            var fileName = $"{today}_{_symbol}_{_sessionTag}.log";
            var filePath = Path.Combine(_logDir, fileName);

            lock (_lock)
            {
                File.AppendAllText(filePath, message + Environment.NewLine);
            }
        }
        catch
        {
            // 로그 저장 실패는 무시 (거래에 영향 없어야 함)
        }
    }

    /// <summary>세션 구분선 기록 (봇 시작/종료 시)</summary>
    public void WriteSeparator(string label)
    {
        var line = $"{'='.ToString().PadRight(60, '=')}";
        Write(line);
        Write($"  {label} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write(line);
    }

    /// <summary>이 세션의 로그 파일 경로 반환 (날짜 지정)</summary>
    public string GetLogFilePath(DateTime date)
        => Path.Combine(_logDir, $"{date:yyyyMMdd}_{_symbol}_{_sessionTag}.log");

    /// <summary>이 심볼의 모든 세션 로그 파일 목록 반환 (최신순)</summary>
    public List<string> GetLogFiles()
        => Directory.GetFiles(_logDir, $"*_{_symbol}_*.log")
            .OrderByDescending(f => f)
            .ToList();
}
