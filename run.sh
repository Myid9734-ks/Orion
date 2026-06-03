#!/bin/bash
PROJECT="src/OKXTradingBot.UI/OKXTradingBot.UI.csproj"
OUTPUT="bin/Release/Orion"

# 현재 PC의 머신 ID 자동 읽기
if [[ "$OSTYPE" == "darwin"* ]]; then
    MACHINE_ID=$(ioreg -rd1 -c IOPlatformExpertDevice | grep IOPlatformUUID | sed 's/.*= "\(.*\)"/\1/')
else
    MACHINE_ID=$(wmic cpu get ProcessorId /value 2>/dev/null | grep ProcessorId | cut -d= -f2 | tr -d '\r\n ')
fi

if [[ -z "$MACHINE_ID" ]]; then
    echo "오류: 머신 ID를 읽을 수 없습니다."
    exit 1
fi

echo "머신 ID: $MACHINE_ID"

# 빌드 (license.dat 자동 생성)
dotnet build "$PROJECT" -c Release -p:CpuId="$MACHINE_ID" -o "$OUTPUT" || exit 1

# 실행
exec "./$OUTPUT/OKXTradingBot.UI"
