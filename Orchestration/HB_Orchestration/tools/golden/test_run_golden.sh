#!/usr/bin/env bash
# run_golden.sh 시험 (외부 호출 없음): PATH 맨 앞에 가짜 dotnet을 두고 사례별 종료 코드를 흉내 낸다.
# 사용: bash test_run_golden.sh   → 끝 줄 "ALL PASS" 또는 "FAIL n"
here="$(cd "$(dirname "$0")" && pwd)"
root="$(git -C "$here" rev-parse --show-toplevel)" || exit 1
fake="$(mktemp -d)"
trap 'rm -rf "$fake"' EXIT
cat > "$fake/dotnet" <<'EOF'
#!/usr/bin/env bash
# 인자: <dll> --golden run <사례> ... — FAKE_CODES="사례=코드 ..."에 있으면 그 코드로 끝난다
c="$4"
echo "$c" >> "$FAKE_LOG"
code=0
for kv in $FAKE_CODES; do [ "${kv%%=*}" = "$c" ] && code="${kv#*=}"; done
case $code in
  0) echo "[$c] 찾음 1/1" ;;
  1) echo "[$c] 실행 실패: 가짜 오류" ;;
  2) echo "[리뷰 지침 없음] 가짜 거절" >&2 ;;
  3) echo "[Quota] 가짜 한도" ;;
esac
exit $code
EOF
chmod +x "$fake/dotnet"

fails=0
first=$(ls "$root/GoldenSet" | grep -E '^(rec|seed)-' | head -1)
second=$(ls "$root/GoldenSet" | grep -E '^(rec|seed)-' | sed -n 2p)
last=$(ls "$root/GoldenSet" | grep -E '^(rec|seed)-' | tail -1)

run() { # 이름 기대코드 FAKE_CODES [stderr에 있어야 할 문구] [마지막으로 불린 사례]
  local name="$1" want="$2" codes="$3" text="$4" lastcase="$5"
  export FAKE_LOG="$fake/log.txt"; : > "$FAKE_LOG"
  local err; err=$(PATH="$fake:$PATH" FAKE_CODES="$codes" bash "$here/run_golden.sh" t review_main 2>&1 >/dev/null); local got=$?
  local ok=1
  [ "$got" = "$want" ] || ok=0
  [ -z "$text" ] || printf '%s' "$err" | grep -qF -- "$text" || ok=0
  [ -z "$lastcase" ] || [ "$(tail -1 "$FAKE_LOG")" = "$lastcase" ] || ok=0
  if [ $ok = 1 ]; then echo "PASS $name"; else echo "FAIL $name (종료 $got, 기대 $want) stderr: $err · 마지막 사례: $(tail -1 "$FAKE_LOG")"; fails=$((fails+1)); fi
}

run '모두 성공 → 0' 0 '' '' "$last"
run '사례 실패(1)는 계속 · 끝에 목록 · 종료 1' 1 "$first=1 $last=1" "[실패 사례 2개] $first $last" "$last"
run '실행 전 거절(2)은 그 사례에서 멈춤' 2 "$second=2" "[중단] $second 골든 종료 코드 2" "$second"
run 'Quota(3)는 멈춤' 3 "$first=3" "[중단] $first 골든 종료 코드 3" "$first"
run '실패 뒤 거절이면 거절 코드' 2 "$first=1 $second=2" "[중단] $second" "$second"

export FAKE_LOG="$fake/log.txt"; : > "$FAKE_LOG"
REVIEW_GUIDELINES_PATH="$root/nope/REVIEW_GUIDELINES.md" PATH="$fake:$PATH" bash "$here/run_golden.sh" t review_main >/dev/null 2>&1; got=$?
if [ $got = 2 ] && [ ! -s "$FAKE_LOG" ]; then echo "PASS 지침 경로 파일 없음 → 호출 전 2"; else echo "FAIL 지침 경로 파일 없음 (종료 $got)"; fails=$((fails+1)); fi
: > "$FAKE_LOG"
REVIEW_GUIDELINES_PATH='REVIEW_GUIDELINES.md' PATH="$fake:$PATH" bash "$here/run_golden.sh" t review_main >/dev/null 2>&1; got=$?
if [ $got = 2 ] && [ ! -s "$FAKE_LOG" ]; then echo "PASS 상대 경로 → 호출 전 2"; else echo "FAIL 상대 경로 (종료 $got)"; fails=$((fails+1)); fi

if [ $fails = 0 ]; then echo "ALL PASS"; else echo "FAIL $fails"; exit 1; fi
