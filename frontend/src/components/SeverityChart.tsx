import { useMemo } from 'react';
import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import type { TimeseriesPoint } from '../api/types';
import { SEVERITIES } from '../api/types';
import { useTheme } from '../context/ThemeContext';
import { formatTime, formatDateTime, formatNumber } from '../utils/format';
import { EmptyState } from './Feedback';

export const SEVERITY_COLORS: Record<string, string> = {
  Trace: '#9ca3af',
  Debug: '#6b7280',
  Information: '#3b82f6',
  Warning: '#f59e0b',
  Error: '#ef4444',
  Critical: '#991b1b',
};

interface SeverityChartProps {
  points: TimeseriesPoint[];
  height?: number;
}

/**
 * Stacked severity-over-time bar chart. The timeseries endpoint returns
 * flat `{bucket, value, severity?}` points; we pivot to one row per bucket
 * with a column per severity (single "Events" series when severity absent).
 */
export function SeverityChart({ points, height = 280 }: SeverityChartProps) {
  const { theme } = useTheme();
  const { rows, series } = useMemo(() => {
    const byBucket = new Map<string, Record<string, number | string>>();
    const seriesSet = new Set<string>();
    for (const p of points ?? []) {
      const key = p.bucket;
      let row = byBucket.get(key);
      if (!row) {
        row = { bucket: key };
        byBucket.set(key, row);
      }
      const s = p.severity ?? 'Events';
      seriesSet.add(s);
      row[s] = ((row[s] as number) ?? 0) + p.value;
    }
    const rows = [...byBucket.values()].sort((a, b) => String(a.bucket).localeCompare(String(b.bucket)));
    const ordered = [...SEVERITIES.filter((s) => seriesSet.has(s)), ...[...seriesSet].filter((s) => !SEVERITIES.includes(s as never))];
    return { rows, series: ordered };
  }, [points]);

  if (rows.length === 0) {
    return <EmptyState message="No events in this time range" />;
  }

  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart data={rows} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
        <CartesianGrid strokeDasharray="3 3" stroke="currentColor" className="text-gray-200 dark:text-gray-800" />
        <XAxis
          dataKey="bucket"
          tickFormatter={(v) => formatTime(String(v))}
          tick={{ fontSize: 11 }}
          stroke="#9ca3af"
          minTickGap={40}
        />
        <YAxis tick={{ fontSize: 11 }} stroke="#9ca3af" width={48} tickFormatter={(v) => formatNumber(Number(v))} />
        <Tooltip
          labelFormatter={(v) => formatDateTime(String(v))}
          contentStyle={
            theme === 'dark'
              ? {
                  backgroundColor: 'rgb(17 24 39)',
                  border: '1px solid rgb(55 65 81)',
                  borderRadius: 6,
                  fontSize: 12,
                  color: '#f9fafb',
                }
              : {
                  backgroundColor: '#ffffff',
                  border: '1px solid rgb(229 231 235)',
                  borderRadius: 6,
                  fontSize: 12,
                  color: '#111827',
                  boxShadow: '0 4px 12px rgb(0 0 0 / 0.08)',
                }
          }
          labelStyle={{ color: theme === 'dark' ? '#f9fafb' : '#111827', fontWeight: 600 }}
        />
        <Legend wrapperStyle={{ fontSize: 12 }} />
        {series.map((s) => (
          <Bar key={s} dataKey={s} stackId="sev" fill={SEVERITY_COLORS[s] ?? '#6366f1'} maxBarSize={28} />
        ))}
      </BarChart>
    </ResponsiveContainer>
  );
}
