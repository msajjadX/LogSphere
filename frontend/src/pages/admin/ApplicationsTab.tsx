import { useState } from 'react';
import { AlertTriangle, KeyRound, Pencil, Plus, ShieldOff } from 'lucide-react';
import { api, toApiError } from '../../api/client';
import type { ApiCredential, Application, CreateCredentialResponse } from '../../api/types';
import { useApi } from '../../hooks/useApi';
import { useLookups } from '../../context/LookupsContext';
import { DataTable, type Column } from '../../components/DataTable';
import { Badge } from '../../components/Badge';
import { ErrorBanner, Spinner } from '../../components/Feedback';
import { Modal, ConfirmDialog } from '../../components/Modal';
import { Drawer } from '../../components/Drawer';
import { CheckboxField, Select, TextField } from '../../components/Select';
import { CopyButton } from '../../components/CopyButton';
import { formatDateTime, relativeTime } from '../../utils/format';

function credentialActive(c: ApiCredential): boolean {
  if (c.revoked === true || c.revokedAt) return false;
  if (c.isActive === false) return false;
  if (c.expiresAt && new Date(c.expiresAt).getTime() < Date.now()) return false;
  return true;
}

function CredentialsDrawer({ app, onClose }: { app: Application | null; onClose: () => void }) {
  const { environments } = useLookups();
  const { data, loading, error, reload } = useApi<ApiCredential[]>(
    () => api.getList<ApiCredential>(`/admin/applications/${encodeURIComponent(String(app?.id))}/credentials`),
    [app?.id],
    app !== null,
  );

  const [createOpen, setCreateOpen] = useState(false);
  const [form, setForm] = useState({ environmentId: '', expiresAt: '', ipAllowlist: '' });
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [plaintextKey, setPlaintextKey] = useState<string | null>(null);
  const [revoking, setRevoking] = useState<ApiCredential | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const create = async () => {
    setBusy(true);
    setFormError(null);
    try {
      const body: Record<string, unknown> = {
        environmentId: environments.find((e) => String(e.id) === form.environmentId)?.id ?? form.environmentId,
      };
      if (form.expiresAt) body.expiresAt = new Date(form.expiresAt).toISOString();
      if (form.ipAllowlist.trim()) body.ipAllowlist = form.ipAllowlist.trim();
      const res = await api.post<CreateCredentialResponse>(
        `/admin/applications/${encodeURIComponent(String(app?.id))}/credentials`,
        body,
      );
      const key = res.key ?? res.apiKey ?? res.plaintextKey ?? null;
      setCreateOpen(false);
      setForm({ environmentId: '', expiresAt: '', ipAllowlist: '' });
      setPlaintextKey(key);
      reload();
    } catch (e) {
      setFormError(toApiError(e).message);
    } finally {
      setBusy(false);
    }
  };

  const revoke = async () => {
    if (!revoking) return;
    setBusy(true);
    setActionError(null);
    try {
      await api.post(`/admin/credentials/${encodeURIComponent(String(revoking.id))}/revoke`);
      setRevoking(null);
      reload();
    } catch (e) {
      setActionError(toApiError(e).message);
      setRevoking(null);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<ApiCredential>[] = [
    { key: 'keyId', header: 'Key ID', render: (c) => <span className="font-mono text-xs">{c.keyId ?? String(c.id)}</span> },
    {
      key: 'env',
      header: 'Environment',
      render: (c) => (
        <span className="text-xs">
          {c.environmentName ?? environments.find((e) => String(e.id) === String(c.environmentId))?.name ?? String(c.environmentId ?? '—')}
        </span>
      ),
    },
    {
      key: 'created',
      header: 'Created',
      render: (c) => <span className="whitespace-nowrap text-xs">{c.createdAt ? formatDateTime(c.createdAt) : '—'}</span>,
    },
    {
      key: 'expires',
      header: 'Expires',
      render: (c) => <span className="whitespace-nowrap text-xs">{c.expiresAt ? formatDateTime(c.expiresAt) : 'Never'}</span>,
    },
    {
      key: 'lastUsed',
      header: 'Last used',
      render: (c) => <span className="whitespace-nowrap text-xs">{c.lastUsedAt ? relativeTime(c.lastUsedAt) : '—'}</span>,
    },
    {
      key: 'status',
      header: 'Status',
      render: (c) => <Badge tone={credentialActive(c) ? 'green' : 'red'}>{credentialActive(c) ? 'Active' : 'Revoked / Expired'}</Badge>,
    },
    {
      key: 'actions',
      header: '',
      render: (c) =>
        credentialActive(c) ? (
          <button
            type="button"
            className="btn-secondary !px-2 !py-1 text-xs text-red-600 dark:text-red-400"
            onClick={() => setRevoking(c)}
          >
            <ShieldOff className="h-3 w-3" /> Revoke
          </button>
        ) : null,
      className: 'text-right',
    },
  ];

  return (
    <Drawer
      open={app !== null}
      onClose={onClose}
      title={`API credentials — ${app?.name ?? ''}`}
      widthClass="max-w-3xl"
      actions={
        <button type="button" className="btn-primary !px-2.5 !py-1 text-xs" onClick={() => setCreateOpen(true)}>
          <Plus className="h-3.5 w-3.5" /> New credential
        </button>
      }
    >
      {actionError && (
        <div className="mb-3">
          <ErrorBanner error={actionError} />
        </div>
      )}
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <DataTable
        columns={columns}
        rows={data ?? []}
        rowKey={(c) => String(c.id)}
        loading={loading}
        emptyMessage="No credentials for this application"
        emptyHint="Create a credential to allow this application to ingest events."
      />

      {/* create modal */}
      <Modal
        open={createOpen}
        title="New ingestion credential"
        onClose={() => setCreateOpen(false)}
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setCreateOpen(false)}>
              Cancel
            </button>
            <button type="button" className="btn-primary" onClick={create} disabled={busy || !form.environmentId}>
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} Create credential
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {formError && <ErrorBanner error={formError} />}
          <Select
            label="Environment"
            value={form.environmentId}
            placeholder="Select environment…"
            options={environments.map((e) => ({ value: String(e.id), label: e.name }))}
            onChange={(v) => setForm((f) => ({ ...f, environmentId: v }))}
          />
          <TextField
            label="Expires at (optional)"
            type="datetime-local"
            value={form.expiresAt}
            onChange={(v) => setForm((f) => ({ ...f, expiresAt: v }))}
          />
          <TextField
            label="IP allowlist (optional, comma-separated)"
            value={form.ipAllowlist}
            onChange={(v) => setForm((f) => ({ ...f, ipAllowlist: v }))}
            placeholder="10.0.0.0/8, 192.168.1.10"
            mono
          />
        </div>
      </Modal>

      {/* plaintext key — shown exactly once */}
      <Modal
        open={plaintextKey !== null}
        title="Credential created"
        onClose={() => setPlaintextKey(null)}
        footer={
          <button type="button" className="btn-primary" onClick={() => setPlaintextKey(null)}>
            I have stored the key
          </button>
        }
      >
        <div className="space-y-3">
          <div className="flex items-start gap-2 rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-800 dark:border-amber-800 dark:bg-amber-950/50 dark:text-amber-300">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
            <p>
              This API key is shown <strong>only once</strong>. Copy it now and store it securely — LogSphere keeps only
              a hash and cannot recover it.
            </p>
          </div>
          {plaintextKey ? (
            <div className="flex items-center gap-2">
              <code className="min-w-0 flex-1 break-all rounded-md bg-gray-100 p-2.5 font-mono text-xs dark:bg-gray-950">
                {plaintextKey}
              </code>
              <CopyButton text={plaintextKey} label="Copy key" />
            </div>
          ) : (
            <p className="text-sm text-gray-500">
              The server did not return a plaintext key in the response. Check the server logs / seeder output.
            </p>
          )}
        </div>
      </Modal>

      <ConfirmDialog
        open={revoking !== null}
        title="Revoke credential"
        message={
          <>
            Revoke credential <strong className="font-mono">{revoking?.keyId ?? String(revoking?.id ?? '')}</strong>?
            Applications using it will immediately stop being able to ingest events. This cannot be undone.
          </>
        }
        confirmLabel="Revoke"
        danger
        busy={busy}
        onConfirm={revoke}
        onCancel={() => setRevoking(null)}
      />
    </Drawer>
  );
}

