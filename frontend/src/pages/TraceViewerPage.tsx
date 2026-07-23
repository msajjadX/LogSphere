import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, GitBranch } from 'lucide-react';
import { api } from '../api/client';
import type { LogEventSummary, TraceResult, TraceSpan } from '../api/types';
import { useApi } from '../hooks/useApi';
import { DataTable, type Column } from '../components/DataTable';
import { SeverityBadge } from '../components/Badge';
import { ErrorBanner, LoadingBlock, EmptyState } from '../components/Feedback';
import { EventDetailDrawer } from '../components/EventDetailDrawer';
import { CopyButton } from '../components/CopyButton';
import { SEVERITY_COLORS } from '../components/SeverityChart';
import { TraceFlowAnimation } from '../components/TraceFlowAnimation';
import { formatDateTimeMs, formatDuration, truncate } from '../utils/format';

interface PositionedSpan extends TraceSpan {
  depth: number;
  offsetMs: number;
  /** true start time (epoch ms) — see startOf() */
  startMs: number;
  /** 1-based execution order across the trace, ranked by true start time */
  order: number;
}

// The SDK submits duration-carrying events AFTER the operation completes
// (stopwatch stops, then eventTimestamp is stamped at submit time), so for
// those events the timestamp is the operation's END. True start = ts - duration.
// Events without a duration are point-in-time events at their timestamp.
const startOf = (s: { start: string; durationMs?: number | null }): number =>
  new Date(s.start).getTime() - (s.durationMs ?? 0);

function layoutSpans(spans: TraceSpan[]): { rows: PositionedSpan[]; totalMs: number; traceStart: number } {
  if (spans.length === 0) return { rows: [], totalMs: 0, traceStart: 0 };
  const byId = new Map<string, TraceSpan>();
  for (const s of spans) byId.set(s.spanId, s);

  const depthCache = new Map<string, number>();
  const depthOf = (s: TraceSpan): number => {
    const cached = depthCache.get(s.spanId);
    if (cached !== undefined) return cached;
    let depth = 0;
    let cur: TraceSpan | undefined = s;
    const seen = new Set<string>();
    while (cur?.parentSpanId && byId.has(cur.parentSpanId) && !seen.has(cur.spanId)) {
      seen.add(cur.spanId);
      cur = byId.get(cur.parentSpanId);
      depth++;
      if (depth > 64) break; // cycle guard
    }
    depthCache.set(s.spanId, depth);
    return depth;
  };

  // Trace window: earliest true start → latest end. The end of every event
  // (with or without duration) is its eventTimestamp, per the SDK semantics.
  const starts = spans.map(startOf).filter((t) => !isNaN(t));
  const ends = spans.map((s) => new Date(s.start).getTime()).filter((t) => !isNaN(t));
  const traceStart = Math.min(...starts);
  const traceEnd = Math.max(...ends);
  const totalMs = Math.max(traceEnd - traceStart, 1);

  // order: children after parents, by true start time (DFS)
  const children = new Map<string | null, TraceSpan[]>();
  for (const s of spans) {
    const parent = s.parentSpanId && byId.has(s.parentSpanId) ? s.parentSpanId : null;
    const list = children.get(parent) ?? [];
    list.push(s);
    children.set(parent, list);
  }
  // Sort key = earliest start in the subtree. A parent logged as a point event
  // at request end (e.g. an Audit "PaymentProcessed") would otherwise sort
  // after its own children, breaking the top-to-bottom execution reading.
  const sortKeyCache = new Map<string, number>();
  const sortKeyOf = (s: TraceSpan, seen: Set<string> = new Set()): number => {
    const cached = sortKeyCache.get(s.spanId);
    if (cached !== undefined) return cached;
    if (seen.has(s.spanId)) return startOf(s); // cycle guard
    seen.add(s.spanId);
    let key = startOf(s);
    for (const c of children.get(s.spanId) ?? []) key = Math.min(key, sortKeyOf(c, seen));
    sortKeyCache.set(s.spanId, key);
    return key;
  };
  for (const list of children.values()) {
    list.sort((a, b) => sortKeyOf(a) - sortKeyOf(b) || startOf(a) - startOf(b));
  }
  const roots = children.get(null) ?? [];
  roots.sort((a, b) => sortKeyOf(a) - sortKeyOf(b) || startOf(a) - startOf(b));
  const rows: PositionedSpan[] = [];
  // Dedup by eventId (not spanId): distinct events sometimes share a span id,
  // and keying on spanId silently dropped those rows — so the waterfall showed
  // fewer entries than the events grid below it.
  const visit = (s: TraceSpan) => {
    if (rows.some((r) => r.eventId === s.eventId)) return;
    const startMs = startOf(s);
    rows.push({ ...s, depth: depthOf(s), startMs, offsetMs: startMs - traceStart, order: 0 });
    for (const c of children.get(s.spanId) ?? []) visit(c);
  };
  for (const root of roots) visit(root);
  // any spans missed by cycles
  for (const s of spans) visit(s);

  // Execution order = the display (tree) order. Rows are already sorted into
  // flow order (roots and children by earliest start in their subtree), and
  // ranking by raw start time instead would let millisecond jitter between a
  // parent and its first child produce badges like 2,1,3 — numbers must read
  // 1..N top-to-bottom.
  rows.forEach((r, i) => {
    r.order = i + 1;
  });

  return { rows, totalMs, traceStart };
}

