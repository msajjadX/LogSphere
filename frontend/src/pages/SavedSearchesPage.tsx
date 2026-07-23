import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Pin, PinOff, Play, Share2, Trash2, Users } from 'lucide-react';
import { api, toApiError } from '../api/client';
import type { SavedSearch } from '../api/types';
import { useApi } from '../hooks/useApi';
import { DataTable, type Column } from '../components/DataTable';
import { Badge } from '../components/Badge';
import { ConfirmDialog } from '../components/Modal';
import { ErrorBanner } from '../components/Feedback';

function describeFilter(f: SavedSearch['filter']): string {
  const parts: string[] = [];
  if (!f) return 'All events';
  if (f.severities?.length) parts.push(`severity: ${f.severities.join('/')}`);
  if (f.eventTypes?.length) parts.push(`types: ${f.eventTypes.join('/')}`);
  if (f.projectId != null) parts.push(`project #${f.projectId}`);
  if (f.correlationId) parts.push(`correlation: ${f.correlationId}`);
  if (f.traceId) parts.push(`trace: ${f.traceId}`);
  if (f.httpRoute) parts.push(`route: ${f.httpRoute}`);
  if (f.text) parts.push(`text: "${f.text}"`);
  return parts.length ? parts.join(' · ') : 'All events';
}

export function SavedSearchesPage() {
  const navigate = useNavigate();
  const { data, loading, error, reload } = useApi<SavedSearch[]>(() => api.getList<SavedSearch>('/searches'), []);
  const [actionError, setActionError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<SavedSearch | null>(null);
  const [busy, setBusy] = useState(false);

  const searches = useMemo(() => {
    const list = [...(data ?? [])];
    list.sort((a, b) => Number(b.pinned) - Number(a.pinned) || a.name.localeCompare(b.name));
    return list;
  }, [data]);

  const run = (s: SavedSearch) => {
    navigate(`/logs?q=${encodeURIComponent(JSON.stringify(s.filter ?? {}))}`);
  };

  const update = async (s: SavedSearch, patch: Partial<SavedSearch>) => {
    setActionError(null);
    try {
      await api.put(`/searches/${encodeURIComponent(String(s.id))}`, { ...s, ...patch });
      reload();
    } catch (e) {
      setActionError(toApiError(e).message);
    }
  };

  const remove = async () => {
    if (!deleting) return;
    setBusy(true);
    try {
      await api.del(`/searches/${encodeURIComponent(String(deleting.id))}`);
      setDeleting(null);
      reload();
    } catch (e) {
      setActionError(toApiError(e).message);
      setDeleting(null);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<SavedSearch>[] = [
    {
      key: 'name',
      header: 'Name',
      render: (s) => (
        <span className="flex items-center gap-1.5 text-sm font-medium">
          {s.pinned && <Pin className="h-3.5 w-3.5 text-indigo-500" />}
          {s.name}
        </span>
      ),
    },
    {
      key: 'filter',
      header: 'Filter',
      render: (s) => <span className="block max-w-2xl truncate text-xs text-gray-500">{describeFilter(s.filter)}</span>,
      className: 'w-full',
    },
    {
      key: 'shared',
      header: 'Visibility',
      render: (s) =>
        s.shared ? (
          <Badge tone="blue">
            <Users className="mr-1 h-3 w-3" /> Shared
          </Badge>
        ) : (
          <Badge tone="gray">Private</Badge>
        ),
    },
    {
      key: 'actions',
      header: '',
      render: (s) => (
        <span className="flex justify-end gap-1.5">
          <button
            type="button"
            className="btn-secondary !px-2 !py-1 text-xs"
            onClick={(e) => {
              e.stopPropagation();
              run(s);
            }}
          >
            <Play className="h-3 w-3" /> Run
          </button>
          <button
            type="button"
            className="btn-ghost !p-1.5"
            aria-label={s.pinned ? `Unpin ${s.name}` : `Pin ${s.name}`}
            title={s.pinned ? 'Unpin' : 'Pin'}
            onClick={(e) => {
              e.stopPropagation();
              void update(s, { pinned: !s.pinned });
            }}
          >
            {s.pinned ? <PinOff className="h-3.5 w-3.5" /> : <Pin className="h-3.5 w-3.5" />}
          </button>
          <button
            type="button"
            className="btn-ghost !p-1.5"
            aria-label={s.shared ? `Make ${s.name} private` : `Share ${s.name}`}
            title={s.shared ? 'Make private' : 'Share with team'}
            onClick={(e) => {
              e.stopPropagation();
              void update(s, { shared: !s.shared });
            }}
          >
            <Share2 className={`h-3.5 w-3.5 ${s.shared ? 'text-blue-500' : ''}`} />
          </button>
          <button
            type="button"
            className="btn-ghost !p-1.5 text-red-500"
            aria-label={`Delete ${s.name}`}
            onClick={(e) => {
              e.stopPropagation();
              setDeleting(s);
            }}
          >
            <Trash2 className="h-3.5 w-3.5" />
          </button>
        </span>
      ),
    },
  ];

  return (
    <div className="space-y-3">
      <p className="text-sm text-gray-500">
        Save filters from the Log Explorer with the “Save search” button. Pinned searches appear first.
      </p>
      {actionError && <ErrorBanner error={actionError} />}
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={searches}
          rowKey={(s) => String(s.id)}
          loading={loading}
          onRowClick={run}
          emptyMessage="No saved searches yet"
          emptyHint="Open the Log Explorer, build a filter, and click Save search."
        />
      </section>

      <ConfirmDialog
        open={deleting !== null}
        title="Delete saved search"
        message={
          <>
            Delete saved search <strong>{deleting?.name}</strong>?
          </>
        }
        confirmLabel="Delete"
        danger
        busy={busy}
        onConfirm={remove}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}
