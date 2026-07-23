import { useState } from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { api, toApiError } from '../../api/client';
import type { RedactionRule } from '../../api/types';
import { useApi } from '../../hooks/useApi';
import { useLookups } from '../../context/LookupsContext';
import { DataTable, type Column } from '../../components/DataTable';
import { Badge } from '../../components/Badge';
import { ErrorBanner, Spinner } from '../../components/Feedback';
import { Modal, ConfirmDialog } from '../../components/Modal';
import { CheckboxField, Select, TextField } from '../../components/Select';

const STRATEGIES = ['Remove', 'Redact', 'MaskLast4', 'Hash'];
const APPLIES_TO = ['All', 'Request', 'Response', 'Properties', 'Headers'];

interface RuleForm {
  tenantId: string;
  projectId: string;
  keyPattern: string;
  isRegex: boolean;
  strategy: string;
  appliesTo: string;
  enabled: boolean;
}

function emptyForm(): RuleForm {
  return { tenantId: '', projectId: '', keyPattern: '', isRegex: false, strategy: 'Redact', appliesTo: 'All', enabled: true };
}

export function RedactionTab() {
  const { data, loading, error, reload } = useApi<RedactionRule[]>(() => api.getList<RedactionRule>('/admin/redaction-rules'), []);
  const { tenants, projects } = useLookups();

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<RedactionRule | null>(null);
  const [form, setForm] = useState<RuleForm>(emptyForm());
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<RedactionRule | null>(null);

  const openCreate = () => {
    setEditing(null);
    setForm(emptyForm());
    setFormError(null);
    setModalOpen(true);
  };
  const openEdit = (r: RedactionRule) => {
    setEditing(r);
    setForm({
      tenantId: r.tenantId != null ? String(r.tenantId) : '',
      projectId: r.projectId != null ? String(r.projectId) : '',
      keyPattern: r.keyPattern,
      isRegex: r.isRegex,
      strategy: String(r.strategy),
      appliesTo: String(r.appliesTo),
      enabled: r.enabled,
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
        keyPattern: form.keyPattern,
        isRegex: form.isRegex,
        strategy: form.strategy,
        appliesTo: form.appliesTo,
        enabled: form.enabled,
      };
      if (editing) await api.put(`/admin/redaction-rules/${encodeURIComponent(String(editing.id))}`, body);
      else await api.post('/admin/redaction-rules', body);
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
      await api.del(`/admin/redaction-rules/${encodeURIComponent(String(deleting.id))}`);
      setDeleting(null);
      reload();
    } catch (e) {
      setFormError(toApiError(e).message);
      setDeleting(null);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<RedactionRule>[] = [
    { key: 'pattern', header: 'Key pattern', render: (r) => <span className="font-mono text-xs">{r.keyPattern}</span> },
    { key: 'regex', header: 'Regex', render: (r) => <span className="text-xs">{r.isRegex ? 'Yes' : 'No'}</span> },
    { key: 'strategy', header: 'Strategy', render: (r) => <Badge tone="purple">{String(r.strategy)}</Badge> },
    { key: 'appliesTo', header: 'Applies to', render: (r) => <span className="text-xs">{String(r.appliesTo)}</span> },
    {
      key: 'scope',
      header: 'Scope',
      render: (r) => (
        <span className="whitespace-nowrap text-xs">
          {r.tenantId != null ? (tenants.find((t) => String(t.id) === String(r.tenantId))?.name ?? `tenant ${r.tenantId}`) : 'Global'}
          {r.projectId != null && ` · ${projects.find((p) => String(p.id) === String(r.projectId))?.name ?? r.projectId}`}
        </span>
      ),
    },
    {
      key: 'enabled',
      header: 'Enabled',
      render: (r) => <Badge tone={r.enabled ? 'green' : 'gray'}>{r.enabled ? 'Enabled' : 'Disabled'}</Badge>,
    },
    {
      key: 'actions',
      header: '',
      render: (r) => (
        <span className="flex justify-end gap-1.5">
          <button
            type="button"
            className="btn-ghost !p-1.5"
            aria-label="Edit redaction rule"
            onClick={(e) => {
              e.stopPropagation();
              openEdit(r);
            }}
          >
            <Pencil className="h-3.5 w-3.5" />
          </button>
          <button
            type="button"
            className="btn-ghost !p-1.5 text-red-500"
            aria-label="Delete redaction rule"
            onClick={(e) => {
              e.stopPropagation();
              setDeleting(r);
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
          Rules are applied server-side on ingestion (in addition to platform defaults). Values are never recoverable.
        </p>
        <button type="button" className="btn-primary shrink-0" onClick={openCreate}>
          <Plus className="h-4 w-4" /> New rule
        </button>
      </div>
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={data ?? []}
          rowKey={(r) => String(r.id)}
          loading={loading}
          onRowClick={openEdit}
          emptyMessage="No custom redaction rules"
          emptyHint="Platform default rules (passwords, tokens, cards …) always apply."
        />
      </section>

      <Modal
        open={modalOpen}
        title={editing ? 'Edit redaction rule' : 'New redaction rule'}
        onClose={() => setModalOpen(false)}
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setModalOpen(false)}>
              Cancel
            </button>
            <button type="button" className="btn-primary" onClick={save} disabled={busy || !form.keyPattern.trim()}>
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} {editing ? 'Save' : 'Create'}
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {formError && <ErrorBanner error={formError} />}
          <TextField
            label="Key pattern"
            value={form.keyPattern}
            onChange={(v) => setForm((f) => ({ ...f, keyPattern: v }))}
            mono
            placeholder={form.isRegex ? '^card(Number|Cvv)$' : 'cardNumber'}
            autoFocus
          />
          <CheckboxField label="Pattern is a regular expression" checked={form.isRegex} onChange={(v) => setForm((f) => ({ ...f, isRegex: v }))} />
          <div className="grid grid-cols-2 gap-3">
            <Select
              label="Strategy"
              value={form.strategy}
              options={STRATEGIES.map((s) => ({ value: s, label: s }))}
              onChange={(v) => setForm((f) => ({ ...f, strategy: v }))}
            />
            <Select
              label="Applies to"
              value={form.appliesTo}
              options={APPLIES_TO.map((s) => ({ value: s, label: s }))}
              onChange={(v) => setForm((f) => ({ ...f, appliesTo: v }))}
            />
            <Select
              label="Tenant scope"
              value={form.tenantId}
              placeholder="Global"
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
          </div>
          <CheckboxField label="Enabled" checked={form.enabled} onChange={(v) => setForm((f) => ({ ...f, enabled: v }))} />
        </div>
      </Modal>

      <ConfirmDialog
        open={deleting !== null}
        title="Delete redaction rule"
        message={
          <>
            Delete the rule for <strong className="font-mono">{deleting?.keyPattern}</strong>? New events will no longer
            be redacted by it.
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
