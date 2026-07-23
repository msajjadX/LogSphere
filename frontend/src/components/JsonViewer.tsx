import { useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { tryParseJson } from '../utils/format';

/**
 * Collapsible pretty-printed JSON, rendered strictly as text nodes
 * (never dangerouslySetInnerHTML) so stored XSS is inert.
 */
export function JsonViewer({ value, initialDepth = 2 }: { value: unknown; initialDepth?: number }) {
  const parsed = tryParseJson(value);
  if (parsed === undefined || parsed === null) {
    return <span className="font-mono text-xs text-gray-400">null</span>;
  }
  return (
    <div className="overflow-x-auto rounded-md border border-gray-200 bg-gray-50 p-2 font-mono text-xs leading-5 dark:border-gray-800 dark:bg-gray-950">
      <JsonNode value={parsed} depth={0} initialDepth={initialDepth} />
    </div>
  );
}

function JsonNode({ value, depth, initialDepth, propName }: { value: unknown; depth: number; initialDepth: number; propName?: string }) {
  const [open, setOpen] = useState(depth < initialDepth);

  const keyEl =
    propName !== undefined ? (
      <span className="text-purple-700 dark:text-purple-400">&quot;{propName}&quot;</span>
    ) : null;

  if (value === null) {
    return (
      <div style={{ paddingLeft: depth > 0 ? 14 : 0 }}>
        {keyEl}
        {keyEl && ': '}
        <span className="text-gray-400">null</span>
      </div>
    );
  }
  if (typeof value === 'string') {
    return (
      <div style={{ paddingLeft: depth > 0 ? 14 : 0 }} className="whitespace-pre-wrap break-all">
        {keyEl}
        {keyEl && ': '}
        <span className="text-emerald-700 dark:text-emerald-400">&quot;{value}&quot;</span>
      </div>
    );
  }
  if (typeof value === 'number' || typeof value === 'bigint') {
    return (
      <div style={{ paddingLeft: depth > 0 ? 14 : 0 }}>
        {keyEl}
        {keyEl && ': '}
        <span className="text-blue-700 dark:text-blue-400">{String(value)}</span>
      </div>
    );
  }
  if (typeof value === 'boolean') {
    return (
      <div style={{ paddingLeft: depth > 0 ? 14 : 0 }}>
        {keyEl}
        {keyEl && ': '}
        <span className="text-amber-700 dark:text-amber-400">{String(value)}</span>
      </div>
    );
  }

  const isArray = Array.isArray(value);
  const entries: [string, unknown][] = isArray
    ? (value as unknown[]).map((v, i) => [String(i), v] as [string, unknown])
    : Object.entries(value as Record<string, unknown>);
  const bracketOpen = isArray ? '[' : '{';
  const bracketClose = isArray ? ']' : '}';

  if (entries.length === 0) {
    return (
      <div style={{ paddingLeft: depth > 0 ? 14 : 0 }}>
        {keyEl}
        {keyEl && ': '}
        <span className="text-gray-500">
          {bracketOpen}
          {bracketClose}
        </span>
      </div>
    );
  }

  return (
    <div style={{ paddingLeft: depth > 0 ? 14 : 0 }}>
      <button
        type="button"
        className="inline-flex items-center gap-0.5 text-gray-600 hover:text-gray-900 dark:text-gray-400 dark:hover:text-gray-100"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-label={open ? 'Collapse' : 'Expand'}
      >
        {open ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
        {keyEl}
        {keyEl && ': '}
        <span className="text-gray-500">{bracketOpen}</span>
        {!open && (
          <span className="text-gray-400">
            {isArray ? ` ${entries.length} items ` : ` ${entries.length} keys `}
            {bracketClose}
          </span>
        )}
      </button>
      {open && (
        <>
          {entries.map(([k, v]) => (
            <JsonNode key={k} value={v} depth={depth + 1} initialDepth={initialDepth} propName={isArray ? undefined : k} />
          ))}
          <div className="text-gray-500">{bracketClose}</div>
        </>
      )}
    </div>
  );
}
