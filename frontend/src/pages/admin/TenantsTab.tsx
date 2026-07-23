import { useState } from 'react';
import { Pencil, Plus } from 'lucide-react';
import { api, toApiError } from '../../api/client';
import type { Tenant } from '../../api/types';
import { useApi } from '../../hooks/useApi';
import { useLookups } from '../../context/LookupsContext';
import { useAuth } from '../../context/AuthContext';
import { DataTable, type Column } from '../../components/DataTable';
import { Badge } from '../../components/Badge';
import { ErrorBanner, Spinner } from '../../components/Feedback';
import { Modal } from '../../components/Modal';
import { CheckboxField, TextField } from '../../components/Select';

export function TenantsTab() {
  const { data, loading, error, reload } = useApi<Tenant[]>(() => api.getList<Tenant>('/admin/tenants'), []);
  const lookups = useLookups();
  const { isSuperAdmin } = useAuth();

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<Tenant | null>(null);
  const [form, setForm] = useState({ name: '', code: '', isActive: true });
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [codeTouched, setCodeTouched] = useState(false);

  const toCodeSuggestion = (name: string) =>
    name.toLowerCase().trim().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  const openCreate = () => {
    setEditing(null);
    setForm({ name: '', code: '', isActive: true });
    setCodeTouched(false);
    setFormError(null);
    setModalOpen(true);
  };
  const openEdit = (t: Tenant) => {
    setEditing(t);
    setForm({ name: t.name, code: t.code, isActive: t.isActive });
    setCodeTouched(true);
    setFormError(null);
    setModalOpen(true);
  };

  const save = async () => {
    setBusy(true);
    setFormError(null);
    try {
      if (editing) await api.put(`/admin/tenants/${encodeURIComponent(String(editing.id))}`, form);
      else await api.post('/admin/tenants', form);
      setModalOpen(false);
      reload();
      lookups.reload();
    } catch (e) {
      setFormError(toApiError(e).message);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<Tenant>[] = [
    { key: 'name', header: 'Name', render: (t) => <span className="text-sm font-medium">{t.name}</span> },
    { key: 'code', header: 'Code', render: (t) => <span className="font-mono text-xs">{t.code}</span> },
    {
      key: 'active',
      header: 'Status',
      render: (t) => <Badge tone={t.isActive ? 'green' : 'gray'}>{t.isActive ? 'Active' : 'Inactive'}</Badge>,
    },
    {
      key: 'actions',
      header: '',
      render: (t) => (
        <button
          type="button"
          className="btn-ghost !p-1.5"
          aria-label={`Edit tenant ${t.name}`}
          onClick={(e) => {
            e.stopPropagation();
            openEdit(t);
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
      {isSuperAdmin && (
        <div className="flex justify-end">
          <button type="button" className="btn-primary" onClick={openCreate}>
            <Plus className="h-4 w-4" /> New tenant
          </button>
        </div>
      )}
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={data ?? []}
          rowKey={(t) => String(t.id)}
          loading={loading}
          onRowClick={openEdit}
          emptyMessage="No tenants"
        />
      </section>

      <Modal
        open={modalOpen}
        title={editing ? `Edit tenant — ${editing.name}` : 'New tenant'}
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
              disabled={busy || !form.name.trim() || !form.code.trim()}
            >
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} {editing ? 'Save' : 'Create'}
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {formError && <ErrorBanner error={formError} />}
          <TextField
            label="Name"
            value={form.name}
            placeholder="e.g. Our Company"
            onChange={(v) => setForm((f) => ({ ...f, name: v, code: codeTouched ? f.code : toCodeSuggestion(v) }))}
            autoFocus
          />
          <TextField
            label="Code"
            value={form.code}
            placeholder="e.g. our-company"
            onChange={(v) => {
              setCodeTouched(true);
              setForm((f) => ({ ...f, code: v }));
            }}
            mono
          />
          <CheckboxField label="Active" checked={form.isActive} onChange={(v) => setForm((f) => ({ ...f, isActive: v }))} />
        </div>
      </Modal>
    </div>
  );
}
