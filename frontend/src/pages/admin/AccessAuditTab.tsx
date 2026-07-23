import { useMemo } from 'react';
import { api } from '../../api/client';
import { useApi } from '../../hooks/useApi';
import { DataTable, type Column } from '../../components/DataTable';
import { ErrorBanner } from '../../components/Feedback';
import { formatDateTime } from '../../utils/format';

type AccessAuditRow = Record<string, unknown>;

const PREFERRED_ORDER = ['timestamp', 'occurredAt', 'createdAt', 'time', 'username', 'userId', 'user', 'action', 'actionName', 'resource', 'details', 'ip', 'sourceIp'];

function looksLikeDate(key: string, value: unknown): boolean {
  return typeof value === 'string' && /at$|time(stamp)?$/i.test(key) && !isNaN(new Date(value).getTime());
}

/**
 * Read-only dashboard access / export audit trail. The row shape is not
 * pinned by the contract, so columns are derived from the returned objects.
 */
export function AccessAuditTab() {
  const { data, loading, error, reload } = useApi<AccessAuditRow[]>(() => api.getList<AccessAuditRow>('/admin/audit-access'), []);
  const rows = useMemo(() => (Array.isArray(data) ? data : []), [data]);

  const columns = useMemo<Column<AccessAuditRow>[]>(() => {
    const keys = new Set<string>();
    for (const row of rows.slice(0, 25)) for (const k of Object.keys(row)) keys.add(k);
    const ordered = [
      ...PREFERRED_ORDER.filter((k) => keys.has(k)),
      ...[...keys].filter((k) => !PREFERRED_ORDER.includes(k)),
    ].slice(0, 8);
    if (ordered.length === 0) return [{ key: 'raw', header: 'Entry', render: (r) => <span className="font-mono text-xs">{JSON.stringify(r)}</span> }];
    return ordered.map((k) => ({
      key: k,
      header: k.replace(/([A-Z])/g, ' $1').replace(/^./, (c) => c.toUpperCase()),
      render: (r: AccessAuditRow) => {
        const v = r[k];
        if (v === null || v === undefined) return <span className="text-xs text-gray-400">—</span>;
        if (looksLikeDate(k, v)) return <span className="whitespace-nowrap text-xs">{formatDateTime(String(v))}</span>;
        if (typeof v === 'object') return <span className="break-all font-mono text-xs">{JSON.stringify(v)}</span>;
        return <span className="break-all text-xs">{String(v)}</span>;
      },
    }));
  }, [rows]);

  return (
    <div className="space-y-3">
      <p className="text-sm text-gray-500">
        Read-only trail of dashboard access and data exports. Entries are immutable.
      </p>
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={rows}
          rowKey={(r) => String((r as { id?: unknown }).id ?? JSON.stringify(r).slice(0, 60))}
          loading={loading}
          emptyMessage="No access audit entries"
        />
      </section>
    </div>
  );
}
