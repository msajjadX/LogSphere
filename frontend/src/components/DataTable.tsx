import { useState, type ReactNode } from 'react';
import { ChevronRight } from 'lucide-react';
import { EmptyState, Skeleton } from './Feedback';
import { HeaderFilterButton, type ColumnFilter } from './HeaderFilter';

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  className?: string;
  headerClassName?: string;
  /** Allow cell content to wrap (default: cells stay on one line). */
  wrap?: boolean;
  /** This column absorbs the table's leftover width. Mark at most one per table. */
  expand?: boolean;
  /** Excel-style filter dropdown attached to this column's header. */
  filter?: ColumnFilter;
}

interface DataTableProps<T> {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string | number;
  onRowClick?: (row: T) => void;
  rowClassName?: (row: T) => string;
  loading?: boolean;
  skeletonRows?: number;
  emptyMessage?: string;
  emptyHint?: string;
  footer?: ReactNode;
  /**
   * When set, rows sharing a non-null key collapse into one expandable "leader"
   * row; expanding reveals the rest. Return null/undefined to leave a row on its
   * own. Groups with a single member also render as a plain row.
   */
  groupBy?: (row: T) => string | number | null | undefined;
  /** Picks the representative (collapsed) row for a group. Default: first row. */
  groupLeader?: (members: T[]) => T;
  /** Rendered next to the chevron on a leader row, e.g. a member-count badge. */
  groupBadge?: (members: T[]) => ReactNode;
}

type DisplayRow<T> =
  | { kind: 'single'; row: T }
  | { kind: 'leader'; groupKey: string; members: T[]; leader: T }
  | { kind: 'child'; row: T };

function buildGroups<T>(
  rows: T[],
  groupBy: (row: T) => string | number | null | undefined,
): { key: string | null; members: T[] }[] {
  const groups: { key: string | null; members: T[] }[] = [];
  const index = new Map<string, number>();
  for (const row of rows) {
    const raw = groupBy(row);
    if (raw === null || raw === undefined) {
      groups.push({ key: null, members: [row] });
      continue;
    }
    const key = String(raw);
    const at = index.get(key);
    if (at === undefined) {
      index.set(key, groups.length);
      groups.push({ key, members: [row] });
    } else {
      groups[at].members.push(row);
    }
  }
  return groups;
}

