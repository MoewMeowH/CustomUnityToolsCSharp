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
