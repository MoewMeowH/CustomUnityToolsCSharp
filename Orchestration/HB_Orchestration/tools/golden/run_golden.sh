#!/usr/bin/env bash
# 골든 리뷰 사례(rec-* · seed-*)를 한 회차 돈다.
# 사용: run_golden.sh <라벨> <모드> [건너뛸 사례 ...]
# 환경 변수 HB_MODES_PATH · REVIEW_GUIDELINES_PATH는 호출하는 쪽에서 준다 (이 프로세스에만 적용). 절대 경로만 받는다.
label="$1"; mode="$2"; shift 2
# 상대 경로는 실행 폴더 기준이라 엉뚱한 파일을 가리킬 수 있다 (2026-10-20 1회차) — 절대 경로가 아니면 멈춘다.
abs_re='^([A-Za-z]:[/\\]|/)'
for v in HB_MODES_PATH REVIEW_GUIDELINES_PATH; do
  p="${!v}"
  if [ -n "$p" ] && ! [[ "$p" =~ $abs_re ]]; then echo "[중단] $v는 절대 경로만: '$p'" >&2; exit 2; fi
  # 없는 파일이면 서버가 다음 후보(루트 REVIEW_GUIDELINES.md · 서버 폴더 modes.json)로 조용히 넘어간다 (2026-10-22)
  if [ -n "$p" ] && ! [ -f "$p" ]; then echo "[중단] $v 파일이 없음: '$p'" >&2; exit 2; fi
done
skip=" $* "
failed=()
# 저장소 루트 (GoldenSet · Orchestration/Publish_Output 기준). 기기마다 다른 절대 경로를 스크립트에 두지 않는다.
root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)" || exit 1
cd "$root" || exit 1
for c in $(ls GoldenSet | grep -E '^(rec|seed)-'); do
  case "$skip" in *" $c "*) continue ;; esac
  out=$(dotnet Orchestration/Publish_Output/HB.Orchestration.Mcp.dll --golden run "$c" --mode "$mode" --dir GoldenSet --label "$label" --yes 2>&1)
  code=$?
  printf '%s\n' "$out" | grep -E "^\[$c\]|실패|\[Quota\]"
  # 실행 전 거절(지침 없음·지침 경로 없음 등, 종료 2)은 위 거르기에 안 걸린다 — 숨기지 않고 멈춘다 (2026-10-22).
  # 3 = Quota도 멈춘다 (CLAUDE.md: 바로 재실행하지 않음). 1 = 사례 실패는 예전처럼 다음 사례로 (실패 1로 센다).
  if [ $code -eq 2 ] || [ $code -eq 3 ]; then echo "[중단] $c 골든 종료 코드 $code:" >&2; printf '%s\n' "$out" | grep -E "^\[" | tail -5 >&2; exit $code; fi
  if [ $code -ne 0 ]; then failed+=("$c"); fi
done
# 실패가 섞인 회차가 성공으로 보이지 않게 끝에 목록을 보이고 0이 아닌 코드로 끝낸다 (2026-10-22)
if [ ${#failed[@]} -gt 0 ]; then echo "[실패 사례 ${#failed[@]}개] ${failed[*]}" >&2; exit 1; fi
