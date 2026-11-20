"""대상 프로젝트의 .claude/settings.json에 project-files 훅 등록(settings.example.json) 중 빠진 것만 더한다. 있는 등록은 건드리지 않는다.

사용: python -I tools/add_hooks.py <프로젝트 폴더>...   |   python -I tools/add_hooks.py -All   (옆의 sync-targets.local.txt 대상 모두)

- Sync-ProjectFiles.ps1은 settings.json을 덮지 않으므로(확인만), 새 훅을 원본에 더한 뒤 대상마다 등록을 맞출 때 쓴다.
- 순서: Sync로 스크립트를 먼저 맞춘 뒤 돌린다. 등록된 스크립트 파일이 대상 .claude/scripts/에 아직 없으면 그 대상은 건너뛴다
  (막는 훅은 '|| exit 2'라 스크립트가 없으면 모든 도구 호출이 막힌다).
- 같은 사건 · 같은 matcher 항목 안에서, 등록된 스크립트 이름(.claude/scripts/<이름>.py)이 아직 없는 훅만 덧붙인다. matcher 항목이 없으면 새로 만든다.
- 바꾸기 전 settings.json을 settings.json.bak-<시각>으로 남긴다. settings.json이 없으면 건너뛴다 (Sync가 없을 때 템플릿으로 만든다).
"""
import datetime
import json
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
EXAMPLE = os.path.join(HERE, '..', 'project-files', '.claude', 'settings.example.json')
TARGETS = os.path.join(HERE, 'sync-targets.local.txt')
SCRIPT = re.compile(r'\.claude/scripts/([A-Za-z0-9_]+\.py)')


def scripts_of(entry: dict) -> set[str]:
    return {m for h in entry.get('hooks') or [] for m in SCRIPT.findall(h.get('command') or '')}


def targets(args: list[str]) -> list[str]:
    if args != ['-All']:
        return args
    with open(TARGETS, encoding='utf-8-sig') as f:
        return [l.strip() for l in f if l.strip() and not l.lstrip().startswith('#')]


def merge(project: str, example: dict, stamp: str) -> str:
    path = os.path.join(project, '.claude', 'settings.json')
    if not os.path.isfile(path):
        return f'[건너뜀] {project}: settings.json 없음'
    needed = {s for entries in example.values() for e in entries for s in scripts_of(e)}
    missing = sorted(s for s in needed if not os.path.isfile(os.path.join(project, '.claude', 'scripts', s)))
    if missing:
        return f'[건너뜀] {project}: 스크립트가 아직 없음 ({", ".join(missing)}) — Sync로 먼저 맞춘다'
    with open(path, encoding='utf-8-sig') as f:
        data = json.load(f)
    hooks = data.setdefault('hooks', {})
    added = []
    for event, entries in example.items():
        target = hooks.setdefault(event, [])
        for e in entries:
            matcher = e.get('matcher')
            same = next((t for t in target if t.get('matcher') == matcher), None)
            if same is None:
                target.append(json.loads(json.dumps(e)))
                added += [f'{event}/{matcher or "*"}/{s}' for s in sorted(scripts_of(e))]
                continue
            have = scripts_of(same)
            for h in e.get('hooks') or []:
                names = set(SCRIPT.findall(h.get('command') or ''))
                if names and not names & have:
                    same.setdefault('hooks', []).append(json.loads(json.dumps(h)))
                    added += [f'{event}/{matcher or "*"}/{n}' for n in sorted(names)]
    if not added:
        return f'[같음] {project}'
    shutil.copyfile(path, f'{path}.bak-{stamp}')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write('\n')
    return f'[더함] {project}: ' + ', '.join(added) + f' (백업 settings.json.bak-{stamp})'


def main() -> int:
    sys.stdout.reconfigure(encoding='utf-8')
    if not sys.argv[1:]:
        print(__doc__)
        return 2
    with open(EXAMPLE, encoding='utf-8') as f:
        example = json.load(f)['hooks']
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    for project in targets(sys.argv[1:]):
        print(merge(project, example, stamp))
    return 0


if __name__ == '__main__':
    sys.exit(main())
