const dtFormat = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
});

const timeFormat = new Intl.DateTimeFormat(undefined, {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
});

export function formatDateTime(iso?: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return dtFormat.format(d);
}

// The `fractionalSecondDigits` Intl option works at runtime but isn't in this
// project's TS lib, so we append the milliseconds ourselves. The base formats
// end on the seconds token (hour12:false, no tz), so a ".mmm" suffix lands
// correctly right after the seconds.
const pad3 = (n: number) => n.toString().padStart(3, '0');

/** Full date + time down to the millisecond, e.g. "Jul 22, 2026, 16:07:43.128". */
export function formatDateTimeMs(iso?: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return `${dtFormat.format(d)}.${pad3(d.getMilliseconds())}`;
}

/** Time-of-day down to the millisecond, e.g. "16:07:43.128". */
export function formatTimeMs(iso?: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return `${timeFormat.format(d)}.${pad3(d.getMilliseconds())}`;
}

export function formatTime(iso?: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return iso;
  return timeFormat.format(d);
}

export function relativeTime(iso?: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '';
  const diff = Date.now() - d.getTime();
  const abs = Math.abs(diff);
  const suffix = diff >= 0 ? 'ago' : 'from now';
  const sec = Math.round(abs / 1000);
  if (sec < 60) return `${sec}s ${suffix}`;
  const min = Math.round(sec / 60);
  if (min < 60) return `${min}m ${suffix}`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr}h ${suffix}`;
  const days = Math.round(hr / 24);
  if (days < 30) return `${days}d ${suffix}`;
  const months = Math.round(days / 30);
  if (months < 12) return `${months}mo ${suffix}`;
  return `${Math.round(months / 12)}y ${suffix}`;
}

export function formatDuration(ms?: number | null): string {
  if (ms === undefined || ms === null || isNaN(ms)) return '—';
  if (ms < 1) return `${ms.toFixed(2)} ms`;
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(2)} s`;
  const min = Math.floor(ms / 60_000);
  const sec = Math.round((ms % 60_000) / 1000);
  return `${min}m ${sec}s`;
}

const numFormat = new Intl.NumberFormat();

export function formatNumber(n?: number | null): string {
  if (n === undefined || n === null || isNaN(n)) return '—';
  return numFormat.format(n);
}

export function formatCompact(n?: number | null): string {
  if (n === undefined || n === null || isNaN(n)) return '—';
  if (Math.abs(n) >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (Math.abs(n) >= 10_000) return `${(n / 1000).toFixed(1)}k`;
  return numFormat.format(n);
}

export function formatPercent(n?: number | null, digits = 2): string {
  if (n === undefined || n === null || isNaN(n)) return '—';
  // API contract: all rate fields are already percentages (0..100). Do NOT guess the
  // scale — the old "values <= 1 are fractions" heuristic inflated genuine sub-1% rates
  // by 100x (e.g. a real 0.47% error rate rendered as "47.00%").
  return `${n.toFixed(digits)}%`;
}

export function formatBytes(mb?: number | null): string {
  if (mb === undefined || mb === null || isNaN(mb)) return '—';
  if (mb >= 1024) return `${(mb / 1024).toFixed(1)} GB`;
  return `${mb.toFixed(1)} MB`;
}

/** Attempts to parse a string that may already be an object. */
export function tryParseJson(value: unknown): unknown {
  if (typeof value !== 'string') return value;
  const trimmed = value.trim();
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) return value;
  try {
    return JSON.parse(trimmed);
  } catch {
    return value;
  }
}

export function truncate(s: string | null | undefined, len = 120): string {
  if (!s) return '';
  return s.length > len ? `${s.slice(0, len)}…` : s;
}
