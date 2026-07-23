import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import type { ReactNode } from 'react';
import { AuthProvider, useAuth } from './context/AuthContext';
import { ThemeProvider } from './context/ThemeContext';
import { TimeRangeProvider } from './context/TimeRangeContext';
import { LookupsProvider } from './context/LookupsContext';
import { AppShell } from './layout/AppShell';
import { LoadingBlock } from './components/Feedback';
import { LoginPage } from './pages/LoginPage';
import { OverviewPage } from './pages/OverviewPage';
import { LogsPage } from './pages/LogsPage';
import { TraceViewerPage } from './pages/TraceViewerPage';
import { TracesListPage } from './pages/TracesListPage';
import { ExceptionsPage } from './pages/ExceptionsPage';
import { ExceptionDetailPage } from './pages/ExceptionDetailPage';
import { AuditPage } from './pages/AuditPage';
import { AlertsPage } from './pages/AlertsPage';
import { SavedSearchesPage } from './pages/SavedSearchesPage';
import { AdminPage } from './pages/admin/AdminPage';
import { HealthPage } from './pages/HealthPage';

function RequireAuth({ children }: { children: ReactNode }) {
  const { user, ready } = useAuth();
  const location = useLocation();
  if (!ready) return <LoadingBlock label="Signing in…" />;
  if (!user) return <Navigate to="/login" state={{ from: location.pathname + location.search }} replace />;
  return <>{children}</>;
}

function RequireAdmin({ children }: { children: ReactNode }) {
  const { isAdmin, ready } = useAuth();
  if (!ready) return <LoadingBlock />;
  if (!isAdmin) return <Navigate to="/" replace />;
  return <>{children}</>;
}

function NotFound() {
  return (
    <div className="py-20 text-center">
      <p className="text-5xl font-bold text-gray-300 dark:text-gray-700">404</p>
      <p className="mt-2 text-sm text-gray-500">This page does not exist.</p>
    </div>
  );
}

export default function App() {
  return (
    <ThemeProvider>
      <AuthProvider>
        <TimeRangeProvider>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route
              element={
                <RequireAuth>
                  <LookupsProvider>
                    <AppShell />
                  </LookupsProvider>
                </RequireAuth>
              }
            >
              <Route path="/" element={<OverviewPage />} />
              <Route path="/logs" element={<LogsPage />} />
              <Route path="/traces" element={<TracesListPage />} />
              <Route path="/traces/:traceId" element={<TraceViewerPage />} />
              <Route path="/exceptions" element={<ExceptionsPage />} />
              <Route path="/exceptions/:id" element={<ExceptionDetailPage />} />
              <Route path="/audit" element={<AuditPage />} />
              <Route path="/alerts" element={<AlertsPage />} />
              <Route path="/searches" element={<SavedSearchesPage />} />
              <Route
                path="/admin"
                element={
                  <RequireAdmin>
                    <AdminPage />
                  </RequireAdmin>
                }
              />
              <Route path="/health" element={<HealthPage />} />
              <Route path="*" element={<NotFound />} />
            </Route>
          </Routes>
        </TimeRangeProvider>
      </AuthProvider>
    </ThemeProvider>
  );
}
