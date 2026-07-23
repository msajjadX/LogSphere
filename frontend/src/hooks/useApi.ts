import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ApiError, toApiError } from '../api/client';

interface UseApiResult<T> {
  data: T | null;
  /** true only while fetching with nothing to show yet (first visit) */
  loading: boolean;
  /** true while revalidating in the background with stale data on screen */
  refreshing: boolean;
  error: ApiError | null;
  reload: () => void;
  setData: (v: T | null) => void;
}

// Session-lifetime response cache (stale-while-revalidate). Keyed per call site
// (fn source text is stable for a given call site) + deps values, so returning
// to a page shows the last data instantly while a fresh copy loads in the
// background — instead of blanking the page into a spinner, which reads as a
// full page refresh.
const responseCache = new Map<string, unknown>();

function safeStringify(v: unknown): string {
  try {
    return JSON.stringify(v) ?? 'undefined';
  } catch {
    return String(v);
  }
}

// Global in-flight counter so the shell can show a slim progress bar while any
// request is running (AppShell listens for 'logsphere:activity').
let activeRequests = 0;
function trackActivity(delta: 1 | -1) {
  activeRequests = Math.max(0, activeRequests + delta);
  window.dispatchEvent(new CustomEvent('logsphere:activity', { detail: activeRequests }));
}

/**
 * Minimal data-fetch hook. `fn` is captured in a ref so callers do not need
 * to memoize it; the request re-runs when `deps` change or `reload()` is called.
 * Previously fetched data (same call site + deps) is shown immediately while
 * revalidating; `loading` is true only when there is nothing to show.
 */
export function useApi<T>(fn: () => Promise<T>, deps: unknown[] = [], enabled = true): UseApiResult<T> {
  const fnRef = useRef(fn);
  fnRef.current = fn;

  // eslint-disable-next-line react-hooks/exhaustive-deps
  const cacheKey = useMemo(() => `${fn.toString()}|${safeStringify(deps)}`, deps);
  const cached = enabled ? (responseCache.get(cacheKey) as T | undefined) : undefined;

  const [data, setDataState] = useState<T | null>(cached ?? null);
  const [loading, setLoading] = useState(enabled && cached === undefined);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [tick, setTick] = useState(0);
  const dataRef = useRef<T | null>(data);
  dataRef.current = data;

  useEffect(() => {
    if (!enabled) return;
    let alive = true;
    const hit = responseCache.get(cacheKey) as T | undefined;
    if (hit !== undefined) {
      // stale data available (from this or a previous mount) — show it, revalidate quietly
      setDataState(hit);
      setLoading(false);
      setRefreshing(true);
    } else if (dataRef.current !== null) {
      // deps changed with no cache for the new key — keep previous data on
      // screen while the new request runs (no spinner flash)
      setRefreshing(true);
    } else {
      setLoading(true);
    }
    setError(null);
    trackActivity(1);
    fnRef
      .current()
      .then((d) => {
        responseCache.set(cacheKey, d);
        if (alive) {
          setDataState(d);
          setLoading(false);
          setRefreshing(false);
        }
      })
      .catch((e) => {
        if (alive) {
          setError(toApiError(e));
          setLoading(false);
          setRefreshing(false);
        }
      })
      .finally(() => trackActivity(-1));
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cacheKey, tick, enabled]);

  const reload = useCallback(() => setTick((t) => t + 1), []);

  const setData = useCallback(
    (v: T | null) => {
      setDataState(v);
      if (v === null) responseCache.delete(cacheKey);
      else responseCache.set(cacheKey, v);
    },
    [cacheKey],
  );

  // Refetch on an app-wide refresh signal — e.g. clicking the active nav item (see AppShell).
  // Only the current page's components are mounted, so this naturally scopes to what's on screen.
  useEffect(() => {
    if (!enabled) return;
    const onRefresh = () => setTick((t) => t + 1);
    window.addEventListener('logsphere:refresh', onRefresh);
    return () => window.removeEventListener('logsphere:refresh', onRefresh);
  }, [enabled]);

  return { data, loading, refreshing, error, reload, setData };
}

/** Auto-refresh helper: calls reload() every `ms` while mounted. */
export function useAutoRefresh(reload: () => void, ms: number, enabled = true): void {
  useEffect(() => {
    if (!enabled) return;
    const id = window.setInterval(reload, ms);
    return () => window.clearInterval(id);
  }, [reload, ms, enabled]);
}
