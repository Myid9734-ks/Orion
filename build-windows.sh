#!/bin/bash
# 윈도우용 exe 빌드
# 사용법: ./build-windows.sh <CpuId> [만료일]
# 예시:   ./build-windows.sh BFEBFBFF000A0655 2027-12-31

PROJECT="src/OKXTradingBot.UI/OKXTradingBot.UI.csproj"
OUTPUT="bin/Release/Orion-Windows"

CPU_ID="${1}"
EXPIRE="${2:-}"  # 미입력 시 1년 자동

if [ -z "$CPU_ID" ]; then
  echo "사용법: ./build-windows.sh <CpuId> [만료일 yyyy-MM-dd]"
  echo "예시:   ./build-windows.sh BFEBFBFF000A0655 2027-12-31"
  exit 1
fi

EXPIRE_ARG=""
if [ -n "$EXPIRE" ]; then
  EXPIRE_ARG="-p:LicenseExpire=$EXPIRE"
fi

echo "Windows exe 빌드 시작..."
echo "CPU ID : $CPU_ID"
echo "만료일 : ${EXPIRE:-오늘부터 1년}"

dotnet publish "$PROJECT" -c Release -r win-x64 --self-contained \
    -p:CpuId="$CPU_ID" $EXPIRE_ARG -o "$OUTPUT" || exit 1

echo ""
echo "✓ 완료: $OUTPUT/OKXTradingBot.UI.exe"