export function TraceViewerPage() {
  const { traceId } = useParams<{ traceId: string }>();
  const [selectedEventId, setSelectedEventId] = useState<string | null>(null);

  const { data, loading, error, reload } = useApi<TraceResult>(
    () => api.get<TraceResult>(`/query/traces/${encodeURIComponent(traceId ?? '')}`),
    [traceId],
    Boolean(traceId),
  );

  const layout = useMemo(() => layoutSpans(data?.spans ?? []), [data]);

  // Same completion-timestamp correction as layoutSpans, applied to the grid.
  // Rows follow the waterfall's flow order (so step numbers match between the
  // two sections); events without a waterfall row fall back to true start time.
  type OrderedEvent = LogEventSummary & { startMs: number; order: number };
  const orderedEvents = useMemo<OrderedEvent[]>(() => {
    const spanOrder = new Map(layout.rows.map((r) => [r.eventId, r.order]));
    const evs = (data?.events ?? []).map((e) => ({
      ...e,
      startMs: new Date(e.eventTimestamp).getTime() - (e.durationMs ?? 0),
      order: 0,
    }));
    evs.sort(
      (a, b) =>
        (spanOrder.get(a.eventId) ?? Infinity) - (spanOrder.get(b.eventId) ?? Infinity) ||
        a.startMs - b.startMs ||
        new Date(a.eventTimestamp).getTime() - new Date(b.eventTimestamp).getTime(),
    );
    return evs.map((e, i) => ({ ...e, order: i + 1 }));
  }, [data, layout]);

  // Total for the events grid: earliest start → latest end (what the trace
  // actually took). Deliberately NOT the sum of step durations — steps nest
  // (a parent like ProcessTransaction contains its children), so a plain sum
  // double-counts the same seconds and reads as a wrong total.
  const eventTotals = useMemo(() => {
    const evs = orderedEvents;
    if (evs.length === 0) return null;
    const starts = evs.map((e) => e.startMs).filter((n) => !isNaN(n));
    const ends = evs.map((e) => new Date(e.eventTimestamp).getTime()).filter((n) => !isNaN(n));
    if (starts.length === 0 || ends.length === 0) return { wallClock: null as number | null };
    return { wallClock: Math.max(Math.max(...ends) - Math.min(...starts), 0) };
  }, [orderedEvents]);

  const eventColumns: Column<OrderedEvent>[] = [
    {
      key: 'order',
      header: '#',
      render: (e) => <span className="text-xs tabular-nums text-gray-400">{e.order}</span>,
    },
    {
      key: 'started',
      header: 'Started',
      render: (e) => (
        <span
          className="whitespace-nowrap font-mono text-xs tabular-nums"
          title={`logged at ${formatDateTimeMs(e.eventTimestamp)} (event is written when the step completes)`}
        >
          {isNaN(e.startMs) ? formatDateTimeMs(e.eventTimestamp) : formatDateTimeMs(new Date(e.startMs).toISOString())}
        </span>
      ),
    },
    {
      key: 'ended',
      header: 'Ended',
      render: (e) => (
        <span className="whitespace-nowrap font-mono text-xs tabular-nums text-gray-500">
          {e.durationMs != null ? formatDateTimeMs(e.eventTimestamp) : '—'}
        </span>
      ),
    },
    { key: 'severity', header: 'Severity', render: (e) => <SeverityBadge severity={e.severity} /> },
    { key: 'type', header: 'Type', render: (e) => <span className="text-xs">{e.eventType}</span> },
    { key: 'module', header: 'Module', render: (e) => <span className="text-xs">{e.module ?? '—'}</span> },
    {
      key: 'message',
      header: 'Message',
      render: (e) => <span className="block max-w-xl truncate text-xs">{truncate(e.message, 160) || '—'}</span>,
      className: 'w-full',
    },
    { key: 'duration', header: 'Duration', render: (e) => <span className="text-xs tabular-nums">{formatDuration(e.durationMs)}</span> },
  ];

  return (
    <div className="space-y-4">
      <div>
        <Link
          to="/traces"
          className="inline-flex items-center gap-1 text-xs text-indigo-600 hover:underline dark:text-indigo-400"
        >
          <ArrowLeft className="h-3 w-3" /> Back to traces
        </Link>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <GitBranch className="h-4 w-4 text-gray-400" />
        <span className="font-mono text-sm">{traceId}</span>
        {traceId && <CopyButton text={traceId} label="Copy trace ID" />}
        {layout.rows.length > 0 && (
          <span className="text-xs text-gray-500">
            {layout.rows.length} spans · total {formatDuration(layout.totalMs)} · started{' '}
            <span className="font-mono tabular-nums">{formatDateTimeMs(new Date(layout.traceStart).toISOString())}</span>
          </span>
        )}
      </div>

      {loading && <LoadingBlock label="Loading trace…" />}
      {error && <ErrorBanner error={error} onRetry={reload} />}

      {!loading && !error && (
        <section className="card p-4">
          <h2 className="mb-3 text-sm font-semibold">Span waterfall</h2>
          {layout.rows.length === 0 ? (
            <EmptyState message="No spans found for this trace" />
          ) : (
            <div className="space-y-1 overflow-x-auto">
              <div className="grid w-full min-w-[720px] grid-cols-[minmax(220px,30%)_1fr_170px] items-center gap-2 px-1 pb-1 text-[10px] font-medium uppercase tracking-wider text-gray-400">
                <span>Step</span>
                <span>Timeline</span>
                <span className="text-right" title="How long after the trace began this step started, and how long it ran">
                  started after · took
                </span>
              </div>
              {layout.rows.map((s) => {
                const left = (s.offsetMs / layout.totalMs) * 100;
                const width = Math.max(((s.durationMs ?? 0) / layout.totalMs) * 100, 0.6);
                const color = SEVERITY_COLORS[s.severity ?? 'Information'] ?? '#3b82f6';
                return (
                  <button
                    key={s.eventId}
                    type="button"
                    className="group grid w-full min-w-[720px] grid-cols-[minmax(220px,30%)_1fr_170px] items-center gap-2 rounded px-1 py-0.5 text-left hover:bg-gray-50 dark:hover:bg-gray-800/50"
                    onClick={() => setSelectedEventId(s.eventId)}
                    title={`#${s.order} ${s.name ?? s.spanId} — started ${formatDateTimeMs(new Date(s.startMs).toISOString())}${
                      s.durationMs != null ? ` · took ${formatDuration(s.durationMs)} · ended ${formatDateTimeMs(s.start)}` : ''
                    }`}
                  >
                    <span
                      className="flex min-w-0 items-center text-xs text-gray-700 dark:text-gray-300"
                      style={{ paddingLeft: `${Math.min(s.depth, 12) * 14}px` }}
                    >
                      <span className="mr-1.5 inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-gray-200 text-[10px] font-semibold tabular-nums text-gray-600 dark:bg-gray-700 dark:text-gray-300">
                        {s.order}
                      </span>
                      <span className="mr-1 text-gray-400">{s.depth > 0 ? '└' : ''}</span>
                      <span className="truncate">{s.name ?? s.eventType ?? s.spanId}</span>
                    </span>
                    <span className="relative block h-4 rounded bg-gray-100 dark:bg-gray-800">
                      <span
                        className="absolute top-0 block h-4 rounded transition-opacity group-hover:opacity-80"
                        style={{ left: `${left}%`, width: `${width}%`, backgroundColor: color }}
                      />
                    </span>
                    <span className="text-right text-xs tabular-nums text-gray-500">
                      <span className="text-gray-400">{s.offsetMs < 1 ? 'at start' : `+${formatDuration(s.offsetMs)}`}</span>
                      <span className="mx-1 text-gray-300 dark:text-gray-600">·</span>
                      {formatDuration(s.durationMs)}
                    </span>
                  </button>
                );
              })}
            </div>
          )}
        </section>
      )}

      {!loading && !error && (
        <section className="card">
          <h2 className="border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
            Other events in this trace
          </h2>
          <DataTable
            columns={eventColumns}
            rows={orderedEvents}
            rowKey={(e) => e.eventId}
            onRowClick={(e) => setSelectedEventId(e.eventId)}
            emptyMessage="No non-span events in this trace"
          />
          {eventTotals && (
            <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 border-t border-gray-200 px-4 py-2 text-xs text-gray-500 dark:border-gray-800">
              <span>{orderedEvents.length} steps</span>
              {eventTotals.wallClock != null && (
                <span
                  className="tabular-nums"
                  title="Earliest step start → latest step end. Not a sum of the Duration column: steps nest inside each other (e.g. ProcessTransaction contains the inquiry, payment and DB steps), so summing would count the same time twice."
                >
                  total <span className="font-semibold text-gray-700 dark:text-gray-300">{formatDuration(eventTotals.wallClock)}</span>
                </span>
              )}
            </div>
          )}
        </section>
      )}

      {!loading && !error && layout.rows.length > 0 && (
        <section className="card overflow-hidden p-4">
          <h2 className="mb-1 text-sm font-semibold">Trace flow</h2>
          <p className="mb-2 text-xs text-gray-500">A live view of every event streaming through this trace, placed at its real time offset.</p>
          <TraceFlowAnimation
            events={layout.rows.map((s) => ({
              eventId: s.eventId,
              name: s.name ?? s.eventType ?? s.spanId,
              offsetMs: s.offsetMs,
              durationMs: s.durationMs,
              severity: s.severity,
              seq: s.order,
            }))}
            totalMs={layout.totalMs}
          />
        </section>
      )}

      <EventDetailDrawer eventId={selectedEventId} onClose={() => setSelectedEventId(null)} />
    </div>
  );
}
