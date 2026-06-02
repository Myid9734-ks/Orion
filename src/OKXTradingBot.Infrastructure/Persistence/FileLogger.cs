using Microsoft.Extensions.Logging;

namespace OKXTradingBot.Infrastructure.Persistence;

/// <summary>
/// ILogger 구현 — 모든 컴포넌트(REST/WS/Executor/GPT)의 _logger.LogXxx 출력을
/// 세션 로그 파일로 라우팅한다. (기존엔 NullLogger로 전부 폐기되어 실거래 디버깅 불가했음)
///
/// 새 NuGet 패키지(Microsoft.Extensions.Logging) 없이 Abstractions만으로 동작하도록
/// ILogger / ILogger&lt;T&gt; 를 직접 구현한다.
/// </summary>
public class FileLogger : ILogger
{
    private readonly Action<string> _sink;
    private readonly string         _category;
    private readonly LogLevel       _minLevel;

    public FileLogger(Action<string> sink, string category, LogLevel minLevel = LogLevel.Debug)
    {
        _sink     = sink;
        _category = category;
        _minLevel = minLevel;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null) return;

        var line = $"[{DateTime.Now:HH:mm:ss}] [{Abbrev(logLevel)}] [{_category}] {message}";
        if (exception != null)
            line += Environment.NewLine + "        " + exception;

        try { _sink(line); } catch { /* 로그 저장 실패는 거래에 영향 없어야 함 */ }
    }

    private static string Abbrev(LogLevel level) => level switch
    {
        LogLevel.Trace       => "TRC",
        LogLevel.Debug       => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning     => "WRN",
        LogLevel.Error       => "ERR",
        LogLevel.Critical    => "CRT",
        _                    => "???"
    };

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}

/// <summary>제네릭 카테고리 로거 — typeof(T).Name 을 카테고리로 사용.</summary>
public sealed class FileLogger<T> : FileLogger, ILogger<T>
{
    public FileLogger(Action<string> sink, LogLevel minLevel = LogLevel.Debug)
        : base(sink, typeof(T).Name, minLevel) { }
}
