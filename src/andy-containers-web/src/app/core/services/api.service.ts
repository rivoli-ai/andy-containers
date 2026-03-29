import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  Container,
  ContainerStats,
  Template,
  TemplateDefinition,
  ValidationResult,
  Provider,
  Workspace,
  ContainerEvent,
  ContainerGitRepository,
  GitCredential,
  ConnectionInfo,
  ExecResult,
  ProviderHealthResult,
  CostEstimate,
  PaginatedResult,
  ApiKeyCredential,
  ApiKeyChangeEntry,
} from '../models';

@Injectable({ providedIn: 'root' })
export class ContainersApiService {
  private readonly baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  // Containers
  getContainers(params?: Record<string, string>): Observable<PaginatedResult<Container>> {
    let httpParams = new HttpParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        httpParams = httpParams.set(key, value);
      });
    }
    return this.http.get<PaginatedResult<Container>>(`${this.baseUrl}/containers`, { params: httpParams });
  }

  getContainer(id: string): Observable<Container> {
    return this.http.get<Container>(`${this.baseUrl}/containers/${id}`);
  }

  createContainer(data: Partial<Container>): Observable<Container> {
    return this.http.post<Container>(`${this.baseUrl}/containers`, data);
  }

  startContainer(id: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/containers/${id}/start`, {});
  }

  stopContainer(id: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/containers/${id}/stop`, {});
  }

  destroyContainer(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/containers/${id}`);
  }

  execCommand(id: string, command: string): Observable<ExecResult> {
    return this.http.post<ExecResult>(`${this.baseUrl}/containers/${id}/exec`, { command });
  }

  getConnectionInfo(id: string): Observable<ConnectionInfo> {
    return this.http.get<ConnectionInfo>(`${this.baseUrl}/containers/${id}/connection`);
  }

  resizeContainer(id: string, resources: { cpuCores: number; memoryMb: number; diskGb: number }): Observable<Container> {
    return this.http.put<Container>(`${this.baseUrl}/containers/${id}/resources`, resources);
  }

  getContainerStats(id: string): Observable<ContainerStats> {
    return this.http.get<ContainerStats>(`${this.baseUrl}/containers/${id}/stats`);
  }

  getContainerEvents(id: string): Observable<ContainerEvent[]> {
    return this.http.get<ContainerEvent[]>(`${this.baseUrl}/containers/${id}/events`);
  }

  getContainerRepositories(id: string): Observable<ContainerGitRepository[]> {
    return this.http.get<ContainerGitRepository[]>(`${this.baseUrl}/containers/${id}/repositories`);
  }

  // Git Credentials
  getGitCredentials(): Observable<GitCredential[]> {
    return this.http.get<GitCredential[]>(`${this.baseUrl}/git-credentials`);
  }

  createGitCredential(data: { label: string; token: string; gitHost?: string }): Observable<GitCredential> {
    return this.http.post<GitCredential>(`${this.baseUrl}/git-credentials`, data);
  }

  // Providers
  getCostEstimate(providerId: string): Observable<CostEstimate> {
    return this.http.get<CostEstimate>(`${this.baseUrl}/providers/${providerId}/cost-estimate`);
  }

  getProviders(): Observable<Provider[]> {
    return this.http.get<Provider[]>(`${this.baseUrl}/providers`);
  }

  getProvider(id: string): Observable<Provider> {
    return this.http.get<Provider>(`${this.baseUrl}/providers/${id}`);
  }

  checkProviderHealth(id: string): Observable<ProviderHealthResult> {
    return this.http.get<ProviderHealthResult>(`${this.baseUrl}/providers/${id}/health`);
  }

  // Templates
  getTemplates(params?: Record<string, string>): Observable<PaginatedResult<Template>> {
    let httpParams = new HttpParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        httpParams = httpParams.set(key, value);
      });
    }
    return this.http.get<PaginatedResult<Template>>(`${this.baseUrl}/templates`, { params: httpParams });
  }

  getTemplate(id: string): Observable<Template> {
    return this.http.get<Template>(`${this.baseUrl}/templates/${id}`);
  }

  getTemplateDefinition(id: string): Observable<TemplateDefinition> {
    return this.http.get<TemplateDefinition>(`${this.baseUrl}/templates/${id}/definition`);
  }

  validateTemplateYaml(content: string): Observable<ValidationResult> {
    return this.http.post<ValidationResult>(`${this.baseUrl}/templates/validate`, { content });
  }

  updateTemplateDefinition(id: string, content: string): Observable<Template> {
    return this.http.put<Template>(`${this.baseUrl}/templates/${id}/definition`, { content });
  }

  updateTemplate(id: string, template: Partial<Template>): Observable<Template> {
    return this.http.put<Template>(`${this.baseUrl}/templates/${id}`, template);
  }

  deleteTemplate(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/templates/${id}`);
  }

  // Workspaces
  getWorkspaces(params?: Record<string, string>): Observable<PaginatedResult<Workspace>> {
    let httpParams = new HttpParams();
    if (params) {
      Object.entries(params).forEach(([key, value]) => {
        httpParams = httpParams.set(key, value);
      });
    }
    return this.http.get<PaginatedResult<Workspace>>(`${this.baseUrl}/workspaces`, { params: httpParams });
  }

  getWorkspace(id: string): Observable<Workspace> {
    return this.http.get<Workspace>(`${this.baseUrl}/workspaces/${id}`);
  }

  createWorkspace(data: { name: string; description?: string; organizationId?: string; teamId?: string; gitRepositoryUrl?: string; gitBranch?: string; gitRepositories?: { url: string; branch?: string; credentialRef?: string; targetPath?: string }[] }): Observable<Workspace> {
    return this.http.post<Workspace>(`${this.baseUrl}/workspaces`, data);
  }

  updateWorkspace(id: string, data: { name?: string; description?: string; gitBranch?: string; gitRepositories?: { url: string; branch?: string; credentialRef?: string; targetPath?: string }[] }): Observable<Workspace> {
    return this.http.put<Workspace>(`${this.baseUrl}/workspaces/${id}`, data);
  }

  deleteWorkspace(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/workspaces/${id}`);
  }

  // API Keys
  getApiKeys(): Observable<ApiKeyCredential[]> {
    return this.http.get<ApiKeyCredential[]>(`${this.baseUrl}/api-keys`);
  }

  createApiKey(data: { label: string; provider: string; apiKey: string; envVarName?: string }): Observable<ApiKeyCredential> {
    return this.http.post<ApiKeyCredential>(`${this.baseUrl}/api-keys`, data);
  }

  updateApiKey(id: string, data: { label?: string; apiKey?: string }): Observable<ApiKeyCredential> {
    return this.http.put<ApiKeyCredential>(`${this.baseUrl}/api-keys/${id}`, data);
  }

  deleteApiKey(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/api-keys/${id}`);
  }

  validateApiKey(id: string): Observable<{ isValid: boolean; error?: string }> {
    return this.http.post<{ isValid: boolean; error?: string }>(`${this.baseUrl}/api-keys/${id}/validate`, {});
  }

  getApiKeyHistory(id: string): Observable<ApiKeyChangeEntry[]> {
    return this.http.get<ApiKeyChangeEntry[]>(`${this.baseUrl}/api-keys/${id}/history`);
  }
}
