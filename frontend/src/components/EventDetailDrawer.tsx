import { useNavigate } from 'react-router-dom';
import { GitBranch, Link2 } from 'lucide-react';
import { api } from '../api/client';
import type { LogEventDetail } from '../api/types';
import { useApi } from '../hooks/useApi';
import { formatDateTime, formatDuration, relativeTime } from '../utils/format';
import { Drawer } from './Drawer';
import { SeverityBadge, StatusBadge } from './Badge';
import { CopyButton } from './CopyButton';
import { ErrorBanner, LoadingBlock } from './Feedback';
import { JsonViewer } from './JsonViewer';

interface EventDetailDrawerProps {
  eventId: string | null;
  onClose: () => void;
  /** When provided (Log Explorer), correlation pivot applies in-page instead of navigating. */
  onApplyCorrelation?: (correlationId: string) => void;
}

function Field({ label, value, mono }: { label: string; value: React.ReactNode; mono?: boolean }) {
  if (value === undefined || value === null || value === '') return null;
  return (
    <div className="min-w-0">
      <dt className="text-[11px] font-medium uppercase tracking-wide text-gray-400 dark:text-gray-500">{label}</dt>
      <dd
        className={`text-sm text-gray-800 dark:text-gray-200 ${
          mono ? 'overflow-x-auto whitespace-nowrap font-mono text-xs [scrollbar-width:none]' : 'break-all'
        }`}
      >
        {value}
      </dd>
    </div>
  );
}

function JsonSection({ title, value }: { title: string; value: unknown }) {
  if (value === undefined || value === null) return null;
  return (
    <section className="mt-4">
      <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">{title}</h3>
      <JsonViewer value={value} />
    </section>
  );
}

export function EventDetailDrawer({ eventId, onClose, onApplyCorrelation }: EventDetailDrawerProps) {
  const navigate = useNavigate();
  const { data, loading, error, reload } = useApi<LogEventDetail>(
    () => api.get<LogEventDetail>(`/query/logs/${eventId}`),
    [eventId],
    eventId !== null,
  );

  const e = data;
  const exceptionData = e?.exceptionData ?? e?.exception;

  return (
    <Drawer
      open={eventId !== null}
      onClose={onClose}
      title={
        <span className="flex items-center gap-2">
          Event detail
          {e && <SeverityBadge severity={e.severity} />}
        </span>
      }
      widthClass="max-w-3xl"
    >
      {loading && <LoadingBlock label="Loading event…" />}
      {error && <ErrorBanner error={error} onRetry={reload} />}
      {e && (
        <div>
          <div className="mb-3 flex flex-wrap items-center gap-2">
            <CopyButton text={e.eventId} label="Copy event ID" />
            {e.correlationId && (
              <button
                type="button"
                className="btn-secondary !px-2 !py-1 text-xs"
                onClick={() => {
                  if (onApplyCorrelation) onApplyCorrelation(e.correlationId!);
                  else navigate(`/logs?correlationId=${encodeURIComponent(e.correlationId!)}`);
                  onClose();
                }}
              >
                <Link2 className="h-3 w-3" /> View correlation
              </button>
            )}
            {e.traceId && (
              <button
                type="button"
                className="btn-secondary !px-2 !py-1 text-xs"
                onClick={() => {
                  navigate(`/traces/${encodeURIComponent(e.traceId!)}`);
                  onClose();
                }}
              >
                <GitBranch className="h-3 w-3" /> Open trace
              </button>
            )}
          </div>

          {e.message && (
            <p className="mb-3 whitespace-pre-wrap break-words rounded-md bg-gray-50 p-2.5 text-sm text-gray-800 dark:bg-gray-950 dark:text-gray-200">
              {e.message}
            </p>
          )}

          <dl className="grid grid-cols-2 gap-x-4 gap-y-2.5 sm:grid-cols-3">
            <Field label="Timestamp" value={`${formatDateTime(e.eventTimestamp)} (${relativeTime(e.eventTimestamp)})`} />
            <Field label="Event type" value={e.eventType} />
            <Field label="Status" value={e.status ? <StatusBadge status={e.status} /> : null} />
            <Field label="Project" value={e.projectName ?? e.projectId} />
            <Field label="Application" value={e.applicationName ?? e.applicationId} />
            <Field label="Environment" value={e.environmentName ?? e.environmentId} />
            <Field label="Module" value={e.module} />
            <Field label="Component" value={e.component} />
            <Field label="Action" value={e.actionName} />
            {/* identity fields stay visible even when empty so users learn they exist */}
            <Field label="Event ID" value={e.eventId} mono />
            <Field label="Correlation ID" value={e.correlationId ?? '—'} mono />
            <Field label="Trace ID" value={e.traceId ?? '—'} mono />
            <Field label="Span ID" value={e.spanId ?? '—'} mono />
            <Field label="Parent span" value={e.parentSpanId} mono />
            <Field label="User" value={e.userName ?? e.userId} />
            <Field label="User ID" value={e.userId} mono />
            <Field label="Session" value={e.sessionId} mono />
            <Field
              label="Entity"
              value={e.businessEntityType ? `${e.businessEntityType} / ${e.businessEntityId ?? '—'}` : '—'}
            />
            <Field label="Duration" value={e.durationMs != null ? formatDuration(e.durationMs) : null} />
            <Field label="DB duration" value={e.dbDurationMs != null ? formatDuration(e.dbDurationMs) : null} />
            <Field label="External duration" value={e.externalDurationMs != null ? formatDuration(e.externalDurationMs) : null} />
            <Field
              label="HTTP"
              value={
                e.http?.method || e.httpMethod
                  ? `${e.http?.method ?? e.httpMethod ?? ''} ${e.http?.route ?? e.httpRoute ?? ''} → ${e.http?.statusCode ?? e.httpStatusCode ?? '—'}`
                  : e.httpRoute
                    ? `${e.httpRoute} → ${e.httpStatusCode ?? '—'}`
                    : null
              }
              mono
            />
            <Field label="Client IP" value={e.http?.clientIp} mono />
            <Field label="Machine" value={e.machineName} />
            <Field label="App version" value={e.applicationVersion} />
            <Field label="Deployment" value={e.deploymentVersion} />
            <Field label="Exception type" value={e.exceptionType} mono />
            <Field label="Fingerprint" value={e.exceptionFingerprint ?? e.fingerprint} mono />
            <Field
              label="Sanitization"
              value={
                e.sanitizationApplied
                  ? `applied${e.fieldsSanitized ? ` (${e.fieldsSanitized} fields)` : ''}${e.truncated ? ', truncated' : ''}`
                  : e.truncated
                    ? 'truncated'
                    : null
              }
            />
          </dl>

          <JsonSection title="Properties" value={e.properties} />
          <JsonSection title="Request data" value={e.requestData} />
          <JsonSection title="Response data" value={e.responseData} />
          <JsonSection title="Exception data" value={exceptionData} />
        </div>
      )}
    </Drawer>
  );
}
