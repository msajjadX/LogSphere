import { useState } from 'react';
import { Pencil, Plus } from 'lucide-react';
import { api, toApiError } from '../../api/client';
import type { Project } from '../../api/types';
import { useApi } from '../../hooks/useApi';
import { useLookups } from '../../context/LookupsContext';
import { DataTable, type Column } from '../../components/DataTable';
import { Badge } from '../../components/Badge';
import { ErrorBanner, Spinner } from '../../components/Feedback';
import { Modal } from '../../components/Modal';
import { CheckboxField, Select, TextField } from '../../components/Select';

export function ProjectsTab() {
  const { data, loading, error, reload } = useApi<Project[]>(() => api.getList<Project>('/admin/projects'), []);
  const lookups = useLookups();
  const { tenants } = lookups;

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<Project | null>(null);
  const [form, setForm] = useState({ tenantId: '', name: '', code: '', description: '', isActive: true });
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [codeTouched, setCodeTouched] = useState(false);

  const toCodeSuggestion = (name: string) =>
    name.toLowerCase().trim().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  const openCreate = () => {
    setEditing(null);
    setForm({ tenantId: tenants[0] ? String(tenants[0].id) : '', name: '', code: '', description: '', isActive: true });
    setCodeTouched(false);
    setFormError(null);
    setModalOpen(true);
  };
  const openEdit = (p: Project) => {
    setEditing(p);
    setCodeTouched(true);
    setForm({
      tenantId: String(p.tenantId),
      name: p.name,
      code: p.code,
      description: p.description ?? '',
      isActive: p.isActive,
    });
    setFormError(null);
    setModalOpen(true);
  };

  const save = async () => {
    setBusy(true);
    setFormError(null);
    try {
      const body = {
        tenantId: tenants.find((t) => String(t.id) === form.tenantId)?.id ?? form.tenantId,
        name: form.name,
        code: form.code,
        description: form.description || null,
        isActive: form.isActive,
      };
      if (editing) await api.put(`/admin/projects/${encodeURIComponent(String(editing.id))}`, body);
      else await api.post('/admin/projects', body);
      setModalOpen(false);
      reload();
      lookups.reload();
    } catch (e) {
      setFormError(toApiError(e).message);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<Project>[] = [
    { key: 'name', header: 'Name', render: (p) => <span className="text-sm font-medium">{p.name}</span> },
    { key: 'code', header: 'Code', render: (p) => <span className="font-mono text-xs">{p.code}</span> },
    {
      key: 'tenant',
      header: 'Tenant',
      render: (p) => (
        <span className="text-xs">{tenants.find((t) => String(t.id) === String(p.tenantId))?.name ?? String(p.tenantId)}</span>
      ),
    },
    {
      key: 'description',
      header: 'Description',
      render: (p) => <span className="block max-w-md truncate text-xs text-gray-500">{p.description ?? '—'}</span>,
      className: 'w-full',
    },
    {
      key: 'active',
      header: 'Status',
      render: (p) => <Badge tone={p.isActive ? 'green' : 'gray'}>{p.isActive ? 'Active' : 'Inactive'}</Badge>,
    },
    {
      key: 'actions',
      header: '',
      render: (p) => (
        <button
          type="button"
          className="btn-ghost !p-1.5"
          aria-label={`Edit project ${p.name}`}
          onClick={(e) => {
            e.stopPropagation();
            openEdit(p);
          }}
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
      ),
      className: 'text-right',
    },
  ];

  return (
    <div className="space-y-3">
      <div className="flex justify-end">
        <button type="button" className="btn-primary" onClick={openCreate}>
          <Plus className="h-4 w-4" /> New project
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
          emptyMessage="No projects"
        />
      </section>

      <Modal
        open={modalOpen}
        title={editing ? `Edit project — ${editing.name}` : 'New project'}
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
              disabled={busy || !form.name.trim() || !form.code.trim() || !form.tenantId}
            >
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} {editing ? 'Save' : 'Create'}
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {formError && <ErrorBanner error={formError} />}
          <Select
            label="Tenant"
            value={form.tenantId}
            placeholder="Select tenant…"
            options={tenants.map((t) => ({ value: String(t.id), label: t.name }))}
            onChange={(v) => setForm((f) => ({ ...f, tenantId: v }))}
          />
          <TextField
            label="Name"
            value={form.name}
            placeholder="e.g. ETEA Examination Platform"
            onChange={(v) => setForm((f) => ({ ...f, name: v, code: codeTouched ? f.code : toCodeSuggestion(v) }))}
          />
          <TextField
            label="Code"
            value={form.code}
            placeholder="e.g. etea-examination-platform"
            onChange={(v) => {
              setCodeTouched(true);
              setForm((f) => ({ ...f, code: v }));
            }}
            mono
          />
          <div>
            <label htmlFor="proj-desc" className="label">
              Description
            </label>
            <textarea
              id="proj-desc"
              className="input min-h-[4rem]"
              value={form.description}
              onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))}
            />
          </div>
          <CheckboxField label="Active" checked={form.isActive} onChange={(v) => setForm((f) => ({ ...f, isActive: v }))} />
        </div>
      </Modal>
    </div>
  );
}
