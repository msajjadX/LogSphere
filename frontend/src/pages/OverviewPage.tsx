import { useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Activity,
  AlertOctagon,
  Bug,
  Clock,
  Database,
  Gauge,
  Layers,
  TrendingUp,
} from 'lucide-react';
import { api } from '../api/client';
import type { LogEventSummary, OverviewStats, TimeseriesPoint } from '../api/types';
import { useApi } from '../hooks/useApi';
import { resolveRange, suggestedInterval, useTimeRange } from '../context/TimeRangeContext';
import { useLookups } from '../context/LookupsContext';
import { StatCard } from '../components/StatCard';
import { SeverityChart } from '../components/SeverityChart';
import { DataTable, type Column } from '../components/DataTable';
import { SeverityBadge } from '../components/Badge';
import { ErrorBanner, LoadingBlock } from '../components/Feedback';
import { formatCompact, formatDateTime, formatDuration, formatNumber, formatPercent, relativeTime, truncate } from '../utils/format';
import type { TopProjectStat, TopRouteStat } from '../api/types';

export function OverviewPage() {
  const { range } = useTimeRange();
  const navigate = useNavigate();
  const { projects } = useLookups();
  const window = useMemo(() => resolveRange(range), [range]);

  // Jump to the Log Explorer, scoped to one project's error-severity events.
  // The stat row only carries the project name, so resolve its id from the lookup.
  const goToProjectErrors = (p: TopProjectStat) => {
    const label = p.projectName ?? p.name;
    const projectId =
      p.projectId ?? projects.find((pr) => pr.name === label || String(pr.id) === String(p.projectId ?? label))?.id;
    const filter: Record<string, unknown> = { severities: ['Error', 'Critical'] };
    if (projectId != null) filter.projectId = projectId;
    navigate(`/logs?q=${encodeURIComponent(JSON.stringify(filter))}`);
  };

  const overview = useApi<OverviewStats>(
    () => api.post<OverviewStats>('/query/stats/overview', { from: window.from, to: window.to }),
    [window.from, window.to],
  );
  const timeseries = useApi<TimeseriesPoint[]>(
    () =>
      api.post<TimeseriesPoint[]>('/query/stats/timeseries', {
        from: window.from,
        to: window.to,
        intervalMinutes: suggestedInterval(range),
        metric: 'count',
      }),
    [window.from, window.to],
  );

  const s = overview.data;
  const loading = overview.loading;

  const totalEvents = useMemo(() => {
    if (!s) return undefined;
    if (typeof s.totalEvents === 'number') return s.totalEvents;
    if (typeof s.totals === 'number') return s.totals;
    if (s.severityCounts) return Object.values(s.severityCounts).reduce((a, b) => a + b, 0);
    return undefined;
  }, [s]);

  const routeColumns: Column<TopRouteStat>[] = [
    {
      key: 'route',
      header: 'Route',
      render: (r) => (
        <span className="font-mono text-xs">
          {r.method && <span className="mr-1.5 text-gray-400">{r.method}</span>}
          {r.route ?? r.httpRoute ?? '—'}
        </span>
      ),
    },
    { key: 'count', header: 'Requests', render: (r) => formatNumber(r.count), className: 'tabular-nums' },
    {
      key: 'errors',
      header: 'Errors',
      render: (r) => <span className="text-red-600 dark:text-red-400">{formatNumber(r.errorCount ?? 0)}</span>,
      className: 'tabular-nums',
    },
    {
      key: 'rate',
      header: 'Error rate',
      render: (r) =>
        r.errorRate != null ? formatPercent(r.errorRate) : r.count ? formatPercent((100 * (r.errorCount ?? 0)) / r.count) : '—',
      className: 'tabular-nums',
    },
  ];

  const projectColumns: Column<TopProjectStat>[] = [
    { key: 'name', header: 'Project', render: (p) => p.projectName ?? p.name ?? String(p.projectId ?? '—') },
    { key: 'count', header: 'Events', render: (p) => formatNumber(p.count), className: 'tabular-nums' },
    {
      key: 'errors',
      header: 'Errors',
      render: (p) =>
        (p.errorCount ?? 0) > 0 ? (
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              goToProjectErrors(p);
            }}
            title="View these error logs"
            className="font-medium text-red-600 underline decoration-dotted underline-offset-2 hover:decoration-solid dark:text-red-400"
          >
            {formatNumber(p.errorCount ?? 0)}
          </button>
        ) : (
          <span className="text-gray-400">{formatNumber(p.errorCount ?? 0)}</span>
        ),
      className: 'tabular-nums',
    },
  ];

  return (
    <div className="space-y-4">
      {overview.error && <ErrorBanner error={overview.error} onRetry={overview.reload} />}

      {/* KPI cards */}
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
        <StatCard label="Total events" value={formatCompact(totalEvents)} icon={Layers} loading={loading} />
        <StatCard
          label="Error rate"
          value={formatPercent(s?.errorRate)}
          sub={`Warnings ${formatPercent(s?.warningRate)}`}
          icon={AlertOctagon}
          tone={(s?.errorRate ?? 0) > 5 ? 'bad' : 'default'}
          loading={loading}
        />
        <StatCard
          label="Avg duration"
          value={formatDuration(s?.avgDurationMs)}
          sub={`P95 ${formatDuration(s?.p95DurationMs)} · P99 ${formatDuration(s?.p99DurationMs)}`}
          icon={Clock}
          loading={loading}
        />
        <StatCard label="Requests / min" value={formatNumber(s?.requestsPerMinute)} icon={TrendingUp} loading={loading} />
        <StatCard
          label="Active exception groups"
          value={formatNumber(s?.activeExceptionGroups)}
          icon={Bug}
          tone={(s?.activeExceptionGroups ?? 0) > 0 ? 'warn' : 'default'}
          loading={loading}
        />
        <StatCard label="Queue depth" value={formatNumber(s?.queueDepth)} icon={Database} loading={loading} />
        <StatCard
          label="Ingestion"
          value={s?.ingestionHealthy === false ? 'Degraded' : s?.ingestionHealthy ? 'Healthy' : '—'}
          icon={Activity}
          tone={s?.ingestionHealthy === false ? 'bad' : s?.ingestionHealthy ? 'good' : 'default'}
          loading={loading}
        />
        <StatCard label="P95 duration" value={formatDuration(s?.p95DurationMs)} icon={Gauge} loading={loading} />
      </div>

      {/* severity over time */}
      <section className="card p-4">
        <h2 className="mb-3 text-sm font-semibold">Events by severity over time</h2>
        {timeseries.loading ? (
          <LoadingBlock />
        ) : timeseries.error ? (
          <ErrorBanner error={timeseries.error} onRetry={timeseries.reload} />
        ) : (
          <SeverityChart points={timeseries.data ?? []} />
        )}
      </section>

      <div className="grid gap-4 xl:grid-cols-2">
        <section className="card">
          <h2 className="border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
            Top failing routes
          </h2>
          <DataTable
            columns={routeColumns}
            rows={s?.topFailingRoutes ?? []}
            rowKey={(r) => `${r.method ?? ''}-${r.route ?? r.httpRoute ?? ''}`}
            loading={loading}
            emptyMessage="No failing routes in this time range"
          />
        </section>
        <section className="card">
          <h2 className="border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
            Most affected projects
          </h2>
          <DataTable
            columns={projectColumns}
            rows={s?.topProjects ?? []}
            rowKey={(p) => String(p.projectId ?? p.projectName ?? p.name)}
            loading={loading}
            emptyMessage="No project activity in this time range"
          />
        </section>
      </div>

      {/* recent criticals */}
      <section className="card">
        <h2 className="border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
          Recent errors &amp; critical events
        </h2>
        {loading ? (
          <LoadingBlock />
        ) : (s?.recentCritical ?? []).length === 0 ? (
          <p className="px-4 py-8 text-center text-sm text-gray-500">No error or critical events in this time range.</p>
        ) : (
          <ul className="divide-y divide-gray-100 dark:divide-gray-800/70">
            {(s?.recentCritical ?? []).map((e: LogEventSummary) => (
              <li key={e.eventId}>
                <button
                  type="button"
                  className="flex w-full items-start gap-3 px-4 py-2.5 text-left hover:bg-gray-50 dark:hover:bg-gray-800/50"
                  onClick={() => navigate(`/logs?correlationId=${encodeURIComponent(e.correlationId ?? '')}&text=${encodeURIComponent(e.correlationId ? '' : (e.message ?? ''))}`)}
                >
                  <SeverityBadge severity={e.severity} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-sm">{truncate(e.message, 140) || e.exceptionType || e.eventType}</span>
                    <span className="block text-xs text-gray-500">
                      {e.projectName ?? ''} {e.module ? `· ${e.module}` : ''} · {formatDateTime(e.eventTimestamp)} (
                      {relativeTime(e.eventTimestamp)})
                    </span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
