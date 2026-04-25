using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.OKX;

/// <summary>
/// OKX REST API 클라이언트
/// 공식 문서: https://www.okx.com/docs-v5/en/
/// </summary>
public class OkxRestClient
{
    private const string BaseUrl = "https://www.okx.com";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _passphrase;
    private readonly ILogger<OkxRestClient> _logger;

    public OkxRestClient(string apiKey, string apiSecret, string passphrase,
                         ILogger<OkxRestClient> logger)
    {
        _apiKey     = apiKey;
        _apiSecret  = apiSecret;
        _passphrase = passphrase;
        _logger     = logger;
        _http       = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ─────────────────────────────────────────────
    // Public Market Data (인증 불필요)
    // ─────────────────────────────────────────────

    /// <summary>최근 N개 캔들 조회 (bar: 1m, 5m, 15m, 1H, 4H 등)</summary>
    public async Task<List<Candle>> GetCandlesAsync(string instId, int limit = 30, string bar = "1m")
    {
        var url = $"/api/v5/market/candles?instId={instId}&bar={bar}&limit={limit}";
        var json = await GetPublicAsync(url);

        var candles = new List<Candle>();
        var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        foreach (var item in data.EnumerateArray())
        {
            // OKX 응답: [ts, open, high, low, close, vol, volCcy, volCcyQuote, confirm]
            var ts    = long.Parse(item[0].GetString()!);
            candles.Add(new Candle
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                Open      = decimal.Parse(item[1].GetString()!),
                High      = decimal.Parse(item[2].GetString()!),
                Low       = decimal.Parse(item[3].GetString()!),
                Close     = decimal.Parse(item[4].GetString()!),
                Volume    = decimal.Parse(item[5].GetString()!)
            });
        }

        // OKX는 최신봉이 앞에 오므로 역순 정렬 (오래된 봉 → 최신 봉)
        candles.Reverse();
        return candles;
    }

    /// <summary>현재 티커 가격 조회</summary>
    public async Task<decimal> GetTickerPriceAsync(string instId)
    {
        var url  = $"/api/v5/market/ticker?instId={instId}";
        var json = await GetPublicAsync(url);
        var doc  = JsonDocument.Parse(json);
        var last = doc.RootElement.GetProperty("data")[0].GetProperty("last").GetString()!;
        return decimal.Parse(last);
    }

    // ─────────────────────────────────────────────
    // Private Account (인증 필요)
    // ─────────────────────────────────────────────

    /// <summary>USDT 잔고 조회</summary>
    public async Task<decimal> GetBalanceAsync()
    {
        var json = await GetPrivateAsync("/api/v5/account/balance?ccy=USDT");
        var doc  = JsonDocument.Parse(json);
        var details = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("details");

        foreach (var item in details.EnumerateArray())
        {
            if (item.GetProperty("ccy").GetString() == "USDT")
                return decimal.Parse(item.GetProperty("availBal").GetString()!);
        }
        return 0m;
    }

    /// <summary>
    /// Taker/Maker 수수료율 조회 (SWAP).
    /// 진입은 지정가(Maker) 시도 후 시장가(Taker) fallback이므로 둘 다 반환.
    /// </summary>
    public async Task<(decimal Taker, decimal Maker)> GetFeeRatesAsync(string instId)
    {
        try
        {
            var json = await GetPrivateAsync($"/api/v5/account/trade-fee?instType=SWAP&instId={instId}");
            var doc  = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return (0.0005m, 0.0002m);

            var item      = data[0];
            var takerStr  = item.TryGetProperty("taker", out var t) ? t.GetString() : "-0.0005";
            var makerStr  = item.TryGetProperty("maker", out var m) ? m.GetString() : "-0.0002";

            decimal.TryParse(takerStr, out var taker);
            decimal.TryParse(makerStr, out var maker);

            taker = taker != 0 ? Math.Abs(taker) : 0.0005m;
            maker = maker != 0 ? Math.Abs(maker) : 0.0002m;

            return (taker, maker);
        }
        catch
        {
            return (0.0005m, 0.0002m);
        }
    }

    /// <summary>레버리지 및 마진 모드 설정 (mgnMode: "cross" | "isolated")</summary>
    public async Task<bool> SetLeverageAsync(string instId, int leverage, string mgnMode = "cross")
    {
        var body = JsonSerializer.Serialize(new
        {
            instId,
            lever   = leverage.ToString(),
            mgnMode
        });

        var json = await PostPrivateAsync("/api/v5/account/set-leverage", body);
        var doc  = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("code").GetString() == "0";
    }

