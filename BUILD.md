# OKX AutoTradingBot — 빌드 가이드

## 필요 환경
- .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 또는 macOS

## 빌드 및 실행

```bash
# 솔루션 루트에서
dotnet restore
dotnet build

# Core + Infrastructure만 빌드 (1단계 확인용)
dotnet build src/OKXTradingBot.Core/OKXTradingBot.Core.csproj
dotnet build src/OKXTradingBot.Infrastructure/OKXTradingBot.Infrastructure.csproj
```

## 현재 완성 단계

| 단계 | 내용 | 상태 |
|------|------|------|
| 1단계 | 프로젝트 구조, OKX API, 기본 주문 실행 | ✅ 완료 |
| 2단계 | GPT 분석 모듈, 마틴게일 로직 | 🔜 다음 |
| 3단계 | 백테스트 엔진, 과거 데이터 수집 | 🔜 예정 |
| 4단계 | Avalonia UI | 🔜 예정 |
| 5단계 | 텔레그램, 로그, SQLite | 🔜 예정 |
| 6~7단계 | 통합 테스트, 실매매 | 🔜 예정 |

## 프로젝트 구조

```
OKXTradingBot/
├── src/
│   ├── OKXTradingBot.Core/              # 비즈니스 로직 (프레임워크 무관)
│   │   ├── Interfaces/
│   │   │   ├── IDataProvider.cs         # 캔들 데이터 공급 인터페이스
│   │   │   ├── IOrderExecutor.cs        # 주문 실행 인터페이스
│   │   │   └── INotifier.cs             # 알림 인터페이스
│   │   ├── Models/
│   │   │   ├── Candle.cs                # 1분봉 모델
│   │   │   ├── TradeConfig.cs           # 전략 설정 (총 투자금, 마틴 횟수 등)
│   │   │   ├── Position.cs              # 포지션 상태
│   │   │   ├── OrderResult.cs           # 주문 결과
│   │   │   └── GptAnalysisResult.cs     # GPT 분석 결과
│   │   └── Trading/
│   │       └── TradingCore.cs           # 마틴게일 매매 로직 (핵심)
│   │
│   ├── OKXTradingBot.Infrastructure/    # 외부 의존성 구현체
│   │   ├── OKX/
│   │   │   ├── OkxRestClient.cs         # REST API (인증, 주문, 잔고)
│   │   │   ├── OkxWebSocketClient.cs    # WebSocket (1분봉 실시간)
│   │   │   ├── OkxDataProvider.cs       # IDataProvider 실매매 구현
│   │   │   └── OkxOrderExecutor.cs      # IOrderExecutor 실매매 구현
│   │   ├── Backtest/
│   │   │   └── VirtualOrderExecutor.cs  # IOrderExecutor 백테스트 구현
│   │   └── Notifications/
│   │       └── TelegramNotifier.cs      # INotifier 텔레그램 구현
│   │
│   └── OKXTradingBot.UI/                # Avalonia UI (4단계에서 구현)
```

## 설정값 예시

```csharp
var config = new TradeConfig
{
    ApiKey       = "YOUR_OKX_API_KEY",
    ApiSecret    = "YOUR_OKX_SECRET",
    Passphrase   = "YOUR_PASSPHRASE",
    GptApiKey    = "YOUR_OPENAI_KEY",

    Symbol       = "BTC-USDT-SWAP",
    TotalBudget  = 1000m,   // 총 투자금 1000 USDT
    Leverage     = 10,
    MartinCount  = 9,       // → 1회 진입금 = 1000/9 ≈ 111 USDT
    MartinGap    = 0.5m,    // 0.5% 간격마다 추가 진입
    TargetProfit = 0.5m,    // 0.5% 달성 시 익절

    StopLossEnabled = false,
    StopLossPercent = 3.0m,

    TelegramBotToken = "YOUR_BOT_TOKEN",
    TelegramChatId   = "YOUR_CHAT_ID"
};
// SingleOrderAmount = 1000 / 9 = 111.11 USDT (자동 계산)
```
