import type { LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';
import { Skeleton } from './Feedback';

const tones = {
  default: 'text-gray-900 dark:text-gray-100',
  good: 'text-emerald-600 dark:text-emerald-400',
  warn: 'text-amber-600 dark:text-amber-400',
  bad: 'text-red-600 dark:text-red-400',
} as const;

interface StatCardProps {
  label: string;
  value: ReactNode;
  sub?: ReactNode;
  icon?: LucideIcon;
  tone?: keyof typeof tones;
  loading?: boolean;
}

export function StatCard({ label, value, sub, icon: Icon, tone = 'default', loading }: StatCardProps) {
  return (
    <div className="card px-4 py-3">
      <div className="flex items-center justify-between gap-2">
        <p className="truncate text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">{label}</p>
        {Icon && <Icon className="h-4 w-4 shrink-0 text-gray-300 dark:text-gray-600" />}
      </div>
      {loading ? (
        <Skeleton className="mt-2 h-7 w-20" />
      ) : (
        <p className={`mt-1 truncate text-2xl font-semibold tabular-nums ${tones[tone]}`}>{value}</p>
      )}
      {sub && !loading && <p className="mt-0.5 truncate text-xs text-gray-500 dark:text-gray-400">{sub}</p>}
    </div>
  );
}