    /// <summary>시장가 주문 (무기한 선물)</summary>
    public async Task<OrderResult> PlaceMarketOrderAsync(
        string instId, string side, string posSide, decimal sz, string mgnMode = "cross")
    {
        // side: buy / sell
        // posSide: long / short (양방향 포지션 모드 기준)
        // tgtCcy=quote_ccy: sz를 계약 수가 아닌 USDT 금액으로 해석하도록 명시
        var body = JsonSerializer.Serialize(new
        {
            instId,
            tdMode  = mgnMode,
            side,
            posSide,
            ordType = "market",
            sz      = sz.ToString("F4"),
            tgtCcy  = "quote_ccy"
        });

        _logger.LogInformation("[🔍TEST-1] 시장가 주문 요청: {body}", body);
        var json   = await PostPrivateAsync("/api/v5/trade/order", body);
        _logger.LogInformation("[🔍TEST-1] 시장가 주문 응답: {json}", json);

        var doc    = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("data")[0];
        var code   = doc.RootElement.GetProperty("code").GetString();

        if (code != "0")
        {
            var sCode = result.TryGetProperty("sCode", out var sc) ? sc.GetString() : code;
            var sMsg  = result.TryGetProperty("sMsg",  out var sm) ? sm.GetString() : "unknown";
            _logger.LogError("[🔍TEST-1] 주문 실패: [{sCode}] {sMsg}", sCode, sMsg);
            return new OrderResult { Success = false, ErrorMessage = $"[{sCode}] {sMsg}" };
        }

        var ordId = result.GetProperty("ordId").GetString()!;
        _logger.LogInformation("[🔍TEST-1] 주문 성공: ordId={ordId}", ordId);

        // 체결 상세 조회 (fillPx 확인)
        var filled = await QueryOrderFillAsync(instId, ordId);
        _logger.LogInformation("[🔍TEST-1] 체결 상세: fillPx={px} fillSz={sz} state={st}",
            filled.FilledPrice, filled.FilledSize, filled.State);

        return new OrderResult
        {
            Success      = true,
            OrderId      = ordId,
            FilledPrice  = filled.FilledPrice,
            FilledSize   = filled.FilledSize
        };
    }

    // 인스트루먼트 메타 캐시 (ctVal, lotSz, minSz)
    private readonly Dictionary<string, (decimal CtVal, decimal LotSz, decimal MinSz)> _instSpecCache = new();
    private readonly SemaphoreSlim _instSpecLock = new(1, 1);

    /// <summary>인스트루먼트 메타 조회 (ctVal: 1계약당 기초자산 수량). 30분 캐시.</summary>
    public async Task<(decimal CtVal, decimal LotSz, decimal MinSz)> GetInstrumentSpecAsync(string instId)
    {
        await _instSpecLock.WaitAsync();
        try
        {
            if (_instSpecCache.TryGetValue(instId, out var cached) && cached.CtVal > 0)
                return cached;

            var url  = $"/api/v5/public/instruments?instType=SWAP&instId={instId}";
            var json = await GetPublicAsync(url);
            var doc  = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return (0, 0, 0);

            var item = data[0];
            decimal.TryParse(item.TryGetProperty("ctVal", out var cv) ? cv.GetString() : "0", out var ctVal);
            decimal.TryParse(item.TryGetProperty("lotSz", out var lz) ? lz.GetString() : "1", out var lotSz);
            decimal.TryParse(item.TryGetProperty("minSz", out var ms) ? ms.GetString() : "1", out var minSz);

            var spec = (ctVal, lotSz, minSz);
            _instSpecCache[instId] = spec;
            return spec;
        }
        finally { _instSpecLock.Release(); }
    }

    /// <summary>USDT 명목금액을 계약 수로 변환. contracts = usdtAmount / (price * ctVal), lotSz 정렬.</summary>
    public async Task<decimal> ConvertUsdtToContractsAsync(string instId, decimal usdtAmount, decimal price, int leverage)
    {
        var (ctVal, lotSz, minSz) = await GetInstrumentSpecAsync(instId);
        if (ctVal <= 0 || price <= 0) return 0m;

        var raw = usdtAmount / (price * ctVal); // usdtAmount는 명목금액(notional), 레버리지 곱셈 불필요
        if (lotSz <= 0) lotSz = 1m;

        // lotSz 단위로 내림 정렬
        var contracts = Math.Floor(raw / lotSz) * lotSz;
        if (contracts < minSz) contracts = minSz;
        return contracts;
    }

