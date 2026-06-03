#!/bin/bash
PROJECT="src/OKXTradingBot.UI/OKXTradingBot.UI.csproj"
OUTPUT="bin/Release/Orion"
APP_BUNDLE="bin/Release/Orion.app"

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

# macOS .app 번들 생성
if [[ "$OSTYPE" == "darwin"* ]]; then
    echo "macOS .app 번들 생성 중..."
    rm -rf "$APP_BUNDLE"
    mkdir -p "$APP_BUNDLE/Contents/MacOS"
    mkdir -p "$APP_BUNDLE/Contents/Resources"

    # 실행 파일 + 전체 파일 복사
    cp -R "$OUTPUT/"* "$APP_BUNDLE/Contents/MacOS/"

    # 아이콘 복사
    ICON_SRC="src/OKXTradingBot.UI/Assets/Orion.icns"
    if [ -f "$ICON_SRC" ]; then
        cp "$ICON_SRC" "$APP_BUNDLE/Contents/Resources/Orion.icns"
    fi

    # Info.plist 생성
    cat > "$APP_BUNDLE/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>Orion</string>
    <key>CFBundleIdentifier</key>
    <string>com.orion.okxtradingbot</string>
    <key>CFBundleName</key>
    <string>Orion</string>
    <key>CFBundleDisplayName</key>
    <string>Orion Trading Bot</string>
    <key>CFBundleIconFile</key>
    <string>Orion</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

    chmod +x "$APP_BUNDLE/Contents/MacOS/Orion"
    echo "✓ .app 번들 생성 완료: $APP_BUNDLE"
    echo "  → 파인더에서 더블클릭으로 실행 가능"
    echo ""
fi

# 실행
exec "./$OUTPUT/Orion"
