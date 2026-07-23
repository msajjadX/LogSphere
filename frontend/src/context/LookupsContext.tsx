import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api } from '../api/client';
import type { Application, Environment, Project, Tenant } from '../api/types';

/**
 * Reference data (tenants/projects/applications/environments) used by filter
 * bars and admin forms. Sourced from the admin endpoints; non-admin users may
 * receive 403 — in that case the lists stay empty and dependent selects
 * gracefully show only "All".
 */
interface LookupsContextValue {
  tenants: Tenant[];
  projects: Project[];
  applications: Application[];
  environments: Environment[];
  reload: () => void;
}

const LookupsContext = createContext<LookupsContextValue>({
  tenants: [],
  projects: [],
  applications: [],
  environments: [],
  reload: () => {},
});

export function LookupsProvider({ children }: { children: ReactNode }) {
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [applications, setApplications] = useState<Application[]>([]);
  const [environments, setEnvironments] = useState<Environment[]>([]);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let alive = true;
    const safe = <T,>(p: Promise<T[]>, set: (v: T[]) => void) =>
      p.then((v) => alive && set(Array.isArray(v) ? v : [])).catch(() => {});
    safe(api.getList<Tenant>('/admin/tenants'), setTenants);
    safe(api.getList<Project>('/admin/projects'), setProjects);
    safe(api.getList<Application>('/admin/applications'), setApplications);
    safe(api.getList<Environment>('/admin/environments'), setEnvironments);
    return () => {
      alive = false;
    };
  }, [tick]);

  const reload = useCallback(() => setTick((t) => t + 1), []);

  return (
    <LookupsContext.Provider value={{ tenants, projects, applications, environments, reload }}>
      {children}
    </LookupsContext.Provider>
  );
}

export function useLookups() {
  return useContext(LookupsContext);
}