    /// <summary>지정가 주문 (무기한 선물). sz는 계약 수.</summary>
    public async Task<OrderResult> PlaceLimitOrderAsync(
        string instId, string side, string posSide, decimal contractSz, decimal px,
        string mgnMode = "cross", bool reduceOnly = false)
    {
        var body = JsonSerializer.Serialize(new
        {
            instId,
            tdMode  = mgnMode,
            side,
            posSide,
            ordType = "limit",
            sz      = contractSz.ToString("0.########"),
            px      = px.ToString("0.########"),
            reduceOnly = reduceOnly ? "true" : "false"
        });

        _logger.LogInformation("[Limit] 지정가 주문 요청: {body}", body);
        var json   = await PostPrivateAsync("/api/v5/trade/order", body);
        _logger.LogInformation("[Limit] 지정가 주문 응답: {json}", json);

        var doc    = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("data")[0];
        var code   = doc.RootElement.GetProperty("code").GetString();

        if (code != "0")
        {
            var sCode = result.TryGetProperty("sCode", out var sc) ? sc.GetString() : code;
            var sMsg  = result.TryGetProperty("sMsg",  out var sm) ? sm.GetString() : "unknown";
            _logger.LogError("[Limit] 주문 실패: [{sCode}] {sMsg}", sCode, sMsg);
            return new OrderResult { Success = false, ErrorMessage = $"[{sCode}] {sMsg}" };
        }

        var ordId = result.GetProperty("ordId").GetString()!;
        return new OrderResult { Success = true, OrderId = ordId };
    }

    /// <summary>USDT 금액 + 가격으로 지정가 주문 (계약수 자동 환산).</summary>
    public async Task<OrderResult> PlaceLimitOrderUsdtAsync(
        string instId, string side, string posSide,
        decimal usdtAmount, decimal px, int leverage,
        string mgnMode = "cross", bool reduceOnly = false)
    {
        var contracts = await ConvertUsdtToContractsAsync(instId, usdtAmount, px, leverage);
        if (contracts <= 0)
        {
            _logger.LogError("[Limit] 계약수 환산 실패: usdt={u} px={p} lev={l}", usdtAmount, px, leverage);
            return new OrderResult { Success = false, ErrorMessage = "계약수 환산 실패" };
        }
        _logger.LogInformation("[Limit] {usdt} USDT ÷ ({px} × ctVal) = {ct} 계약",
            usdtAmount, px, contracts);
        return await PlaceLimitOrderAsync(instId, side, posSide, contracts, px, mgnMode, reduceOnly);
    }

    /// <summary>지정가 주문 가격 정정 (newPx).</summary>
    public async Task<bool> AmendOrderAsync(string instId, string ordId, decimal newPx)
    {
        var body = JsonSerializer.Serialize(new
        {
            instId,
            ordId,
            newPx = newPx.ToString("F8")
        });

        _logger.LogInformation("[Amend] 정정 요청: ordId={oid} newPx={px}", ordId, newPx);
        var json = await PostPrivateAsync("/api/v5/trade/amend-order", body);
        var doc  = JsonDocument.Parse(json);
        var code = doc.RootElement.GetProperty("code").GetString();
        return code == "0";
    }

    /// <summary>일반 주문 취소 (algo 아님).</summary>
    public async Task<bool> CancelOrderAsync(string instId, string ordId)
    {
        var body = JsonSerializer.Serialize(new { instId, ordId });
        var json = await PostPrivateAsync("/api/v5/trade/cancel-order", body);
        var doc  = JsonDocument.Parse(json);
        var code = doc.RootElement.GetProperty("code").GetString();
        return code == "0";
    }

