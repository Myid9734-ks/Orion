using Microsoft.Data.Sqlite;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.Persistence;

/// <summary>
/// 거래 기록 SQLite 영속화
/// DB 파일: ~/.okxtradingbot/trades.db
/// </summary>
public class TradeHistoryRepository
{
    private readonly string _connectionString;

    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".okxtradingbot", "trades.db");

    public TradeHistoryRepository()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _connectionString = $"Data Source={DbPath}";
        InitializeDatabase();
    }

    // ─────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS trades (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol       TEXT    NOT NULL,
                direction    TEXT    NOT NULL,
                avg_entry    REAL    NOT NULL,
                exit_price   REAL    NOT NULL,
                total_amount REAL    NOT NULL,
                martin_step  INTEGER NOT NULL,
                martin_max   INTEGER NOT NULL,
                pnl_percent  REAL    NOT NULL,
                pnl_amount   REAL    NOT NULL,
                is_stop_loss INTEGER NOT NULL DEFAULT 0,
                leverage     INTEGER NOT NULL DEFAULT 1,
                opened_at    TEXT    NOT NULL,
                closed_at    TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ─────────────────────────────────────────────
    // 거래 저장
    // ─────────────────────────────────────────────

    public void Save(TradeClosedEventArgs trade)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO trades
                (symbol, direction, avg_entry, exit_price, total_amount,
                 martin_step, martin_max, pnl_percent, pnl_amount,
                 is_stop_loss, leverage, opened_at, closed_at)
            VALUES
                ($symbol, $dir, $avgEntry, $exitPrice, $totalAmount,
                 $martinStep, $martinMax, $pnlPct, $pnlAmt,
                 $isStopLoss, $leverage, $openedAt, $closedAt);
            """;

        cmd.Parameters.AddWithValue("$symbol",      trade.Symbol);
        cmd.Parameters.AddWithValue("$dir",         trade.Direction == TradeDirection.Long ? "LONG" : "SHORT");
        cmd.Parameters.AddWithValue("$avgEntry",    (double)trade.AvgEntryPrice);
        cmd.Parameters.AddWithValue("$exitPrice",   (double)trade.ExitPrice);
        cmd.Parameters.AddWithValue("$totalAmount", (double)trade.TotalAmount);
        cmd.Parameters.AddWithValue("$martinStep",  trade.MartinStep);
        cmd.Parameters.AddWithValue("$martinMax",   trade.MartinMax);
        cmd.Parameters.AddWithValue("$pnlPct",      (double)trade.PnlPercent);
        cmd.Parameters.AddWithValue("$pnlAmt",      (double)trade.PnlAmount);
        cmd.Parameters.AddWithValue("$isStopLoss",  trade.IsStopLoss ? 1 : 0);
        cmd.Parameters.AddWithValue("$leverage",    trade.Leverage);
        cmd.Parameters.AddWithValue("$openedAt",    trade.OpenedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$closedAt",    trade.ClosedAt.ToString("o"));

        cmd.ExecuteNonQuery();
    }

    // ─────────────────────────────────────────────
    // 거래 기록 로드
    // ─────────────────────────────────────────────

    /// <summary>
    /// 전체 거래 기록 로드 (최신순)
    /// </summary>
    public List<TradeClosedEventArgs> LoadAll(string? symbol = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(symbol))
        {
            cmd.CommandText = "SELECT * FROM trades ORDER BY id DESC;";
        }
        else
        {
            cmd.CommandText = "SELECT * FROM trades WHERE symbol = $symbol ORDER BY id DESC;";
            cmd.Parameters.AddWithValue("$symbol", symbol);
        }

        var list = new List<TradeClosedEventArgs>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TradeClosedEventArgs
            {
                Symbol        = reader.GetString(reader.GetOrdinal("symbol")),
                Direction     = reader.GetString(reader.GetOrdinal("direction")) == "LONG"
                                    ? TradeDirection.Long : TradeDirection.Short,
                AvgEntryPrice = (decimal)reader.GetDouble(reader.GetOrdinal("avg_entry")),
                ExitPrice     = (decimal)reader.GetDouble(reader.GetOrdinal("exit_price")),
                TotalAmount   = (decimal)reader.GetDouble(reader.GetOrdinal("total_amount")),
                MartinStep    = reader.GetInt32(reader.GetOrdinal("martin_step")),
                MartinMax     = reader.GetInt32(reader.GetOrdinal("martin_max")),
                PnlPercent    = (decimal)reader.GetDouble(reader.GetOrdinal("pnl_percent")),
                PnlAmount     = (decimal)reader.GetDouble(reader.GetOrdinal("pnl_amount")),
                IsStopLoss    = reader.GetInt32(reader.GetOrdinal("is_stop_loss")) == 1,
                Leverage      = reader.GetInt32(reader.GetOrdinal("leverage")),
                OpenedAt      = DateTime.Parse(reader.GetString(reader.GetOrdinal("opened_at"))),
                ClosedAt      = DateTime.Parse(reader.GetString(reader.GetOrdinal("closed_at")))
            });
        }

        return list;
    }

    /// <summary>
    /// 기간별 거래 기록 로드
    /// </summary>
    public List<TradeClosedEventArgs> LoadByPeriod(DateTime from, DateTime to, string? symbol = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        var symbolFilter = string.IsNullOrEmpty(symbol) ? "" : "AND symbol = $symbol";
        cmd.CommandText = $"""
            SELECT * FROM trades
            WHERE closed_at >= $from AND closed_at <= $to
            {symbolFilter}
            ORDER BY id DESC;
            """;

        cmd.Parameters.AddWithValue("$from", from.ToString("o"));
        cmd.Parameters.AddWithValue("$to",   to.ToString("o"));
        if (!string.IsNullOrEmpty(symbol))
            cmd.Parameters.AddWithValue("$symbol", symbol);

        var list = new List<TradeClosedEventArgs>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new TradeClosedEventArgs
            {
                Symbol        = reader.GetString(reader.GetOrdinal("symbol")),
                Direction     = reader.GetString(reader.GetOrdinal("direction")) == "LONG"
                                    ? TradeDirection.Long : TradeDirection.Short,
                AvgEntryPrice = (decimal)reader.GetDouble(reader.GetOrdinal("avg_entry")),
                ExitPrice     = (decimal)reader.GetDouble(reader.GetOrdinal("exit_price")),
                TotalAmount   = (decimal)reader.GetDouble(reader.GetOrdinal("total_amount")),
                MartinStep    = reader.GetInt32(reader.GetOrdinal("martin_step")),
                MartinMax     = reader.GetInt32(reader.GetOrdinal("martin_max")),
                PnlPercent    = (decimal)reader.GetDouble(reader.GetOrdinal("pnl_percent")),
                PnlAmount     = (decimal)reader.GetDouble(reader.GetOrdinal("pnl_amount")),
                IsStopLoss    = reader.GetInt32(reader.GetOrdinal("is_stop_loss")) == 1,
                Leverage      = reader.GetInt32(reader.GetOrdinal("leverage")),
                OpenedAt      = DateTime.Parse(reader.GetString(reader.GetOrdinal("opened_at"))),
                ClosedAt      = DateTime.Parse(reader.GetString(reader.GetOrdinal("closed_at")))
            });
        }

        return list;
    }

    /// <summary>전체 거래 기록 삭제</summary>
    public void DeleteAll()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM trades;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>DB 통계 요약</summary>
    public (int total, decimal totalPnl, int wins, int losses) GetStats(string? symbol = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        var filter = string.IsNullOrEmpty(symbol) ? "" : "WHERE symbol = $symbol";
        cmd.CommandText = $"""
            SELECT
                COUNT(*) as total,
                SUM(pnl_amount) as totalPnl,
                SUM(CASE WHEN pnl_amount > 0 THEN 1 ELSE 0 END) as wins,
                SUM(CASE WHEN pnl_amount <= 0 THEN 1 ELSE 0 END) as losses
            FROM trades {filter};
            """;

        if (!string.IsNullOrEmpty(symbol))
            cmd.Parameters.AddWithValue("$symbol", symbol);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (
                reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : (decimal)reader.GetDouble(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
            );
        }
        return (0, 0, 0, 0);
    }
}
