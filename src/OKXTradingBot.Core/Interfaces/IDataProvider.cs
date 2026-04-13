using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Core.Interfaces;

/// <summary>
/// 캔들 데이터 공급 인터페이스.
/// 실매매: OkxDataProvider (WebSocket 실시간)
/// 백테스트: BacktestDataProvider (SQLite 과거 데이터)
/// </summary>
public interface IDataProvider
{
    /// <summary>완성된 1분봉 캔들 이벤트 (사이클마다 1회 발생)</summary>
    event EventHandler<Candle> OnCandleCompleted;

    /// <summary>최근 N개 캔들 반환 (GPT 분석 입력용)</summary>
    Task<List<Candle>> GetRecentCandlesAsync(int count, string bar = "1m");

    /// <summary>현재 가격 반환</summary>
    Task<decimal> GetCurrentPriceAsync();

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
