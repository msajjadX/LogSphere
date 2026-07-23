import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Search } from 'lucide-react';
import { api } from '../api/client';
import type { ExceptionGroup } from '../api/types';
import { EXCEPTION_STATUSES } from '../api/types';
import { useApi } from '../hooks/useApi';
import { resolveRange, useTimeRange } from '../context/TimeRangeContext';
import { useLookups } from '../context/LookupsContext';
import { DataTable, type Column } from '../components/DataTable';
import { Badge, exceptionStatusTone } from '../components/Badge';
import { ErrorBanner } from '../components/Feedback';
import { MultiSelect, TextField } from '../components/Select';
import { formatDateTime, formatNumber, relativeTime, truncate } from '../utils/format';

const STATUS_TABS = ['All', ...EXCEPTION_STATUSES];

export function ExceptionsPage() {
  const { range } = useTimeRange();
  const { projects, applications } = useLookups();
  const navigate = useNavigate();

  const [statusTab, setStatusTab] = useState('All');
  const [text, setText] = useState('');
  const [appliedText, setAppliedText] = useState('');
  const [projectIds, setProjectIds] = useState<string[]>([]);
  const [moduleFilter, setModuleFilter] = useState('');
  // client-side column filter (groups aggregate application names as text)
  const [appNames, setAppNames] = useState<string[]>([]);

  const window = useMemo(() => resolveRange(range), [range]);

  const { data, loading, error, reload } = useApi<ExceptionGroup[] | { items: ExceptionGroup[] }>(
    () =>
      api.post<ExceptionGroup[] | { items: ExceptionGroup[] }>('/exceptions/groups/search', {
        from: window.from,
        to: window.to,
        status: statusTab === 'All' ? null : statusTab,
        text: appliedText || null,
        projectIds: projectIds.length > 0 ? projectIds : null,
        module: moduleFilter || null,
        limit: 200,
      }),
    [window.from, window.to, statusTab, appliedText, projectIds, moduleFilter],
  );

  const groups = useMemo(() => {
    const list = Array.isArray(data) ? data : (data?.items ?? []);
    return list.filter(
      (g) => appNames.length === 0 || appNames.some((n) => (g.applicationName ?? '').includes(n)),
    );
  }, [data, appNames]);

  // Canonical explorer column order: Time (last seen) → Project → Application →
  // Environment → record identity (Module, Exception) → metrics.
  const columns: Column<ExceptionGroup>[] = [
    {
      key: 'lastSeen',
      header: 'Last seen',
      render: (g) => (
        <span className="whitespace-nowrap text-xs" title={formatDateTime(g.lastSeen)}>
          {relativeTime(g.lastSeen)}
        </span>
      ),
    },
    {
      key: 'project',
      header: 'Project',
      render: (g) => <span className="whitespace-nowrap text-xs">{g.projectName ?? g.projectId ?? '—'}</span>,
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
      render: (g) => <span className="whitespace-nowrap text-xs">{g.applicationName ?? '—'}</span>,
      filter: {
        kind: 'multi',
        options: [...new Set(applications.map((a) => a.name))].map((n) => ({ value: n, label: n })),
        values: appNames,
        onApply: setAppNames,
      },
    },
    {
      key: 'module',
      header: 'Module',
      render: (g) => <span className="text-xs">{g.module ?? '—'}</span>,
      filter: {
        kind: 'text',
        value: moduleFilter,
        onApply: setModuleFilter,
        placeholder: 'Contains…',
        suggestions: [...new Set(groups.map((g) => g.module).filter((v): v is string => !!v))].sort(),
      },
    },
    {
      key: 'type',
      header: 'Exception',
      render: (g) => (
        <div className="min-w-0">
          <p className="truncate font-mono text-xs font-semibold text-red-700 dark:text-red-400">
            {g.exceptionType ?? 'Unknown'}
          </p>
          <p className="max-w-md truncate text-xs text-gray-600 dark:text-gray-400">{truncate(g.message, 140) || '—'}</p>
        </div>
      ),
      className: 'w-full',
      filter: {
        kind: 'text',
        value: appliedText,
        onApply: (v) => {
          setText(v);
          setAppliedText(v);
        },
        placeholder: 'Type or message…',
      },
    },
    {
      key: 'firstSeen',
      header: 'First seen',
      render: (g) => (
        <span className="whitespace-nowrap text-xs" title={formatDateTime(g.firstSeen)}>
          {relativeTime(g.firstSeen)}
        </span>
      ),
    },
    {
      key: 'counts',
      header: 'Count (total / 1h / 24h)',
      render: (g) => (
        <span className="whitespace-nowrap text-xs tabular-nums">
          <strong>{formatNumber(g.totalCount)}</strong>
          <span className="text-gray-400"> / {formatNumber(g.lastHourCount ?? 0)} / {formatNumber(g.last24hCount ?? 0)}</span>
        </span>
      ),
    },
    { key: 'status', header: 'Status', render: (g) => <Badge tone={exceptionStatusTone(g.status)}>{g.status}</Badge> },
    {
      key: 'assignee',
      header: 'Assignee',
      render: (g) => <span className="whitespace-nowrap text-xs">{g.assignedToName ?? (g.assignedToUserId ? String(g.assignedToUserId) : '—')}</span>,
    },
  ];

  return (
    <div className="space-y-3">
      {/* status tabs */}
      <div className="flex overflow-x-auto border-b border-gray-200 dark:border-gray-800" role="tablist">
        {STATUS_TABS.map((s) => (
          <button
            key={s}
            type="button"
            role="tab"
            aria-selected={statusTab === s}
            className={`tab whitespace-nowrap ${statusTab === s ? 'tab-active' : ''}`}
            onClick={() => setStatusTab(s)}
          >
            {s}
          </button>
        ))}
      </div>

      <div className="flex flex-wrap items-end gap-2">
        <MultiSelect
          label="Project"
          className="w-48"
          values={projectIds}
          placeholder="All projects"
          options={projects.map((p) => ({ value: String(p.id), label: p.name }))}
          onChange={setProjectIds}
        />
        <TextField
          label="Search"
          className="w-64"
          value={text}
          onChange={setText}
          placeholder="Type or message contains…"
        />
        <button type="button" className="btn-primary" onClick={() => setAppliedText(text)}>
          <Search className="h-4 w-4" /> Search
        </button>
      </div>

      {error && <ErrorBanner error={error} onRetry={reload} />}

      <section className="card">
        <DataTable
          columns={columns}
          rows={groups}
          rowKey={(g) => String(g.id)}
          loading={loading}
          onRowClick={(g) => navigate(`/exceptions/${encodeURIComponent(String(g.id))}`)}
          emptyMessage="No exception groups match your filters"
          emptyHint="Try a wider time range or a different status tab."
        />
      </section>
    </div>
  );
}
