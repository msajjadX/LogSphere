import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Bookmark, Download, ListTree, Pause, Play, Radio, RotateCcw, Search } from 'lucide-react';
import { api, downloadExport, toApiError, type ApiError } from '../api/client';
import type { LogEventSummary, LogFilter, LogQueryResult, SavedSearch } from '../api/types';
import { EVENT_TYPES, SEVERITIES } from '../api/types';
import { resolveRange, useTimeRange } from '../context/TimeRangeContext';
import { useLookups } from '../context/LookupsContext';
import { useAuth } from '../context/AuthContext';
import { DataTable, type Column } from '../components/DataTable';
import { SeverityBadge, severityAccent } from '../components/Badge';
import { MultiSelect, Select, TextField, CheckboxField } from '../components/Select';
import { ErrorBanner, Spinner } from '../components/Feedback';
import { EventDetailDrawer } from '../components/EventDetailDrawer';
import { Modal } from '../components/Modal';
import { formatDateTime, formatDuration, relativeTime, truncate } from '../utils/format';

const PAGE_SIZE = 100;

/** The one-at-a-time identifier filters, collapsed into a single "Search by" combo + input. */
const ID_FIELDS = [
  { key: 'correlationId', label: 'Correlation ID', mono: true, placeholder: 'all events of one request' },
  { key: 'eventId', label: 'Event ID', mono: true, placeholder: 'full GUID' },
  { key: 'traceId', label: 'Trace ID', mono: true, placeholder: 'W3C trace id' },
  { key: 'userId', label: 'User ID', mono: false, placeholder: 'who did it' },
  { key: 'businessEntityType', label: 'Entity type', mono: false, placeholder: 'e.g. Payment' },
  { key: 'businessEntityId', label: 'Entity ID', mono: false, placeholder: 'e.g. PAY-991' },
  { key: 'fingerprint', label: 'Exception fingerprint', mono: true, placeholder: 'from an exception group' },
] as const;
type IdFieldKey = (typeof ID_FIELDS)[number]['key'];
const TAIL_INTERVAL_MS = 3000;

function emptyFilter(): LogFilter {
  return {
    projectId: null,
    applicationId: null,
    environmentId: null,
    projectIds: [],
    applicationIds: [],
    environmentIds: [],
    eventTypes: [],
    severities: [],
    eventId: '',
    correlationId: '',
    traceId: '',
    userId: '',
    businessEntityType: '',
    businessEntityId: '',
    httpRoute: '',
    httpStatusCode: null,
    text: '',
    module: '',
    actionName: '',
  };
}

function filterFromParams(params: URLSearchParams): LogFilter {
  const f = emptyFilter();
  const q = params.get('q');
  if (q) {
    try {
      const parsed = JSON.parse(q) as LogFilter;
      return { ...f, ...parsed };
    } catch {
      /* ignore malformed */
    }
  }
  const keys: (keyof LogFilter)[] = [
    'eventId',
    'correlationId',
    'traceId',
    'userId',
    'businessEntityType',
    'businessEntityId',
    'httpRoute',
    'text',
    'fingerprint',
    'exceptionType',
  ];
  for (const k of keys) {
    const v = params.get(k as string);
    if (v) (f as Record<string, unknown>)[k as string] = v;
  }
  const projectId = params.get('projectId');
  if (projectId) f.projectIds = [projectId];
  // comma-separated list params (e.g. /logs?severities=Error,Critical from the sidebar)
  const severities = params.get('severities');
  if (severities) f.severities = severities.split(',').filter(Boolean);
  const eventTypes = params.get('eventTypes');
  if (eventTypes) f.eventTypes = eventTypes.split(',').filter(Boolean);
  return f;
}

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Picks the "main" event to show for a correlation group: the entry point of the
 * request — the root span if there is one, else the earliest by sequence, else
 * the earliest timestamp.
 */
