// 서버 /api/catalog (원본: HBLint.Core NodeCatalog) 모양.

export type Socket = 'Scope' | 'Types' | 'Methods' | 'Sites' | 'Groups';

export type FieldType = 'string' | 'stringArray' | 'int' | 'bool' | 'choice' | 'choiceArray';

export type FieldHint = 'type' | 'member' | 'namespace' | 'define' | 'assembly';

export interface FieldSpec {
  name: string;
  type: FieldType;
  required: boolean;
  description: string;
  choices?: string[];
  min?: number;
  max?: number;
  hint?: FieldHint;
}

export interface InputSpec {
  name: string;
  many: boolean;
  accepts: Socket[];
}

export interface NodeSpec {
  kind: string;
  title: string;
  output: Socket | null;
  inputs: InputSpec[];
  fields: FieldSpec[];
  requireOneOf?: string[];
  /** 처음 보는 사람용 설명 · 한 줄 예. */
  help?: string;
  example?: string;
}

export interface HelpText {
  help: string;
  example: string;
}

export interface GlossaryTerm {
  term: string;
  meaning: string;
}

export interface Catalog {
  scopeName: string;
  reportKind: string;
  sockets: Socket[];
  severities: string[];
  idPrefix: string;
  idFirst: number;
  idLast: number;
  maxNodes: number;
  head: FieldSpec[];
  nodes: NodeSpec[];
  scopeHelp?: HelpText;
  socketHelp?: Partial<Record<Socket, string>>;
  glossary?: GlossaryTerm[];
}

/** 팔레트 묶음 이름 → 소켓 설명 (필터 묶음은 따로). */
export function groupHelp(catalog: Catalog, group: string): string | undefined {
  if (group === '끝') return '규칙의 끝 — 경고를 띄운다';
  if (group === 'Sites 거르기 · 합치기') return '찾은 곳(Sites)을 받아 조건으로 거르거나 합쳐 다시 Sites로 내보낸다';
  return catalog.socketHelp?.[group as Socket];
}

/** 팔레트 마우스 올림 글 — 설명 첫 문장 + 받는 것. */
export function paletteTitle(spec: NodeSpec): string {
  const first = spec.help ? spec.help.split(/(?<=\.)\s/)[0] : '';
  const accepts = `받음: ${spec.inputs.map((i) => `${i.name}(${i.accepts.join('·')})`).join(', ')}`;
  return [first, `${spec.kind} — ${accepts}`].filter((x) => x.length > 0).join('\n');
}

export function findSpec(catalog: Catalog, kind: unknown): NodeSpec | undefined {
  return typeof kind === 'string' ? catalog.nodes.find((n) => n.kind === kind) : undefined;
}

/** 소켓 색 — 손잡이 · 선 · 팔레트 묶음. */
export const socketColor: Record<Socket | 'End', string> = {
  Scope: '#9aa4b2',
  Types: '#e0a03c',
  Methods: '#4fa3e0',
  Sites: '#5cc08a',
  Groups: '#b07de0',
  End: '#e06c6c',
};

/** 팔레트 묶음 이름 (출력 소켓, 필터는 Sites → Sites). */
export function paletteGroup(spec: NodeSpec): string {
  if (spec.output === null) return '끝';
  if (spec.kind.startsWith('filter.') || spec.kind === 'sites.union') return 'Sites 거르기 · 합치기';
  return spec.output;
}
