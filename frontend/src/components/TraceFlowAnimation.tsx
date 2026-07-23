import { useMemo } from 'react';
import type { Severity } from '../api/types';
import { SEVERITY_COLORS } from './SeverityChart';
import { formatDuration } from '../utils/format';

export interface TraceFlowEvent {
  eventId: string;
  name: string;
  offsetMs: number;
  durationMs?: number | null;
  severity?: Severity | null;
  /** 1-based execution order, shown above the node */
  seq?: number;
}

interface TraceFlowAnimationProps {
  events: TraceFlowEvent[];
  totalMs: number;
}

// SVG canvas geometry (viewBox units — the element scales responsively).
const W = 1000;
const H = 210;
const PAD_X = 48;
const TRACK_Y = 118;
const TRACK_W = W - PAD_X * 2;
/** One full replay of the trace, in seconds. */
const SWEEP_S = 8;

/**
 * Animated replay of the trace: a glowing time cursor sweeps the timeline and
 * each step "fires" (bursts + lights up) at its real start offset, in execution
 * order — arcs flow from step 1 → 2 → 3 so the sequence is readable even as a
 * still. Pure inline SVG + CSS/SMIL, no deps, loops forever, and honors
 * prefers-reduced-motion.
 */
export function TraceFlowAnimation({ events, totalMs }: TraceFlowAnimationProps) {
  const nodes = useMemo(() => {
    const total = Math.max(totalMs, 1);
    return events
      .map((e) => {
        const frac = Math.min(Math.max(e.offsetMs, 0), total) / total;
        const x = PAD_X + frac * TRACK_W;
        const barW = Math.max((Math.min(e.durationMs ?? 0, total) / total) * TRACK_W, 0);
        const color = SEVERITY_COLORS[e.severity ?? 'Information'] ?? '#3b82f6';
        // fire exactly when the sweeping cursor reaches this node. Clamp just
        // below one full period: a step at exactly totalMs would get delay ≡ 0
        // (mod period) and wrongly fire while the cursor is still at the start.
        const fireDelay = (Math.min(frac, 0.99) * SWEEP_S).toFixed(3);
        return { ...e, x, barW, color, fireDelay };
      })
      .sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));
  }, [events, totalMs]);

  // Arcs linking consecutive steps in execution order (1→2, 2→3, …).
  const arcs = useMemo(() => {
    const out: { from: (typeof nodes)[number]; to: (typeof nodes)[number]; d: string; key: string }[] = [];
    for (let i = 0; i + 1 < nodes.length; i++) {
      const a = nodes[i];
      const b = nodes[i + 1];
      if (Math.abs(b.x - a.x) < 4) continue; // simultaneous steps — no room for an arc
      const rise = Math.min(18 + Math.abs(b.x - a.x) / 7, 62);
      const d = `M ${a.x} ${TRACK_Y - 10} Q ${(a.x + b.x) / 2} ${TRACK_Y - 10 - rise} ${b.x} ${TRACK_Y - 10}`;
      out.push({ from: a, to: b, d, key: `${a.eventId}-${b.eventId}` });
    }
    return out;
  }, [nodes]);

  if (nodes.length === 0) return null;

  const replaySpeed = totalMs / (SWEEP_S * 1000);

  return (
    <svg
      viewBox={`0 0 ${W} ${H}`}
      className="trace-flow block w-full"
      role="img"
      aria-label="Animated replay of this trace: each step fires at its real time offset, in execution order"
      preserveAspectRatio="xMidYMid meet"
      style={{ ['--sweep-w' as string]: `${TRACK_W}px` }}
    >
      <style>{`
        @keyframes tf-sweep { from { transform: translateX(0) } to { transform: translateX(var(--sweep-w)) } }
        /* nodes idle dim, flare up the instant the cursor passes them */
        @keyframes tf-fire {
          0%      { opacity: .4; transform: scale(1) }
          2%      { opacity: 1;  transform: scale(1.9) }
          14%     { opacity: .95; transform: scale(1.15) }
          70%     { opacity: .8; transform: scale(1) }
          100%    { opacity: .4; transform: scale(1) }
        }
        @keyframes tf-burst {
          0%      { r: 6; opacity: .8 }
          12%     { r: 26; opacity: 0 }
          100%    { r: 26; opacity: 0 }
        }
        @keyframes tf-badge-fire {
          0%      { opacity: .45 }
          2%      { opacity: 1 }
          60%     { opacity: .9 }
          100%    { opacity: .45 }
        }
        @keyframes tf-dash  { to { stroke-dashoffset: -40 } }
        .tf-sweep { animation: tf-sweep ${SWEEP_S}s linear infinite; }
        .tf-fire  { animation: tf-fire ${SWEEP_S}s linear infinite; transform-box: fill-box; transform-origin: center; }
        .tf-burst { animation: tf-burst ${SWEEP_S}s linear infinite; }
        .tf-badge { animation: tf-badge-fire ${SWEEP_S}s linear infinite; }
        .tf-arc   { stroke-dasharray: 5 7; animation: tf-dash 1.4s linear infinite; }
        .tf-track { stroke-dasharray: 6 10; animation: tf-dash 1.8s linear infinite; }
        @media (prefers-reduced-motion: reduce) {
          .tf-sweep, .tf-fire, .tf-burst, .tf-badge, .tf-arc, .tf-track { animation: none; }
          .tf-sweep { display: none; }
          .tf-burst { display: none; }
          .tf-fire, .tf-badge { opacity: 1; }
        }
      `}</style>

      <defs>
        <linearGradient id="tf-track-grad" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stopColor="#6366f1" />
          <stop offset="50%" stopColor="#3b82f6" />
          <stop offset="100%" stopColor="#22d3ee" />
        </linearGradient>
        <linearGradient id="tf-cursor-grad" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#22d3ee" stopOpacity="0" />
          <stop offset="55%" stopColor="#22d3ee" stopOpacity="0.9" />
          <stop offset="100%" stopColor="#22d3ee" stopOpacity="0" />
        </linearGradient>
        <filter id="tf-glow" x="-80%" y="-80%" width="260%" height="260%">
          <feGaussianBlur stdDeviation="2.6" result="b" />
          <feMerge>
            <feMergeNode in="b" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
        <marker id="tf-arrow" viewBox="0 0 8 8" refX="6" refY="4" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M 0 0 L 8 4 L 0 8 z" fill="#3b82f6" fillOpacity="0.7" />
        </marker>
      </defs>

      {/* base rail + flowing rail */}
      <line x1={PAD_X} y1={TRACK_Y} x2={W - PAD_X} y2={TRACK_Y} stroke="currentColor" strokeOpacity="0.12" strokeWidth="3" strokeLinecap="round" className="text-gray-500" />
      <line x1={PAD_X} y1={TRACK_Y} x2={W - PAD_X} y2={TRACK_Y} stroke="url(#tf-track-grad)" strokeWidth="2" strokeLinecap="round" className="tf-track" />

      {/* execution-order arcs: step N flows into step N+1 */}
      {arcs.map((a) => (
        <path
          key={a.key}
          d={a.d}
          fill="none"
          stroke="url(#tf-track-grad)"
          strokeWidth="1.6"
          strokeOpacity="0.55"
          className="tf-arc"
          markerEnd="url(#tf-arrow)"
        />
      ))}

      {/* duration bars under each span */}
      {nodes.map((n) =>
        n.barW > 1 ? (
          <rect key={`bar-${n.eventId}`} x={n.x} y={TRACK_Y + 9} width={n.barW} height="5" rx="2.5" fill={n.color} fillOpacity="0.35">
            <title>{`${n.name} · ${formatDuration(n.durationMs)}`}</title>
          </rect>
        ) : null,
      )}

      {/* event nodes — idle dim, fire when the cursor arrives */}
      {nodes.map((n, i) => {
        const badgeY = TRACK_Y + 34 + (i % 2) * 18;
        return (
          <g key={n.eventId}>
            <circle cx={n.x} cy={TRACK_Y} r="6" fill={n.color} className="tf-burst" style={{ animationDelay: `${n.fireDelay}s` }} />
            <g className="tf-fire" style={{ animationDelay: `${n.fireDelay}s` }}>
              <circle cx={n.x} cy={TRACK_Y} r="5.5" fill={n.color} filter="url(#tf-glow)">
                <title>{`${n.seq != null ? `#${n.seq} ` : ''}${n.name} · starts +${formatDuration(n.offsetMs)}${
                  n.durationMs != null ? ` · takes ${formatDuration(n.durationMs)}` : ''
                }`}</title>
              </circle>
            </g>
            {/* sequence badge below the rail */}
            {n.seq != null && (
              <g className="tf-badge" style={{ animationDelay: `${n.fireDelay}s` }}>
                <line x1={n.x} y1={TRACK_Y + 8} x2={n.x} y2={badgeY - 9} stroke={n.color} strokeWidth="1.2" strokeOpacity="0.45" />
                <circle cx={n.x} cy={badgeY} r="9" fill={n.color} fillOpacity="0.16" stroke={n.color} strokeOpacity="0.55" strokeWidth="1" />
                <text x={n.x} y={badgeY + 3.5} fill={n.color} fontSize="10" fontWeight="600" textAnchor="middle">
                  {n.seq}
                </text>
              </g>
            )}
          </g>
        );
      })}

      {/* the sweeping time cursor */}
      <g className="tf-sweep">
        <line x1={PAD_X} y1={44} x2={PAD_X} y2={TRACK_Y + 18} stroke="url(#tf-cursor-grad)" strokeWidth="2" />
        <circle cx={PAD_X} cy={TRACK_Y} r="3.5" fill="#e0f2fe" filter="url(#tf-glow)" />
      </g>

      {/* captions */}
      <text x={W / 2} y={24} className="fill-gray-400" fontSize="12" textAnchor="middle" letterSpacing="1.5">
        {nodes.length} STEPS · {formatDuration(totalMs)} · REPLAYED {replaySpeed >= 1 ? `AT ${replaySpeed.toFixed(1)}× SPEED` : 'IN SLOW MOTION'}
      </text>
      <text x={PAD_X} y={H - 10} className="fill-gray-400" fontSize="12" textAnchor="start">
        0 ms
      </text>
      <text x={W - PAD_X} y={H - 10} className="fill-gray-400" fontSize="12" textAnchor="end">
        {formatDuration(totalMs)}
      </text>
    </svg>
  );
}
