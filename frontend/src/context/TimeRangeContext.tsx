import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

export interface TimeRange {
  preset: string; // '15m' | '1h' | '6h' | '24h' | '7d' | '30d' | 'custom'
  from?: string; // ISO — only for custom
  to?: string; // ISO — only for custom
}

export const TIME_PRESETS: { key: string; label: string; minutes: number }[] = [
  { key: '15m', label: 'Last 15 min', minutes: 15 },
  { key: '1h', label: 'Last 1 hour', minutes: 60 },
  { key: '6h', label: 'Last 6 hours', minutes: 360 },
  { key: '24h', label: 'Last 24 hours', minutes: 1440 },
  { key: '7d', label: 'Last 7 days', minutes: 10080 },
  { key: '30d', label: 'Last 30 days', minutes: 43200 },
];

export function resolveRange(range: TimeRange): { from: string; to: string } {
  if (range.preset === 'custom' && range.from && range.to) {
    return { from: range.from, to: range.to };
  }
  const preset = TIME_PRESETS.find((p) => p.key === range.preset) ?? TIME_PRESETS[3];
  const to = new Date();
  const from = new Date(to.getTime() - preset.minutes * 60_000);
  return { from: from.toISOString(), to: to.toISOString() };
}

export function rangeLabel(range: TimeRange): string {
  if (range.preset === 'custom') return 'Custom range';
  return TIME_PRESETS.find((p) => p.key === range.preset)?.label ?? 'Last 24 hours';
}

/** Suggests a chart bucket interval (minutes) for the range span. */
export function suggestedInterval(range: TimeRange): number {
  const { from, to } = resolveRange(range);
  const spanMin = (new Date(to).getTime() - new Date(from).getTime()) / 60_000;
  if (spanMin <= 60) return 1;
  if (spanMin <= 360) return 5;
  if (spanMin <= 1440) return 30;
  if (spanMin <= 10080) return 120;
  return 720;
}

interface TimeRangeContextValue {
  range: TimeRange;
  setRange: (r: TimeRange) => void;
}

const TimeRangeContext = createContext<TimeRangeContextValue | null>(null);

const RANGE_KEY = 'logsphere.timerange';

function loadSavedRange(): TimeRange {
  try {
    const raw = localStorage.getItem(RANGE_KEY);
    if (!raw) return { preset: '24h' };
    const parsed = JSON.parse(raw) as TimeRange;
    if (parsed.preset === 'custom' && parsed.from && parsed.to) return parsed;
    if (TIME_PRESETS.some((p) => p.key === parsed.preset)) return { preset: parsed.preset };
  } catch {
    /* corrupted value — fall through to default */
  }
  return { preset: '24h' };
}

export function TimeRangeProvider({ children }: { children: ReactNode }) {
  // restore the user's last selection so a page refresh doesn't reset to 24h
  const [range, setRange] = useState<TimeRange>(loadSavedRange);
  useEffect(() => {
    try {
      localStorage.setItem(RANGE_KEY, JSON.stringify(range));
    } catch {
      /* storage unavailable (private mode) — selection just won't persist */
    }
  }, [range]);
  const value = useMemo(() => ({ range, setRange }), [range]);
  return <TimeRangeContext.Provider value={value}>{children}</TimeRangeContext.Provider>;
}

export function useTimeRange(): TimeRangeContextValue {
  const ctx = useContext(TimeRangeContext);
  if (!ctx) throw new Error('useTimeRange must be used within TimeRangeProvider');
  return ctx;
}
