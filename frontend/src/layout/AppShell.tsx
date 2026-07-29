import { useEffect, useRef, useState } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router-dom';
import {
  Activity,
  AlertTriangle,
  BellRing,
  Bookmark,
  Bug,
  GitBranch,
  ChevronsLeft,
  ChevronsRight,
  ClipboardList,
  KeyRound,
  LayoutDashboard,
  LifeBuoy,
  LogOut,
  Menu,
  Moon,
  ScrollText,
  Settings,
  Sun,
  User,
  X,
} from 'lucide-react';
import { useAuth } from '../context/AuthContext';
import { useTheme } from '../context/ThemeContext';
import { useTimeRange } from '../context/TimeRangeContext';
import { TimeRangePicker } from '../components/TimeRangePicker';
import { Modal } from '../components/Modal';
import { TextField } from '../components/Select';
import { ErrorBanner } from '../components/Feedback';
import { api, toApiError } from '../api/client';
import { initSupportHub, openSupportPanel, SUPPORT_ERROR_EVENT } from '../support/supporthub';

const SIDEBAR_KEY = 'logsphere.sidebar';

// "Errors & Critical" is the Log Explorer pre-filtered by severity — same page,
// its own entry so error hunting is one click. Active-state logic below keys on
// the severities query param to light up the right one of the two /logs items.
const ERRORS_QUERY = 'severities=Error,Critical';

const NAV_ITEMS = [
  { to: '/', label: 'Overview', icon: LayoutDashboard, end: true },
  { to: '/logs', label: 'Log Explorer', icon: ScrollText },
  { to: `/logs?${ERRORS_QUERY}`, label: 'Errors & Critical', icon: AlertTriangle },
  { to: '/traces', label: 'Traces', icon: GitBranch },
  { to: '/exceptions', label: 'Exceptions', icon: Bug },
  { to: '/audit', label: 'Audit', icon: ClipboardList },
  { to: '/alerts', label: 'Alerts', icon: BellRing },
  { to: '/searches', label: 'Saved Searches', icon: Bookmark },
  { to: '/health', label: 'Platform Health', icon: Activity },
];

/** Routes where the global time range picker applies. */
const TIME_RANGE_ROUTES = ['/', '/logs', '/traces', '/exceptions', '/audit'];

function pageTitle(pathname: string, search = ''): string {
  if (pathname === '/') return 'Overview';
  if (pathname.startsWith('/logs') && search.includes('severities=Error')) return 'Errors & Critical';
  if (pathname.startsWith('/logs')) return 'Log Explorer';
  if (pathname === '/traces') return 'Traces';
  if (pathname.startsWith('/traces')) return 'Trace Viewer';
  if (pathname.startsWith('/exceptions')) return 'Exceptions';
  if (pathname.startsWith('/audit')) return 'Audit Explorer';
  if (pathname.startsWith('/alerts')) return 'Alerts';
  if (pathname.startsWith('/searches')) return 'Saved Searches';
  if (pathname.startsWith('/admin')) return 'Administration';
  if (pathname.startsWith('/health')) return 'Platform Health';
  return 'LogSphere';
}

function ChangePasswordModal({
  open,
  onClose,
  forced = false,
  onChanged,
}: {
  open: boolean;
  onClose: () => void;
  /** true when the account is flagged must-change-password: cannot be dismissed */
  forced?: boolean;
  onChanged?: () => void | Promise<void>;
}) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post('/auth/change-password', { currentPassword: current, newPassword: next });
      setDone(true);
      setCurrent('');
      setNext('');
      await onChanged?.();
      if (forced) {
        // full reload so every panel refetches now that the account is unblocked
        window.location.reload();
        return;
      }
    } catch (e) {
      setError(toApiError(e).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      open={open}
      title={forced ? 'Set a new password to continue' : 'Change password'}
      onClose={forced ? () => {} : onClose}
      footer={
        <>
          {!forced && (
            <button type="button" className="btn-secondary" onClick={onClose}>
              Close
            </button>
          )}
          <button type="button" className="btn-primary" onClick={submit} disabled={busy || !current || !next}>
            Update password
          </button>
        </>
      }
    >
      <div className="space-y-3">
        {forced && (
          <p className="rounded-md bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-950/50 dark:text-amber-300">
            Your password was reset by an administrator. Enter the temporary password you were
            given as the current password, then choose your own new password (at least 10
            characters). You cannot use the platform until this is done.
          </p>
        )}
        {error && <ErrorBanner error={error} />}
        {done && (
          <p className="rounded-md bg-emerald-50 px-3 py-2 text-sm text-emerald-700 dark:bg-emerald-950/50 dark:text-emerald-300">
            Password updated.
          </p>
        )}
        <TextField label="Current password" type="password" value={current} onChange={setCurrent} />
        <TextField label="New password" type="password" value={next} onChange={setNext} />
      </div>
    </Modal>
  );
}

