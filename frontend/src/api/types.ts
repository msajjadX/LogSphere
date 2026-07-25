// ---------------------------------------------------------------------------
// LogSphere API contract types (mirrors docs/API.md v1)
// ---------------------------------------------------------------------------

export type Id = number | string;

export interface ApiEnvelope<T> {
  success: boolean;
  statusCode: string;
  statusDetails: string;
  data: T;
  correlationId?: string;
}

// --- enums -----------------------------------------------------------------

export type Severity = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical';
export const SEVERITIES: Severity[] = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'];

export type EventType =
  | 'ApiRequest'
  | 'ApiResponse'
  | 'Audit'
  | 'Exception'
  | 'Performance'
  | 'Integration'
  | 'BackgroundJob'
  | 'Security'
  | 'Custom'
  | 'Application';
export const EVENT_TYPES: EventType[] = [
  'ApiRequest',
  'ApiResponse',
  'Audit',
  'Exception',
  'Performance',
  'Integration',
  'BackgroundJob',
  'Security',
  'Custom',
  'Application',
];

export type EventStatus =
  | 'Started'
  | 'InProgress'
  | 'Completed'
  | 'Failed'
  | 'Retrying'
  | 'Cancelled'
  | 'TimedOut'
  | 'PartiallyCompleted';
export const EVENT_STATUSES: EventStatus[] = [
  'Started',
  'InProgress',
  'Completed',
  'Failed',
  'Retrying',
  'Cancelled',
  'TimedOut',
  'PartiallyCompleted',
];

export type ExceptionStatus = 'New' | 'Investigating' | 'Identified' | 'Resolved' | 'Ignored' | 'Recurring';
export const EXCEPTION_STATUSES: ExceptionStatus[] = [
  'New',
  'Investigating',
  'Identified',
  'Resolved',
  'Ignored',
  'Recurring',
];

export type AlertConditionType =
  | 'ErrorCountThreshold'
  | 'ErrorRatePercent'
  | 'SameFingerprintCount'
  | 'DurationP95Threshold'
  | 'SeverityDetected'
  | 'AuthFailureCount'
  | 'IngestSilence'
  | 'QueueDepth';
export const ALERT_CONDITION_TYPES: AlertConditionType[] = [
  'ErrorCountThreshold',
  'ErrorRatePercent',
  'SameFingerprintCount',
  'DurationP95Threshold',
  'SeverityDetected',
  'AuthFailureCount',
  'IngestSilence',
  'QueueDepth',
];

export const ROLES = [
  'SuperAdmin',
  'TenantAdmin',
  'ProjectAdmin',
  'Developer',
  'Support',
  'SecurityAuditor',
  'ComplianceAuditor',
  'ReadOnly',
] as const;
export type Role = (typeof ROLES)[number];

// --- auth ------------------------------------------------------------------

export interface UserGrant {
  role: string;
  tenantId?: Id | null;
  projectId?: Id | null;
  environmentId?: Id | null;
  categories?: string[] | null;
  minSeverity?: string | null;
  canViewBodies?: boolean;
  canExport?: boolean;
  canViewSecurity?: boolean;
}

export interface AuthUser {
  id: Id;
  username: string;
  displayName: string;
  mustChangePassword?: boolean;
  roles: string[];
  grants: UserGrant[];
}

export interface LoginResponse {
  token: string;
  expiresAt: string;
  user: AuthUser;
}

// --- query / logs ----------------------------------------------------------

