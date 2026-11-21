import type { Catalog } from './catalog';

// 토큰은 앱이 연 주소의 #token= 으로 받는다 (조각이라 서버 로그 · Referer에 남지 않음). 새로 고쳐도 되게 sessionStorage에.
const tokenKey = 'hblint-token';

function readToken(): string {
  const match = /(?:^#|&)token=([0-9a-f]+)/.exec(location.hash);
  if (match) {
    sessionStorage.setItem(tokenKey, match[1]);
    history.replaceState(null, '', location.pathname + location.search);
  }
  return sessionStorage.getItem(tokenKey) ?? '';
}

const token = readToken();

export class ApiError extends Error {
  /** data = 오류 응답 본문 (JSON이면) — 예: 프로젝트 읽기 실패의 csproj 후보. */
  constructor(message: string, readonly status: number, readonly data?: unknown) {
    super(message);
  }
}

async function call<T>(method: string, path: string, body?: string | object): Promise<T> {
  const headers: Record<string, string> = { 'X-HBLint-Token': token };
  let payload: string | undefined;
  if (typeof body === 'string') {
    headers['Content-Type'] = 'text/plain; charset=utf-8';
    payload = body;
  } else if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    payload = JSON.stringify(body);
  }
  const response = await fetch('api' + path, { method, headers, body: payload });
  const text = await response.text();
  if (!response.ok) {
    let message = text || response.statusText;
    let data: unknown;
    try {
      data = JSON.parse(text);
      message = (data as { error?: string }).error ?? message;
    } catch {
      // 글 그대로
    }
    if (response.status === 401) message = '토큰이 없거나 틀렸습니다 — hblint-app이 연 주소(#token=…)로 다시 여세요';
    throw new ApiError(message, response.status, data);
  }
  return (text ? JSON.parse(text) : undefined) as T;
}

export interface IrError {
  message: string;
  line: number;
  column: number;
  warning: boolean;
  node: string | null;
}

export interface NameProblem {
  node: string;
  field: string;
  value: string;
  message: string;
}

export interface Validation {
  ok: boolean;
  errors: IrError[];
  names: NameProblem[] | null;
}

export interface RuleSummary {
  file: string;
  id: string | null;
  title: string | null;
  errors: number;
  warnings: number;
  /** false = 생성(CLI)은 읽지만 앱에서는 못 여는 파일 (이름 규칙 밖 · 폴더 밖 링크). */
  editable: boolean;
}

export interface RecentEntry {
  rules: string;
  out: string;
  project: string | null;
  csproj: string | null;
}

export interface ProjectState {
  csproj: string;
  kind: 'unity' | 'sdk';
  assemblyName: string;
  sources: number;
  references: number;
  missing: string[];
  types: number;
  loadMs: number;
  candidates: string[];
}

export interface AppStateDto {
  defaults: { rules: string | null; out: string | null; project: string | null };
  rules: string | null;
  out: string | null;
  project: ProjectState | null;
  recent: RecentEntry[];
}

export interface TypeEntry {
  metadataName: string;
  name: string;
  kind: string;
  assembly: string;
}

export interface MemberEntry {
  name: string;
  kind: string;
  overloads: number;
}

export interface PreviewHit {
  id: string;
  severity: string;
  file: string | null;
  line: number;
  column: number;
  message: string;
}

export interface PreviewResult {
  hits: PreviewHit[];
  total: number;
  compileErrors: number;
  elapsedMs: number;
}

/** POST /unity — 처음이면 needsSetup(+ 만들 자리 · 패키지 있는지), 열었으면 state · 만든 파일. */
export interface UnityOpen {
  needsSetup: boolean;
  rules?: string;
  out?: string;
  hasPackage?: boolean;
  created?: string[];
  state?: AppStateDto;
}

export interface GenerateResult {
  ok: boolean;
  output: string;
  count: number;
  errors: string[];
  warnings: string[];
}

const q = encodeURIComponent;

export const api = {
  hasToken: () => token.length > 0,
  catalog: () => call<Catalog>('GET', '/catalog'),
  state: () => call<AppStateDto>('GET', '/state'),
  openWorkspace: (rules: string, out: string) => call<AppStateDto>('POST', '/workspace', { rules, out }),
  openUnity: (path: string, setup: boolean) => call<UnityOpen>('POST', '/unity', { path, setup }),
  /** 자리만 알아봄 — 서버 상태를 바꾸지 않는다. */
  probeUnity: (path: string) => call<UnityOpen>('POST', '/unity', { path, probe: true }),
  loadProject: (path: string, csproj: string | null) => call<AppStateDto>('POST', '/project', { path, csproj }),
  rules: () => call<{ rules: RuleSummary[]; nextId: string | null }>('GET', '/rules'),
  rule: (file: string) => call<{ file: string; text: string; validation: Validation }>('GET', '/rules/' + q(file)),
  save: (file: string, text: string, create: boolean) =>
    call<{ file: string; changed: boolean; text: string; validation: Validation }>(create ? 'POST' : 'PUT', '/rules/' + q(file), text),
  validate: (text: string) => call<Validation>('POST', '/validate', text),
  generate: () => call<GenerateResult>('POST', '/generate'),
  preview: (text: string) => call<PreviewResult>('POST', '/preview', text),
  types: (text: string) => call<TypeEntry[]>('GET', '/symbols/types?q=' + q(text)),
  namespaces: (text: string) => call<string[]>('GET', '/symbols/namespaces?q=' + q(text)),
  members: (type: string, text: string) => call<MemberEntry[]>('GET', '/symbols/members?type=' + q(type) + '&q=' + q(text)),
};
