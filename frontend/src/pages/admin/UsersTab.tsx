import { useState } from 'react';
import { KeyRound, Pencil, Plus, Trash2 } from 'lucide-react';
import { api, toApiError } from '../../api/client';
import type { AdminUser, UserGrant } from '../../api/types';
import { ROLES, SEVERITIES } from '../../api/types';
import { useApi } from '../../hooks/useApi';
import { useLookups } from '../../context/LookupsContext';
import { useAuth } from '../../context/AuthContext';
import { DataTable, type Column } from '../../components/DataTable';
import { Badge } from '../../components/Badge';
import { ErrorBanner, Spinner } from '../../components/Feedback';
import { Modal } from '../../components/Modal';
import { CheckboxField, Select, TextField } from '../../components/Select';

interface GrantDraft {
  role: string;
  tenantId: string;
  projectId: string;
  environmentId: string;
  minSeverity: string;
  canViewBodies: boolean;
  canExport: boolean;
  canViewSecurity: boolean;
}

function emptyGrant(): GrantDraft {
  return {
    role: 'ReadOnly',
    tenantId: '',
    projectId: '',
    environmentId: '',
    minSeverity: '',
    canViewBodies: false,
    canExport: false,
    canViewSecurity: false,
  };
}

function grantToDraft(g: UserGrant): GrantDraft {
  return {
    role: g.role,
    tenantId: g.tenantId != null ? String(g.tenantId) : '',
    projectId: g.projectId != null ? String(g.projectId) : '',
    environmentId: g.environmentId != null ? String(g.environmentId) : '',
    minSeverity: g.minSeverity ?? '',
    canViewBodies: Boolean(g.canViewBodies),
    canExport: Boolean(g.canExport),
    canViewSecurity: Boolean(g.canViewSecurity),
  };
}

