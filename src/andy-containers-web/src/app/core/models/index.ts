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
  hostIp?: string;
  networkConfig?: string;
  createdAt: string;
  startedAt?: string;
  stoppedAt?: string;
  expiresAt?: string;
  lastActivityAt?: string;
  creationSource?: string;
  clientInfo?: string;
  codeAssistant?: string;
  containerUser?: string;
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
  codeAssistant?: string;
  guiType?: string; // "none" or "vnc"
}

export interface CodeAssistantConfig {
  tool: string;
  autoStart: boolean;
  apiKeyEnvVar?: string;
}

export const CODE_ASSISTANT_TOOLS = [
  { value: 'ClaudeCode', label: 'Claude Code', apiKeyEnv: 'ANTHROPIC_API_KEY', apiKeyProvider: 'Anthropic',
    supportsModel: false, supportsBaseUrl: false },
  { value: 'CodexCli', label: 'Codex CLI', apiKeyEnv: 'OPENAI_API_KEY', apiKeyProvider: 'OpenAI',
    supportsModel: true, supportsBaseUrl: true, modelEnvVar: 'OPENAI_MODEL', defaultModel: 'gpt-4o' },
  { value: 'Aider', label: 'Aider', apiKeyEnv: 'OPENAI_API_KEY', apiKeyProvider: 'OpenAI',
    supportsModel: true, supportsBaseUrl: true, modelEnvVar: 'AIDER_MODEL', defaultModel: 'gpt-4o' },
  { value: 'Continue', label: 'Continue', apiKeyEnv: 'CONTINUE_API_KEY', apiKeyProvider: 'Custom',
    supportsModel: false, supportsBaseUrl: false },
  { value: 'OpenCode', label: 'Open Code', apiKeyEnv: 'OPENAI_API_KEY', apiKeyProvider: 'OpenAI',
    supportsModel: true, supportsBaseUrl: true, modelEnvVar: 'LLM_MODEL', defaultModel: 'gpt-4o' },
  { value: 'QwenCoder', label: 'Qwen Coder', apiKeyEnv: 'DASHSCOPE_API_KEY', apiKeyProvider: 'Dashscope',
    supportsModel: true, supportsBaseUrl: true, modelEnvVar: 'LLM_MODEL', defaultModel: 'qwen-coder-turbo' },
  { value: 'GeminiCode', label: 'Gemini Code', apiKeyEnv: 'GOOGLE_API_KEY', apiKeyProvider: 'Google',
    supportsModel: true, supportsBaseUrl: true, modelEnvVar: 'LLM_MODEL', defaultModel: 'gemini-2.0-flash' },
  { value: 'GitHubCopilot', label: 'GitHub Copilot', apiKeyEnv: 'GITHUB_TOKEN', apiKeyProvider: 'Custom',
    supportsModel: false, supportsBaseUrl: false },
  { value: 'AmazonQ', label: 'Amazon Q', apiKeyEnv: 'AWS_ACCESS_KEY_ID', apiKeyProvider: 'Custom',
    supportsModel: false, supportsBaseUrl: false },
  { value: 'Cline', label: 'Cline', apiKeyEnv: 'ANTHROPIC_API_KEY', apiKeyProvider: 'Anthropic',
    supportsModel: true, supportsBaseUrl: false, modelEnvVar: 'LLM_MODEL', defaultModel: 'claude-sonnet-4-20250514' },
] as const;

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
  gitRepositories?: string;
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

export interface ContainerStats {
  cpuPercent: number;
  memoryUsageBytes: number;
  memoryLimitBytes: number;
  memoryPercent: number;
  diskUsageBytes: number;
  diskLimitBytes: number;
  diskPercent: number;
  timestamp: string;
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
  cloneMetadata?: string;
}

export interface GitCloneMetadata {
  fileCount?: number;
  diskUsageBytes?: number;
  lastCommitHash?: string;
  lastCommitMessage?: string;
  lastCommitAuthor?: string;
  lastCommitDate?: string;
  checkedOutBranch?: string;
}

export interface ApiKeyCredential {
  id: string;
  label: string;
  provider: string;
  envVarName: string;
  maskedValue: string;
  isValid: boolean;
  lastValidatedAt?: string;
  lastUsedAt?: string;
  baseUrl?: string;
  modelName?: string;
  createdAt: string;
  updatedAt?: string;
}

export interface ApiKeyChangeEntry {
  action: string;
  timestamp: string;
  ipAddress?: string;
  details?: string;
}

export const API_KEY_PROVIDERS = [
  { value: 'Anthropic', label: 'Anthropic', defaultEnvVar: 'ANTHROPIC_API_KEY' },
  { value: 'OpenAI', label: 'OpenAI', defaultEnvVar: 'OPENAI_API_KEY' },
  { value: 'Google', label: 'Google', defaultEnvVar: 'GOOGLE_API_KEY' },
  { value: 'Dashscope', label: 'Dashscope', defaultEnvVar: 'DASHSCOPE_API_KEY' },
  { value: 'Custom', label: 'Custom', defaultEnvVar: 'API_KEY' },
  { value: 'OpenRouter', label: 'OpenRouter', defaultEnvVar: 'OPENROUTER_API_KEY' },
  { value: 'Ollama', label: 'Ollama (Local)', defaultEnvVar: '' },
  { value: 'OpenAiCompatible', label: 'OpenAI Compatible', defaultEnvVar: 'OPENAI_API_KEY' },
] as const;

export interface WorkspaceGitRepo {
  url: string;
  branch?: string;
  credentialRef?: string;
  targetPath?: string;
}

export interface ContainerScreenshot {
  available: boolean;
  ansiText?: string;
  capturedAt?: string;
  cols?: number;
  rows?: number;
}

export interface Organization {
  id: string;
  name: string;
  description?: string;
  ownerId: string;
  createdAt: string;
  updatedAt?: string;
}

export interface Team {
  id: string;
  organizationId: string;
  name: string;
  description?: string;
  createdAt: string;
}

export interface ImageBuildRecord {
  id: string;
  imageReference: string;
  templateCode?: string;
  status: TemplateBuildStatus;
  lastBuiltAt?: string;
  dockerfileChecksum?: string;
  lastBuildError?: string;
  buildLog?: string;
  imageSizeBytes?: number;
  architecture?: string;
  os?: string;
  layerCount?: number;
  imageDigest?: string;
  imageCreatedAt?: string;
  checkedAt: string;
}

export type TemplateBuildStatus = 'Unknown' | 'NotBuilt' | 'Building' | 'Built' | 'Outdated' | 'Failed';
