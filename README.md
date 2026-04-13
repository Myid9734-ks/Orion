# Orion — OKX AutoTrading Bot

OKX 무기한 선물(SWAP) 대상 **마틴게일 자동매매 봇** (Avalonia UI, .NET 8, macOS/Windows)

> GPT 방향 분석 + 마틴게일 분할 진입 + 탭별 독립 복수 종목 동시 운용

---

## 주요 기능

| 기능 | 설명 |
|------|------|
| **복수 종목 탭** | 최대 5개 종목을 탭별로 독립 운용 (설정·포지션·로그 분리) |
| **마틴게일 전략** | 분할 진입 최대 20회, 단계별 커스텀 간격·익절 설정 |
| **GPT 방향 분석** | OpenAI 모델로 캔들 데이터 분석 후 Long/Short 결정 |
| **백테스트 모드** | 가상 잔고로 실매매와 동일한 로직 검증 |
| **실시간 차트** | OKX WebSocket 기반 라인/캔들 차트 (1m·5m·15m·1H·4H·1D) |
| **텔레그램 알림** | 진입·청산·손절·오류 알림, 수신 제한 시간 설정 |
| **설정 암호화** | API 키는 AES-256으로 로컬 암호화 저장 |

---

## 스크린샷

> 트레이딩 탭 — 실시간 차트 + 마틴게일 설정 + 포지션 현황

---

## 빌드 및 실행

### 필요 환경

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- macOS 12+ 또는 Windows 10/11

### 실행

```bash
git clone https://github.com/Myid9734-ks/Orion.git
cd Orion

dotnet restore
dotnet run --project src/OKXTradingBot.UI/OKXTradingBot.UI.csproj
```

---

## 프로젝트 구조

```
OKXTradingBot/
├── src/
│   ├── OKXTradingBot.Core/              # 비즈니스 로직 (프레임워크 무관)
│   │   ├── Interfaces/                  # IDataProvider, IOrderExecutor, INotifier
│   │   ├── Models/                      # Candle, TradeConfig, Position, OrderResult
│   │   └── Trading/
│   │       └── TradingCore.cs           # 마틴게일 매매 핵심 로직
│   │
│   ├── OKXTradingBot.Infrastructure/    # 외부 의존성 구현체
│   │   ├── OKX/
│   │   │   ├── OkxRestClient.cs         # REST API (인증·주문·잔고·캔들)
│   │   │   ├── OkxWebSocketClient.cs    # WebSocket 실시간 캔들
│   │   │   ├── OkxDataProvider.cs       # IDataProvider 실매매 구현
│   │   │   └── OkxOrderExecutor.cs      # IOrderExecutor 실매매 구현
│   │   ├── Backtest/
│   │   │   └── VirtualOrderExecutor.cs  # IOrderExecutor 백테스트 구현
│   │   └── Notifications/
│   │       └── TelegramNotifier.cs      # 텔레그램 알림
│   │
│   └── OKXTradingBot.UI/                # Avalonia UI
│       ├── AppConstants.cs              # MaxSymbolTabs 등 빌드 상수
│       ├── ViewModels/
│       │   ├── MainWindowViewModel.cs   # 전역 설정·탭 관리
│       │   └── SymbolTabViewModel.cs    # 탭별 독립 상태·TradingCore
│       └── Views/
│           ├── MainWindow.axaml         # 메인 윈도우 (트레이딩·수익률·설정 탭)
│           └── SymbolTabView.axaml      # 종목 탭 UI (차트·로그·컨트롤)
```

---

## 설정 방법

앱 실행 후 **설정 탭**에서 입력:

| 항목 | 설명 |
|------|------|
| OKX API Key / Secret / Passphrase | OKX 거래소 API 키 (선물 거래 권한 필요) |
| GPT API Key | OpenAI API 키 |
| GPT 모델 | 자동 목록 조회 또는 직접 입력 |
| Telegram Bot Token / Chat ID | 알림 수신용 봇 설정 |

API 키는 **AES-256** 으로 암호화되어 로컬(`~/.okxtradingbot/settings.enc.json`)에 저장됩니다.

---

## 전략 파라미터

| 파라미터 | 기본값 | 설명 |
|----------|--------|------|
| 분할 횟수 | 9 | 최대 마틴게일 진입 횟수 |
| 진입 간격 (%) | 0.5 | 추가 진입 트리거 가격 변동폭 |
| 목표 수익 (%) | 0.5 | 익절 기준 |
| 레버리지 | 10 | 선물 레버리지 (1~125) |
| 손절 기준 (%) | 비활성 | 선택적 손절 설정 |
| 자동반복 | 활성 | 청산 후 자동으로 다음 사이클 시작 |

단계별 커스텀 모드에서 각 마틴게일 단계마다 진입 간격·목표수익을 개별 설정할 수 있습니다.

---

## 복수 종목 운용

- **`+` 버튼**으로 탭 추가 (최대 5개, `AppConstants.MaxSymbolTabs`로 빌드 시 변경 가능)
- 각 탭은 심볼·레버리지·마틴 설정·포지션·로그가 완전히 독립
- 탭별로 시작/중지 가능 — 한 종목이 실행 중이어도 다른 종목 추가 가능
- `×` 버튼으로 탭 닫기 (실행 중이면 자동 중지 후 제거)

---

## 개발 로드맵

| 단계 | 내용 | 상태 |
|------|------|------|
| 1단계 | 프로젝트 구조, OKX API, 기본 주문 실행 | ✅ 완료 |
| 2단계 | GPT 분석 모듈 실제 연동 | 🔧 진행 중 |
| 3단계 | 백테스트 엔진, 과거 데이터 수집 | 🔜 예정 |
| 4단계 | Avalonia UI — 복수 종목 탭, 실시간 차트 | ✅ 완료 |
| 5단계 | 텔레그램 알림, 수익률 통계 | ✅ 완료 |
| 6단계 | 통합 테스트, 실매매 검증 | 🔜 예정 |

---

## 주의 사항

- 이 봇은 **실제 자금을 운용**합니다. 충분한 백테스트 후 소액으로 시작하세요.
- 마틴게일 전략은 연속 손실 시 투자금이 빠르게 소진될 수 있습니다.
- API 키에는 **출금 권한을 부여하지 마세요** (거래 권한만 필요).

---

## 라이선스

MIT License