function pickTraceLeader(members: LogEventSummary[]): LogEventSummary {
  return members.reduce((best, e) => {
    const eIsRoot = !e.parentSpanId;
    const bestIsRoot = !best.parentSpanId;
    if (eIsRoot !== bestIsRoot) return eIsRoot ? e : best;
    if (e.sequence != null && best.sequence != null && e.sequence !== best.sequence)
      return e.sequence < best.sequence ? e : best;
    return e.eventTimestamp < best.eventTimestamp ? e : best;
  });
}

/** Removes empty strings / empty arrays / nulls so the API receives a clean filter. */
function cleanFilter(f: LogFilter): LogFilter {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(f)) {
    if (v === null || v === undefined || v === '') continue;
    if (Array.isArray(v) && v.length === 0) continue;
    // eventId is a strict GUID server-side — only send it once it parses
    if (k === 'eventId' && !(typeof v === 'string' && GUID_RE.test(v.trim()))) continue;
    out[k] = typeof v === 'string' && k === 'eventId' ? v.trim() : v;
  }
  return out as LogFilter;
}

export function LogsPage() {
  const [searchParams] = useSearchParams();
  const { range } = useTimeRange();
  const { projects, applications, environments } = useLookups();
  const { canExport } = useAuth();

  const [filter, setFilter] = useState<LogFilter>(() => filterFromParams(searchParams));
  const [applied, setApplied] = useState<LogFilter>(filter);
  const [idField, setIdField] = useState<IdFieldKey>(() => {
    const f = filterFromParams(searchParams);
    return (ID_FIELDS.find((d) => f[d.key])?.key ?? 'correlationId') as IdFieldKey;
  });

  // when the filter is replaced externally (saved search, correlation pivot, URL),
  // point the "Search by" combo at whichever identifier carries a value
  useEffect(() => {
    if (filter[idField]) return;
    const active = ID_FIELDS.find((d) => filter[d.key]);
    if (active) setIdField(active.key as IdFieldKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter]);
  const [items, setItems] = useState<LogEventSummary[]>([]);
  const [nextCursor, setNextCursor] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [selectedEventId, setSelectedEventId] = useState<string | null>(null);

  // group rows sharing a correlation id under one expandable "main" row (default on)
  const [groupByCorrelation, setGroupByCorrelation] = useState(true);

  // live tail
  const [tailOn, setTailOn] = useState(false);
  const [tailPaused, setTailPaused] = useState(false);
  const maxIdRef = useRef<number | null>(null);
  const tailBusyRef = useRef(false);

  // save-search modal
  const [saveOpen, setSaveOpen] = useState(false);
  const [saveName, setSaveName] = useState('');
  const [saveShared, setSaveShared] = useState(false);
  const [savePinned, setSavePinned] = useState(false);
  const [saveBusy, setSaveBusy] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);

  const timeWindow = useMemo(() => resolveRange(range), [range]);

  const queryBody = useCallback(
    (f: LogFilter): LogFilter => ({
      ...cleanFilter(f),
      from: timeWindow.from,
      to: timeWindow.to,
      limit: PAGE_SIZE,
      order: 'desc',
    }),
    [timeWindow],
  );

  const runSearch = useCallback(
    async (f: LogFilter) => {
      setLoading(true);
      setError(null);
      try {
        const res = await api.post<LogQueryResult>('/query/logs', queryBody(f));
        const list = res.items ?? [];
        setItems(list);
        setNextCursor(res.nextCursor ?? null);
        maxIdRef.current = list.reduce<number | null>((m, e) => (m === null || e.id > m ? e.id : m), null);
      } catch (e) {
        setError(toApiError(e));
      } finally {
        setLoading(false);
      }
    },
    [queryBody],
  );

  // run on mount, when applied filter or global time range changes
  useEffect(() => {
    void runSearch(applied);
  }, [applied, runSearch]);

  // re-run the current search when the active nav item is re-clicked (see AppShell)
  useEffect(() => {
    const onRefresh = () => void runSearch(applied);
    window.addEventListener('logsphere:refresh', onRefresh);
    return () => window.removeEventListener('logsphere:refresh', onRefresh);
  }, [runSearch, applied]);

  // Refresh when the user clicks the "Log Explorer" nav item while already on this
  // page (a same-route click doesn't remount, so nothing would refetch otherwise).
  useEffect(() => {
    const onRefresh = () => void runSearch(applied);
    window.addEventListener('logsphere:refresh', onRefresh);
    return () => window.removeEventListener('logsphere:refresh', onRefresh);
  }, [applied, runSearch]);

  // re-initialize when navigated with new params (saved search run, correlation link)
  const paramsKey = searchParams.toString();
  const firstParams = useRef(true);
  useEffect(() => {
    if (firstParams.current) {
      firstParams.current = false;
      return;
    }
    const f = filterFromParams(searchParams);
    setTailOn(false);
    setFilter(f);
    setApplied(f);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paramsKey]);

  // auto-disable live tail when the draft filter changes
  const draftKey = JSON.stringify(filter);
  const prevDraftKey = useRef(draftKey);
  useEffect(() => {
    if (prevDraftKey.current !== draftKey) {
      prevDraftKey.current = draftKey;
      if (tailOn) setTailOn(false);
    }
  }, [draftKey, tailOn]);

  // live tail polling
  useEffect(() => {
    if (!tailOn || tailPaused) return;
    const id = window.setInterval(async () => {
      if (tailBusyRef.current) return;
      tailBusyRef.current = true;
      try {
        const body: LogFilter = {
          ...cleanFilter(applied),
          from: timeWindow.from,
          to: new Date().toISOString(),
          afterId: maxIdRef.current,
          limit: PAGE_SIZE,
          order: 'desc',
        };
        const res = await api.post<LogQueryResult | LogEventSummary[]>('/query/logs/tail', body);
        const fresh = Array.isArray(res) ? res : (res.items ?? []);
        if (fresh.length > 0) {
          setItems((prev) => {
            const known = new Set(prev.map((e) => e.id));
            const add = fresh.filter((e) => !known.has(e.id)).sort((a, b) => b.id - a.id);
            return add.length ? [...add, ...prev] : prev;
          });
          maxIdRef.current = fresh.reduce<number | null>(
            (m, e) => (m === null || e.id > m ? e.id : m),
            maxIdRef.current,
          );
        }
      } catch {
        /* transient tail errors are silent; next poll retries */
      } finally {
        tailBusyRef.current = false;
      }
    }, TAIL_INTERVAL_MS);
    return () => window.clearInterval(id);
  }, [tailOn, tailPaused, applied, timeWindow]);

  const apply = () => {
    setTailOn(false);
    setApplied(filter);
    prevDraftKey.current = JSON.stringify(filter);
  };

  const reset = () => {
    const f = emptyFilter();
    setTailOn(false);
    setFilter(f);
    setApplied(f);
    setIdField('correlationId');
    prevDraftKey.current = JSON.stringify(f);
  };

  const applyQuickFilter = useCallback((partial: Partial<LogFilter>) => {
    const f = { ...emptyFilter(), ...partial };
    setTailOn(false);
    setFilter(f);
    setApplied(f);
    prevDraftKey.current = JSON.stringify(f);
  }, []);

  /** Column-header (Excel-style) filters merge into the current filter and apply immediately. */
  const applyPartial = useCallback((partial: Partial<LogFilter>) => {
    setTailOn(false);
    setFilter((prev) => {
      const f = { ...prev, ...partial };
      setApplied(f);
      prevDraftKey.current = JSON.stringify(f);
      return f;
    });
  }, []);

  const loadMore = async () => {
    if (nextCursor === null) return;
    setLoadingMore(true);
    try {
      const res = await api.post<LogQueryResult>('/query/logs', { ...queryBody(applied), afterId: nextCursor });
      setItems((prev) => {
        const known = new Set(prev.map((e) => e.id));
        return [...prev, ...(res.items ?? []).filter((e) => !known.has(e.id))];
      });
      setNextCursor(res.nextCursor ?? null);
    } catch (e) {
      setError(toApiError(e));
    } finally {
      setLoadingMore(false);
    }
  };

  const saveSearch = async () => {
    setSaveBusy(true);
    setSaveError(null);
    try {
      await api.post<SavedSearch>('/searches', {
        name: saveName,
        filter: cleanFilter(applied),
        shared: saveShared,
        pinned: savePinned,
      });
      setSaveOpen(false);
      setSaveName('');
      setSaveShared(false);
      setSavePinned(false);
    } catch (e) {
      setSaveError(toApiError(e).message);
    } finally {
      setSaveBusy(false);
    }
  };

  const set = <K extends keyof LogFilter>(key: K, value: LogFilter[K]) => setFilter((f) => ({ ...f, [key]: value }));

  // Canonical explorer column order: Time → Project → Application → Environment →
  // record identity → Message → metrics. Excel-style filters live on the headers.
  const columns: Column<LogEventSummary>[] = [
    {
      key: 'time',
      header: 'Time',
      render: (e) => (
        <span className="whitespace-nowrap text-xs" title={relativeTime(e.eventTimestamp)}>
          {formatDateTime(e.eventTimestamp)}
        </span>
      ),
    },
    {
      key: 'project',
      header: 'Project',
      render: (e) => <span className="whitespace-nowrap text-xs">{e.projectName ?? e.projectId ?? '—'}</span>,
      filter: {
        kind: 'multi',
        options: projects.map((p) => ({ value: String(p.id), label: p.name })),
        values: (filter.projectIds ?? []).map(String),
        onApply: (v) => applyPartial({ projectIds: v }),
      },
    },
    {
      key: 'application',
      header: 'Application',
      render: (e) => <span className="whitespace-nowrap text-xs">{e.applicationName ?? '—'}</span>,
      filter: {
        kind: 'multi',
        options: applications
          .filter((a) => (filter.projectIds ?? []).length === 0 || (filter.projectIds ?? []).map(String).includes(String(a.projectId)))
          .map((a) => ({ value: String(a.id), label: a.name })),
        values: (filter.applicationIds ?? []).map(String),
        onApply: (v) => applyPartial({ applicationIds: v }),
      },
    },
    {
      key: 'severity',
      header: 'Severity',
      render: (e) => <SeverityBadge severity={e.severity} />,
      filter: {
        kind: 'multi',
        options: SEVERITIES.map((s) => ({ value: s, label: s })),
        values: filter.severities ?? [],
        onApply: (v) => applyPartial({ severities: v }),
      },
    },
    {
      key: 'type',
      header: 'Type',
      render: (e) => <span className="whitespace-nowrap text-xs">{e.eventType}</span>,
      filter: {
        kind: 'multi',
        options: EVENT_TYPES.map((t) => ({ value: t, label: t })),
        values: filter.eventTypes ?? [],
        onApply: (v) => applyPartial({ eventTypes: v }),
      },
    },
    {
      key: 'module',
      header: 'Module',
      render: (e) => <span className="text-xs">{e.module ?? '—'}</span>,
      filter: {
        kind: 'text',
        value: filter.module ?? '',
        onApply: (v) => applyPartial({ module: v }),
        placeholder: 'Contains…',
        suggestions: [...new Set(items.map((e) => e.module).filter((m): m is string => !!m))].sort(),
      },
    },
    {
      key: 'message',
      header: 'Message',
      render: (e) => <span className="block max-w-xl truncate text-xs">{truncate(e.message, 160) || '—'}</span>,
      className: 'w-full',
      filter: {
        kind: 'text',
        value: filter.text ?? '',
        onApply: (v) => applyPartial({ text: v }),
        placeholder: 'Contains…',
      },
    },
    {
      key: 'duration',
      header: 'Duration',
      render: (e) => <span className="whitespace-nowrap text-xs tabular-nums">{formatDuration(e.durationMs)}</span>,
    },
    {
      key: 'status',
      header: 'HTTP',
      render: (e) =>
        e.httpStatusCode != null ? (
          <span
            className={`text-xs font-medium tabular-nums ${
              e.httpStatusCode >= 500
                ? 'text-red-600 dark:text-red-400'
                : e.httpStatusCode >= 400
                  ? 'text-amber-600 dark:text-amber-400'
                  : 'text-gray-600 dark:text-gray-300'
            }`}
          >
            {e.httpStatusCode}
          </span>
        ) : (
          <span className="text-xs text-gray-400">—</span>
        ),
      filter: {
        kind: 'text',
        value: filter.httpStatusCode != null ? String(filter.httpStatusCode) : '',
        onApply: (v) => applyPartial({ httpStatusCode: v === '' ? null : Number(v) }),
        placeholder: 'e.g. 500',
      },
    },
  ];

  return (
    <div className="space-y-3">
      {/* filter bar */}
      <section className="card p-3">
        <form
          onSubmit={(e) => {
            e.preventDefault();
            apply();
          }}
        >
        <div className="grid grid-cols-2 gap-2.5 md:grid-cols-3 xl:grid-cols-5">
          <MultiSelect
            label="Project"
            values={(filter.projectIds ?? []).map(String)}
            placeholder="All projects"
            options={projects.map((p) => ({ value: String(p.id), label: p.name }))}
            onChange={(v) => set('projectIds', v)}
          />
          <MultiSelect
            label="Application"
            values={(filter.applicationIds ?? []).map(String)}
            placeholder="All applications"
            options={applications
              .filter(
                (a) =>
                  (filter.projectIds ?? []).length === 0 ||
                  (filter.projectIds ?? []).map(String).includes(String(a.projectId)),
              )
              .map((a) => ({ value: String(a.id), label: a.name }))}
            onChange={(v) => set('applicationIds', v)}
          />
          <MultiSelect
            label="Environment"
            values={(filter.environmentIds ?? []).map(String)}
            placeholder="All environments"
            options={environments.map((e) => ({ value: String(e.id), label: e.name }))}
            onChange={(v) => set('environmentIds', v.map(Number))}
          />
          <MultiSelect
            label="Event types"
            values={filter.eventTypes ?? []}
            options={EVENT_TYPES.map((t) => ({ value: t, label: t }))}
            onChange={(v) => set('eventTypes', v)}
          />
          <MultiSelect
            label="Severities"
            values={filter.severities ?? []}
            options={SEVERITIES.map((s) => ({ value: s, label: s }))}
            onChange={(v) => set('severities', v)}
          />
          <Select
            label="Search by"
            value={idField}
            options={ID_FIELDS.map((d) => ({ value: d.key, label: d.label }))}
            onChange={(v) => {
              const next = (v || 'correlationId') as IdFieldKey;
              // switching the identifier type clears the previous one
              setFilter((f) => {
                const cleared = { ...f };
                for (const d of ID_FIELDS) (cleared as Record<string, unknown>)[d.key] = '';
                return cleared;
              });
              setIdField(next);
            }}
          />
          {(() => {
            const def = ID_FIELDS.find((d) => d.key === idField)!;
            return (
              <TextField
                label={def.label}
                value={(filter[def.key] as string | null | undefined) ?? ''}
                onChange={(v) => set(def.key, v)}
                mono={def.mono}
                placeholder={def.placeholder}
              />
            );
          })()}
          <TextField label="HTTP route" value={filter.httpRoute ?? ''} onChange={(v) => set('httpRoute', v)} mono />
          <TextField
            label="HTTP status"
            type="number"
            value={filter.httpStatusCode != null ? String(filter.httpStatusCode) : ''}
            onChange={(v) => set('httpStatusCode', v === '' ? null : Number(v))}
          />
          <TextField
            label="Free text"
            value={filter.text ?? ''}
            onChange={(v) => set('text', v)}
            placeholder="Search messages…"
          />
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <button type="submit" className="btn-primary" disabled={loading}>
            <Search className="h-4 w-4" /> Search
          </button>
          <button type="button" className="btn-secondary" onClick={reset}>
            <RotateCcw className="h-3.5 w-3.5" /> Reset
          </button>
          <button type="button" className="btn-secondary" onClick={() => setSaveOpen(true)}>
            <Bookmark className="h-3.5 w-3.5" /> Save search
          </button>
          {canExport && (
            <button
              type="button"
              className="btn-secondary"
              onClick={() => {
                setExportError(null);
                downloadExport(queryBody(applied), 'csv').catch((e) => setExportError(toApiError(e).message));
              }}
            >
              <Download className="h-3.5 w-3.5" /> Export CSV
            </button>
          )}
          <button
            type="button"
            className={groupByCorrelation ? 'btn-primary' : 'btn-secondary'}
            onClick={() => setGroupByCorrelation((g) => !g)}
            aria-pressed={groupByCorrelation}
            title="Collapse events that share a correlation ID under one expandable main row"
          >
            <ListTree className="h-3.5 w-3.5" /> {groupByCorrelation ? 'Grouped by correlation' : 'Group by correlation'}
          </button>
          <span className="flex-1" />
          {/* live tail controls */}
          {tailOn && (
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setTailPaused((p) => !p)}
              aria-label={tailPaused ? 'Resume live tail' : 'Pause live tail'}
            >
              {tailPaused ? <Play className="h-3.5 w-3.5" /> : <Pause className="h-3.5 w-3.5" />}
              {tailPaused ? 'Resume' : 'Pause'}
            </button>
          )}
          <button
            type="button"
            className={tailOn ? 'btn-primary !bg-emerald-600 hover:!bg-emerald-700' : 'btn-secondary'}
            onClick={() => {
              setTailPaused(false);
              setTailOn((t) => !t);
            }}
            aria-pressed={tailOn}
          >
            <Radio className={`h-3.5 w-3.5 ${tailOn && !tailPaused ? 'animate-pulse' : ''}`} />
            {tailOn ? (tailPaused ? 'Live tail (paused)' : 'Live tail on') : 'Live tail'}
          </button>
        </div>
        {exportError && (
          <div className="mt-2">
            <ErrorBanner error={exportError} />
          </div>
        )}
        </form>
      </section>

      {error && <ErrorBanner error={error} onRetry={() => runSearch(applied)} />}

      {/* results */}
      <section className="card">
        <DataTable
          columns={columns}
          rows={items}
          rowKey={(e) => e.id ?? e.eventId}
          loading={loading}
          skeletonRows={10}
          onRowClick={(e) => setSelectedEventId(e.eventId)}
          rowClassName={(e) => `border-l-2 ${severityAccent(e.severity)}`}
          emptyMessage="No events match your filters"
          emptyHint="Try widening the time range or removing filters."
          groupBy={groupByCorrelation ? (e) => e.correlationId ?? null : undefined}
          groupLeader={pickTraceLeader}
          groupBadge={(members) => (
            <span className="rounded-full bg-gray-100 px-1.5 py-0.5 text-[10px] font-semibold tabular-nums text-gray-600 dark:bg-gray-800 dark:text-gray-300">
              {members.length}
            </span>
          )}
        />
        {!loading && nextCursor !== null && items.length > 0 && (
          <div className="border-t border-gray-100 p-3 text-center dark:border-gray-800">
            <button type="button" className="btn-secondary" onClick={loadMore} disabled={loadingMore}>
              {loadingMore && <Spinner className="h-3.5 w-3.5" />} Load more
            </button>
          </div>
        )}
      </section>

      <EventDetailDrawer
        eventId={selectedEventId}
        onClose={() => setSelectedEventId(null)}
        onApplyCorrelation={(cid) => applyQuickFilter({ correlationId: cid })}
      />

      {/* save search modal */}
      <Modal
        open={saveOpen}
        title="Save current search"
        onClose={() => setSaveOpen(false)}
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setSaveOpen(false)}>
              Cancel
            </button>
            <button type="button" className="btn-primary" onClick={saveSearch} disabled={saveBusy || !saveName.trim()}>
              {saveBusy && <Spinner className="h-3.5 w-3.5 !text-white" />} Save
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {saveError && <ErrorBanner error={saveError} />}
          <TextField label="Name" value={saveName} onChange={setSaveName} placeholder="e.g. Payment errors (prod)" autoFocus />
          <CheckboxField label="Share with other users" checked={saveShared} onChange={setSaveShared} />
          <CheckboxField label="Pin to top" checked={savePinned} onChange={setSavePinned} />
          <p className="text-xs text-gray-500">The currently applied filters will be saved.</p>
        </div>
      </Modal>
    </div>
  );
}
