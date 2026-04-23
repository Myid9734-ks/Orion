# 라이센스 RSA 키 (중요)

이 디렉토리에는 라이센스 서명용 RSA 키 쌍이 들어있습니다.

## 파일

- **`private.pem`** — **절대 유출/커밋 금지!** 판매자만 보관.
  - 유출 시 누구나 유효한 라이센스를 생성할 수 있어 모든 사본 무력화.
  - `.gitignore`에 포함되어 있음.
- **`public.pem`** — 앱에 내장되는 공개키. 검증 전용.
  - `src/OKXTradingBot.UI/Assets/public.pem`으로 복사되어 빌드에 포함됨.

## 라이센스 발급 절차

1. 구매자가 앱 실행 → 라이센스 오류 다이얼로그에 표시된 **머신 ID** 복사 후 전달.
2. 판매자가 LicenseGen 도구로 라이센스 파일 생성:

```bash
cd OKXTradingBot
dotnet run --project src/OKXTradingBot.LicenseGen -- \
  --machine "A3F9-BC12-45D7-89AB" \
  --owner  "홍길동" \
  --expires 2027-12-31 \
  --key    keys/private.pem \
  --out    license.dat
```

3. 생성된 `license.dat`를 구매자에게 전달.
4. 구매자가 파일을 아래 경로에 배치:
   - Windows: `%UserProfile%\.okxtradingbot\license.dat`
   - macOS/Linux: `~/.okxtradingbot/license.dat`

## 키 재생성 (비상시)

개인키 유출이 의심되면 키 쌍을 재생성하고 앱을 재배포해야 합니다.

```bash
openssl genrsa -out private.pem 2048
openssl rsa -in private.pem -pubout -out public.pem
cp public.pem ../src/OKXTradingBot.UI/Assets/public.pem
```

이후 모든 기존 라이센스는 무효화되며, 전체 구매자에게 재발급이 필요합니다.