export function UsersTab() {
  const { data, loading, error, reload } = useApi<AdminUser[]>(() => api.getList<AdminUser>('/admin/users'), []);
  const { tenants, projects, environments } = useLookups();
  const { isSuperAdmin } = useAuth();

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<AdminUser | null>(null);
  const [form, setForm] = useState({ username: '', displayName: '', email: '', password: '', isActive: true });
  const [grants, setGrants] = useState<GrantDraft[]>([]);
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  // reset password
  const [resetUser, setResetUser] = useState<AdminUser | null>(null);
  const [newPassword, setNewPassword] = useState('');
  const [resetError, setResetError] = useState<string | null>(null);
  const [resetDone, setResetDone] = useState(false);
  /** Set only when the server generated the password: it is the one copy anyone will ever see. */
  const [generatedPassword, setGeneratedPassword] = useState<string | null>(null);

  const openCreate = () => {
    setEditing(null);
    setForm({ username: '', displayName: '', email: '', password: '', isActive: true });
    setGrants([emptyGrant()]);
    setFormError(null);
    setModalOpen(true);
  };
  const openEdit = (u: AdminUser) => {
    setEditing(u);
    setForm({ username: u.username, displayName: u.displayName, email: u.email ?? '', password: '', isActive: u.isActive });
    setGrants((u.grants ?? []).map(grantToDraft));
    setFormError(null);
    setModalOpen(true);
  };

  const draftToGrant = (d: GrantDraft): UserGrant => ({
    role: d.role,
    tenantId: d.tenantId === '' ? null : (tenants.find((t) => String(t.id) === d.tenantId)?.id ?? d.tenantId),
    projectId: d.projectId === '' ? null : (projects.find((p) => String(p.id) === d.projectId)?.id ?? d.projectId),
    environmentId:
      d.environmentId === '' ? null : (environments.find((e) => String(e.id) === d.environmentId)?.id ?? d.environmentId),
    minSeverity: d.minSeverity || null,
    canViewBodies: d.canViewBodies,
    canExport: d.canExport,
    canViewSecurity: d.canViewSecurity,
  });

  const save = async () => {
    setBusy(true);
    setFormError(null);
    try {
      const body: Record<string, unknown> = {
        username: form.username,
        displayName: form.displayName,
        email: form.email || null,
        isActive: form.isActive,
        grants: grants.map(draftToGrant),
      };
      if (!editing && form.password) body.password = form.password;
      if (editing) await api.put(`/admin/users/${encodeURIComponent(String(editing.id))}`, body);
      else await api.post('/admin/users', body);
      setModalOpen(false);
      reload();
    } catch (e) {
      setFormError(toApiError(e).message);
    } finally {
      setBusy(false);
    }
  };

  const resetPassword = async () => {
    if (!resetUser) return;
    setBusy(true);
    setResetError(null);
    try {
      const result = await api.post<{ temporaryPassword: string; generated: boolean }>(
        `/admin/users/${encodeURIComponent(String(resetUser.id))}/reset-password`,
        { newPassword: newPassword.trim() ? newPassword : null },
      );
      // Nothing else in the system can ever show this value again, so keep it on screen
      // rather than telling the admin to "share the new password" they were never given.
      setGeneratedPassword(result.generated ? result.temporaryPassword : null);
      setResetDone(true);
    } catch (e) {
      setResetError(toApiError(e).message);
    } finally {
      setBusy(false);
    }
  };

  const setGrant = (i: number, patch: Partial<GrantDraft>) =>
    setGrants((gs) => gs.map((g, idx) => (idx === i ? { ...g, ...patch } : g)));

  const columns: Column<AdminUser>[] = [
    {
      key: 'user',
      header: 'User',
      render: (u) => (
        <div>
          <p className="text-sm font-medium">{u.displayName || u.username}</p>
          <p className="font-mono text-xs text-gray-500">{u.username}</p>
        </div>
      ),
    },
    {
      key: 'roles',
      header: 'Roles',
      render: (u) => (
        <span className="flex flex-wrap gap-1">
          {[...new Set([...(u.roles ?? []), ...(u.grants ?? []).map((g) => g.role)])].map((r) => (
            <Badge key={r} tone={r === 'SuperAdmin' ? 'purple' : 'indigo'}>
              {r}
            </Badge>
          ))}
        </span>
      ),
    },
    {
      key: 'grants',
      header: 'Grants',
      render: (u) => <span className="text-xs tabular-nums">{(u.grants ?? []).length}</span>,
    },
    {
      key: 'active',
      header: 'Status',
      render: (u) => <Badge tone={u.isActive ? 'green' : 'gray'}>{u.isActive ? 'Active' : 'Disabled'}</Badge>,
    },
    {
      key: 'actions',
      header: '',
      render: (u) =>
        isSuperAdmin ? (
          <span className="flex justify-end gap-1.5">
            <button
              type="button"
              className="btn-secondary !px-2 !py-1 text-xs"
              onClick={(e) => {
                e.stopPropagation();
                setResetUser(u);
                setNewPassword('');
                setResetDone(false);
                setResetError(null);
                setGeneratedPassword(null);
              }}
            >
              <KeyRound className="h-3 w-3" /> Reset password
            </button>
            <button
              type="button"
              className="btn-ghost !p-1.5"
              aria-label={`Edit user ${u.username}`}
              onClick={(e) => {
                e.stopPropagation();
                openEdit(u);
              }}
            >
              <Pencil className="h-3.5 w-3.5" />
            </button>
          </span>
        ) : null,
    },
  ];

  return (
    <div className="space-y-3">
      {isSuperAdmin && (
        <div className="flex justify-end">
          <button type="button" className="btn-primary" onClick={openCreate}>
            <Plus className="h-4 w-4" /> New user
          </button>
        </div>
      )}
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={data ?? []}
          rowKey={(u) => String(u.id)}
          loading={loading}
          onRowClick={isSuperAdmin ? openEdit : undefined}
          emptyMessage="No users"
        />
      </section>

      {/* create / edit modal */}
      <Modal
        open={modalOpen}
        title={editing ? `Edit user — ${editing.username}` : 'New user'}
        onClose={() => setModalOpen(false)}
        widthClass="max-w-3xl"
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setModalOpen(false)}>
              Cancel
            </button>
            <button
              type="button"
              className="btn-primary"
              onClick={save}
              disabled={busy || !form.username.trim() || (!editing && !form.password)}
            >
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} {editing ? 'Save' : 'Create user'}
            </button>
          </>
        }
      >
        <div className="space-y-4">
          {formError && <ErrorBanner error={formError} />}
          <div className="grid grid-cols-2 gap-3">
            <TextField label="Username" value={form.username} onChange={(v) => setForm((f) => ({ ...f, username: v }))} />
            <TextField label="Display name" value={form.displayName} onChange={(v) => setForm((f) => ({ ...f, displayName: v }))} />
            <TextField label="Email" type="email" value={form.email} onChange={(v) => setForm((f) => ({ ...f, email: v }))} />
            {!editing && (
              <TextField label="Initial password" type="password" value={form.password} onChange={(v) => setForm((f) => ({ ...f, password: v }))} />
            )}
          </div>
          <CheckboxField label="Account active" checked={form.isActive} onChange={(v) => setForm((f) => ({ ...f, isActive: v }))} />

          {/* grants editor */}
          <div>
            <div className="mb-2 flex items-center justify-between">
              <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500">Access grants</h3>
              <button type="button" className="btn-secondary !px-2 !py-1 text-xs" onClick={() => setGrants((g) => [...g, emptyGrant()])}>
                <Plus className="h-3 w-3" /> Add grant
              </button>
            </div>
            {grants.length === 0 && <p className="text-sm text-gray-500">No grants — the user cannot see any data.</p>}
            <div className="space-y-3">
              {grants.map((g, i) => (
                <div key={i} className="rounded-md border border-gray-200 p-3 dark:border-gray-800">
                  <div className="grid grid-cols-2 gap-2.5 md:grid-cols-5">
                    <Select
                      label="Role"
                      value={g.role}
                      options={ROLES.map((r) => ({ value: r, label: r }))}
                      onChange={(v) => setGrant(i, { role: v })}
                    />
                    <Select
                      label="Tenant"
                      value={g.tenantId}
                      placeholder="All tenants"
                      options={tenants.map((t) => ({ value: String(t.id), label: t.name }))}
                      onChange={(v) => setGrant(i, { tenantId: v })}
                    />
                    <Select
                      label="Project"
                      value={g.projectId}
                      placeholder="All projects"
                      options={projects.map((p) => ({ value: String(p.id), label: p.name }))}
                      onChange={(v) => setGrant(i, { projectId: v })}
                    />
                    <Select
                      label="Environment"
                      value={g.environmentId}
                      placeholder="All environments"
                      options={environments.map((e) => ({ value: String(e.id), label: e.name }))}
                      onChange={(v) => setGrant(i, { environmentId: v })}
                    />
                    <Select
                      label="Min severity"
                      value={g.minSeverity}
                      placeholder="Any"
                      options={SEVERITIES.map((s) => ({ value: s, label: s }))}
                      onChange={(v) => setGrant(i, { minSeverity: v })}
                    />
                  </div>
                  <div className="mt-2.5 flex flex-wrap items-center gap-4">
                    <CheckboxField label="Can view bodies" checked={g.canViewBodies} onChange={(v) => setGrant(i, { canViewBodies: v })} />
                    <CheckboxField label="Can export" checked={g.canExport} onChange={(v) => setGrant(i, { canExport: v })} />
                    <CheckboxField label="Can view security logs" checked={g.canViewSecurity} onChange={(v) => setGrant(i, { canViewSecurity: v })} />
                    <span className="flex-1" />
                    <button
                      type="button"
                      className="btn-ghost !p-1.5 text-red-500"
                      aria-label="Remove grant"
                      onClick={() => setGrants((gs) => gs.filter((_, idx) => idx !== i))}
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </Modal>

      {/* reset password modal */}
      <Modal
        open={resetUser !== null}
        title={`Reset password — ${resetUser?.username ?? ''}`}
        onClose={() => setResetUser(null)}
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setResetUser(null)}>
              Close
            </button>
            <button
              type="button"
              className="btn-primary"
              onClick={resetPassword}
              disabled={busy || (newPassword.length > 0 && newPassword.trim().length < 10)}
            >
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} Reset password
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {resetError && <ErrorBanner error={resetError} />}
          {resetDone && (
            <div className="space-y-2 rounded-md bg-emerald-50 px-3 py-2 text-sm text-emerald-700 dark:bg-emerald-950/50 dark:text-emerald-300">
              {generatedPassword ? (
                <>
                  <p>
                    Password reset. This generated password is shown once and cannot be retrieved
                    again — copy it now and share it through a secure channel.
                  </p>
                  <code className="block select-all break-all rounded bg-emerald-100 px-2 py-1 font-mono text-sm text-emerald-900 dark:bg-emerald-900/60 dark:text-emerald-100">
                    {generatedPassword}
                  </code>
                </>
              ) : (
                <p>Password reset. Share the new password with the user through a secure channel.</p>
              )}
              <p>The user must change it at next login.</p>
            </div>
          )}
          <TextField label="New password" type="password" value={newPassword} onChange={setNewPassword} autoFocus />
          <p className="text-xs text-slate-500 dark:text-slate-400">
            At least 10 characters — or leave blank to generate one.
          </p>
        </div>
      </Modal>
    </div>
  );
}
