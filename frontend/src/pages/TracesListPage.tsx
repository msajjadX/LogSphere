import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { ArrowRight, FilterX, Search } from 'lucide-react';
import { api } from '../api/client';
import { useApi } from '../hooks/useApi';
import { useTimeRange, resolveRange } from '../context/TimeRangeContext';
import { useLookups } from '../context/LookupsContext';
import { DataTable, type Column } from '../components/DataTable';
import { Badge } from '../components/Badge';
import { ErrorBanner } from '../components/Feedback';
import { MultiSelect, Select, TextField } from '../components/Select';
import { formatDateTime, formatDuration, relativeTime, truncate } from '../utils/format';

interface TraceRow {
  traceId: string;
  events: number;
  started: string;
  ended: string;
  errors: number;
  projectName?: string | null;
  name?: string | null;
  maxDurationMs?: number | null;
  applicationName?: string | null;
  environmentName?: string | null;
}

interface TraceFilters {
  traceId: string;
  name: string;
  applicationIds: string[];
  environmentIds: string[];
  minEvents: string;
  errorsOnly: boolean;
  /** seconds; keep as text so the field can be cleared */
  minLongestStepSec: string;
}

const EMPTY_FILTERS: TraceFilters = {
  traceId: '',
  name: '',
  applicationIds: [],
  environmentIds: [],
  minEvents: '',
  errorsOnly: false,
  minLongestStepSec: '',
};

