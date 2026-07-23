import type { ReactNode } from 'react';

const severityStyles: Record<string, string> = {
  Trace: 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400',
  Debug: 'bg-gray-200 text-gray-700 dark:bg-gray-700 dark:text-gray-300',
  Information: 'bg-blue-100 text-blue-700 dark:bg-blue-900/50 dark:text-blue-300',
  Warning: 'bg-amber-100 text-amber-800 dark:bg-amber-900/50 dark:text-amber-300',
  Error: 'bg-red-100 text-red-700 dark:bg-red-900/50 dark:text-red-300',
  Critical: 'bg-red-800 text-white dark:bg-red-800 dark:text-red-100',
};

export function SeverityBadge({ severity }: { severity?: string | null }) {
  if (!severity) return <span className="text-gray-400">—</span>;
  const cls = severityStyles[severity] ?? severityStyles.Trace;
  return (
    <span className={`inline-flex items-center rounded px-1.5 py-0.5 text-xs font-semibold ${cls}`}>{severity}</span>
  );
}

/** Left border color per severity — used on table rows / list items. */
export function severityAccent(severity?: string | null): string {
  switch (severity) {
    case 'Critical':
      return 'border-l-red-800';
    case 'Error':
      return 'border-l-red-500';
    case 'Warning':
      return 'border-l-amber-500';
    case 'Information':
      return 'border-l-blue-500';
    default:
      return 'border-l-gray-300 dark:border-l-gray-700';
  }
}

const toneStyles = {
  gray: 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300',
  blue: 'bg-blue-100 text-blue-700 dark:bg-blue-900/50 dark:text-blue-300',
  green: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/50 dark:text-emerald-300',
  amber: 'bg-amber-100 text-amber-800 dark:bg-amber-900/50 dark:text-amber-300',
  red: 'bg-red-100 text-red-700 dark:bg-red-900/50 dark:text-red-300',
  purple: 'bg-purple-100 text-purple-700 dark:bg-purple-900/50 dark:text-purple-300',
  indigo: 'bg-indigo-100 text-indigo-700 dark:bg-indigo-900/50 dark:text-indigo-300',
} as const;

export type BadgeTone = keyof typeof toneStyles;

export function Badge({ tone = 'gray', children }: { tone?: BadgeTone; children: ReactNode }) {
  return (
    <span className={`inline-flex items-center rounded px-1.5 py-0.5 text-xs font-medium ${toneStyles[tone]}`}>
      {children}
    </span>
  );
}

export function exceptionStatusTone(status?: string | null): BadgeTone {
  switch (status) {
    case 'New':
      return 'red';
    case 'Investigating':
      return 'amber';
    case 'Identified':
      return 'purple';
    case 'Resolved':
      return 'green';
    case 'Ignored':
      return 'gray';
    case 'Recurring':
      return 'indigo';
    default:
      return 'gray';
  }
}

export function alertStateTone(state?: string | null): BadgeTone {
  switch (state) {
    case 'Open':
      return 'red';
    case 'Acknowledged':
      return 'amber';
    case 'Resolved':
      return 'green';
    default:
      return 'gray';
  }
}

export function StatusBadge({ status }: { status?: string | null }) {
  if (!status) return <span className="text-gray-400">—</span>;
  const tone: BadgeTone =
    status === 'Completed'
      ? 'green'
      : status === 'Failed' || status === 'TimedOut'
        ? 'red'
        : status === 'Retrying' || status === 'PartiallyCompleted'
          ? 'amber'
          : 'gray';
  return <Badge tone={tone}>{status}</Badge>;
}
