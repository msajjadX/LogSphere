import { AlertTriangle, Inbox, Loader2, RefreshCw } from 'lucide-react';
import type { ApiError } from '../api/client';

export function Spinner({ className = 'h-5 w-5' }: { className?: string }) {
  return <Loader2 aria-label="Loading" className={`animate-spin text-indigo-500 ${className}`} />;
}

export function LoadingBlock({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="flex items-center justify-center gap-2 py-12 text-sm text-gray-500 dark:text-gray-400">
      <Spinner /> {label}
    </div>
  );
}

export function Skeleton({ className = '' }: { className?: string }) {
  return <div className={`animate-pulse rounded bg-gray-200 dark:bg-gray-800 ${className}`} />;
}

export function EmptyState({ message, hint }: { message: string; hint?: string }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 py-12 text-center">
      <Inbox className="h-8 w-8 text-gray-300 dark:text-gray-600" />
      <p className="text-sm font-medium text-gray-600 dark:text-gray-300">{message}</p>
      {hint && <p className="text-xs text-gray-400 dark:text-gray-500">{hint}</p>}
    </div>
  );
}

export function ErrorBanner({ error, onRetry }: { error: ApiError | Error | string; onRetry?: () => void }) {
  const message = typeof error === 'string' ? error : error.message;
  const code = typeof error === 'object' && 'statusCode' in error ? (error as ApiError).statusCode : undefined;
  return (
    <div
      role="alert"
      className="flex items-start justify-between gap-3 rounded-md border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/50 dark:text-red-300"
    >
      <div className="flex items-start gap-2">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
        <div>
          <span>{message}</span>
          {code && <span className="ml-2 font-mono text-xs opacity-70">{code}</span>}
        </div>
      </div>
      {onRetry && (
        <button type="button" onClick={onRetry} className="btn-secondary shrink-0 !px-2 !py-1 text-xs">
          <RefreshCw className="h-3 w-3" /> Retry
        </button>
      )}
    </div>
  );
}
