import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, clearToken, getCachedUser, getToken, setCachedUser, setToken, setUnauthorizedHandler } from '../api/client';
import type { AuthUser, LoginResponse } from '../api/types';

interface AuthContextValue {
  user: AuthUser | null;
  /** true once the initial token check has completed */
  ready: boolean;
  isAdmin: boolean;
  isSuperAdmin: boolean;
  canExport: boolean;
  login: (username: string, password: string) => Promise<void>;
  logout: () => void;
  /** Re-fetches /auth/me (e.g. after a forced password change clears the flag). */
  refreshUser: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(() => (getToken() ? getCachedUser<AuthUser>() : null));
  const [ready, setReady] = useState(false);
  const navigate = useNavigate();

  const logout = useCallback(() => {
    clearToken();
    setUser(null);
    navigate('/login', { replace: true });
  }, [navigate]);

  useEffect(() => {
    setUnauthorizedHandler(() => {
      setUser(null);
      navigate('/login', { replace: true });
    });
    return () => setUnauthorizedHandler(null);
  }, [navigate]);

  useEffect(() => {
    let alive = true;
    if (!getToken()) {
      setReady(true);
      return;
    }
    api
      .get<AuthUser>('/auth/me')
      .then((u) => {
        if (!alive) return;
        setUser(u);
        setCachedUser(u);
      })
      .catch(() => {
        /* 401 handled globally; other errors keep cached user */
      })
      .finally(() => {
        if (alive) setReady(true);
      });
    return () => {
      alive = false;
    };
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    const res = await api.post<LoginResponse>('/auth/login', { username, password });
    setToken(res.token);
    setCachedUser(res.user);
    setUser(res.user);
  }, []);

  const refreshUser = useCallback(async () => {
    const u = await api.get<AuthUser>('/auth/me');
    setUser(u);
    setCachedUser(u);
  }, []);

  const isSuperAdmin = useMemo(() => {
    if (!user) return false;
    const roles = new Set([...(user.roles ?? []), ...(user.grants ?? []).map((g) => g.role)]);
    return roles.has('SuperAdmin');
  }, [user]);

  const isAdmin = useMemo(() => {
    if (!user) return false;
    const roles = new Set([...(user.roles ?? []), ...(user.grants ?? []).map((g) => g.role)]);
    return roles.has('SuperAdmin') || roles.has('TenantAdmin');
  }, [user]);

  const canExport = useMemo(() => {
    if (!user) return false;
    if (isAdmin) return true;
    return (user.grants ?? []).some((g) => g.canExport);
  }, [user, isAdmin]);

  return (
    <AuthContext.Provider value={{ user, ready, isAdmin, isSuperAdmin, canExport, login, logout, refreshUser }}>{children}</AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
