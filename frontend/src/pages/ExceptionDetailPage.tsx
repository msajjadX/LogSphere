import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, ExternalLink, Save } from 'lucide-react';
import { api, toApiError } from '../api/client';
import type { AdminUser, ExceptionGroupDetail, LogEventDetail, LogEventSummary } from '../api/types';
import { EXCEPTION_STATUSES } from '../api/types';
import { useApi } from '../hooks/useApi';
import { Badge, exceptionStatusTone, SeverityBadge } from '../components/Badge';
import { DataTable, type Column } from '../components/DataTable';
import { ErrorBanner, LoadingBlock, Spinner } from '../components/Feedback';
import { Select, TextField } from '../components/Select';
import { EventDetailDrawer } from '../components/EventDetailDrawer';
import { SeverityChart } from '../components/SeverityChart';
import { CopyButton } from '../components/CopyButton';
import { formatDateTime, formatNumber, relativeTime, truncate, tryParseJson } from '../utils/format';

interface ActivityEntry {
  id: number;
  userId?: string | null;
  actor?: string | null;
  action: string;
  details?: { from?: string; to?: string; fromUserId?: string; toUserId?: string; url?: string } | null;
  occurredAt: string;
}

function describeActivity(a: ActivityEntry, userName: (id?: string | null) => string): string {
  switch (a.action) {
    case 'StatusChanged':
      return `changed status ${a.details?.from ?? '?'} → ${a.details?.to ?? '?'}`;
    case 'Assigned':
      return a.details?.fromUserId
        ? `reassigned ${userName(a.details.fromUserId)} → ${userName(a.details?.toUserId)}`
        : `assigned to ${userName(a.details?.toUserId)}`;
    case 'NotesUpdated':
      return 'updated the notes';
    case 'IssueLinked':
      return `linked issue ${a.details?.url ?? ''}`;
    default:
      return a.action;
  }
}

/** Assembles a self-contained, investigation-first prompt for any AI assistant. */
function buildAiPrompt(
  group: ExceptionGroupDetail,
  stackTrace: string | null,
  sample: LogEventDetail | null,
  occurrences: LogEventSummary[],
): string {
  const occLines = occurrences
    .slice(0, 10)
    .map((o) => `- ${o.eventTimestamp} [${o.severity}] ${o.environmentName ?? o.environmentId ?? ''} v${o.applicationVersion ?? '?'}: ${o.message ?? ''}`)
    .join('\n');
  return `You are a senior software engineer investigating a production exception. Do NOT jump to a fix.

WORK IN TWO PHASES:

PHASE 1 — INVESTIGATE FIRST (do this before proposing any solution):
1. Read all context below carefully and restate what is actually failing, where, and under what conditions.
2. List every plausible root-cause hypothesis, ranked by likelihood, with the evidence for and against each.
3. List what additional evidence would confirm/eliminate each hypothesis (specific log queries, code to inspect, configs to check, repro steps). Ask me for anything you need that is missing.
4. Only when the root cause is identified (or the top hypothesis is clearly dominant), move to Phase 2.

PHASE 2 — SOLUTION:
5. Propose the fix (with code), plus any short-term mitigation if the real fix is larger.
6. Explain the blast radius: what else this bug may have affected.
7. Suggest how to prevent regression (test, validation, alert rule).

=== EXCEPTION CONTEXT (from LogSphere) ===
Exception type: ${group.exceptionType ?? 'Unknown'}
Message: ${group.message ?? '—'}
Project: ${group.projectName ?? '—'} | Module: ${group.module ?? '—'}
First seen: ${group.firstSeen} | Last seen: ${group.lastSeen}
Occurrences: ${group.totalCount} total, ${group.last24hCount ?? '?'} in the last 24h
Affected versions: ${(group.affectedVersions ?? []).join(', ') || '—'}
Fingerprint: ${group.fingerprint}
Status: ${group.status}${group.notes ? `\nInvestigation notes so far: ${group.notes}` : ''}

=== SAMPLE STACK TRACE ===
${stackTrace ?? '(no stack trace captured)'}

=== SAMPLE EVENT ===
${sample ? `Correlation ID: ${sample.correlationId ?? '—'} | Environment: ${sample.environmentName ?? sample.environmentId} | Machine: ${sample.machineName ?? '—'} | App version: ${sample.applicationVersion ?? '—'}
Action: ${sample.actionName ?? '—'} | HTTP: ${sample.httpMethod ?? ''} ${sample.httpRoute ?? ''} ${sample.httpStatusCode ?? ''}
Exception data: ${JSON.stringify(sample.exceptionData ?? null)}` : '(not available)'}

=== RECENT OCCURRENCES ===
${occLines || '(none listed)'}
`;
}