export function TracesListPage() {
  const navigate = useNavigate();
  const { range } = useTimeRange();
  const { projects, applications, environments } = useLookups();
  const [projectIds, setProjectIds] = useState<string[]>([]);
  const [traceInput, setTraceInput] = useState('');
  const [filters, setFilters] = useState<TraceFilters>(EMPTY_FILTERS);
  const [sort, setSort] = useState<'newest' | 'slowest' | 'errors'>('newest');

  const window = useMemo(() => resolveRange(range), [range]);

  // Debounce text inputs so each keystroke doesn't fire a query; the filters are
  // sent to the SERVER, which returns up to 200 traces that MATCH the criteria
  // (searching the whole time range, not just the newest 200).
  const [applied, setApplied] = useState<TraceFilters>(filters);
  useEffect(() => {
    const id = setTimeout(() => setApplied(filters), 400);
    return () => clearTimeout(id);
  }, [filters]);

  const { data, loading, error, reload } = useApi<{ items: TraceRow[] }>(
    () => {
      const minEvents = parseInt(applied.minEvents, 10);
      const minSec = parseFloat(applied.minLongestStepSec);
      return api.post<{ items: TraceRow[] }>('/query/traces/search', {
        from: window.from,
        to: window.to,
        projectIds: projectIds.length > 0 ? projectIds : null,
        applicationIds: applied.applicationIds.length > 0 ? applied.applicationIds : null,
        environmentIds: applied.environmentIds.length > 0 ? applied.environmentIds.map(Number) : null,
        traceIdContains: applied.traceId || null,
        nameContains: applied.name || null,
        minEvents: isNaN(minEvents) ? null : minEvents,
        errorsOnly: applied.errorsOnly,
        minDurationMs: isNaN(minSec) ? null : minSec * 1000,
        sort,
        limit: 200,
      });
    },
    [window.from, window.to, projectIds, applied, sort],
  );

  const set = <K extends keyof TraceFilters>(key: K, value: TraceFilters[K]) =>
    setFilters((f) => ({ ...f, [key]: value }));
  const hasFilters =
    filters.traceId || filters.name || filters.applicationIds.length > 0 || filters.environmentIds.length > 0 ||
    filters.minEvents || filters.errorsOnly || filters.minLongestStepSec;

  const items = data?.items ?? [];

  const open = (id: string) => {
    if (id.trim()) navigate(`/traces/${encodeURIComponent(id.trim())}`);
  };

  // Canonical explorer column order: Time → Project → Application → Environment →
  // record identity (Name, Trace ID) → metrics.
  const columns: Column<TraceRow>[] = [
    {
      key: 'started',
      header: 'Started',
      render: (t) => (
        <span className="whitespace-nowrap text-xs" title={formatDateTime(t.started)}>
          {formatDateTime(t.started)} <span className="text-gray-400">({relativeTime(t.started)})</span>
        </span>
      ),
    },
    {
      key: 'project',
      header: 'Project',
      render: (t) => <span className="text-xs">{t.projectName ?? '—'}</span>,
      filter: {
        kind: 'multi',
        options: projects.map((p) => ({ value: String(p.id), label: p.name })),
        values: projectIds,
        onApply: setProjectIds,
      },
    },
    {
      key: 'application',
      header: 'Application',
      render: (t) => <span className="text-xs">{t.applicationName ?? '—'}</span>,
      filter: {
        kind: 'multi',
        options: applications.map((a) => ({ value: String(a.id), label: a.name })),
        values: filters.applicationIds,
        onApply: (v) => set('applicationIds', v),
      },
    },
    {
      key: 'name',
      header: 'Name',
      render: (t) => <span className="text-xs">{t.name ?? '—'}</span>,
      wrap: true,
      expand: true,
      filter: {
        kind: 'text',
        value: filters.name,
        onApply: (v) => set('name', v),
        placeholder: 'Contains…',
        suggestions: [...new Set(items.map((t) => t.name).filter((v): v is string => !!v))].sort(),
      },
    },
    {
      key: 'trace',
      header: 'Trace ID',
      render: (t) => <span className="font-mono text-xs">{truncate(t.traceId, 28)}</span>,
      filter: {
        kind: 'text',
        value: filters.traceId,
        onApply: (v) => set('traceId', v),
        placeholder: 'Contains…',
        mono: true,
      },
    },
    { key: 'events', header: 'Events', render: (t) => <span className="text-xs tabular-nums">{t.events}</span> },
    {
      key: 'errors',
      header: 'Errors',
      render: (t) =>
        t.errors > 0 ? <Badge tone="red">{t.errors}</Badge> : <span className="text-xs text-gray-400">0</span>,
    },
    {
      key: 'duration',
      header: 'Longest step',
      render: (t) => (
        <span
          className={`text-xs tabular-nums ${(t.maxDurationMs ?? 0) >= 3000 ? 'font-semibold text-amber-600 dark:text-amber-400' : ''}`}
        >
          {t.maxDurationMs != null ? formatDuration(t.maxDurationMs) : '—'}
        </span>
      ),
    },
    {
      key: 'openIt',
      header: '',
      render: () => <ArrowRight className="h-3.5 w-3.5 text-gray-400" />,
      className: 'text-right',
    },
  ];

  return (
    <div className="space-y-4">
      <section className="card p-3">
        <div className="flex flex-wrap items-end gap-2.5">
          <div className="min-w-[260px] flex-1">
            <TextField
              label="Open a trace by ID"
              value={traceInput}
              onChange={setTraceInput}
              mono
              placeholder="paste a trace id…"
            />
          </div>
          <button type="button" className="btn-primary" onClick={() => open(traceInput)} disabled={!traceInput.trim()}>
            <Search className="h-4 w-4" /> Open trace
          </button>
          <div className="min-w-[200px]">
            <MultiSelect
              label="Project"
              values={projectIds}
              placeholder="All projects"
              options={projects.map((p) => ({ value: String(p.id), label: p.name }))}
              onChange={setProjectIds}
            />
          </div>
        </div>

        {/* column filters — applied instantly to the results below */}
        <div className="mt-3 flex flex-wrap items-end gap-2.5 border-t border-gray-100 pt-3 dark:border-gray-800">
          <div className="w-40">
            <TextField label="Trace ID contains" value={filters.traceId} onChange={(v) => set('traceId', v)} mono placeholder="filter…" />
          </div>
          <div className="w-40">
            <TextField label="Name contains" value={filters.name} onChange={(v) => set('name', v)} placeholder="filter…" />
          </div>
          <div className="w-44">
            <MultiSelect
              label="Application"
              values={filters.applicationIds}
              placeholder="All applications"
              options={applications.map((a) => ({ value: String(a.id), label: a.name }))}
              onChange={(v) => set('applicationIds', v)}
            />
          </div>
          <div className="w-40">
            <MultiSelect
              label="Environment"
              values={filters.environmentIds}
              placeholder="All environments"
              options={environments.map((e) => ({ value: String(e.id), label: e.name }))}
              onChange={(v) => set('environmentIds', v)}
            />
          </div>
          <div className="w-28">
            <TextField label="Min events" value={filters.minEvents} onChange={(v) => set('minEvents', v)} placeholder="e.g. 5" />
          </div>
          <div className="w-36">
            <TextField
              label="Longest step ≥ (s)"
              value={filters.minLongestStepSec}
              onChange={(v) => set('minLongestStepSec', v)}
              placeholder="e.g. 3"
            />
          </div>
          <div className="w-40">
            <Select
              label="Sort by"
              value={sort}
              options={[
                { value: 'newest', label: 'Newest first' },
                { value: 'slowest', label: 'Longest step first' },
                { value: 'errors', label: 'Most errors first' },
              ]}
              onChange={(v) => setSort(v as typeof sort)}
            />
          </div>
          <label className="flex items-center gap-1.5 pb-2 text-xs text-gray-600 dark:text-gray-400">
            <input
              type="checkbox"
              className="h-3.5 w-3.5 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
              checked={filters.errorsOnly}
              onChange={(e) => set('errorsOnly', e.target.checked)}
            />
            With errors only
          </label>
          {hasFilters && (
            <button type="button" className="btn-ghost !px-2 pb-1.5 text-xs" onClick={() => setFilters(EMPTY_FILTERS)}>
              <FilterX className="h-3.5 w-3.5" /> Clear filters
            </button>
          )}
        </div>
      </section>

      {error && <ErrorBanner error={error} onRetry={reload} />}

      <section className="card">
        <h2 className="flex items-center justify-between border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
          <span>Recent traces</span>
          <span className="text-xs font-normal text-gray-500">
            {items.length === 200
              ? 'first 200 matches — narrow the filters or time range for more specific results'
              : `${items.length} ${hasFilters ? 'matching ' : ''}traces`}
          </span>
        </h2>
        <DataTable
          columns={columns}
          rows={items}
          rowKey={(t) => t.traceId}
          loading={loading}
          onRowClick={(t) => open(t.traceId)}
          emptyMessage={hasFilters ? 'No traces match the current filters' : 'No traces in this time range'}
          emptyHint={
            hasFilters
              ? 'Loosen or clear the column filters above.'
              : 'Traces appear when applications send a traceId with their events. Events without a traceId are still fully searchable in the Log Explorer via their correlation ID.'
          }
        />
      </section>
    </div>
  );
}
