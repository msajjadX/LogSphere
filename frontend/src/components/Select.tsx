import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Check, ChevronDown } from 'lucide-react';

export interface Option {
  value: string;
  label: string;
}

interface SelectProps {
  label?: string;
  value: string;
  options: Option[];
  onChange: (value: string) => void;
  placeholder?: string;
  className?: string;
  disabled?: boolean;
}

/** Labeled native select (accessible, keyboard friendly). */
export function Select({ label, value, options, onChange, placeholder, className = '', disabled }: SelectProps) {
  const id = useId();
  return (
    <div className={className}>
      {label && (
        <label htmlFor={id} className="label">
          {label}
        </label>
      )}
      <select id={id} className="input" value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled}>
        {placeholder !== undefined && <option value="">{placeholder}</option>}
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </div>
  );
}

interface TextFieldProps {
  label?: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: string;
  className?: string;
  required?: boolean;
  autoFocus?: boolean;
  mono?: boolean;
}

export function TextField({
  label,
  value,
  onChange,
  placeholder,
  type = 'text',
  className = '',
  required,
  autoFocus,
  mono,
}: TextFieldProps) {
  const id = useId();
  return (
    <div className={className}>
      {label && (
        <label htmlFor={id} className="label">
          {label}
        </label>
      )}
      <input
        id={id}
        type={type}
        className={`input ${mono ? 'font-mono' : ''}`}
        value={value}
        placeholder={placeholder}
        required={required}
        autoFocus={autoFocus}
        onChange={(e) => onChange(e.target.value)}
      />
    </div>
  );
}

export function CheckboxField({
  label,
  checked,
  onChange,
  className = '',
}: {
  label: ReactNode;
  checked: boolean;
  onChange: (v: boolean) => void;
  className?: string;
}) {
  const id = useId();
  return (
    <label htmlFor={id} className={`flex cursor-pointer items-center gap-2 text-sm ${className}`}>
      <input
        id={id}
        type="checkbox"
        className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500 dark:border-gray-600 dark:bg-gray-800"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span>{label}</span>
    </label>
  );
}

interface MultiSelectProps {
  label?: string;
  values: string[];
  options: Option[];
  onChange: (values: string[]) => void;
  placeholder?: string;
  className?: string;
}

/** Dropdown multi-select with checkboxes. */
export function MultiSelect({ label, values, options, onChange, placeholder = 'All', className = '' }: MultiSelectProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const id = useId();

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

  const toggle = (v: string) => {
    onChange(values.includes(v) ? values.filter((x) => x !== v) : [...values, v]);
  };

  const summary =
    values.length === 0
      ? placeholder
      : values.length <= 2
        ? values.join(', ')
        : `${values.length} selected`;

  return (
    <div className={`relative ${className}`} ref={ref}>
      {label && (
        <label htmlFor={id} className="label">
          {label}
        </label>
      )}
      <button
        id={id}
        type="button"
        className="input flex items-center justify-between gap-2 text-left"
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <span className={`truncate ${values.length === 0 ? 'text-gray-400 dark:text-gray-500' : ''}`}>{summary}</span>
        <ChevronDown className="h-3.5 w-3.5 shrink-0 text-gray-400" />
      </button>
      {open && (
        <div
          role="listbox"
          className="absolute z-30 mt-1 max-h-64 w-full min-w-[12rem] overflow-y-auto rounded-md border border-gray-200 bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
        >
          {values.length > 0 && (
            <button
              type="button"
              className="block w-full px-3 py-1.5 text-left text-xs text-indigo-600 hover:bg-gray-50 dark:text-indigo-400 dark:hover:bg-gray-800"
              onClick={() => onChange([])}
            >
              Clear selection
            </button>
          )}
          {options.map((o) => {
            const selected = values.includes(o.value);
            return (
              <button
                key={o.value}
                type="button"
                role="option"
                aria-selected={selected}
                className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800"
                onClick={() => toggle(o.value)}
              >
                <span
                  className={`flex h-4 w-4 items-center justify-center rounded border ${
                    selected
                      ? 'border-indigo-600 bg-indigo-600 text-white'
                      : 'border-gray-300 dark:border-gray-600'
                  }`}
                >
                  {selected && <Check className="h-3 w-3" />}
                </span>
                <span className="truncate">{o.label}</span>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
