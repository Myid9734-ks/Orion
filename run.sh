#!/bin/bash
PROJECT="src/OKXTradingBot.UI/OKXTradingBot.UI.csproj"
OUTPUT="bin/Release/Orion"

# 현재 PC의 머신 ID 자동 읽기
if [[ "$OSTYPE" == "darwin"* ]]; then
    MACHINE_ID=$(ioreg -rd1 -c IOPlatformExpertDevice | grep IOPlatformUUID | sed 's/.*= "\(.*\)"/\1/')
    RID="osx-arm64"
else
    MACHINE_ID=$(wmic cpu get ProcessorId /value 2>/dev/null | grep ProcessorId | cut -d= -f2 | tr -d '\r\n ')
    RID="win-x64"
fi

if [[ -z "$MACHINE_ID" ]]; then
    echo "오류: 머신 ID를 읽을 수 없습니다."
    exit 1
fi

echo "머신 ID: $MACHINE_ID"
echo "플랫폼: $RID"

# 퍼블리시 (자체 실행 파일 생성)
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained \
    -p:CpuId="$MACHINE_ID" -o "$OUTPUT" || exit 1

# 실행
exec "./$OUTPUT/OKXTradingBot.UI"
