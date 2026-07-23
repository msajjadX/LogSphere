import { useState } from 'react';
import { BellRing, Check, CheckCheck, Pencil, Plus, Trash2 } from 'lucide-react';
import { api, toApiError } from '../api/client';
import type { AlertChannel, AlertOccurrence, AlertRule } from '../api/types';
import { ALERT_CONDITION_TYPES, SEVERITIES } from '../api/types';
import { useApi } from '../hooks/useApi';
import { useLookups } from '../context/LookupsContext';
import { DataTable, type Column } from '../components/DataTable';
import { Badge, alertStateTone } from '../components/Badge';
import { ErrorBanner, Spinner } from '../components/Feedback';
import { Modal, ConfirmDialog } from '../components/Modal';
import { CheckboxField, MultiSelect, Select, TextField } from '../components/Select';
import { formatDateTime, relativeTime, truncate } from '../utils/format';

type Tab = 'occurrences' | 'rules' | 'channels';

// ---------------------------------------------------------------------------
// Occurrences
// ---------------------------------------------------------------------------

function OccurrencesTab() {
  const [state, setState] = useState<'open' | 'all'>('open');
  const [actionError, setActionError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const { data, loading, error, reload } = useApi<AlertOccurrence[]>(
    () => api.getList<AlertOccurrence>(`/alerts/occurrences?state=${state}`),
    [state],
  );

  const act = async (occ: AlertOccurrence, action: 'ack' | 'resolve') => {
    setBusyId(String(occ.id));
    setActionError(null);
    try {
      await api.post(`/alerts/occurrences/${encodeURIComponent(String(occ.id))}/${action}`);
      reload();
    } catch (e) {
      setActionError(toApiError(e).message);
    } finally {
      setBusyId(null);
    }
  };

  const columns: Column<AlertOccurrence>[] = [
    {
      key: 'triggered',
      header: 'Triggered',
      render: (o) => (
        <span className="whitespace-nowrap text-xs" title={formatDateTime(o.triggeredAt)}>
          {relativeTime(o.triggeredAt)}
        </span>
      ),
    },
    { key: 'rule', header: 'Rule', render: (o) => <span className="whitespace-nowrap text-xs font-medium">{o.ruleName ?? String(o.ruleId)}</span> },
    { key: 'state', header: 'State', render: (o) => <Badge tone={alertStateTone(o.state)}>{o.state}</Badge> },
    {
      key: 'summary',
      header: 'Summary',
      render: (o) => (
        <div className="min-w-0">
          <p className="max-w-xl truncate text-xs">{truncate(o.summary, 160) || '—'}</p>
          {o.details && <p className="max-w-xl truncate text-xs text-gray-400">{truncate(o.details, 160)}</p>}
        </div>
      ),
      className: 'w-full',
    },
    {
      key: 'actions',
      header: '',
      render: (o) => (
        <span className="flex justify-end gap-1.5">
          {o.state === 'Open' && (
            <button
              type="button"
              className="btn-secondary !px-2 !py-1 text-xs"
              disabled={busyId === String(o.id)}
              onClick={(e) => {
                e.stopPropagation();
                void act(o, 'ack');
              }}
            >
              <Check className="h-3 w-3" /> Ack
            </button>
          )}
          {o.state !== 'Resolved' && (
            <button
              type="button"
              className="btn-secondary !px-2 !py-1 text-xs"
              disabled={busyId === String(o.id)}
              onClick={(e) => {
                e.stopPropagation();
                void act(o, 'resolve');
              }}
            >
              <CheckCheck className="h-3 w-3" /> Resolve
            </button>
          )}
        </span>
      ),
    },
  ];

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <Select
          label="State"
          className="w-40"
          value={state}
          options={[
            { value: 'open', label: 'Open only' },
            { value: 'all', label: 'All states' },
          ]}
          onChange={(v) => setState(v === 'all' ? 'all' : 'open')}
        />
      </div>
      {actionError && <ErrorBanner error={actionError} />}
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={data ?? []}
          rowKey={(o) => String(o.id)}
          loading={loading}
          emptyMessage={state === 'open' ? 'No open alerts — all clear' : 'No alert occurrences'}
        />
      </section>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Rules
// ---------------------------------------------------------------------------

interface RuleForm {
  name: string;
  enabled: boolean;
  projectId: string;
  environmentId: string;
  conditionType: string;
  threshold: string;
  windowMinutes: string;
  cooldownMinutes: string;
  severityFilter: string;
  channels: string[];
  moduleFilter: string;
  actionFilter: string;
  minCount: string;
}

function emptyRuleForm(): RuleForm {
  return {
    name: '',
    enabled: true,
    projectId: '',
    environmentId: '',
    conditionType: 'ErrorCountThreshold',
    threshold: '10',
    windowMinutes: '5',
    cooldownMinutes: '15',
    severityFilter: '',
    channels: [],
    moduleFilter: '',
    actionFilter: '',
    minCount: '0',
  };
}

function RulesTab({ channels }: { channels: AlertChannel[] }) {
  const { projects, environments } = useLookups();
  const { data, loading, error, reload } = useApi<AlertRule[]>(() => api.getList<AlertRule>('/alerts/rules'), []);

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<AlertRule | null>(null);
  const [form, setForm] = useState<RuleForm>(emptyRuleForm());
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<AlertRule | null>(null);

  const openCreate = () => {
    setEditing(null);
    setForm(emptyRuleForm());
    setFormError(null);
    setModalOpen(true);
  };

  const openEdit = (r: AlertRule) => {
    setEditing(r);
    setForm({
      name: r.name,
      enabled: r.enabled,
      projectId: r.projectId != null ? String(r.projectId) : '',
      environmentId: r.environmentId != null ? String(r.environmentId) : '',
      conditionType: String(r.conditionType),
      threshold: String(r.threshold ?? ''),
      windowMinutes: String(r.windowMinutes ?? ''),
      cooldownMinutes: String(r.cooldownMinutes ?? ''),
      severityFilter: r.severityFilter ?? '',
      channels: (r.channels ?? []).map(String),
      moduleFilter: r.moduleFilter ?? '',
      actionFilter: r.actionFilter ?? '',
      minCount: String(r.minCount ?? 0),
    });
    setFormError(null);
    setModalOpen(true);
  };

  const save = async () => {
    setBusy(true);
    setFormError(null);
    try {
      const body = {
        name: form.name,
        enabled: form.enabled,
        projectId: form.projectId === '' ? null : (projects.find((p) => String(p.id) === form.projectId)?.id ?? form.projectId),
        environmentId:
          form.environmentId === ''
            ? null
            : (environments.find((e) => String(e.id) === form.environmentId)?.id ?? form.environmentId),
        conditionType: form.conditionType,
        threshold: Number(form.threshold),
        windowMinutes: Number(form.windowMinutes),
        cooldownMinutes: Number(form.cooldownMinutes),
        severityFilter: form.severityFilter || null,
        channels: form.channels.map((c) => channels.find((ch) => String(ch.id) === c)?.id ?? c),
        moduleFilter: form.conditionType === 'IngestSilence' ? form.moduleFilter.trim() || null : null,
        actionFilter: form.conditionType === 'IngestSilence' ? form.actionFilter.trim() || null : null,
        minCount: form.conditionType === 'IngestSilence' ? Math.max(0, Number(form.minCount) || 0) : 0,
      };
      if (editing) await api.put(`/alerts/rules/${encodeURIComponent(String(editing.id))}`, body);
      else await api.post('/alerts/rules', body);
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
      await api.del(`/alerts/rules/${encodeURIComponent(String(deleting.id))}`);
      setDeleting(null);
      reload();
    } catch (e) {
      setFormError(toApiError(e).message);
      setDeleting(null);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<AlertRule>[] = [
    { key: 'name', header: 'Name', render: (r) => <span className="text-xs font-medium">{r.name}</span> },
    {
      key: 'enabled',
      header: 'Enabled',
      render: (r) => <Badge tone={r.enabled ? 'green' : 'gray'}>{r.enabled ? 'Enabled' : 'Disabled'}</Badge>,
    },
    {
      key: 'scope',
      header: 'Scope',
      render: (r) => (
        <span className="whitespace-nowrap text-xs">
          {r.projectId != null ? (projects.find((p) => String(p.id) === String(r.projectId))?.name ?? String(r.projectId)) : 'All projects'}
          {r.environmentId != null &&
            ` · ${environments.find((e) => String(e.id) === String(r.environmentId))?.name ?? r.environmentId}`}
        </span>
      ),
    },
    { key: 'condition', header: 'Condition', render: (r) => <span className="whitespace-nowrap font-mono text-xs">{String(r.conditionType)}</span> },
    {
      key: 'params',
      header: 'Threshold / Window / Cooldown',
      render: (r) => (
        <span className="whitespace-nowrap text-xs tabular-nums">
          {r.threshold} / {r.windowMinutes}m / {r.cooldownMinutes}m
        </span>
      ),
    },
    { key: 'sev', header: 'Severity', render: (r) => <span className="text-xs">{r.severityFilter ?? 'Any'}</span> },
    {
      key: 'channels',
      header: 'Channels',
      render: (r) => (
        <span className="text-xs">
          {(r.channels ?? [])
            .map((id) => channels.find((c) => String(c.id) === String(id))?.name ?? String(id))
            .join(', ') || '—'}
        </span>
      ),
    },
    {
      key: 'actions',
      header: '',
      render: (r) => (
        <span className="flex justify-end gap-1.5">
          <button
            type="button"
            className="btn-ghost !p-1.5"
            aria-label={`Edit rule ${r.name}`}
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
            aria-label={`Delete rule ${r.name}`}
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
      <div className="flex justify-end">
        <button type="button" className="btn-primary" onClick={openCreate}>
          <Plus className="h-4 w-4" /> New rule
        </button>
      </div>
      {formError && !modalOpen && <ErrorBanner error={formError} />}
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={data ?? []}
          rowKey={(r) => String(r.id)}
          loading={loading}
          onRowClick={openEdit}
          emptyMessage="No alert rules configured"
          emptyHint="Create a rule to get notified about errors, latency, or silence."
        />
      </section>

      <Modal
        open={modalOpen}
        title={editing ? `Edit rule — ${editing.name}` : 'New alert rule'}
        onClose={() => setModalOpen(false)}
        widthClass="max-w-xl"
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setModalOpen(false)}>
              Cancel
            </button>
            <button type="button" className="btn-primary" onClick={save} disabled={busy || !form.name.trim()}>
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} {editing ? 'Save changes' : 'Create rule'}
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {formError && <ErrorBanner error={formError} />}
          <TextField label="Name" value={form.name} onChange={(v) => setForm((f) => ({ ...f, name: v }))} autoFocus />
          <div className="grid grid-cols-2 gap-3">
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
          </div>
          <Select
            label="Condition type"
            value={form.conditionType}
            options={ALERT_CONDITION_TYPES.map((c) => ({ value: c, label: c }))}
            onChange={(v) => setForm((f) => ({ ...f, conditionType: v }))}
          />
          {form.conditionType === 'IngestSilence' && (
            <div className="rounded-md border border-indigo-200 bg-indigo-50/60 p-3 dark:border-indigo-900 dark:bg-indigo-950/30">
              <p className="mb-2 text-xs text-gray-600 dark:text-gray-400">
                Dead-man switch: alert when the expected events <strong>don&apos;t</strong> arrive. Leave both filters
                empty to watch the whole project/environment scope.
              </p>
              <div className="grid grid-cols-3 gap-3">
                <TextField
                  label="Module contains"
                  value={form.moduleFilter}
                  onChange={(v) => setForm((f) => ({ ...f, moduleFilter: v }))}
                  placeholder="e.g. PayrollInterface"
                />
                <TextField
                  label="Action contains"
                  value={form.actionFilter}
                  onChange={(v) => setForm((f) => ({ ...f, actionFilter: v }))}
                  placeholder="e.g. NIGHTLY_SYNC"
                />
                <TextField
                  label="Expected more than"
                  type="number"
                  value={form.minCount}
                  onChange={(v) => setForm((f) => ({ ...f, minCount: v }))}
                  placeholder="0 = any event"
                />
              </div>
            </div>
          )}
          <div className="grid grid-cols-3 gap-3">
            <TextField label="Threshold" type="number" value={form.threshold} onChange={(v) => setForm((f) => ({ ...f, threshold: v }))} />
            <TextField label="Window (min)" type="number" value={form.windowMinutes} onChange={(v) => setForm((f) => ({ ...f, windowMinutes: v }))} />
            <TextField label="Cooldown (min)" type="number" value={form.cooldownMinutes} onChange={(v) => setForm((f) => ({ ...f, cooldownMinutes: v }))} />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <Select
              label="Severity filter"
              value={form.severityFilter}
              placeholder="Any severity"
              options={SEVERITIES.map((s) => ({ value: s, label: s }))}
              onChange={(v) => setForm((f) => ({ ...f, severityFilter: v }))}
            />
            <MultiSelect
              label="Notification channels"
              values={form.channels}
              options={channels.map((c) => ({ value: String(c.id), label: `${c.name} (${c.type})` }))}
              onChange={(v) => setForm((f) => ({ ...f, channels: v }))}
              placeholder="Dashboard only"
            />
          </div>
          <CheckboxField label="Rule enabled" checked={form.enabled} onChange={(v) => setForm((f) => ({ ...f, enabled: v }))} />
        </div>
      </Modal>

      <ConfirmDialog
        open={deleting !== null}
        title="Delete alert rule"
        message={
          <>
            Delete rule <strong>{deleting?.name}</strong>? Existing occurrences are kept, but no new alerts will fire.
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

// ---------------------------------------------------------------------------
// Channels
// ---------------------------------------------------------------------------

function ChannelsTab({
  channels,
  loading,
  reload,
  error,
}: {
  channels: AlertChannel[];
  loading: boolean;
  reload: () => void;
  error: ReturnType<typeof toApiError> | null;
}) {
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<AlertChannel | null>(null);
  const [form, setForm] = useState({ name: '', type: 'Email', target: '', enabled: true });
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<AlertChannel | null>(null);

  const openCreate = () => {
    setEditing(null);
    setForm({ name: '', type: 'Email', target: '', enabled: true });
    setFormError(null);
    setModalOpen(true);
  };
  const openEdit = (c: AlertChannel) => {
    setEditing(c);
    setForm({ name: c.name, type: String(c.type), target: c.target, enabled: c.enabled });
    setFormError(null);
    setModalOpen(true);
  };

  const save = async () => {
    setBusy(true);
    setFormError(null);
    try {
      if (editing) await api.put(`/alerts/channels/${encodeURIComponent(String(editing.id))}`, form);
      else await api.post('/alerts/channels', form);
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
      await api.del(`/alerts/channels/${encodeURIComponent(String(deleting.id))}`);
      setDeleting(null);
      reload();
    } catch (e) {
      setFormError(toApiError(e).message);
      setDeleting(null);
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<AlertChannel>[] = [
    { key: 'name', header: 'Name', render: (c) => <span className="text-xs font-medium">{c.name}</span> },
    { key: 'type', header: 'Type', render: (c) => <Badge tone={c.type === 'Email' ? 'blue' : 'purple'}>{c.type}</Badge> },
    { key: 'target', header: 'Target', render: (c) => <span className="break-all font-mono text-xs">{c.target}</span>, className: 'w-full' },
    {
      key: 'enabled',
      header: 'Enabled',
      render: (c) => <Badge tone={c.enabled ? 'green' : 'gray'}>{c.enabled ? 'Enabled' : 'Disabled'}</Badge>,
    },
    {
      key: 'actions',
      header: '',
      render: (c) => (
        <span className="flex justify-end gap-1.5">
          <button
            type="button"
            className="btn-ghost !p-1.5"
            aria-label={`Edit channel ${c.name}`}
            onClick={(e) => {
              e.stopPropagation();
              openEdit(c);
            }}
          >
            <Pencil className="h-3.5 w-3.5" />
          </button>
          <button
            type="button"
            className="btn-ghost !p-1.5 text-red-500"
            aria-label={`Delete channel ${c.name}`}
            onClick={(e) => {
              e.stopPropagation();
              setDeleting(c);
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
      <div className="flex justify-end">
        <button type="button" className="btn-primary" onClick={openCreate}>
          <Plus className="h-4 w-4" /> New channel
        </button>
      </div>
      {formError && !modalOpen && <ErrorBanner error={formError} />}
      {error && <ErrorBanner error={error} onRetry={reload} />}
      <section className="card">
        <DataTable
          columns={columns}
          rows={channels}
          rowKey={(c) => String(c.id)}
          loading={loading}
          onRowClick={openEdit}
          emptyMessage="No notification channels"
          emptyHint="Add an Email or Webhook channel to route alerts."
        />
      </section>

      <Modal
        open={modalOpen}
        title={editing ? `Edit channel — ${editing.name}` : 'New notification channel'}
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
              disabled={busy || !form.name.trim() || !form.target.trim()}
            >
              {busy && <Spinner className="h-3.5 w-3.5 !text-white" />} {editing ? 'Save changes' : 'Create channel'}
            </button>
          </>
        }
      >
        <div className="space-y-3">
          {formError && <ErrorBanner error={formError} />}
          <TextField label="Name" value={form.name} onChange={(v) => setForm((f) => ({ ...f, name: v }))} autoFocus />
          <Select
            label="Type"
            value={form.type}
            options={[
              { value: 'Email', label: 'Email' },
              { value: 'Webhook', label: 'Webhook (Slack/Teams compatible)' },
            ]}
            onChange={(v) => setForm((f) => ({ ...f, type: v }))}
          />
          <TextField
            label={form.type === 'Email' ? 'Email address' : 'Webhook URL'}
            value={form.target}
            onChange={(v) => setForm((f) => ({ ...f, target: v }))}
            placeholder={form.type === 'Email' ? 'oncall@example.com' : 'https://hooks.slack.com/…'}
            mono={form.type !== 'Email'}
          />
          <CheckboxField label="Channel enabled" checked={form.enabled} onChange={(v) => setForm((f) => ({ ...f, enabled: v }))} />
        </div>
      </Modal>

      <ConfirmDialog
        open={deleting !== null}
        title="Delete channel"
        message={
          <>
            Delete channel <strong>{deleting?.name}</strong>? Rules referencing it will stop notifying through it.
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

// ---------------------------------------------------------------------------

export function AlertsPage() {
  const [tab, setTab] = useState<Tab>('occurrences');
  const channelsApi = useApi<AlertChannel[]>(() => api.getList<AlertChannel>('/alerts/channels'), []);
  const channels = channelsApi.data ?? [];

  return (
    <div className="space-y-4">
      <div className="flex border-b border-gray-200 dark:border-gray-800" role="tablist">
        {(
          [
            ['occurrences', 'Occurrences'],
            ['rules', 'Rules'],
            ['channels', 'Channels'],
          ] as [Tab, string][]
        ).map(([key, label]) => (
          <button
            key={key}
            type="button"
            role="tab"
            aria-selected={tab === key}
            className={`tab ${tab === key ? 'tab-active' : ''}`}
            onClick={() => setTab(key)}
          >
            {key === 'occurrences' && <BellRing className="mr-1 inline h-3.5 w-3.5" />}
            {label}
          </button>
        ))}
      </div>

      {tab === 'occurrences' && <OccurrencesTab />}
      {tab === 'rules' && <RulesTab channels={channels} />}
      {tab === 'channels' && (
        <ChannelsTab channels={channels} loading={channelsApi.loading} reload={channelsApi.reload} error={channelsApi.error} />
      )}
    </div>
  );
}
