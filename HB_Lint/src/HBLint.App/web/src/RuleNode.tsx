import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';
import { socketColor, type NodeSpec, type Socket } from './catalog';

export interface RuleNodeData extends Record<string, unknown> {
  name: string;
  spec: NodeSpec | null;
  /** 규칙 시작 (scope) — 머리 칸. */
  scope: boolean;
  title: string;
  summary: string;
  errors: number;
  warnings: number;
  note: string;
}

export type RuleFlowNode = Node<RuleNodeData, 'rule'>;

const color = (socket: Socket | null | undefined) => (socket ? socketColor[socket] : socketColor.End);

/** 노드 카드. 입력 손잡이는 InputSpec마다 왼쪽에 (id = 입력 이름), 출력은 오른쪽 하나 (id = out). */
export function RuleNode({ data, selected }: NodeProps<RuleFlowNode>) {
  const { spec, scope } = data;
  const output: Socket | null = scope ? 'Scope' : (spec?.output ?? null);
  const inputs = spec?.inputs ?? [];
  const accent = scope ? socketColor.Scope : spec ? color(spec.output) : '#e06c6c';
  const state = data.errors > 0 ? 'error' : data.warnings > 0 ? 'warning' : '';

  return (
    <div className={`rule-node ${selected ? 'selected' : ''} ${state}`} style={{ borderTopColor: accent }}>
      <div className="rule-node-head">
        <span className="rule-node-title">{data.title}</span>
        {data.errors > 0 && <span className="badge error" title="오류">{data.errors}</span>}
        {data.warnings > 0 && <span className="badge warning" title="경고 · 이름 확인">{data.warnings}</span>}
      </div>
      <div className="rule-node-name">{scope ? '규칙 시작' : data.name}{!scope && spec ? ` · ${spec.kind}` : ''}</div>
      {data.summary && <div className="rule-node-summary" title={data.summary}>{data.summary}</div>}
      {data.note && <div className="rule-node-note" title={data.note}>📝 {data.note}</div>}

      {inputs.map((input, i) => {
        const top = inputs.length === 1 ? '50%' : `${((i + 1) / (inputs.length + 1)) * 100}%`;
        return (
          <div key={input.name}>
            <Handle
              type="target"
              id={input.name}
              position={Position.Left}
              style={{ top, background: color(input.accepts[0]), width: input.many ? 14 : 10, height: input.many ? 14 : 10 }}
              title={`${input.name}: ${input.accepts.join(' · ')}${input.many ? ' (여럿)' : ''}`}
            />
            {(inputs.length > 1 || input.many) && (
              <span className="handle-label" style={{ top }}>{input.name}{input.many ? ' ⋯' : ''}</span>
            )}
          </div>
        );
      })}
      {output && (
        <Handle type="source" id="out" position={Position.Right} style={{ background: color(output) }} title={`내보냄: ${output}`} />
      )}
    </div>
  );
}
