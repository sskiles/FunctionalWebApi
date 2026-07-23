#!/usr/bin/env bash
# Smoke tests for FunctionalWebApi AOT binary
# Usage: ./smoke-test.sh [path-to-binary]

set -euo pipefail

BINARY="${1:-./bin/Release/net10.0/linux-x64/publish/FunctionalWebApi}"
DB="/tmp/webapi_smoke.db"
URL="http://localhost:5050"

if [[ ! -f "$BINARY" ]]; then
    echo "Binary not found at $BINARY"
    echo "Build first: dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64"
    exit 1
fi

cleanup() {
    kill "$APP_PID" 2>/dev/null || true
    rm -f "$DB"
}
trap cleanup EXIT

rm -f "$DB"

echo "Starting $BINARY ..."
ASPNETCORE_URLS="$URL" \
ConnectionStrings__Sqlite="Data Source=$DB" \
"$BINARY" &
APP_PID=$!

# Wait for server to be ready
for i in {1..10}; do
    if curl -fsS "$URL/users" >/dev/null 2>&1; then
        break
    fi
    sleep 0.5
done

echo "== create =="
curl -fsS -X POST "$URL/users" \
  -H "content-type: application/json" \
  -d '{"name":"Alice","email":"alice@example.com","password":"hunter2","confirmPassword":"hunter2"}'
echo

echo "== get-by-id =="
curl -fsS "$URL/users/1"
echo

echo "== list =="
curl -fsS "$URL/users"
echo

echo "== login =="
TOKEN=$(curl -fsS -X POST "$URL/login" \
  -H "content-type: application/json" \
  -d '{"username":"alice@example.com","password":"hunter2"}' | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")
echo "$TOKEN"
echo

echo "== login wrong (expect 401) =="
curl -sS -o /dev/null -w "status=%{http_code}\n" -X POST "$URL/login" \
  -H "content-type: application/json" \
  -d '{"username":"alice@example.com","password":""}'
echo

echo "== change-password =="
curl -sS -o /tmp/cp.json -w "status=%{http_code}\n" -X PUT "$URL/users/1/password" \
  -H "content-type: application/json" \
  -d '{"currentPassword":"hunter2","newPassword":"new-secret","confirmNewPassword":"new-secret"}'
cat /tmp/cp.json
echo

echo "== login with new password =="
curl -sS -o /tmp/ln.json -w "status=%{http_code}\n" -X POST "$URL/login" \
  -H "content-type: application/json" \
  -d '{"username":"alice@example.com","password":"new-secret"}'
cat /tmp/ln.json
echo

echo "All smoke tests passed!"