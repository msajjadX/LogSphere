import { useState } from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { api, toApiError } from '../../api/client';
import type { RetentionPolicy } from '../../api/types';
import { EVENT_TYPES, SEVERITIES } from '../../api/types';
import { useApi } from '../../hooks/useApi';
import { useLookups } from '../../context/LookupsContext';
import { DataTable, type Column } from '../../components/DataTable';
import { Badge } from '../../components/Badge';
import { ErrorBanner, Spinner } from '../../components/Feedback';
import { Modal, ConfirmDialog } from '../../components/Modal';
import { CheckboxField, Select, TextField } from '../../components/Select';

interface PolicyForm {
  tenantId: string;
  projectId: string;
  environmentId: string;
  eventType: string;
  severity: string;
  retentionDays: string;
  archiveBeforeDrop: boolean;
  legalHold: boolean;
}

function emptyForm(): PolicyForm {
  return {
    tenantId: '',
    projectId: '',
    environmentId: '',
    eventType: '',
    severity: '',
    retentionDays: '90',
    archiveBeforeDrop: false,
    legalHold: false,
  };
}

export function RetentionTab() {
  const { data, loading, error, reload } = useApi<RetentionPolicy[]>(
    () => api.getList<RetentionPolicy>('/admin/retention-policies'),
    [],
  );
  const { tenants, projects, environments } = useLookups();

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<RetentionPolicy | null>(null);
  const [form, setForm] = useState<PolicyForm>(emptyForm());
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<RetentionPolicy | null>(null);

  const openCreate = () => {
    setEditing(null);
    setForm(emptyForm());
    setFormError(null);
    setModalOpen(true);
  };
  const openEdit = (p: RetentionPolicy) => {
    setEditing(p);
    setForm({
      tenantId: p.tenantId != null ? String(p.tenantId) : '',
      projectId: p.projectId != null ? String(p.projectId) : '',
      environmentId: p.environmentId != null ? String(p.environmentId) : '',
      eventType: p.eventType ?? '',
      severity: p.severity ?? '',
      retentionDays: String(p.retentionDays),
      archiveBeforeDrop: p.archiveBeforeDrop,
      legalHold: p.legalHold,
    });
    setFormError(null);
    setModalOpen(true);
  };

  const save = async () => {
    setBusy(true);
    setFormError(null);
    try {
      const body = {
        tenantId: form.tenantId === '' ? null : (tenants.find((t) => String(t.id) === form.tenantId)?.id ?? form.tenantId),
        projectId: form.projectId === '' ? null : (projects.find((p) => String(p.id) === form.projectId)?.id ?? form.projectId),
        environmentId:
          form.environmentId === ''
            ? null
            : (environments.find((e) => String(e.id) === form.environmentId)?.id ?? form.environmentId),
        eventType: form.eventType || null,
        severity: form.severity || null,
        retentionDays: Number(form.retentionDays),
        archiveBeforeDrop: form.archiveBeforeDrop,
        legalHold: form.legalHold,
      };
      if (editing) await api.put(`/admin/retention-policies/${encodeURIComponent(String(editing.id))}`, body);
      else await api.post('/admin/retention-policies', body);
      setModalOpen(false);
      reload();
    } catch (e) {
      setFormError(toApiError(e).message);
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!deleting) return;
    setBusy(true);
    try {
      await api.del(`/admin/retention-policies/${encodeURIComponent(String(deleting.id))}`);
      setDeleting(null);
      reload();
    } catch (e) {
      setFormError(toApiError(e).message);
      setDeleting(null);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<RetentionPolicy>[] = [
    {
      key: 'scope',
      header: 'Scope',
      render: (p) => (
        <span className="whitespace-nowrap text-xs">
          {p.tenantId != null ? (tenants.find((t) => String(t.id) === String(p.tenantId))?.name ?? `tenant ${p.tenantId}`) : 'All tenants'}
          {p.projectId != null && ` · ${projects.find((x) => String(x.id) === String(p.projectId))?.name ?? p.projectId}`}
          {p.environmentId != null &&
            ` · ${environments.find((e) => String(e.id) === String(p.environmentId))?.name ?? p.environmentId}`}
        </span>
      ),
    },
    { key: 'eventType', header: 'Event type', render: (p) => <span className="text-xs">{p.eventType ?? 'All'}</span> },
    { key: 'severity', header: 'Severity', render: (p) => <span className="text-xs">{p.severity ?? 'All'}</span> },
    {
      key: 'days',
      header: 'Retention',
      render: (p) => <span className="text-xs font-medium tabular-nums">{p.retentionDays} days</span>,
    },
    {
      key: 'archive',
      header: 'Archive',
      render: (p) => <span className="text-xs">{p.archiveBeforeDrop ? 'Before drop' : 'No'}</span>,
    },
    {
      key: 'hold',
      header: 'Legal hold',
      render: (p) => (p.legalHold ? <Badge tone="red">Held</Badge> : <span className="text-xs text-gray-400">—</span>),
    },
    {
      key: 'actions',
      header: '',
      render: (p) => (
        <span className="flex justify-end gap-1.5">
          <button
            type="button"
            className="btn-ghost !p-1.5"
            aria-label="Edit retention policy"
            onClick={(e) => {
              e.stopPropagation();
              openEdit(p);
            }}
          >
            <Pencil className="h-3.5 w-3.5" />
          </button>
          <button
            type="button"
            className="btn-ghost !p-1.5 text-red-500"
            aria-label="Delete retention policy"
            onClick={(e) => {
              e.stopPropagation();
              setDeleting(p);
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
      <div className="flex items-center justify-between gap-3">
        <p className="text-sm text-gray-500">
          Enforced at month granularity via partition drops. Legal hold blocks drops covering the held scope.
        </p>
        <button type="button" className="btn-primary shrink-0" onClick={openCreate}>
          <Plus className="h-4 w-4" /> New policy
        </button>
      </div>
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={data ?? []}
          rowKey={(p) => String(p.id)}
          loading={loading}
          onRowClick={openEdit}
          emptyMessage="No retention policies"
          emptyHint="Platform defaults apply where no policy matches."
        />
      </section>

      <Modal
        open={modalOpen}
        title={editing ? 'Edit retention policy' : 'New retention policy'}
        onClose={() => setModalOpen(false)}
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setModalOpen(false)}>
              Cancel
            </button>
            <button
              type="button"
              className="btn-primary"
              onClick={save}
              disabled={busy || !form.retentionDays || Number(form.retentionDays) <= 0}
            >
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} {editing ? 'Save' : 'Create'}
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {formError && <ErrorBanner error={formError} />}
          <div className="grid grid-cols-2 gap-3">
            <Select
              label="Tenant scope"
              value={form.tenantId}
              placeholder="All tenants"
              options={tenants.map((t) => ({ value: String(t.id), label: t.name }))}
              onChange={(v) => setForm((f) => ({ ...f, tenantId: v }))}
            />
            <Select
              label="Project scope"
              value={form.projectId}
              placeholder="All projects"
              options={projects.map((p) => ({ value: String(p.id), label: p.name }))}
              onChange={(v) => setForm((f) => ({ ...f, projectId: v }))}
            />
            <Select
              label="Environment scope"
              value={form.environmentId}
              placeholder="All environments"
              options={environments.map((e) => ({ value: String(e.id), label: e.name }))}
              onChange={(v) => setForm((f) => ({ ...f, environmentId: v }))}
            />
            <Select
              label="Event type"
              value={form.eventType}
              placeholder="All types"
              options={EVENT_TYPES.map((t) => ({ value: t, label: t }))}
              onChange={(v) => setForm((f) => ({ ...f, eventType: v }))}
            />
            <Select
              label="Severity"
              value={form.severity}
              placeholder="All severities"
              options={SEVERITIES.map((s) => ({ value: s, label: s }))}
              onChange={(v) => setForm((f) => ({ ...f, severity: v }))}
            />
            <TextField
              label="Retention (days)"
              type="number"
              value={form.retentionDays}
              onChange={(v) => setForm((f) => ({ ...f, retentionDays: v }))}
            />
          </div>
          <CheckboxField
            label="Archive to compressed storage before dropping"
            checked={form.archiveBeforeDrop}
            onChange={(v) => setForm((f) => ({ ...f, archiveBeforeDrop: v }))}
          />
          <CheckboxField
            label="Legal hold (blocks all drops in this scope)"
            checked={form.legalHold}
            onChange={(v) => setForm((f) => ({ ...f, legalHold: v }))}
          />
        </div>
      </Modal>

      <ConfirmDialog
        open={deleting !== null}
        title="Delete retention policy"
        message="Delete this retention policy? The platform default retention will apply to its scope."
        confirmLabel="Delete"
        danger
        busy={busy}
        onConfirm={remove}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}
