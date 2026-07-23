import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Link2, Search } from 'lucide-react';
import { api, toApiError, type ApiError } from '../api/client';
import type { AuditEvent, AuditFilter, AuditQueryResult } from '../api/types';
import { resolveRange, useTimeRange } from '../context/TimeRangeContext';
import { useLookups } from '../context/LookupsContext';
import { DataTable, type Column } from '../components/DataTable';
import { Drawer } from '../components/Drawer';
import { ErrorBanner, Spinner } from '../components/Feedback';
import { MultiSelect, TextField } from '../components/Select';
import { JsonViewer } from '../components/JsonViewer';
import { formatDateTime, relativeTime, truncate, tryParseJson } from '../utils/format';

const PAGE_SIZE = 100;

function stringify(v: unknown): string {
  if (v === undefined) return '';
  if (v === null) return 'null';
  if (typeof v === 'string') return v;
  try {
    return JSON.stringify(v);
  } catch {
    return String(v);
  }
}

/** Side-by-side old/new diff with changed fields highlighted. */
function DiffView({ event }: { event: AuditEvent }) {
  const oldValues = (tryParseJson(event.oldValues) ?? {}) as Record<string, unknown>;
  const newValues = (tryParseJson(event.newValues) ?? {}) as Record<string, unknown>;
  const changed = new Set(event.changedFields ?? []);
  const keys = [...new Set([...Object.keys(oldValues), ...Object.keys(newValues)])].sort();

  if (keys.length === 0) {
    return <p className="text-sm text-gray-500">No field changes recorded on this event.</p>;
  }

  return (
    <div className="overflow-x-auto rounded-md border border-gray-200 dark:border-gray-800">
      <table className="min-w-full text-xs">
        <thead>
          <tr className="border-b border-gray-200 bg-gray-50 dark:border-gray-800 dark:bg-gray-950">
            <th scope="col" className="px-2.5 py-1.5 text-left font-semibold text-gray-500">Field</th>
            <th scope="col" className="px-2.5 py-1.5 text-left font-semibold text-gray-500">Old value</th>
            <th scope="col" className="px-2.5 py-1.5 text-left font-semibold text-gray-500">New value</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100 dark:divide-gray-800/70">
          {keys.map((k) => {
            const oldV = stringify(oldValues[k]);
            const newV = stringify(newValues[k]);
            const isChanged = changed.size > 0 ? changed.has(k) : oldV !== newV;
            return (
              <tr key={k} className={isChanged ? 'bg-amber-50 dark:bg-amber-950/30' : ''}>
                <td className="whitespace-nowrap px-2.5 py-1.5 font-mono font-medium">{k}</td>
                <td className={`break-all px-2.5 py-1.5 font-mono ${isChanged ? 'text-red-700 line-through decoration-red-300 dark:text-red-400' : 'text-gray-600 dark:text-gray-400'}`}>
                  {oldV || <span className="italic text-gray-400 no-underline">empty</span>}
                </td>
                <td className={`break-all px-2.5 py-1.5 font-mono ${isChanged ? 'font-semibold text-emerald-700 dark:text-emerald-400' : 'text-gray-600 dark:text-gray-400'}`}>
                  {newV || <span className="italic text-gray-400">empty</span>}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

export function AuditPage() {
  const { range } = useTimeRange();
  const { projects, applications } = useLookups();
  const navigate = useNavigate();

  const [filter, setFilter] = useState<AuditFilter>({});
  const [applied, setApplied] = useState<AuditFilter>({});
  const [items, setItems] = useState<AuditEvent[]>([]);
  const [nextCursor, setNextCursor] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [selected, setSelected] = useState<AuditEvent | null>(null);

  const window = useMemo(() => resolveRange(range), [range]);

  const buildBody = useCallback(
    (f: AuditFilter, afterId?: number | null): AuditFilter => {
      const body: AuditFilter = { from: window.from, to: window.to, limit: PAGE_SIZE };
      if (f.projectId != null && f.projectId !== '') body.projectId = f.projectId;
      if ((f.projectIds ?? []).length > 0) body.projectIds = f.projectIds;
      if ((f.applicationIds ?? []).length > 0) body.applicationIds = f.applicationIds;
      if ((f.environmentIds ?? []).length > 0) body.environmentIds = f.environmentIds;
      if (f.actorUserId) body.actorUserId = f.actorUserId;
      if (f.actionName) body.actionName = f.actionName;
      if (f.entityType) body.entityType = f.entityType;
      if (f.entityId) body.entityId = f.entityId;
      if (f.text) body.text = f.text;
      if (afterId != null) body.afterId = afterId;
      return body;
    },
    [window],
  );

  const run = useCallback(
    async (f: AuditFilter) => {
      setLoading(true);
      setError(null);
      try {
        const res = await api.post<AuditQueryResult | AuditEvent[]>('/query/audit', buildBody(f));
        const list = Array.isArray(res) ? res : (res.items ?? []);
        setItems(list);
        setNextCursor(Array.isArray(res) ? null : (res.nextCursor ?? null));
      } catch (e) {
        setError(toApiError(e));
      } finally {
        setLoading(false);
      }
    },
    [buildBody],
  );

  useEffect(() => {
    void run(applied);
  }, [applied, run]);

  // re-run the current query when the active nav item is re-clicked (see AppShell). The local
  // `window` (resolved range) shadows the global, so subscribe via globalThis.
  useEffect(() => {
    const onRefresh = () => void run(applied);
    globalThis.addEventListener('logsphere:refresh', onRefresh);
    return () => globalThis.removeEventListener('logsphere:refresh', onRefresh);
  }, [run, applied]);

  const loadMore = async () => {
    if (nextCursor === null) return;
    setLoadingMore(true);
    try {
      const res = await api.post<AuditQueryResult>('/query/audit', buildBody(applied, nextCursor));
      setItems((prev) => [...prev, ...(res.items ?? [])]);
      setNextCursor(res.nextCursor ?? null);
    } catch (e) {
      setError(toApiError(e));
    } finally {
      setLoadingMore(false);
    }
  };

  const set = <K extends keyof AuditFilter>(k: K, v: AuditFilter[K]) => setFilter((f) => ({ ...f, [k]: v }));

  /** Column-header (Excel-style) filters merge into the current filter and apply immediately. */
  const applyPartial = useCallback((partial: Partial<AuditFilter>) => {
    setFilter((prev) => {
      const f = { ...prev, ...partial };
      setApplied(f);
      return f;
    });
  }, []);

  // Canonical explorer column order: Time → Project → Application → Environment →
  // record identity (Actor, Action, Entity) → Summary.
  const columns: Column<AuditEvent>[] = [
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
          .filter(
            (a) =>
              (filter.projectIds ?? []).length === 0 ||
              (filter.projectIds ?? []).map(String).includes(String(a.projectId)),
          )
          .map((a) => ({ value: String(a.id), label: a.name })),
        values: (filter.applicationIds ?? []).map(String),
        onApply: (v) => applyPartial({ applicationIds: v }),
      },
    },
    {
      key: 'actor',
      header: 'Actor',
      render: (e) => <span className="whitespace-nowrap text-xs">{e.userName ?? e.userId ?? '—'}</span>,
      filter: {
        kind: 'text',
        value: filter.actorUserId ?? '',
        onApply: (v) => applyPartial({ actorUserId: v }),
        placeholder: 'Name or id…',
        suggestions: [...new Set(items.map((e) => e.userName ?? e.userId).filter((v): v is string => !!v))].sort(),
      },
    },
    {
      key: 'action',
      header: 'Action',
      render: (e) => <span className="whitespace-nowrap font-mono text-xs">{e.actionName ?? '—'}</span>,
      filter: {
        kind: 'text',
        value: filter.actionName ?? '',
        onApply: (v) => applyPartial({ actionName: v }),
        placeholder: 'Contains…',
        mono: true,
        suggestions: [...new Set(items.map((e) => e.actionName).filter((v): v is string => !!v))].sort(),
      },
    },
    {
      key: 'entity',
      header: 'Entity',
      render: (e) => (
        <span className="whitespace-nowrap text-xs">
          {e.businessEntityType ?? '—'}
          {e.businessEntityId && <span className="text-gray-400"> / {e.businessEntityId}</span>}
        </span>
      ),
      filter: {
        kind: 'text',
        value: filter.entityType ?? '',
        onApply: (v) => applyPartial({ entityType: v }),
        placeholder: 'Entity type…',
        suggestions: [...new Set(items.map((e) => e.businessEntityType).filter((v): v is string => !!v))].sort(),
      },
    },
    {
      key: 'message',
      header: 'Summary',
      render: (e) => <span className="block max-w-lg truncate text-xs">{truncate(e.message, 140) || '—'}</span>,
      className: 'w-full',
      filter: {
        kind: 'text',
        value: filter.text ?? '',
        onApply: (v) => applyPartial({ text: v }),
        placeholder: 'Contains…',
      },
    },
  ];

  return (
    <div className="space-y-3">
      <section className="card p-3">
        <div className="grid grid-cols-2 gap-2.5 md:grid-cols-3 xl:grid-cols-5">
          <MultiSelect
            label="Project"
            values={(filter.projectIds ?? []).map(String)}
            placeholder="All projects"
            options={projects.map((p) => ({ value: String(p.id), label: p.name }))}
            onChange={(v) => set('projectIds', v)}
          />
          <TextField label="Actor user" value={filter.actorUserId ?? ''} onChange={(v) => set('actorUserId', v)} placeholder="user id" />
          <TextField label="Action name" value={filter.actionName ?? ''} onChange={(v) => set('actionName', v)} placeholder="e.g. OrderUpdated" />
          <TextField label="Entity type" value={filter.entityType ?? ''} onChange={(v) => set('entityType', v)} placeholder="e.g. Order" />
          <TextField label="Entity ID" value={filter.entityId ?? ''} onChange={(v) => set('entityId', v)} />
        </div>
        <div className="mt-3">
          <button type="button" className="btn-primary" onClick={() => setApplied(filter)} disabled={loading}>
            <Search className="h-4 w-4" /> Search
          </button>
        </div>
      </section>

      {error && <ErrorBanner error={error} onRetry={() => run(applied)} />}

      <section className="card">
        <DataTable
          columns={columns}
          rows={items}
          rowKey={(e) => e.id ?? e.eventId}
          loading={loading}
          skeletonRows={8}
          onRowClick={setSelected}
          emptyMessage="No audit events match your filters"
        />
        {!loading && nextCursor !== null && items.length > 0 && (
          <div className="border-t border-gray-100 p-3 text-center dark:border-gray-800">
            <button type="button" className="btn-secondary" onClick={loadMore} disabled={loadingMore}>
              {loadingMore && <Spinner className="h-3.5 w-3.5" />} Load more
            </button>
          </div>
        )}
      </section>

      <Drawer
        open={selected !== null}
        onClose={() => setSelected(null)}
        title={`Audit — ${selected?.actionName ?? selected?.eventId ?? ''}`}
        widthClass="max-w-3xl"
      >
        {selected && (
          <div className="space-y-4">
            <dl className="grid grid-cols-2 gap-x-4 gap-y-2.5 text-sm sm:grid-cols-3">
              <div>
                <dt className="text-[11px] font-medium uppercase text-gray-400">When</dt>
                <dd>{formatDateTime(selected.eventTimestamp)}</dd>
              </div>
              <div>
                <dt className="text-[11px] font-medium uppercase text-gray-400">Actor</dt>
                <dd>{selected.userName ?? selected.userId ?? '—'}</dd>
              </div>
              <div>
                <dt className="text-[11px] font-medium uppercase text-gray-400">Action</dt>
                <dd className="font-mono text-xs">{selected.actionName ?? '—'}</dd>
              </div>
              <div>
                <dt className="text-[11px] font-medium uppercase text-gray-400">Entity</dt>
                <dd>
                  {selected.businessEntityType ?? '—'} {selected.businessEntityId ? `/ ${selected.businessEntityId}` : ''}
                </dd>
              </div>
              <div>
                <dt className="text-[11px] font-medium uppercase text-gray-400">Source IP</dt>
                <dd className="font-mono text-xs">{selected.sourceIp ?? '—'}</dd>
              </div>
              <div>
                <dt className="text-[11px] font-medium uppercase text-gray-400">Reason</dt>
                <dd>{selected.reason ?? '—'}</dd>
              </div>
              <div>
                <dt className="text-[11px] font-medium uppercase text-gray-400">Application</dt>
                <dd>{selected.applicationName ?? '—'}</dd>
              </div>
              <div>
                <dt className="text-[11px] font-medium uppercase text-gray-400">Environment</dt>
                <dd>{selected.environmentName ?? '—'}</dd>
              </div>
            </dl>

            {selected.correlationId && (
              <button
                type="button"
                className="btn-secondary !px-2 !py-1 text-xs"
                onClick={() => navigate(`/logs?correlationId=${encodeURIComponent(selected.correlationId!)}`)}
              >
                <Link2 className="h-3 w-3" /> View correlated events
              </button>
            )}

            <section>
              <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-gray-500">Changes (old → new)</h3>
              <DiffView event={selected} />
            </section>

            {selected.message && (
              <section>
                <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-gray-500">Message</h3>
                <p className="whitespace-pre-wrap break-words rounded-md bg-gray-50 p-2.5 text-sm dark:bg-gray-950">{selected.message}</p>
              </section>
            )}

            {(selected.oldValues || selected.newValues) && (
              <details>
                <summary className="cursor-pointer text-xs font-semibold uppercase tracking-wide text-gray-500">
                  Raw values
                </summary>
                <div className="mt-2 grid gap-3 md:grid-cols-2">
                  <div>
                    <p className="label">Old</p>
                    <JsonViewer value={selected.oldValues} />
                  </div>
                  <div>
                    <p className="label">New</p>
                    <JsonViewer value={selected.newValues} />
                  </div>
                </div>
              </details>
            )}
          </div>
        )}
      </Drawer>
    </div>
  );
}
