import { useEffect, useRef, useState } from 'react';
import { CalendarClock, ChevronDown } from 'lucide-react';
import { TIME_PRESETS, rangeLabel, type TimeRange } from '../context/TimeRangeContext';

interface TimeRangePickerProps {
  value: TimeRange;
  onChange: (r: TimeRange) => void;
  compact?: boolean;
}

function toLocalInput(iso?: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function TimeRangePicker({ value, onChange, compact }: TimeRangePickerProps) {
  const [open, setOpen] = useState(false);
  const [customFrom, setCustomFrom] = useState(() => toLocalInput(value.from));
  const [customTo, setCustomTo] = useState(() => toLocalInput(value.to));
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const applyCustom = () => {
    if (!customFrom || !customTo) return;
    const from = new Date(customFrom);
    const to = new Date(customTo);
    if (isNaN(from.getTime()) || isNaN(to.getTime()) || from >= to) return;
    onChange({ preset: 'custom', from: from.toISOString(), to: to.toISOString() });
    setOpen(false);
  };

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        className="btn-secondary"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <CalendarClock className="h-4 w-4 text-gray-400" />
        {!compact && <span>{rangeLabel(value)}</span>}
        <ChevronDown className="h-3.5 w-3.5 text-gray-400" />
      </button>
      {open && (
        <div className="absolute right-0 z-30 mt-1 w-72 rounded-md border border-gray-200 bg-white p-2 shadow-lg dark:border-gray-700 dark:bg-gray-900">
          <div className="grid grid-cols-2 gap-1">
            {TIME_PRESETS.map((p) => (
              <button
                key={p.key}
                type="button"
                className={`rounded px-2 py-1.5 text-left text-sm hover:bg-gray-100 dark:hover:bg-gray-800 ${
                  value.preset === p.key ? 'bg-indigo-50 font-medium text-indigo-700 dark:bg-indigo-950 dark:text-indigo-300' : ''
                }`}
                onClick={() => {
                  onChange({ preset: p.key });
                  setOpen(false);
                }}
              >
                {p.label}
              </button>
            ))}
          </div>
          <div className="mt-2 border-t border-gray-200 pt-2 dark:border-gray-800">
            <p className="label">Custom range (local time)</p>
            <div className="space-y-1.5">
              <label className="block">
                <span className="sr-only">From</span>
                <input
                  type="datetime-local"
                  className="input"
                  value={customFrom}
                  onChange={(e) => setCustomFrom(e.target.value)}
                  aria-label="Custom range from"
                />
              </label>
              <label className="block">
                <span className="sr-only">To</span>
                <input
                  type="datetime-local"
                  className="input"
                  value={customTo}
                  onChange={(e) => setCustomTo(e.target.value)}
                  aria-label="Custom range to"
                />
              </label>
              <button type="button" className="btn-primary w-full" onClick={applyCustom}>
                Apply custom range
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
