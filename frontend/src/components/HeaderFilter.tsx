import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Check, Filter, X } from 'lucide-react';
import type { Option } from './Select';

/**
 * Excel-style column filter attached to a table header.
 *
 * - `multi`: a checkbox list of known values (with search + select-all), like
 *   Excel's AutoFilter value list. Applies via the page's server-side filter.
 * - `text`: a "contains / starts with" input for high-cardinality columns.
 *
 * Filters apply immediately on "Apply" (no separate Search click) and stay in
 * sync with the page's top filter bar because both edit the same filter state.
 */
export type ColumnFilter =
  | {
      kind: 'multi';
      options: Option[];
      values: string[];
      onApply: (values: string[]) => void;
    }
  | {
      kind: 'text';
      value: string;
      onApply: (value: string) => void;
      placeholder?: string;
      mono?: boolean;
      /** Distinct values from the loaded rows, shown as clickable quick picks (Excel-style). */
      suggestions?: string[];
    };

export function headerFilterActive(f: ColumnFilter): boolean {
  return f.kind === 'multi' ? f.values.length > 0 : f.value.trim() !== '';
}

const POPOVER_W = 232;

export function HeaderFilterButton({ filter, label }: { filter: ColumnFilter; label: string }) {
  const [open, setOpen] = useState(false);
  const btnRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);

  // draft state (committed on Apply, like Excel's OK)
  const [draftValues, setDraftValues] = useState<string[]>([]);
  const [draftText, setDraftText] = useState('');
  const [search, setSearch] = useState('');

  const active = headerFilterActive(filter);

  const openPopover = () => {
    if (filter.kind === 'multi') setDraftValues(filter.values);
    else setDraftText(filter.value);
    setSearch('');
    setOpen(true);
  };

  // fixed positioning so the popover escapes the table's overflow-x container
  useLayoutEffect(() => {
    if (!open || !btnRef.current) return;
    const r = btnRef.current.getBoundingClientRect();
    const left = Math.min(r.left, window.innerWidth - POPOVER_W - 8);
    setPos({ top: r.bottom + 4, left: Math.max(8, left) });
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (
        popRef.current && !popRef.current.contains(e.target as Node) &&
        btnRef.current && !btnRef.current.contains(e.target as Node)
      )
        setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    // Close only when the PAGE scrolls (the fixed popover would detach from its
    // anchor) — never when the user scrolls the value list inside the popover.
    const onScroll = (e: Event) => {
      if (popRef.current && e.target instanceof Node && popRef.current.contains(e.target)) return;
      setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    window.addEventListener('scroll', onScroll, true);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('scroll', onScroll, true);
    };
  }, [open]);

  const apply = () => {
    if (filter.kind === 'multi') filter.onApply(draftValues);
    else filter.onApply(draftText.trim());
    setOpen(false);
  };
  const clear = () => {
    if (filter.kind === 'multi') filter.onApply([]);
    else filter.onApply('');
    setOpen(false);
  };

  const visibleOptions =
    filter.kind === 'multi'
      ? filter.options.filter((o) => o.label.toLowerCase().includes(search.trim().toLowerCase()))
      : [];
  const allVisibleSelected =
    visibleOptions.length > 0 && visibleOptions.every((o) => draftValues.includes(o.value));

  const toggleAll = () => {
    if (allVisibleSelected) {
      const vis = new Set(visibleOptions.map((o) => o.value));
      setDraftValues((v) => v.filter((x) => !vis.has(x)));
    } else {
      setDraftValues((v) => [...new Set([...v, ...visibleOptions.map((o) => o.value)])]);
    }
  };
  const toggleOne = (value: string) =>
    setDraftValues((v) => (v.includes(value) ? v.filter((x) => x !== value) : [...v, value]));

  return (
    <>
      <button
        ref={btnRef}
        type="button"
        aria-label={`Filter by ${label}`}
        aria-expanded={open}
        title={`Filter by ${label}`}
        className={`rounded p-0.5 transition-colors ${
          active
            ? 'text-indigo-600 hover:text-indigo-700 dark:text-indigo-400 dark:hover:text-indigo-300'
            : 'text-gray-300 hover:text-gray-600 dark:text-gray-600 dark:hover:text-gray-300'
        }`}
        onClick={(e) => {
          e.stopPropagation();
          if (open) setOpen(false);
          else openPopover();
        }}
      >
        <Filter className={`h-3 w-3 ${active ? 'fill-current' : ''}`} />
      </button>

      {open && pos && (
        <div
          ref={popRef}
          role="dialog"
          aria-label={`Filter ${label}`}
          style={{ position: 'fixed', top: pos.top, left: pos.left, width: POPOVER_W, zIndex: 60 }}
          className="rounded-md border border-gray-200 bg-white p-2 text-left shadow-xl dark:border-gray-700 dark:bg-gray-900"
          onClick={(e) => e.stopPropagation()}
        >
          <div className="mb-1.5 flex items-center justify-between">
            <span className="text-[11px] font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">
              Filter: {label}
            </span>
            <button
              type="button"
              aria-label="Close filter"
              className="rounded p-0.5 text-gray-400 hover:text-gray-700 dark:hover:text-gray-200"
              onClick={() => setOpen(false)}
            >
              <X className="h-3 w-3" />
            </button>
          </div>

          {filter.kind === 'multi' ? (
            <>
              {filter.options.length > 5 && (
                <input
                  type="text"
                  className="input mb-1.5 !py-1 text-xs"
                  placeholder="Search values…"
                  value={search}
                  autoFocus
                  onChange={(e) => setSearch(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') apply();
                  }}
                />
              )}
              <div className="max-h-52 overflow-y-auto">
                {visibleOptions.length > 1 && (
                  <CheckRow label="(Select all)" checked={allVisibleSelected} onToggle={toggleAll} bold />
                )}
                {visibleOptions.length === 0 && (
                  <p className="px-1.5 py-2 text-xs text-gray-400">No values match</p>
                )}
                {visibleOptions.map((o) => (
                  <CheckRow
                    key={o.value}
                    label={o.label}
                    checked={draftValues.includes(o.value)}
                    onToggle={() => toggleOne(o.value)}
                  />
                ))}
              </div>
            </>
          ) : (
            <>
              <input
                type="text"
                className={`input !py-1 text-xs ${filter.mono ? 'font-mono' : ''}`}
                placeholder={filter.placeholder ?? 'Contains…'}
                value={draftText}
                autoFocus
                onChange={(e) => setDraftText(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') apply();
                }}
              />
              {(filter.suggestions?.length ?? 0) > 0 && (
                <div className="mt-1.5 max-h-44 overflow-y-auto border-t border-gray-100 pt-1 dark:border-gray-800">
                  <p className="px-1.5 pb-0.5 text-[10px] font-semibold uppercase tracking-wide text-gray-400">
                    Values in results
                  </p>
                  {filter
                    .suggestions!.filter((s) => s.toLowerCase().includes(draftText.trim().toLowerCase()))
                    .slice(0, 20)
                    .map((s) => (
                      <button
                        key={s}
                        type="button"
                        className={`block w-full truncate rounded px-1.5 py-1 text-left text-xs hover:bg-gray-50 dark:hover:bg-gray-800 ${filter.mono ? 'font-mono' : ''}`}
                        title={s}
                        onClick={() => {
                          filter.onApply(s);
                          setOpen(false);
                        }}
                      >
                        {s}
                      </button>
                    ))}
                </div>
              )}
            </>
          )}

          <div className="mt-2 flex items-center justify-between gap-2 border-t border-gray-100 pt-2 dark:border-gray-800">
            <button
              type="button"
              className="text-xs text-gray-500 hover:text-red-600 disabled:opacity-40 dark:text-gray-400 dark:hover:text-red-400"
              onClick={clear}
              disabled={!active}
            >
              Clear
            </button>
            <button type="button" className="btn-primary !px-2.5 !py-1 text-xs" onClick={apply}>
              Apply
            </button>
          </div>
        </div>
      )}
    </>
  );
}

function CheckRow({
  label,
  checked,
  onToggle,
  bold,
}: {
  label: string;
  checked: boolean;
  onToggle: () => void;
  bold?: boolean;
}) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={checked}
      className="flex w-full items-center gap-2 rounded px-1.5 py-1 text-left text-xs hover:bg-gray-50 dark:hover:bg-gray-800"
      onClick={onToggle}
    >
      <span
        className={`flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded border ${
          checked ? 'border-indigo-600 bg-indigo-600 text-white' : 'border-gray-300 dark:border-gray-600'
        }`}
      >
        {checked && <Check className="h-2.5 w-2.5" />}
      </span>
      <span className={`truncate ${bold ? 'font-semibold' : ''}`}>{label}</span>
    </button>
  );
}
