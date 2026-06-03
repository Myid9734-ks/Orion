#!/bin/bash
# 사용법: ./gen-license.sh <CpuId> <만료일>
# 예시:   ./gen-license.sh BF4BD8A3-D4E4-5E81-B13A-703BEC34FDBD 2027-12-31

if [ -z "$1" ] || [ -z "$2" ]; then
  echo "사용법: ./gen-license.sh <CpuId> <만료일 yyyy-MM-dd>"
  echo "예시:   ./gen-license.sh BF4BD8A3-D4E4-5E81-B13A-703BEC34FDBD 2027-12-31"
  exit 1
fi

mkdir -p license
dotnet run --project src/OKXTradingBot.LicenseGen -c Release -- \
  --machine "$1" \
  --owner "Orion" \
  --expires "$2" \
  --key /Users/mypc/.okxtradingbot/keys/private.pem \
  --out license/license.dat