export function AppShell() {
  const { user, isAdmin, logout, refreshUser } = useAuth();
  const forcedPasswordChange = user?.mustChangePassword === true;
  const { theme, toggleTheme } = useTheme();
  const { range, setRange } = useTimeRange();
  const location = useLocation();

  const [collapsed, setCollapsed] = useState(() => localStorage.getItem(SIDEBAR_KEY) === 'collapsed');
  const [mobileOpen, setMobileOpen] = useState(false);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [pwOpen, setPwOpen] = useState(false);
  const [fetching, setFetching] = useState(false);
  const [supportReady, setSupportReady] = useState(false);
  const [supportError, setSupportError] = useState<string | null>(null);
  const userMenuRef = useRef<HTMLDivElement>(null);

  // Shown when opening support fails (e.g. the account has no email address) —
  // the SDK closes the panel and reports the reason instead of a dead widget.
  useEffect(() => {
    const onError = (e: Event) => setSupportError((e as CustomEvent).detail as string);
    window.addEventListener(SUPPORT_ERROR_EVENT, onError);
    return () => window.removeEventListener(SUPPORT_ERROR_EVENT, onError);
  }, []);

  useEffect(() => {
    if (!supportError) return;
    const timer = setTimeout(() => setSupportError(null), 10000);
    return () => clearTimeout(timer);
  }, [supportError]);

  // SupportHub widget: initialised once per app load (idempotent under strict-mode double
  // effects); the button stays hidden when support is not configured server-side. The context
  // callback reads window.location live so the panel reports the screen it was opened FROM.
  useEffect(() => {
    let cancelled = false;
    initSupportHub(() => ({
      module: pageTitle(window.location.pathname, window.location.search),
      page: window.location.pathname + window.location.search,
    })).then((enabled) => {
      if (!cancelled) setSupportReady(enabled);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  // Slim progress bar while any API request is in flight (pages keep showing
  // their last data while revalidating — this is the only "loading" signal).
  useEffect(() => {
    const onActivity = (e: Event) => setFetching(((e as CustomEvent).detail as number) > 0);
    window.addEventListener('logsphere:activity', onActivity);
    return () => window.removeEventListener('logsphere:activity', onActivity);
  }, []);

  useEffect(() => {
    localStorage.setItem(SIDEBAR_KEY, collapsed ? 'collapsed' : 'open');
  }, [collapsed]);

  useEffect(() => {
    setMobileOpen(false);
    setUserMenuOpen(false);
  }, [location.pathname]);

  useEffect(() => {
    if (!userMenuOpen) return;
    const onDown = (e: MouseEvent) => {
      if (userMenuRef.current && !userMenuRef.current.contains(e.target as Node)) setUserMenuOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [userMenuOpen]);

  const showTimeRange = TIME_RANGE_ROUTES.some((r) =>
    r === '/' ? location.pathname === '/' : location.pathname.startsWith(r),
  );

  const navItems = isAdmin ? [...NAV_ITEMS, { to: '/admin', label: 'Admin', icon: Settings }] : NAV_ITEMS;

  const sidebarContent = (
    <>
      <div className={`flex h-14 items-center gap-2 border-b border-gray-200 px-4 dark:border-gray-800 ${collapsed ? 'justify-center px-2' : ''}`}>
        <img src="/brand/mark-light.jpg" alt="" className="h-8 w-8 shrink-0 rounded-md dark:hidden" />
        <img src="/brand/mark-dark.jpg" alt="" className="hidden h-8 w-8 shrink-0 rounded-md dark:block" />
        {!collapsed && <span className="truncate text-sm font-semibold tracking-tight">LogSphere</span>}
      </div>
      <nav className="flex-1 space-y-0.5 overflow-y-auto p-2" aria-label="Main navigation">
        {navItems.map((item) => {
          const Icon = item.icon;
          const pathOnly = item.to.split('?')[0];
          const itemQuery = item.to.includes('?') ? item.to.split('?')[1] : null;
          // Two sidebar items share the /logs path (Log Explorer and Errors & Critical);
          // the severities query decides which of them is the active one.
          const errorsViewOpen =
            location.pathname.startsWith('/logs') && location.search.includes('severities=Error');
          const isItemActive = (routerActive: boolean): boolean => {
            if (itemQuery) return errorsViewOpen;
            if (pathOnly === '/logs') return routerActive && !errorsViewOpen;
            return routerActive;
          };
          return (
            <NavLink
              key={item.to}
              to={item.to}
              end={'end' in item ? (item as { end?: boolean }).end : false}
              title={item.label}
              onClick={() => {
                // Clicking the nav item for the section you're already in is a no-op navigation,
                // so it won't refetch. Signal the active page to refresh its data from the API
                // instead. startsWith covers sub-routes (e.g. /admin/users under the Admin item);
                // '/' (Overview) is exact-match only so it doesn't match every path.
                const onPath = pathOnly === '/' ? location.pathname === '/' : location.pathname.startsWith(pathOnly);
                const onActivePage = onPath && (itemQuery ? errorsViewOpen : !(pathOnly === '/logs' && errorsViewOpen));
                if (onActivePage)
                  window.dispatchEvent(new CustomEvent('logsphere:refresh', { detail: item.to }));
              }}
              className={({ isActive }) =>
                `flex items-center gap-2.5 rounded-md px-2.5 py-2 text-sm font-medium transition-colors ${
                  isItemActive(isActive)
                    ? 'bg-indigo-50 text-indigo-700 dark:bg-indigo-950/70 dark:text-indigo-300'
                    : 'text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800'
                } ${collapsed ? 'justify-center px-2' : ''}`
              }
            >
              <Icon className="h-4 w-4 shrink-0" />
              {!collapsed && <span className="truncate">{item.label}</span>}
            </NavLink>
          );
        })}
      </nav>
      <div className="hidden border-t border-gray-200 p-2 dark:border-gray-800 lg:block">
        <button
          type="button"
          className="btn-ghost w-full !justify-center"
          onClick={() => setCollapsed((c) => !c)}
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          {collapsed ? <ChevronsRight className="h-4 w-4" /> : <ChevronsLeft className="h-4 w-4" />}
          {!collapsed && <span className="text-xs">Collapse</span>}
        </button>
      </div>
    </>
  );

  return (
    <div className="flex h-screen overflow-hidden">
      {/* desktop sidebar */}
      <aside
        className={`hidden shrink-0 flex-col border-r border-gray-200 bg-white transition-all dark:border-gray-800 dark:bg-gray-900 lg:flex ${
          collapsed ? 'w-14' : 'w-56'
        }`}
      >
        {sidebarContent}
      </aside>

      {/* mobile sidebar */}
      {mobileOpen && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <div className="absolute inset-0 bg-black/40" onClick={() => setMobileOpen(false)} aria-hidden="true" />
          <aside className="absolute inset-y-0 left-0 flex w-64 flex-col border-r border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
            <button
              type="button"
              className="absolute right-2 top-3 btn-ghost !p-1.5"
              onClick={() => setMobileOpen(false)}
              aria-label="Close menu"
            >
              <X className="h-4 w-4" />
            </button>
            {sidebarContent}
          </aside>
        </div>
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        {/* top bar */}
        <header className="flex h-14 shrink-0 items-center justify-between gap-3 border-b border-gray-200 bg-white px-4 dark:border-gray-800 dark:bg-gray-900">
          <div className="flex min-w-0 items-center gap-3">
            <button
              type="button"
              className="btn-ghost !p-1.5 lg:hidden"
              onClick={() => setMobileOpen(true)}
              aria-label="Open menu"
            >
              <Menu className="h-5 w-5" />
            </button>
            <h1 className="truncate text-base font-semibold">{pageTitle(location.pathname, location.search)}</h1>
          </div>
          <div className="flex items-center gap-2">
            {showTimeRange && <TimeRangePicker value={range} onChange={setRange} />}
            {supportReady && (
              <button
                type="button"
                className="btn-ghost !p-2"
                onClick={openSupportPanel}
                title="Support"
                aria-label="Report an issue or contact support"
              >
                <LifeBuoy className="h-4 w-4" />
              </button>
            )}
            <button
              type="button"
              className="btn-ghost !p-2"
              onClick={toggleTheme}
              aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            >
              {theme === 'dark' ? <Sun className="h-4 w-4" /> : <Moon className="h-4 w-4" />}
            </button>
            <div className="relative" ref={userMenuRef}>
              <button
                type="button"
                className="btn-ghost !px-2"
                onClick={() => setUserMenuOpen((o) => !o)}
                aria-haspopup="menu"
                aria-expanded={userMenuOpen}
              >
                <span className="flex h-7 w-7 items-center justify-center rounded-full bg-indigo-100 text-xs font-semibold text-indigo-700 dark:bg-indigo-900 dark:text-indigo-300">
                  {(user?.displayName ?? user?.username ?? '?').slice(0, 1).toUpperCase()}
                </span>
                <span className="hidden max-w-[10rem] truncate text-sm sm:block">{user?.displayName ?? user?.username}</span>
              </button>
              {userMenuOpen && (
                <div
                  role="menu"
                  className="absolute right-0 z-30 mt-1 w-56 rounded-md border border-gray-200 bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
                >
                  <div className="border-b border-gray-100 px-3 py-2 dark:border-gray-800">
                    <p className="flex items-center gap-1.5 truncate text-sm font-medium">
                      <User className="h-3.5 w-3.5 text-gray-400" /> {user?.displayName ?? user?.username}
                    </p>
                    <p className="truncate text-xs text-gray-500">{(user?.roles ?? []).join(', ') || 'No global roles'}</p>
                  </div>
                  <button
                    type="button"
                    role="menuitem"
                    className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-gray-50 dark:hover:bg-gray-800"
                    onClick={() => {
                      setUserMenuOpen(false);
                      setPwOpen(true);
                    }}
                  >
                    <KeyRound className="h-3.5 w-3.5 text-gray-400" /> Change password
                  </button>
                  <button
                    type="button"
                    role="menuitem"
                    className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-red-600 hover:bg-gray-50 dark:text-red-400 dark:hover:bg-gray-800"
                    onClick={logout}
                  >
                    <LogOut className="h-3.5 w-3.5" /> Sign out
                  </button>
                </div>
              )}
            </div>
          </div>
        </header>

        {/* activity bar: sits directly under the header, indeterminate sweep */}
        <div className="relative h-0.5 shrink-0 overflow-hidden" aria-hidden="true">
          {fetching && (
            <div className="absolute inset-y-0 w-1/3 animate-[shimmer_1s_ease-in-out_infinite] rounded-full bg-indigo-500/80" />
          )}
        </div>

        <main className="flex-1 overflow-y-auto p-4 lg:p-6">
          <Outlet />
        </main>
      </div>

      {supportError && (
        <div
          className="fixed bottom-4 right-4 z-50 w-full max-w-sm cursor-pointer"
          onClick={() => setSupportError(null)}
          title="Dismiss"
        >
          <ErrorBanner error={supportError} />
        </div>
      )}

      <ChangePasswordModal
        open={pwOpen || forcedPasswordChange}
        forced={forcedPasswordChange}
        onChanged={refreshUser}
        onClose={() => setPwOpen(false)}
      />
    </div>
  );
}
