import { useMemo } from 'react';
import {
  AlertOctagon,
  Clock,
  Database,
  HardDrive,
  HeartPulse,
  Inbox,
  ShieldAlert,
  Zap,
} from 'lucide-react';
import { api } from '../api/client';
import type { DeadLetterEvent, StoragePartition, SystemMetrics, TableStat, WorkerStatus } from '../api/types';
import { useApi, useAutoRefresh } from '../hooks/useApi';
import { StatCard } from '../components/StatCard';
import { DataTable, type Column } from '../components/DataTable';
import { Badge } from '../components/Badge';
import { ErrorBanner } from '../components/Feedback';
import { JsonViewer } from '../components/JsonViewer';
import { formatBytes, formatDateTime, formatDuration, formatNumber, relativeTime } from '../utils/format';

const REFRESH_MS = 10_000;

function WorkerCard({ worker }: { worker: WorkerStatus }) {
  const healthy = worker.healthy;
  return (
    <div
      className={`card flex items-center justify-between gap-3 px-4 py-3 ${
        healthy ? 'border-emerald-200 dark:border-emerald-900' : 'border-red-300 dark:border-red-900'
      }`}
    >
      <div className="min-w-0">
        <p className="truncate text-sm font-medium">{worker.name}</p>
        <p className="text-xs text-gray-500">
          Heartbeat {worker.lastHeartbeat ? relativeTime(worker.lastHeartbeat) : 'never'}
        </p>
      </div>
      <Badge tone={healthy ? 'green' : 'red'}>{healthy ? 'Healthy' : 'Stale'}</Badge>
    </div>
  );
}