/** Turns a display name into a code suggestion: lowercase, spaces/symbols → dashes. */
function toCodeSuggestion(name: string): string {
  return name
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

export function ApplicationsTab() {
  const { data, loading, error, reload } = useApi<Application[]>(() => api.getList<Application>('/admin/applications'), []);
  const lookups = useLookups();
  const { projects, tenants } = lookups;

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<Application | null>(null);
  const [form, setForm] = useState({ projectId: '', name: '', code: '', isActive: true });
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [credentialsApp, setCredentialsApp] = useState<Application | null>(null);
  const [projectSearch, setProjectSearch] = useState('');
  const [codeTouched, setCodeTouched] = useState(false);

  const openCreate = () => {
    setEditing(null);
    setForm({ projectId: '', name: '', code: '', isActive: true });
    setProjectSearch('');
    setCodeTouched(false);
    setFormError(null);
    setModalOpen(true);
  };
  const openEdit = (a: Application) => {
    setEditing(a);
    setForm({ projectId: String(a.projectId), name: a.name, code: a.code, isActive: a.isActive });
    setProjectSearch('');
    setCodeTouched(true);
    setFormError(null);
    setModalOpen(true);
  };

  const selectedProject = projects.find((p) => String(p.id) === form.projectId) ?? null;
  const selectedTenant = selectedProject
    ? (tenants.find((t) => String(t.id) === String(selectedProject.tenantId)) ?? null)
    : null;
  const filteredProjects = projects.filter((p) => {
    const q = projectSearch.trim().toLowerCase();
    if (!q) return true;
    const tenant = tenants.find((t) => String(t.id) === String(p.tenantId));
    return (
      p.name.toLowerCase().includes(q) ||
      p.code.toLowerCase().includes(q) ||
      (tenant?.name.toLowerCase().includes(q) ?? false)
    );
  });

  const save = async () => {
    setBusy(true);
    setFormError(null);
    try {
      const body = {
        projectId: projects.find((p) => String(p.id) === form.projectId)?.id ?? form.projectId,
        name: form.name,
        code: form.code,
        isActive: form.isActive,
      };
      if (editing) await api.put(`/admin/applications/${encodeURIComponent(String(editing.id))}`, body);
      else await api.post('/admin/applications', body);
      setModalOpen(false);
      reload();
      lookups.reload();
    } catch (e) {
      setFormError(toApiError(e).message);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<Application>[] = [
    { key: 'name', header: 'Name', render: (a) => <span className="text-sm font-medium">{a.name}</span> },
    { key: 'code', header: 'Code', render: (a) => <span className="font-mono text-xs">{a.code}</span> },
    {
      key: 'project',
      header: 'Project',
      render: (a) => (
        <span className="text-xs">
          {a.projectName ?? projects.find((p) => String(p.id) === String(a.projectId))?.name ?? String(a.projectId)}
        </span>
      ),
    },
    {
      key: 'active',
      header: 'Status',
      render: (a) => <Badge tone={a.isActive ? 'green' : 'gray'}>{a.isActive ? 'Active' : 'Inactive'}</Badge>,
    },
    {
      key: 'actions',
      header: '',
      render: (a) => (
        <span className="flex justify-end gap-1.5">
          <button
            type="button"
            className="btn-secondary !px-2 !py-1 text-xs"
            onClick={(e) => {
              e.stopPropagation();
              setCredentialsApp(a);
            }}
          >
            <KeyRound className="h-3 w-3" /> Credentials
          </button>
          <button
            type="button"
            className="btn-ghost !p-1.5"
            aria-label={`Edit application ${a.name}`}
            onClick={(e) => {
              e.stopPropagation();
              openEdit(a);
            }}
          >
            <Pencil className="h-3.5 w-3.5" />
          </button>
        </span>
      ),
    },
  ];

  return (
    <div className="space-y-3">
      <div className="flex justify-end">
        <button type="button" className="btn-primary" onClick={openCreate}>
          <Plus className="h-4 w-4" /> New application
        </button>
      </div>
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={data ?? []}
          rowKey={(a) => String(a.id)}
          loading={loading}
          onRowClick={openEdit}
          emptyMessage="No applications"
        />
      </section>

      <Modal
        open={modalOpen}
        title={editing ? `Edit application — ${editing.name}` : 'New application'}
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
              disabled={busy || !form.name.trim() || !form.code.trim() || !form.projectId}
            >
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} {editing ? 'Save' : 'Create'}
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {formError && <ErrorBanner error={formError} />}

          <div>
            <TextField
              label="Project"
              value={projectSearch}
              onChange={setProjectSearch}
              placeholder="Search project by name, code or tenant…"
            />
            {(projectSearch.trim() || !form.projectId) && (
              <ul
                className="mt-1 max-h-44 overflow-y-auto rounded-md border border-gray-200 dark:border-gray-700"
                role="listbox"
                aria-label="Matching projects"
              >
                {filteredProjects.length === 0 && (
                  <li className="px-3 py-2 text-xs text-gray-500">No projects match your search</li>
                )}
                {filteredProjects.slice(0, 20).map((p) => {
                  const tenant = tenants.find((t) => String(t.id) === String(p.tenantId));
                  const selected = String(p.id) === form.projectId;
                  return (
                    <li key={String(p.id)}>
                      <button
                        type="button"
                        role="option"
                        aria-selected={selected}
                        className={`flex w-full items-center justify-between gap-2 px-3 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800/60 ${
                          selected ? 'bg-indigo-50 dark:bg-indigo-950/40' : ''
                        }`}
                        onClick={() => {
                          setForm((f) => ({ ...f, projectId: String(p.id) }));
                          setProjectSearch('');
                        }}
                      >
                        <span className="min-w-0 truncate">
                          {p.name} <span className="font-mono text-xs text-gray-500">({p.code})</span>
                        </span>
                        <span className="shrink-0 text-xs text-gray-400">{tenant?.name ?? ''}</span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          {selectedProject && (
            <div className="rounded-md border border-indigo-200 bg-indigo-50/60 p-3 text-xs dark:border-indigo-900 dark:bg-indigo-950/30">
              <div className="mb-1 font-semibold uppercase tracking-wide text-indigo-500 dark:text-indigo-400">
                Selected project
              </div>
              <dl className="grid grid-cols-2 gap-x-4 gap-y-1">
                <div>
                  <dt className="text-gray-500">Project</dt>
                  <dd className="font-medium text-gray-800 dark:text-gray-200">
                    {selectedProject.name} <span className="font-mono text-[11px] text-gray-500">({selectedProject.code})</span>
                  </dd>
                </div>
                <div>
                  <dt className="text-gray-500">Tenant</dt>
                  <dd className="font-medium text-gray-800 dark:text-gray-200">
                    {selectedTenant ? (
                      <>
                        {selectedTenant.name}{' '}
                        <span className="font-mono text-[11px] text-gray-500">({selectedTenant.code})</span>
                      </>
                    ) : (
                      '—'
                    )}
                  </dd>
                </div>
                {selectedProject.description && (
                  <div className="col-span-2">
                    <dt className="text-gray-500">Description</dt>
                    <dd className="text-gray-700 dark:text-gray-300">{selectedProject.description}</dd>
                  </div>
                )}
              </dl>
            </div>
          )}

          <TextField
            label="Name"
            value={form.name}
            placeholder="e.g. Billing API"
            onChange={(v) =>
              setForm((f) => ({ ...f, name: v, code: codeTouched ? f.code : toCodeSuggestion(v) }))
            }
          />
          <TextField
            label="Code"
            value={form.code}
            placeholder="e.g. billing-api"
            onChange={(v) => {
              setCodeTouched(true);
              setForm((f) => ({ ...f, code: v }));
            }}
            mono
          />
          <CheckboxField label="Active" checked={form.isActive} onChange={(v) => setForm((f) => ({ ...f, isActive: v }))} />
        </div>
      </Modal>

      <CredentialsDrawer app={credentialsApp} onClose={() => setCredentialsApp(null)} />
    </div>
  );
}