export interface LogFilter {
  from?: string | null;
  to?: string | null;
  tenantId?: Id | null;
  projectId?: Id | null;
  applicationId?: Id | null;
  environmentId?: Id | null;
  /** multi-select variants used by the column filters (ANDed with the singles) */
  projectIds?: Id[];
  applicationIds?: Id[];
  environmentIds?: number[];
  module?: string | null;
  component?: string | null;
  eventTypes?: string[];
  severities?: string[];
  statuses?: string[];
  eventId?: string | null;
  correlationId?: string | null;
  traceId?: string | null;
  userId?: string | null;
  businessEntityType?: string | null;
  businessEntityId?: string | null;
  actionName?: string | null;
  httpRoute?: string | null;
  httpStatusCode?: number | null;
  exceptionType?: string | null;
  fingerprint?: string | null;
  applicationVersion?: string | null;
  machineName?: string | null;
  text?: string | null;
  afterId?: number | null;
  limit?: number;
  order?: 'asc' | 'desc';
}

export interface LogEventSummary {
  /** monotonically increasing storage id, used as pagination / tail cursor */
  id: number;
  eventId: string;
  eventType: string;
  eventTimestamp: string;
  receivedTimestamp?: string | null;
  severity: Severity;
  status?: string | null;
  correlationId?: string | null;
  traceId?: string | null;
  spanId?: string | null;
  parentSpanId?: string | null;
  sequence?: number | null;
  tenantId?: Id | null;
  projectId?: Id | null;
  applicationId?: Id | null;
  environmentId?: Id | null;
  projectName?: string | null;
  applicationName?: string | null;
  environmentName?: string | null;
  module?: string | null;
  component?: string | null;
  message?: string | null;
  actionName?: string | null;
  userId?: string | null;
  userName?: string | null;
  sessionId?: string | null;
  businessEntityType?: string | null;
  businessEntityId?: string | null;
  httpMethod?: string | null;
  httpRoute?: string | null;
  httpStatusCode?: number | null;
  durationMs?: number | null;
  dbDurationMs?: number | null;
  externalDurationMs?: number | null;
  machineName?: string | null;
  applicationVersion?: string | null;
  deploymentVersion?: string | null;
  exceptionType?: string | null;
  fingerprint?: string | null;
  exceptionFingerprint?: string | null;
}

export interface HttpInfo {
  method?: string | null;
  route?: string | null;
  statusCode?: number | null;
  clientIp?: string | null;
  userAgent?: string | null;
  requestSize?: number | null;
  responseSize?: number | null;
}

export interface LogEventDetail extends LogEventSummary {
  http?: HttpInfo | null;
  properties?: unknown;
  requestData?: unknown;
  responseData?: unknown;
  /** server column name */
  exceptionData?: unknown;
  /** envelope name */
  exception?: unknown;
  sanitizationApplied?: boolean;
  fieldsSanitized?: number | null;
  truncated?: boolean;
  schemaVersion?: string | null;
}

export interface LogQueryResult {
  items: LogEventSummary[];
  nextCursor?: number | null;
}

// --- traces ----------------------------------------------------------------

export interface TraceSpan {
  spanId: string;
  parentSpanId?: string | null;
  name?: string | null;
  start: string;
  durationMs?: number | null;
  eventType?: string | null;
  severity?: Severity | null;
  eventId: string;
}

export interface TraceResult {
  spans: TraceSpan[];
  events: LogEventSummary[];
}

// --- stats -----------------------------------------------------------------

export interface TopProjectStat {
  projectId?: Id;
  projectName?: string | null;
  name?: string | null;
  count: number;
  errorCount?: number;
  errorRate?: number;
}

export interface TopRouteStat {
  route?: string | null;
  httpRoute?: string | null;
  method?: string | null;
  count: number;
  errorCount?: number;
  errorRate?: number;
  avgDurationMs?: number;
}

export interface TopModuleStat {
  module?: string | null;
  name?: string | null;
  count: number;
  errorCount?: number;
}

export interface OverviewStats {
  totalEvents?: number;
  totals?: number | Record<string, number>;
  errorRate?: number;
  warningRate?: number;
  avgDurationMs?: number;
  p95DurationMs?: number;
  p99DurationMs?: number;
  requestsPerMinute?: number;
  activeExceptionGroups?: number;
  topProjects?: TopProjectStat[];
  topFailingRoutes?: TopRouteStat[];
  topModules?: TopModuleStat[];
  recentCritical?: LogEventSummary[];
  severityCounts?: Record<string, number>;
  queueDepth?: number;
  ingestionHealthy?: boolean;
}