export function HealthPage() {
  const metrics = useApi<SystemMetrics>(() => api.get<SystemMetrics>('/system/metrics'), []);
  const deadLetters = useApi<DeadLetterEvent[]>(() => api.getList<DeadLetterEvent>('/system/dead-letters?limit=50'), []);

  useAutoRefresh(metrics.reload, REFRESH_MS);
  useAutoRefresh(deadLetters.reload, REFRESH_MS * 3);

  const m = metrics.data;
  const loading = metrics.loading && !m;

  const storageColumns: Column<StoragePartition>[] = [
    { key: 'partition', header: 'Partition', render: (p) => <span className="font-mono text-xs">{p.partition}</span> },
    { key: 'size', header: 'Size', render: (p) => <span className="text-xs tabular-nums">{formatBytes(p.sizeMb)}</span> },
    { key: 'rows', header: 'Rows', render: (p) => <span className="text-xs tabular-nums">{formatNumber(p.rows)}</span> },
  ];

  const tableColumns: Column<TableStat>[] = [
    { key: 'table', header: 'Table', render: (t) => <span className="font-mono text-xs">{t.table}</span> },
    { key: 'size', header: 'Size', render: (t) => <span className="text-xs tabular-nums">{formatBytes(t.sizeMb)}</span> },
    { key: 'live', header: 'Live rows', render: (t) => <span className="text-xs tabular-nums">{formatNumber(t.liveRows)}</span> },
    {
      key: 'dead',
      header: 'Dead rows',
      render: (t) => (
        <span className={`text-xs tabular-nums ${t.deadRows > Math.max(1000, t.liveRows * 0.2) ? 'text-amber-600 dark:text-amber-400' : ''}`}>
          {formatNumber(t.deadRows)}
        </span>
      ),
    },
    {
      key: 'vacuum',
      header: 'Last autovacuum',
      render: (t) => (
        <span className="whitespace-nowrap text-xs text-gray-500">
          {t.lastAutovacuum ? relativeTime(t.lastAutovacuum) : 'never'}
        </span>
      ),
    },
  ];

  const dlRows = useMemo(() => (Array.isArray(deadLetters.data) ? deadLetters.data : []), [deadLetters.data]);
  const dlColumns: Column<DeadLetterEvent>[] = [
    {
      key: 'received',
      header: 'Received',
      render: (d) => (
        <span className="whitespace-nowrap text-xs">
          {d.receivedAt ? `${formatDateTime(String(d.receivedAt))} (${relativeTime(String(d.receivedAt))})` : '—'}
        </span>
      ),
    },
    { key: 'source', header: 'Source', render: (d) => <span className="text-xs">{d.source ?? '—'}</span> },
    { key: 'attempts', header: 'Attempts', render: (d) => <span className="text-xs tabular-nums">{d.attempts ?? '—'}</span> },
    {
      key: 'reason',
      header: 'Reason',
      render: (d) => <span className="block max-w-md break-words text-xs text-red-700 dark:text-red-400">{d.reason ?? '—'}</span>,
      className: 'w-full',
    },
    {
      key: 'raw',
      header: 'Diagnostics',
      render: (d) => (
        <details>
          <summary className="cursor-pointer text-xs text-indigo-600 dark:text-indigo-400">view</summary>
          <div className="mt-1 max-w-lg">
            <JsonViewer value={d} initialDepth={1} />
          </div>
        </details>
      ),
    },
  ];

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-xs text-gray-500">Auto-refreshes every {REFRESH_MS / 1000}s.</p>
        {m?.dbHealthy === false && <Badge tone="red">Database unhealthy</Badge>}
      </div>

      {metrics.error && <ErrorBanner error={metrics.error} onRetry={metrics.reload} />}

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
        <StatCard
          label="Queue depth"
          value={formatNumber(m?.queueDepth)}
          icon={Inbox}
          tone={(m?.queueDepth ?? 0) > 10000 ? 'bad' : (m?.queueDepth ?? 0) > 1000 ? 'warn' : 'default'}
          loading={loading}
        />
        <StatCard
          label="Dead letters"
          value={formatNumber(m?.deadLetterCount)}
          icon={AlertOctagon}
          tone={(m?.deadLetterCount ?? 0) > 0 ? 'warn' : 'good'}
          loading={loading}
        />
        <StatCard label="Events / sec" value={formatNumber(m?.eventsPerSecond)} icon={Zap} loading={loading} />
        <StatCard label="Avg ingest latency" value={formatDuration(m?.avgIngestLatencyMs)} icon={Clock} loading={loading} />
        <StatCard
          label="Failed writes"
          value={formatNumber(m?.failedWrites)}
          icon={Database}
          tone={(m?.failedWrites ?? 0) > 0 ? 'bad' : 'good'}
          loading={loading}
        />
        <StatCard
          label="Sanitization failures"
          value={formatNumber(m?.sanitizationFailures)}
          icon={ShieldAlert}
          tone={(m?.sanitizationFailures ?? 0) > 0 ? 'warn' : 'default'}
          loading={loading}
        />
        <StatCard
          label="Auth failures"
          value={formatNumber(m?.authFailures)}
          icon={ShieldAlert}
          tone={(m?.authFailures ?? 0) > 0 ? 'warn' : 'default'}
          loading={loading}
        />
        <StatCard
          label="Database"
          value={m?.dbHealthy === false ? 'Unhealthy' : m?.dbHealthy ? 'Healthy' : '—'}
          icon={HeartPulse}
          tone={m?.dbHealthy === false ? 'bad' : m?.dbHealthy ? 'good' : 'default'}
          loading={loading}
        />
      </div>

      <section>
        <h2 className="mb-2 text-sm font-semibold">Workers</h2>
        {(m?.workers ?? []).length === 0 && !loading ? (
          <p className="text-sm text-gray-500">No worker heartbeats reported.</p>
        ) : (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
            {(m?.workers ?? []).map((w) => (
              <WorkerCard key={w.name} worker={w} />
            ))}
          </div>
        )}
      </section>

      {m?.db && (
        <section>
          <h2 className="mb-2 flex items-center gap-2 text-sm font-semibold">
            <Database className="h-4 w-4 text-gray-400" /> Database health
          </h2>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-6">
            <StatCard label="Database size" value={formatBytes(m.db.databaseSizeMb)} icon={HardDrive} loading={loading} />
            <StatCard
              label="PgBouncer (pooled)"
              value={formatNumber(m.db.pooledConnections?.total ?? 0)}
              sub={`${m.db.pooledConnections?.active ?? 0} active · ${m.db.pooledConnections?.idle ?? 0} idle${(m.db.pooledConnections?.waiting ?? 0) > 0 ? ` · ${m.db.pooledConnections?.waiting} waiting` : ''}`}
              icon={Zap}
              tone={m.db.connectionsTotal > m.db.maxConnections * 0.8 ? 'warn' : 'default'}
              loading={loading}
            />
            <StatCard
              label="Direct connections"
              value={formatNumber(m.db.directConnections?.total ?? 0)}
              sub={`${m.db.directConnections?.active ?? 0} active · ${m.db.directConnections?.idle ?? 0} idle · ${formatNumber(m.db.connectionsTotal)}/${formatNumber(m.db.maxConnections)} total backends`}
              icon={Zap}
              tone={m.db.connectionsTotal > m.db.maxConnections * 0.8 ? 'warn' : 'default'}
              loading={loading}
            />
            <StatCard
              label="Cache hit ratio"
              value={`${m.db.cacheHitRatio.toFixed(1)}%`}
              icon={HeartPulse}
              tone={m.db.cacheHitRatio < 90 ? 'warn' : 'good'}
              loading={loading}
            />
            <StatCard
              label="Longest query"
              value={formatDuration(m.db.longestQuerySeconds * 1000)}
              icon={Clock}
              tone={m.db.longestQuerySeconds > 30 ? 'warn' : 'default'}
              loading={loading}
            />
            <StatCard
              label="Deadlocks"
              value={formatNumber(m.db.deadlocks)}
              icon={AlertOctagon}
              tone={m.db.deadlocks > 0 ? 'warn' : 'good'}
              loading={loading}
            />
            <StatCard
              label="Temp spill"
              value={formatBytes(m.db.tempBytesMb)}
              sub={`${formatNumber(m.db.tempFiles)} temp files · ${formatNumber(m.db.transactionsCommitted)} commits · ${formatNumber(m.db.transactionsRolledBack)} rollbacks`}
              icon={HardDrive}
              tone={m.db.tempBytesMb > 1024 ? 'warn' : 'default'}
              loading={loading}
            />
          </div>
          <div className="card mt-3">
            <h3 className="border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
              Largest tables
            </h3>
            <DataTable
              columns={tableColumns}
              rows={m.db.topTables}
              rowKey={(t) => t.table}
              loading={loading}
              emptyMessage="No table statistics available"
            />
          </div>
        </section>
      )}

      <div className="grid gap-4 xl:grid-cols-2">
        <section className="card">
          <h2 className="flex items-center gap-2 border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
            <HardDrive className="h-4 w-4 text-gray-400" /> Storage per partition
          </h2>
          <DataTable
            columns={storageColumns}
            rows={m?.storage ?? []}
            rowKey={(p) => p.partition}
            loading={loading}
            emptyMessage="No partition statistics available"
          />
        </section>

        <section className="card">
          <h2 className="flex items-center gap-2 border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
            <AlertOctagon className="h-4 w-4 text-gray-400" /> Dead letters
          </h2>
          {deadLetters.error ? (
            <div className="p-3">
              <ErrorBanner error={deadLetters.error} onRetry={deadLetters.reload} />
            </div>
          ) : (
            <DataTable
              columns={dlColumns}
              rows={dlRows}
              rowKey={(d, ) => String(d.id ?? JSON.stringify(d).slice(0, 40))}
              loading={deadLetters.loading && dlRows.length === 0}
              emptyMessage="No quarantined events — ingestion is clean"
            />
          )}
        </section>
      </div>
    </div>
  );
}
