import { useState } from 'react';
import { TenantsTab } from './TenantsTab';
import { ProjectsTab } from './ProjectsTab';
import { ApplicationsTab } from './ApplicationsTab';
import { UsersTab } from './UsersTab';
import { RedactionTab } from './RedactionTab';
import { RetentionTab } from './RetentionTab';
import { AccessAuditTab } from './AccessAuditTab';

const TABS = [
  ['tenants', 'Tenants'],
  ['projects', 'Projects'],
  ['applications', 'Applications'],
  ['users', 'Users'],
  ['redaction', 'Redaction Rules'],
  ['retention', 'Retention Policies'],
  ['access-audit', 'Access Audit'],
] as const;

type TabKey = (typeof TABS)[number][0];

export function AdminPage() {
  const [tab, setTab] = useState<TabKey>('tenants');

  return (
    <div className="space-y-4">
      <div className="flex overflow-x-auto border-b border-gray-200 dark:border-gray-800" role="tablist">
        {TABS.map(([key, label]) => (
          <button
            key={key}
            type="button"
            role="tab"
            aria-selected={tab === key}
            className={`tab whitespace-nowrap ${tab === key ? 'tab-active' : ''}`}
            onClick={() => setTab(key)}
          >
            {label}
          </button>
        ))}
      </div>

      {tab === 'tenants' && <TenantsTab />}
      {tab === 'projects' && <ProjectsTab />}
      {tab === 'applications' && <ApplicationsTab />}
      {tab === 'users' && <UsersTab />}
      {tab === 'redaction' && <RedactionTab />}
      {tab === 'retention' && <RetentionTab />}
      {tab === 'access-audit' && <AccessAuditTab />}
    </div>
  );
}