export interface TimeseriesPoint {
  bucket: string;
  value: number;
  severity?: string | null;
}

export interface TimeseriesRequest {
  from: string;
  to: string;
  projectId?: Id | null;
  intervalMinutes: number;
  metric: 'count' | 'errors' | 'avgDuration';
}

// --- exceptions ------------------------------------------------------------

export interface ExceptionGroup {
  id: Id;
  fingerprint: string;
  exceptionType?: string | null;
  message?: string | null;
  module?: string | null;
  projectId?: Id | null;
  projectName?: string | null;
  applicationName?: string | null;
  environmentName?: string | null;
  firstSeen?: string | null;
  lastSeen?: string | null;
  totalCount: number;
  lastHourCount?: number;
  last24hCount?: number;
  status: ExceptionStatus | string;
  assignedToUserId?: Id | null;
  assignedToName?: string | null;
  sampleEventId?: string | null;
  affectedVersions?: string[] | null;
  notes?: string | null;
  linkedIssueUrl?: string | null;
}

export interface ExceptionGroupDetail extends ExceptionGroup {
  trend?: TimeseriesPoint[] | null;
  trendBuckets?: TimeseriesPoint[] | null;
  recentOccurrences?: LogEventSummary[] | null;
  occurrences?: LogEventSummary[] | null;
}

export interface ExceptionGroupPatch {
  status?: string;
  assignedToUserId?: Id | null;
  notes?: string | null;
  linkedIssueUrl?: string | null;
}

// --- audit -----------------------------------------------------------------

export interface AuditFilter {
  from?: string | null;
  to?: string | null;
  projectId?: Id | null;
  projectIds?: Id[];
  applicationIds?: Id[];
  environmentIds?: number[];
  actorUserId?: string | null;
  actionName?: string | null;
  entityType?: string | null;
  entityId?: string | null;
  /** substring match on the audit message/summary */
  text?: string | null;
  limit?: number;
  afterId?: number | null;
}

export interface AuditEvent extends LogEventSummary {
  oldValues?: Record<string, unknown> | null;
  newValues?: Record<string, unknown> | null;
  changedFields?: string[] | null;
  reason?: string | null;
  sourceIp?: string | null;
}

export interface AuditQueryResult {
  items: AuditEvent[];
  nextCursor?: number | null;
}

// --- alerts ----------------------------------------------------------------

export interface AlertRule {
  id: Id;
  name: string;
  enabled: boolean;
  tenantId?: Id | null;
  projectId?: Id | null;
  environmentId?: Id | null;
  conditionType: AlertConditionType | string;
  threshold: number;
  windowMinutes: number;
  cooldownMinutes: number;
  severityFilter?: string | null;
  channels: Id[];
  /** IngestSilence only: watch a specific module / action (contains match); null = whole scope */
  moduleFilter?: string | null;
  actionFilter?: string | null;
  /** IngestSilence only: trigger when the window saw ≤ this many events (0 = total silence) */
  minCount?: number;
  /** optional schedule: rule evaluates only inside this window (minutes since midnight in timeZone;
   * from > to wraps past midnight); activeDays 0=Sun…6=Sat, empty = every day */
  activeFromMinute?: number | null;
  activeToMinute?: number | null;
  activeDays?: number[] | null;
  timeZone?: string | null;
}

export interface AlertOccurrence {
  id: Id;
  ruleId: Id;
  ruleName?: string | null;
  triggeredAt: string;
  state: 'Open' | 'Acknowledged' | 'Resolved' | string;
  summary?: string | null;
  details?: string | null;
  projectId?: Id | null;
}

export interface AlertChannel {
  id: Id;
  name: string;
  type: 'Email' | 'Webhook' | string;
  target: string;
  enabled: boolean;
}

