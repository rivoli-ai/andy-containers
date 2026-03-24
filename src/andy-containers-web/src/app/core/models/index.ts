export interface Container {
  id: string;
  name: string;
  templateId: string;
  template?: Template;
  providerId: string;
  provider?: Provider;
  externalId?: string;
  status: string;
  ownerId: string;
  organizationId?: string;
  teamId?: string;
  gitRepository?: string;
  ideEndpoint?: string;
  vncEndpoint?: string;
  networkConfig?: string;
  createdAt: string;
  startedAt?: string;
  stoppedAt?: string;
  expiresAt?: string;
  lastActivityAt?: string;
  creationSource?: string;
  clientInfo?: string;
}

export interface Template {
  id: string;
  code: string;
  name: string;
  description?: string;
  version: string;
  baseImage: string;
  catalogScope: string;
  ideType: string;
  defaultResources?: string;
  tags: string[];
  isPublished: boolean;
  scripts?: string;
}

export interface Provider {
  id: string;
  code: string;
  name: string;
  type: string;
  region?: string;
  isEnabled: boolean;
  healthStatus: string;
  lastHealthCheck?: string;
  capabilities?: string;
}

export interface Workspace {
  id: string;
  name: string;
  description?: string;
  ownerId: string;
  organizationId?: string;
  teamId?: string;
  defaultContainerId?: string;
  defaultContainer?: Container;
  gitRepositoryUrl?: string;
  gitBranch?: string;
  status: string;
  createdAt: string;
  updatedAt?: string;
  lastAccessedAt?: string;
  containers?: Container[];
}

export interface ContainerEvent {
  id: string;
  containerId: string;
  eventType: string;
  subjectId?: string;
  details?: string;
  timestamp: string;
}

export interface ConnectionInfo {
  ipAddress?: string;
  ideEndpoint?: string;
  vncEndpoint?: string;
  sshEndpoint?: string;
  agentEndpoint?: string;
  portMappings?: Record<string, string>;
}

export interface ExecResult {
  exitCode: number;
  stdOut?: string;
  stdErr?: string;
}

export interface ProviderHealthResult {
  status: string;
  error?: string;
}

export interface CostEstimate {
  hourlyCostUsd: number;
  monthlyCostUsd: number;
  freeTierNote?: string;
}

export interface PaginatedResult<T> {
  items: T[];
  totalCount: number;
}

export interface TemplateDefinition {
  code: string;
  content: string;
}

export interface ValidationResult {
  isValid: boolean;
  errors: ValidationIssue[];
  warnings: ValidationIssue[];
}

export interface ValidationIssue {
  field?: string;
  message: string;
  line?: number;
}

export interface GitCredential {
  id: string;
  label: string;
  gitHost?: string;
  credentialType: string;
  createdAt: string;
  lastUsedAt?: string;
}

export interface ContainerGitRepository {
  id: string;
  containerId: string;
  url: string;
  branch?: string;
  targetPath: string;
  cloneStatus: string;
  cloneError?: string;
  cloneStartedAt?: string;
  cloneCompletedAt?: string;
}