function extractStackTrace(detail: LogEventDetail | null): string | null {
  if (!detail) return null;
  const ex = tryParseJson(detail.exceptionData ?? detail.exception) as
    | { stackTrace?: string; StackTrace?: string }
    | null
    | undefined;
  if (!ex || typeof ex !== 'object') return null;
  const st = ex.stackTrace ?? ex.StackTrace;
  return typeof st === 'string' && st.length > 0 ? st : null;
}

export function ExceptionDetailPage() {
  const { id } = useParams<{ id: string }>();
  const [selectedEventId, setSelectedEventId] = useState<string | null>(null);

  const { data: group, loading, error, reload } = useApi<ExceptionGroupDetail>(
    () =>
      api
        .get<ExceptionGroupDetail & { group?: ExceptionGroupDetail }>(
          `/exceptions/groups/${encodeURIComponent(id ?? '')}`,
        )
        .then((res) => (res.group ? { ...res, ...res.group } : res)),
    [id],
    Boolean(id),
  );

  // sample event for the stack trace
  const sampleId = group?.sampleEventId ?? null;
  const { data: sample } = useApi<LogEventDetail>(
    () => api.get<LogEventDetail>(`/query/logs/${sampleId}`),
    [sampleId],
    Boolean(sampleId),
  );
  const stackTrace = extractStackTrace(sample);

  // users for the assignment select (admin endpoint; degrades to free-text id)
  const [users, setUsers] = useState<AdminUser[]>([]);
  useEffect(() => {
    let alive = true;
    api
      .getList<AdminUser>('/admin/users')
      .then((u) => alive && setUsers(u))
      .catch(() => {});
    return () => {
      alive = false;
    };
  }, []);

  // workflow panel state
  const [status, setStatus] = useState('');
  const [assignee, setAssignee] = useState('');
  const [notes, setNotes] = useState('');
  const [issueUrl, setIssueUrl] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (!group) return;
    setStatus(String(group.status ?? 'New'));
    setAssignee(group.assignedToUserId != null ? String(group.assignedToUserId) : '');
    setNotes(group.notes ?? '');
    setIssueUrl(group.linkedIssueUrl ?? '');
  }, [group]);

  const saveWorkflow = async () => {
    setSaving(true);
    setSaveError(null);
    setSaved(false);
    try {
      await api.patch(`/exceptions/groups/${encodeURIComponent(id ?? '')}`, {
        status,
        assignedToUserId: assignee === '' ? null : (users.find((u) => String(u.id) === assignee)?.id ?? assignee),
        notes: notes || null,
        linkedIssueUrl: issueUrl || null,
      });
      setSaved(true);
      reload();
    } catch (e) {
      setSaveError(toApiError(e).message);
    } finally {
      setSaving(false);
    }
  };

  const occurrences = useMemo(
    () => group?.recentOccurrences ?? group?.occurrences ?? [],
    [group],
  );
  const trend = useMemo(() => group?.trend ?? group?.trendBuckets ?? [], [group]);
  const activity = useMemo(
    () => ((group as { activity?: ActivityEntry[] } | null)?.activity ?? []),
    [group],
  );
  const resolveUserName = (userId?: string | null): string => {
    if (!userId) return 'unassigned';
    const u = users.find((x) => String(x.id) === String(userId));
    return u ? u.displayName || u.username : truncate(userId, 12);
  };

  const occColumns: Column<LogEventSummary>[] = [
    { key: 'time', header: 'Time', render: (e) => <span className="whitespace-nowrap text-xs">{formatDateTime(e.eventTimestamp)}</span> },
    { key: 'severity', header: 'Severity', render: (e) => <SeverityBadge severity={e.severity} /> },
    { key: 'env', header: 'Environment', render: (e) => <span className="text-xs">{e.environmentName ?? '—'}</span> },
    { key: 'version', header: 'Version', render: (e) => <span className="text-xs">{e.applicationVersion ?? '—'}</span> },
    {
      key: 'message',
      header: 'Message',
      render: (e) => <span className="block max-w-xl truncate text-xs">{truncate(e.message, 140) || '—'}</span>,
      className: 'w-full',
    },
  ];

  if (loading) return <LoadingBlock label="Loading exception group…" />;
  if (error) return <ErrorBanner error={error} onRetry={reload} />;
  if (!group) return null;

  return (
    <div className="space-y-4">
      <div>
        <Link to="/exceptions" className="inline-flex items-center gap-1 text-xs text-indigo-600 hover:underline dark:text-indigo-400">
          <ArrowLeft className="h-3 w-3" /> Back to exceptions
        </Link>
        <div className="mt-2 flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <h1 className="break-all font-mono text-lg font-semibold text-red-700 dark:text-red-400">
              {group.exceptionType ?? 'Unknown exception'}
            </h1>
            <p className="mt-0.5 max-w-3xl break-words text-sm text-gray-600 dark:text-gray-400">{group.message ?? '—'}</p>
            <div className="mt-1.5 flex flex-wrap items-center gap-2 text-xs text-gray-500">
              <Badge tone={exceptionStatusTone(String(group.status))}>{String(group.status)}</Badge>
              {group.module && <span>Module: {group.module}</span>}
              {group.projectName && <span>Project: {group.projectName}</span>}
              <span>First seen {relativeTime(group.firstSeen)}</span>
              <span>Last seen {relativeTime(group.lastSeen)}</span>
              <span className="font-mono">fp: {truncate(group.fingerprint, 24)}</span>
              <CopyButton text={group.fingerprint} label="Copy fingerprint" />
              <CopyButton
                text={buildAiPrompt(group, stackTrace, sample ?? null, occurrences)}
                label="Copy AI prompt"
              />
            </div>
          </div>
          <div className="flex gap-4 text-right">
            <div>
              <p className="text-2xl font-semibold tabular-nums">{formatNumber(group.totalCount)}</p>
              <p className="text-xs text-gray-500">total</p>
            </div>
            <div>
              <p className="text-2xl font-semibold tabular-nums">{formatNumber(group.lastHourCount ?? 0)}</p>
              <p className="text-xs text-gray-500">last hour</p>
            </div>
            <div>
              <p className="text-2xl font-semibold tabular-nums">{formatNumber(group.last24hCount ?? 0)}</p>
              <p className="text-xs text-gray-500">last 24h</p>
            </div>
          </div>
        </div>
      </div>

      <div className="grid gap-4 xl:grid-cols-3">
        <div className="space-y-4 xl:col-span-2">
          {/* trend */}
          <section className="card p-4">
            <h2 className="mb-3 text-sm font-semibold">Occurrence trend</h2>
            <SeverityChart points={trend.map((t) => ({ ...t, severity: t.severity ?? 'Error' }))} height={200} />
          </section>

          {/* stack trace */}
          <section className="card p-4">
            <h2 className="mb-2 text-sm font-semibold">Sample stack trace</h2>
            {stackTrace ? (
              <pre className="max-h-96 overflow-auto whitespace-pre-wrap break-all rounded-md bg-gray-950 p-3 font-mono text-xs leading-5 text-gray-200">
                {stackTrace}
              </pre>
            ) : sampleId ? (
              <p className="text-sm text-gray-500">
                No stack trace available on the sample event.{' '}
                <button type="button" className="text-indigo-600 hover:underline dark:text-indigo-400" onClick={() => setSelectedEventId(sampleId)}>
                  Open sample event
                </button>
              </p>
            ) : (
              <p className="text-sm text-gray-500">No sample event recorded for this group.</p>
            )}
            {group.affectedVersions && group.affectedVersions.length > 0 && (
              <p className="mt-2 text-xs text-gray-500">
                Affected versions: <span className="font-mono">{group.affectedVersions.join(', ')}</span>
              </p>
            )}
          </section>

          {/* occurrences */}
          <section className="card">
            <h2 className="border-b border-gray-200 px-4 py-3 text-sm font-semibold dark:border-gray-800">
              Recent occurrences
            </h2>
            <DataTable
              columns={occColumns}
              rows={occurrences}
              rowKey={(e) => e.eventId}
              onRowClick={(e) => setSelectedEventId(e.eventId)}
              emptyMessage="No recent occurrences"
            />
          </section>
        </div>

        {/* workflow panel */}
        <section className="card h-fit p-4">
          <h2 className="mb-3 text-sm font-semibold">Workflow</h2>
          <div className="space-y-3">
            {saveError && <ErrorBanner error={saveError} />}
            {saved && (
              <p className="rounded-md bg-emerald-50 px-3 py-2 text-xs text-emerald-700 dark:bg-emerald-950/50 dark:text-emerald-300">
                Changes saved.
              </p>
            )}
            <Select
              label="Status"
              value={status}
              options={EXCEPTION_STATUSES.map((s) => ({ value: s, label: s }))}
              onChange={setStatus}
            />
            {users.length > 0 ? (
              <Select
                label="Assign to"
                value={assignee}
                placeholder="Unassigned"
                options={users
                  .filter((u) => u.isActive !== false || String(u.id) === assignee)
                  .map((u) => ({ value: String(u.id), label: u.displayName || u.username }))}
                onChange={setAssignee}
              />
            ) : (
              <TextField label="Assign to (user ID)" value={assignee} onChange={setAssignee} placeholder="user id" />
            )}
            <div>
              <label htmlFor="exc-notes" className="label">
                Notes
              </label>
              <textarea
                id="exc-notes"
                className="input min-h-[6rem]"
                value={notes}
                onChange={(e) => setNotes(e.target.value)}
                placeholder="Investigation notes…"
              />
            </div>
            <TextField label="Linked issue URL" value={issueUrl} onChange={setIssueUrl} placeholder="https://…" />
            {issueUrl && (
              <a
                href={issueUrl}
                target="_blank"
                rel="noreferrer noopener"
                className="inline-flex items-center gap-1 text-xs text-indigo-600 hover:underline dark:text-indigo-400"
              >
                <ExternalLink className="h-3 w-3" /> Open linked issue
              </a>
            )}
            <button type="button" className="btn-primary w-full" onClick={saveWorkflow} disabled={saving}>
              {saving ? <Spinner className="h-4 w-4 !text-white" /> : <Save className="h-4 w-4" />} Save changes
            </button>
          </div>

          {/* triage activity trail */}
          <div className="mt-5 border-t border-gray-200 pt-3 dark:border-gray-800">
            <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">
              Activity
            </h3>
            {activity.length === 0 ? (
              <p className="text-xs text-gray-500">No triage actions yet.</p>
            ) : (
              <ol className="space-y-2">
                {activity.map((a) => (
                  <li key={a.id} className="text-xs leading-5">
                    <span className="font-medium text-gray-800 dark:text-gray-200">{a.actor ?? 'Someone'}</span>{' '}
                    <span className="text-gray-600 dark:text-gray-400">{describeActivity(a, resolveUserName)}</span>
                    <span className="ml-1 text-gray-400" title={formatDateTime(a.occurredAt)}>
                      · {relativeTime(a.occurredAt)}
                    </span>
                  </li>
                ))}
              </ol>
            )}
          </div>
        </section>
      </div>

      <EventDetailDrawer eventId={selectedEventId} onClose={() => setSelectedEventId(null)} />
    </div>
  );
}