// --- saved searches --------------------------------------------------------

export interface SavedSearch {
  id: Id;
  name: string;
  filter: LogFilter;
  shared: boolean;
  pinned: boolean;
}

// --- admin -----------------------------------------------------------------

export interface Tenant {
  id: Id;
  name: string;
  code: string;
  isActive: boolean;
}

export interface Project {
  id: Id;
  tenantId: Id;
  name: string;
  code: string;
  description?: string | null;
  isActive: boolean;
}

export interface Application {
  id: Id;
  projectId: Id;
  projectName?: string | null;
  name: string;
  code: string;
  isActive: boolean;
}

export interface Environment {
  id: Id;
  name: string;
  code?: string | null;
}

export interface ApiCredential {
  id: Id;
  keyId?: string | null;
  applicationId?: Id | null;
  environmentId?: Id | null;
  environmentName?: string | null;
  createdAt?: string | null;
  expiresAt?: string | null;
  ipAllowlist?: string | null;
  revoked?: boolean;
  revokedAt?: string | null;
  isActive?: boolean;
  lastUsedAt?: string | null;
}

export interface CreateCredentialResponse extends ApiCredential {
  /** plaintext key, returned exactly once */
  key?: string;
  apiKey?: string;
  plaintextKey?: string;
}

export interface AdminUser {
  id: Id;
  username: string;
  displayName: string;
  email?: string | null;
  isActive: boolean;
  roles?: string[];
  grants: UserGrant[];
}

export interface RedactionRule {
  id: Id;
  tenantId?: Id | null;
  projectId?: Id | null;
  keyPattern: string;
  isRegex: boolean;
  strategy: 'Remove' | 'Redact' | 'MaskLast4' | 'Hash' | string;
  appliesTo: 'All' | 'Request' | 'Response' | 'Properties' | 'Headers' | string;
  enabled: boolean;
}

export interface RetentionPolicy {
  id: Id;
  tenantId?: Id | null;
  projectId?: Id | null;
  environmentId?: Id | null;
  eventType?: string | null;
  severity?: string | null;
  retentionDays: number;
  archiveBeforeDrop: boolean;
  legalHold: boolean;
}

// --- system ----------------------------------------------------------------

export interface WorkerStatus {
  name: string;
  lastHeartbeat?: string | null;
  healthy: boolean;
}

export interface StoragePartition {
  partition: string;
  sizeMb?: number;
  rows?: number;
}

export interface TableStat {
  table: string;
  sizeMb: number;
  liveRows: number;
  deadRows: number;
  lastAutovacuum?: string | null;
  lastAutoanalyze?: string | null;
}

export interface ConnectionGroup {
  total: number;
  active: number;
  idle: number;
  waiting: number;
}

export interface DbStats {
  databaseSizeMb: number;
  connectionsTotal: number;
  connectionsActive: number;
  connectionsIdle: number;
  connectionsWaiting: number;
  maxConnections: number;
  pooledConnections?: ConnectionGroup;
  directConnections?: ConnectionGroup;
  cacheHitRatio: number;
  transactionsCommitted: number;
  transactionsRolledBack: number;
  deadlocks: number;
  tempFiles: number;
  tempBytesMb: number;
  longestQuerySeconds: number;
  topTables: TableStat[];
}

export interface SystemMetrics {
  queueDepth?: number;
  deadLetterCount?: number;
  eventsPerSecond?: number;
  avgIngestLatencyMs?: number;
  failedWrites?: number;
  sanitizationFailures?: number;
  authFailures?: number;
  dbHealthy?: boolean;
  workers?: WorkerStatus[];
  storage?: StoragePartition[];
  db?: DbStats | null;
}

export interface DeadLetterEvent {
  id?: Id;
  receivedAt?: string | null;
  reason?: string | null;
  source?: string | null;
  attempts?: number | null;
  [key: string]: unknown;
}