    /// <summary>심볼의 미체결 일반 주문 목록 (reduceOnly 포함). algo는 별도 endpoint.</summary>
    public async Task<List<PendingOrderInfo>> GetPendingOrdersAsync(string instId)
    {
        var json = await GetPrivateAsync($"/api/v5/trade/orders-pending?instType=SWAP&instId={instId}");
        var doc  = JsonDocument.Parse(json);
        var list = new List<PendingOrderInfo>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        foreach (var item in data.EnumerateArray())
        {
            decimal.TryParse(item.TryGetProperty("px",        out var px) ? px.GetString() : "0", out var pxV);
            decimal.TryParse(item.TryGetProperty("sz",        out var sz) ? sz.GetString() : "0", out var szV);
            decimal.TryParse(item.TryGetProperty("accFillSz", out var af) ? af.GetString() : "0", out var afV);
            long.TryParse   (item.TryGetProperty("cTime",     out var ct) ? ct.GetString() : "0", out var ctMs);
            list.Add(new PendingOrderInfo
            {
                OrderId    = item.TryGetProperty("ordId",   out var oi) ? oi.GetString() ?? "" : "",
                Side       = item.TryGetProperty("side",    out var sd) ? sd.GetString() ?? "" : "",
                PosSide    = item.TryGetProperty("posSide", out var ps) ? ps.GetString() ?? "" : "",
                OrdType    = item.TryGetProperty("ordType", out var ot) ? ot.GetString() ?? "" : "",
                Price      = pxV,
                Size       = szV,
                FilledSize = afV,
                ReduceOnly = (item.TryGetProperty("reduceOnly", out var ro) ? ro.GetString() : "false") == "true",
                CreatedAt  = ctMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ctMs).UtcDateTime : DateTime.UtcNow
            });
        }
        return list;
    }

    /// <summary>지정가 주문 체결 폴링 (timeoutMs 안에 filled 되면 fillPx 반환).</summary>
    public async Task<OrderResult> WaitForFillAsync(string instId, string ordId, int timeoutMs = 3000, int pollMs = 250)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var json = await GetPrivateAsync($"/api/v5/trade/order?instId={instId}&ordId={ordId}");
            var doc  = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var item   = data[0];
                var state  = item.TryGetProperty("state",     out var st) ? st.GetString() : "";
                var fillPx = item.TryGetProperty("avgPx",     out var fp) ? fp.GetString() : "0";
                var accSz  = item.TryGetProperty("accFillSz", out var af) ? af.GetString() : "0";

                if (state == "filled" || state == "partially_filled")
                {
                    decimal.TryParse(fillPx, out var px);
                    decimal.TryParse(accSz,  out var sz);
                    return new OrderResult { Success = true, OrderId = ordId, FilledPrice = px, FilledSize = sz, State = state };
                }
            }
            await Task.Delay(pollMs);
        }
        return new OrderResult { Success = false, OrderId = ordId, State = "timeout" };
    }

    /// <summary>주문 상태 + 미체결 잔량 조회 (state, fillSz, sz, px).</summary>
    public async Task<(string State, decimal FilledSize, decimal TotalSize, decimal Price)> GetOrderStateAsync(string instId, string ordId)
    {
        var json = await GetPrivateAsync($"/api/v5/trade/order?instId={instId}&ordId={ordId}");
        var doc  = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            return ("unknown", 0, 0, 0);
        var item = data[0];
        var state = item.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
        decimal.TryParse(item.TryGetProperty("accFillSz", out var af) ? af.GetString() : "0", out var filled);
        decimal.TryParse(item.TryGetProperty("sz",        out var sz) ? sz.GetString() : "0", out var total);
        decimal.TryParse(item.TryGetProperty("px",        out var px) ? px.GetString() : "0", out var price);
        return (state, filled, total, price);
    }

    /// <summary>주문 체결 상세 조회 — PlaceMarketOrderAsync 직후 fillPx 확인용</summary>
    private async Task<OrderResult> QueryOrderFillAsync(string instId, string ordId)
    {
        try
        {
            // 시장가 주문은 보통 즉시 체결되나 최대 2회 재시도
            for (int i = 0; i < 3; i++)
            {
                var json = await GetPrivateAsync($"/api/v5/trade/order?instId={instId}&ordId={ordId}");
                var doc  = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                    continue;

                var item   = data[0];
                var state  = item.TryGetProperty("state",  out var st) ? st.GetString() : "";
                var fillPx = item.TryGetProperty("fillPx", out var fp) ? fp.GetString() : "0";
                var fillSz = item.TryGetProperty("fillSz", out var fs) ? fs.GetString() : "0";
                var accFillSz = item.TryGetProperty("accFillSz", out var af) ? af.GetString() : "0";

                decimal.TryParse(fillPx,    out var px);
                decimal.TryParse(fillSz,    out var sz);
                decimal.TryParse(accFillSz, out var accSz);

                if (state == "filled" || state == "partially_filled")
                    return new OrderResult { FilledPrice = px, FilledSize = accSz, State = state };

                await Task.Delay(300);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[🔍TEST-1] 체결 상세 조회 실패: {msg}", ex.Message);
        }
        return new OrderResult { State = "unknown" };
    }

    /// <summary>전체 포지션 청산 (시장가)</summary>
    public async Task<OrderResult> ClosePositionAsync(string instId, string posSide, string mgnMode = "cross")
    {
        var body = JsonSerializer.Serialize(new
        {
            instId,
            mgnMode,
            posSide
        });

        var json = await PostPrivateAsync("/api/v5/trade/close-position", body);
        var doc  = JsonDocument.Parse(json);
        var code = doc.RootElement.GetProperty("code").GetString();

        return new OrderResult { Success = code == "0" };
    }

    // ─────────────────────────────────────────────
    // Algo Orders (예약/트리거/익절 주문)
    // ─────────────────────────────────────────────

    /// <summary>
    /// 트리거 시장가 주문 등록 (마틴 N단계 사전 예약).
    /// triggerPx 도달 시 시장가로 즉시 체결됨.
    /// </summary>
    public async Task<OrderResult> PlaceTriggerAlgoOrderAsync(
        string instId, string side, string posSide,
        decimal sz, decimal triggerPx, string mgnMode = "cross",
        bool reduceOnly = false)
    {
        // tgtCcy=quote_ccy: sz를 계약 수가 아닌 USDT 금액으로 해석
        // 시장가 주문(PlaceMarketOrderAsync)과 동일한 단위 사용 필수
        var body = JsonSerializer.Serialize(new
        {
            instId,
            tdMode        = mgnMode,
            side,
            posSide,
            ordType       = "trigger",
            sz            = sz.ToString("F4"),
            tgtCcy        = "quote_ccy",
            triggerPx     = triggerPx.ToString("0.########"),
            orderPx       = "-1",  // -1 = 트리거 발동 시 시장가로 즉시 체결 (미체결 방지)
            triggerPxType = "last",
            reduceOnly    = reduceOnly ? "true" : "false"
        });

        _logger.LogInformation("[🔍TEST-2] 트리거 주문 요청: {body}", body);
        var json = await PostPrivateAsync("/api/v5/trade/order-algo", body);
        _logger.LogInformation("[🔍TEST-2] 트리거 주문 응답: {json}", json);

        return ParseAlgoResponse(json, $"trigger@{triggerPx}");
    }

    /// <summary>
    /// 익절 conditional 주문 등록 (close 100% via closeFraction).
    /// reduceOnly = true 보장 — 포지션 초과 청산 방지.
    /// </summary>
    public async Task<OrderResult> PlaceTakeProfitAlgoOrderAsync(
        string instId, string side, string posSide,
        decimal tpTriggerPx, string mgnMode = "cross")
    {
        var body = JsonSerializer.Serialize(new
        {
            instId,
            tdMode        = mgnMode,
            side,
            posSide,
            ordType       = "conditional",
            closeFraction = "1",     // 100% 포지션 청산
            tpTriggerPx   = tpTriggerPx.ToString("0.########"),
            tpOrdPx       = "-1",    // -1 = 트리거 도달 시 시장가 체결 (미체결 방지)
            tpTriggerPxType = "last",
            reduceOnly    = "true"
        });

        _logger.LogInformation("[🔍TEST-4] TP 주문 요청: {body}", body);
        var json = await PostPrivateAsync("/api/v5/trade/order-algo", body);
        _logger.LogInformation("[🔍TEST-4] TP 주문 응답: {json}", json);

        return ParseAlgoResponse(json, $"tp@{tpTriggerPx}");
    }

    /// <summary>특정 algo 주문 취소</summary>
    public async Task<bool> CancelAlgoOrderAsync(string instId, string algoId)
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { algoId, instId }
        });

        _logger.LogDebug("[ALGO CANCEL REQ] {body}", body);
        var json = await PostPrivateAsync("/api/v5/trade/cancel-algos", body);
        _logger.LogDebug("[ALGO CANCEL RES] {json}", json);

        var doc  = JsonDocument.Parse(json);
        var code = doc.RootElement.GetProperty("code").GetString();
        return code == "0";
    }

    /// <summary>심볼의 모든 pending algo 주문 조회 후 일괄 취소</summary>
    public async Task<bool> CancelAllAlgoOrdersAsync(string instId)
    {
        // 1) trigger + conditional pending 조회
        var allIds = new List<string>();
        foreach (var ordType in new[] { "trigger", "conditional" })
        {
            var listJson = await GetPrivateAsync(
                $"/api/v5/trade/orders-algo-pending?ordType={ordType}&instId={instId}");
            var doc = JsonDocument.Parse(listJson);
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("algoId").GetString();
                if (!string.IsNullOrEmpty(id)) allIds.Add(id);
            }
        }

        if (allIds.Count == 0)
        {
            _logger.LogDebug("[ALGO CANCEL-ALL] {instId} pending 없음", instId);
            return true;
        }

        var body = JsonSerializer.Serialize(allIds.Select(id => new { algoId = id, instId }).ToArray());
        _logger.LogInformation("[ALGO CANCEL-ALL] {n}건 취소 시도: {body}", allIds.Count, body);
        var resJson = await PostPrivateAsync("/api/v5/trade/cancel-algos", body);
        _logger.LogDebug("[ALGO CANCEL-ALL RES] {json}", resJson);

        var doc2 = JsonDocument.Parse(resJson);
        return doc2.RootElement.GetProperty("code").GetString() == "0";
    }

    // ─────────────────────────────────────────────
    // 재시작 동기화 (포지션 / algo 주문 조회)
    // ─────────────────────────────────────────────

    /// <summary>현재 열린 포지션 조회 (재시작 동기화용). 포지션 없으면 null.</summary>
    public async Task<ExchangePositionInfo?> GetPositionAsync(string instId)
    {
        var json = await GetPrivateAsync($"/api/v5/account/positions?instId={instId}");
        var doc  = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

        foreach (var item in data.EnumerateArray())
        {
            var posStr = item.TryGetProperty("pos", out var p) ? p.GetString() : "0";
            if (!decimal.TryParse(posStr, out var posAmt) || posAmt == 0) continue;

            var posSide   = item.TryGetProperty("posSide", out var ps) ? ps.GetString() : "long";
            var direction = posSide == "short" ? TradeDirection.Short : TradeDirection.Long;

            decimal.TryParse(item.TryGetProperty("avgPx",       out var ap)  ? ap.GetString()  : "0", out var avgPx);
            decimal.TryParse(item.TryGetProperty("notionalUsd", out var nu)  ? nu.GetString()  : "0", out var notional);
            long.TryParse   (item.TryGetProperty("uTime",       out var ut)  ? ut.GetString()  : "0", out var uTimeMs);

            return new ExchangePositionInfo
            {
                Symbol        = instId,
                Direction     = direction,
                AvgEntryPrice = avgPx,
                TotalQuantity = Math.Abs(posAmt),
                NotionalUsd   = Math.Abs(notional),
                OpenedAt      = uTimeMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(uTimeMs).UtcDateTime
                    : DateTime.UtcNow
            };
        }

        return null;
    }

    /// <summary>미체결 algo 주문 목록 조회 (재시작 동기화용)</summary>
    public async Task<List<AlgoOrderInfo>> GetOpenAlgoOrdersAsync(string instId)
    {
        var result = new List<AlgoOrderInfo>();

        foreach (var ordType in new[] { "trigger", "conditional" })
        {
            var json = await GetPrivateAsync(
                $"/api/v5/trade/orders-algo-pending?ordType={ordType}&instId={instId}");
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;

            foreach (var item in data.EnumerateArray())
            {
                var algoId     = item.TryGetProperty("algoId",     out var ai) ? ai.GetString() ?? "" : "";
                var reduceOnly = item.TryGetProperty("reduceOnly", out var ro) && ro.GetString() == "true";

                decimal triggerPx = 0, tpTriggerPx = 0;
                if (ordType == "trigger")
                    decimal.TryParse(item.TryGetProperty("triggerPx",   out var tp)  ? tp.GetString()  : "0", out triggerPx);
                else
                    decimal.TryParse(item.TryGetProperty("tpTriggerPx", out var ttp) ? ttp.GetString() : "0", out tpTriggerPx);

                result.Add(new AlgoOrderInfo
                {
                    AlgoId      = algoId,
                    OrdType     = ordType,
                    TriggerPx   = triggerPx,
                    TpTriggerPx = tpTriggerPx,
                    IsClose     = reduceOnly
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 최근 체결/취소된 algo 주문 히스토리 조회 (재시작 누락 복구용).
    /// state: "effective" (발동 후 본주문 생성됨) / "canceled" / "order_failed"
    /// </summary>
    public async Task<List<AlgoOrderInfo>> GetAlgoOrderHistoryAsync(string instId, int limit = 50)
    {
        var result = new List<AlgoOrderInfo>();

        foreach (var ordType in new[] { "trigger", "conditional" })
        {
            var json = await GetPrivateAsync(
                $"/api/v5/trade/orders-algo-history?ordType={ordType}&state=effective&instId={instId}&limit={limit}");
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;

            foreach (var item in data.EnumerateArray())
            {
                var algoId = item.TryGetProperty("algoId", out var ai) ? ai.GetString() ?? "" : "";
                var reduceOnly = item.TryGetProperty("reduceOnly", out var ro) && ro.GetString() == "true";

                decimal triggerPx = 0, tpTriggerPx = 0;
                if (ordType == "trigger")
                    decimal.TryParse(item.TryGetProperty("triggerPx",   out var tp)  ? tp.GetString()  : "0", out triggerPx);
                else
                    decimal.TryParse(item.TryGetProperty("tpTriggerPx", out var ttp) ? ttp.GetString() : "0", out tpTriggerPx);

                long.TryParse(item.TryGetProperty("uTime", out var ut) ? ut.GetString() : "0", out var uTimeMs);

                result.Add(new AlgoOrderInfo
                {
                    AlgoId       = algoId,
                    OrdType      = ordType,
                    TriggerPx    = triggerPx,
                    TpTriggerPx  = tpTriggerPx,
                    IsClose      = reduceOnly,
                    UpdatedAtMs  = uTimeMs
                });
            }
        }

        return result;
    }

    private OrderResult ParseAlgoResponse(string json, string tag)
    {
        var doc    = JsonDocument.Parse(json);
        var code   = doc.RootElement.GetProperty("code").GetString();
        var data   = doc.RootElement.GetProperty("data");

        if (code != "0" || data.GetArrayLength() == 0)
        {
            // sCode + sMsg 모두 포함해 원인 진단 가능하게
            string msg;
            if (data.GetArrayLength() > 0)
            {
                var sCode = data[0].TryGetProperty("sCode", out var sc) ? sc.GetString() : code;
                var sMsg  = data[0].TryGetProperty("sMsg",  out var sm) ? sm.GetString() : "unknown";
                msg = $"[{sCode}] {sMsg}";
            }
            else
            {
                msg = $"[{code}] empty data";
            }
            _logger.LogError("[ALGO {tag}] 실패: {msg} | raw={json}", tag, msg, json);
            return new OrderResult { Success = false, ErrorMessage = msg };
        }

        var algoId = data[0].GetProperty("algoId").GetString()!;
        _logger.LogInformation("[ALGO {tag}] 등록 성공: algoId={algoId}", tag, algoId);

        return new OrderResult { Success = true, OrderId = algoId };
    }

    // ─────────────────────────────────────────────
    // HTTP 헬퍼
    // ─────────────────────────────────────────────

    private async Task<string> GetPublicAsync(string path)
    {
        var resp = await _http.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> GetPrivateAsync(string path)
    {
        var req = BuildPrivateRequest(HttpMethod.Get, path, "");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> PostPrivateAsync(string path, string body)
    {
        var req = BuildPrivateRequest(HttpMethod.Post, path, body);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// OKX 인증 헤더 생성
    /// 서명: Base64(HMAC-SHA256(timestamp + method + path + body, secretKey))
    /// </summary>
    private HttpRequestMessage BuildPrivateRequest(HttpMethod method, string path, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var methodStr = method.Method.ToUpper();
        var message   = timestamp + methodStr + path + body;
        var sign      = Sign(message);

        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("OK-ACCESS-KEY",       _apiKey);
        req.Headers.Add("OK-ACCESS-SIGN",      sign);
        req.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        req.Headers.Add("OK-ACCESS-PASSPHRASE", _passphrase);
        req.Headers.Add("x-simulated-trading", "0"); // 1이면 모의거래
        return req;
    }

    private string Sign(string message)
    {
        var key  = Encoding.UTF8.GetBytes(_apiSecret);
        var data = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }
}