export function DataTable<T>({
  columns,
  rows,
  rowKey,
  onRowClick,
  rowClassName,
  loading,
  skeletonRows = 6,
  emptyMessage = 'No results',
  emptyHint,
  footer,
  groupBy,
  groupLeader,
  groupBadge,
}: DataTableProps<T>) {
  const grouped = !!groupBy;
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const toggle = (key: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const display: DisplayRow<T>[] = [];
  if (!loading && grouped) {
    for (const g of buildGroups(rows, groupBy!)) {
      if (g.key === null || g.members.length === 1) {
        display.push({ kind: 'single', row: g.members[0] });
        continue;
      }
      const leader = groupLeader ? groupLeader(g.members) : g.members[0];
      display.push({ kind: 'leader', groupKey: g.key, members: g.members, leader });
      if (expanded.has(g.key)) {
        for (const m of g.members) if (m !== leader) display.push({ kind: 'child', row: m });
      }
    }
  } else if (!loading) {
    for (const row of rows) display.push({ kind: 'single', row });
  }

  const totalCols = columns.length + (grouped ? 1 : 0);

  const renderCells = (row: T) =>
    columns.map((c) => (
      <td
        key={c.key}
        className={`px-3 py-2 align-top ${c.wrap ? '' : 'whitespace-nowrap'} ${c.expand ? 'w-full' : ''} ${c.className ?? ''}`}
      >
        {c.render(row)}
      </td>
    ));

  const rowInteractionProps = (row: T) => ({
    onClick: onRowClick ? () => onRowClick(row) : undefined,
    tabIndex: onRowClick ? 0 : undefined,
    onKeyDown: onRowClick
      ? (e: React.KeyboardEvent) => {
          if (e.key === 'Enter') onRowClick(row);
        }
      : undefined,
    className: onRowClick
      ? 'cursor-pointer hover:bg-gray-50 focus:bg-gray-50 dark:hover:bg-gray-800/50 dark:focus:bg-gray-800/50'
      : '',
  });

  return (
    <div className="overflow-x-auto">
      <table className="min-w-full divide-y divide-gray-200 text-sm dark:divide-gray-800">
        <thead>
          <tr>
            {grouped && <th scope="col" className="w-8 px-2 py-2" aria-label="Group" />}
            {columns.map((c) => (
              <th
                key={c.key}
                scope="col"
                className={`whitespace-nowrap px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400 ${c.expand ? 'w-full' : ''} ${c.headerClassName ?? ''}`}
              >
                {c.filter ? (
                  <span className="inline-flex items-center gap-1">
                    {c.header}
                    <HeaderFilterButton filter={c.filter} label={typeof c.header === 'string' ? c.header : c.key} />
                  </span>
                ) : (
                  c.header
                )}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100 dark:divide-gray-800/70">
          {loading &&
            Array.from({ length: skeletonRows }).map((_, i) => (
              <tr key={`sk-${i}`}>
                {grouped && <td className="w-8 px-2 py-2.5" />}
                {columns.map((c) => (
                  <td key={c.key} className="px-3 py-2.5">
                    <Skeleton className="h-3.5 w-full max-w-[10rem]" />
                  </td>
                ))}
              </tr>
            ))}

          {!loading &&
            display.map((d) => {
              if (d.kind === 'leader') {
                const isOpen = expanded.has(d.groupKey);
                const interaction = rowInteractionProps(d.leader);
                // While expanded, the leader turns into the group's "header": a
                // solid indigo tint so parent and children read as one block,
                // visually distinct from the surrounding plain rows.
                const openCls = onRowClick
                  ? 'cursor-pointer bg-indigo-50 hover:bg-indigo-100/80 focus:bg-indigo-100/80 dark:bg-indigo-950/40 dark:hover:bg-indigo-900/50 dark:focus:bg-indigo-900/50'
                  : 'bg-indigo-50 dark:bg-indigo-950/40';
                return (
                  <tr
                    key={rowKey(d.leader)}
                    {...interaction}
                    className={`${isOpen ? openCls : interaction.className} ${rowClassName?.(d.leader) ?? ''}`}
                  >
                    <td className="w-8 px-2 py-2 align-top">
                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          toggle(d.groupKey);
                        }}
                        aria-expanded={isOpen}
                        aria-label={isOpen ? 'Collapse group' : 'Expand group'}
                        className="flex items-center gap-1 rounded text-gray-500 hover:text-gray-800 dark:text-gray-400 dark:hover:text-gray-100"
                      >
                        <ChevronRight className={`h-4 w-4 shrink-0 transition-transform ${isOpen ? 'rotate-90' : ''}`} />
                        {groupBadge?.(d.members)}
                      </button>
                    </td>
                    {renderCells(d.leader)}
                  </tr>
                );
              }

              if (d.kind === 'child') {
                const interaction = rowInteractionProps(d.row);
                // Children carry a lighter wash of the leader's tint (never the
                // plain-row hover gray, which would erase the distinction on hover).
                const childCls = onRowClick
                  ? 'cursor-pointer bg-indigo-50/40 hover:bg-indigo-100/60 focus:bg-indigo-100/60 dark:bg-indigo-950/20 dark:hover:bg-indigo-900/40 dark:focus:bg-indigo-900/40'
                  : 'bg-indigo-50/40 dark:bg-indigo-950/20';
                return (
                  <tr
                    key={rowKey(d.row)}
                    onClick={interaction.onClick}
                    tabIndex={interaction.tabIndex}
                    onKeyDown={interaction.onKeyDown}
                    className={`${childCls} ${rowClassName?.(d.row) ?? ''}`}
                  >
                    <td className="w-8 px-2 py-2 align-top">
                      <span className="ml-2 block h-3 w-3 rounded-bl border-b border-l border-indigo-300 dark:border-indigo-700" />
                    </td>
                    {renderCells(d.row)}
                  </tr>
                );
              }

              const interaction = rowInteractionProps(d.row);
              return (
                <tr key={rowKey(d.row)} {...interaction} className={`${interaction.className} ${rowClassName?.(d.row) ?? ''}`}>
                  {grouped && <td className="w-8 px-2 py-2" />}
                  {renderCells(d.row)}
                </tr>
              );
            })}

          {!loading && rows.length === 0 && (
            <tr>
              <td colSpan={totalCols}>
                <EmptyState message={emptyMessage} hint={emptyHint} />
              </td>
            </tr>
          )}
        </tbody>
      </table>
      {footer}
    </div>
  );
}
