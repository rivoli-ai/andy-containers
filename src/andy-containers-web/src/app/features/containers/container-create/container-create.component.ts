import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Template, Provider, GitCredential, Workspace, WorkspaceGitRepo, CodeAssistantConfig, CODE_ASSISTANT_TOOLS, ApiKeyCredential } from '../../../core/models';

@Component({
  selector: 'app-container-create',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <div class="max-w-2xl mx-auto space-y-6">
      <div class="flex items-center gap-3">
        <a routerLink="/containers" class="text-surface-400 hover:text-surface-600 dark:hover:text-surface-300">
          <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
        </a>
        <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Create Container</h1>
      </div>

      <!-- Error -->
      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
      </div>

      <form (ngSubmit)="onSubmit()" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6 space-y-5">
        <!-- Name -->
        <div>
          <label for="name" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Name *</label>
          <input id="name" type="text" [(ngModel)]="name" name="name" required
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500"
            placeholder="my-container" />
        </div>

        <!-- Template -->
        <div>
          <label for="template" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Template *</label>
          <select id="template" [(ngModel)]="selectedTemplateId" name="template" required (ngModelChange)="updateTemplateCodeAssistant()"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500">
            <option value="">Select a template...</option>
            <option *ngFor="let t of templates" [value]="t.id">{{ t.name }} ({{ t.code }})</option>
          </select>
        </div>

        <!-- Provider (optional) -->
        <div>
          <label for="provider" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Provider <span class="text-surface-400">(optional)</span></label>
          <select id="provider" [(ngModel)]="selectedProviderId" name="provider"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500">
            <option value="">Auto-select</option>
            <option *ngFor="let p of providers" [value]="p.id">{{ p.name }} ({{ p.type }})</option>
          </select>
        </div>

        <!-- Workspace (optional) -->
        <div>
          <label for="workspace" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Workspace <span class="text-surface-400">(optional)</span></label>
          <select id="workspace" [(ngModel)]="selectedWorkspaceId" name="workspace" (ngModelChange)="updateWorkspaceRepos()"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500">
            <option value="">None</option>
            <option *ngFor="let w of workspaces" [value]="w.id">{{ w.name }}</option>
          </select>
        </div>

        <!-- Code Assistant -->
        <div>
          <label for="codeAssistant" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Code Assistant <span class="text-surface-400">(optional)</span></label>
          <select id="codeAssistant" [(ngModel)]="selectedCodeAssistant" name="codeAssistant" (ngModelChange)="onCodeAssistantChange()"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500">
            <option value="">None</option>
            <option *ngFor="let tool of codeAssistantTools" [value]="tool.value">{{ tool.label }}</option>
          </select>
          <div *ngIf="templateCodeAssistant && !selectedCodeAssistant" class="mt-1 text-xs text-surface-500 dark:text-surface-400">
            Template default: <span class="font-medium">{{ getToolLabel(templateCodeAssistant.tool) }}</span>
            <span class="text-surface-400 ml-1">(will be used unless overridden)</span>
          </div>
          <div *ngIf="selectedCodeAssistant && hasApiKeyForTool(selectedCodeAssistant)" class="mt-2 rounded-lg bg-green-50 dark:bg-green-900/20 p-3">
            <p class="text-xs text-green-800 dark:text-green-200">
              API key found for {{ getToolLabel(selectedCodeAssistant) }}. The <code class="font-mono bg-green-100 dark:bg-green-800/40 px-1 rounded">{{ getApiKeyEnv(selectedCodeAssistant) }}</code> variable will be injected at runtime.
            </p>
          </div>
          <div *ngIf="selectedCodeAssistant && !hasApiKeyForTool(selectedCodeAssistant)" class="mt-2 rounded-lg bg-red-50 dark:bg-red-900/20 p-3">
            <p class="text-xs text-red-800 dark:text-red-200">
              <span class="font-medium">No API key configured.</span> Go to <a routerLink="/settings" class="underline font-medium">Settings</a> to add a {{ getToolProvider(selectedCodeAssistant) }} API key, or the tool won't be able to authenticate.
            </p>
          </div>
        </div>
        <div *ngIf="selectedCodeAssistant" class="flex items-center gap-2">
          <input id="excludeTemplateAssistant" type="checkbox" [(ngModel)]="excludeTemplateCodeAssistant" name="excludeTemplateAssistant"
            class="rounded border-surface-300 dark:border-surface-600 text-primary-600 focus:ring-primary-500" />
          <label for="excludeTemplateAssistant" class="text-xs text-surface-500 dark:text-surface-400">
            Override template default <span class="text-surface-400">(use my selection instead)</span>
          </label>
        </div>

        <!-- Git Repositories -->
        <div>
          <div class="flex items-center justify-between mb-2">
            <label class="block text-sm font-medium text-surface-700 dark:text-surface-300">Git Repositories <span class="text-surface-400">(optional)</span></label>
            <button type="button" (click)="addRepo()"
              class="text-xs font-medium text-primary-600 hover:text-primary-700">+ Add repository</button>
          </div>

          <!-- Workspace inherited repos -->
          <div *ngIf="selectedWorkspaceId && workspaceRepos.length > 0" class="mb-3 rounded-lg bg-surface-50 dark:bg-surface-900 p-3">
            <p class="text-xs font-medium text-surface-500 dark:text-surface-400 mb-2">Inherited from workspace (will be merged):</p>
            <div *ngFor="let wr of workspaceRepos" class="flex items-center gap-2 text-xs text-surface-600 dark:text-surface-300 mb-1">
              <span class="font-mono truncate">{{ wr.url }}</span>
              <span *ngIf="wr.branch" class="px-1.5 py-0.5 rounded bg-surface-200 dark:bg-surface-700">{{ wr.branch }}</span>
            </div>
          </div>

          <div *ngFor="let repo of repos; let i = index" class="mb-2 rounded-lg border border-surface-200 dark:border-surface-700 p-3 space-y-2">
            <div class="flex gap-2">
              <input type="text" [(ngModel)]="repo.url" [name]="'repoUrl' + i" placeholder="https://github.com/user/repo.git"
                class="flex-1 rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-1.5 text-sm text-surface-900 dark:text-surface-100 font-mono" />
              <button type="button" (click)="removeRepo(i)" class="px-2 text-red-500 hover:text-red-700 text-sm">x</button>
            </div>
            <div class="flex gap-2">
              <input type="text" [(ngModel)]="repo.branch" [name]="'repoBranch' + i" placeholder="branch (default)"
                class="w-32 rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-1.5 text-sm text-surface-900 dark:text-surface-100" />
              <input type="text" [(ngModel)]="repo.targetPath" [name]="'repoPath' + i" placeholder="/workspace/repo"
                class="w-40 rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-1.5 text-sm text-surface-900 dark:text-surface-100 font-mono" />
              <select [(ngModel)]="repo.credentialRef" [name]="'repoCred' + i"
                class="flex-1 rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-1.5 text-sm text-surface-900 dark:text-surface-100">
                <option value="">No credential</option>
                <option *ngFor="let c of credentials" [value]="c.label">{{ c.label }}</option>
              </select>
            </div>
          </div>
          <p *ngIf="repos.length === 0 && workspaceRepos.length === 0" class="text-xs text-surface-400">No repositories added.</p>
        </div>

        <div *ngIf="repos.length > 0" class="flex items-center gap-2">
          <input id="skipValidation" type="checkbox" [(ngModel)]="skipUrlValidation" name="skipValidation"
            class="rounded border-surface-300 dark:border-surface-600 text-primary-600 focus:ring-primary-500" />
          <label for="skipValidation" class="text-xs text-surface-500 dark:text-surface-400">
            Skip URL validation <span class="text-surface-400">(for repos behind firewalls)</span>
          </label>
        </div>

        <!-- Actions -->
        <div class="flex items-center justify-end gap-3 pt-3 border-t border-surface-200 dark:border-surface-700">
          <a routerLink="/containers"
            class="px-4 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700">
            Cancel
          </a>
          <button type="submit" [disabled]="submitting || !name || !selectedTemplateId"
            class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700 disabled:opacity-50 disabled:cursor-not-allowed">
            {{ submitting ? 'Creating...' : 'Create Container' }}
          </button>
        </div>
      </form>
    </div>
  `,
})
export class ContainerCreateComponent implements OnInit {
  name = '';
  selectedTemplateId = '';
  selectedProviderId = '';
  selectedWorkspaceId = '';
  repos: { url: string; branch: string; targetPath: string; credentialRef: string }[] = [];
  selectedCodeAssistant = '';
  excludeTemplateCodeAssistant = false;
  templateCodeAssistant: CodeAssistantConfig | null = null;
  codeAssistantTools = CODE_ASSISTANT_TOOLS;
  skipUrlValidation = false;
  templates: Template[] = [];
  providers: Provider[] = [];
  workspaces: Workspace[] = [];
  credentials: GitCredential[] = [];
  workspaceRepos: WorkspaceGitRepo[] = [];
  apiKeys: ApiKeyCredential[] = [];
  submitting = false;
  error = '';

  constructor(private api: ContainersApiService, private router: Router, private route: ActivatedRoute) {}

  ngOnInit(): void {
    this.selectedWorkspaceId = this.route.snapshot.queryParamMap.get('workspaceId') || '';
    this.api.getTemplates({ take: '100' }).subscribe({
      next: (res) => { this.templates = res.items; },
    });
    this.api.getProviders().subscribe({
      next: (res) => { this.providers = res; },
    });
    this.api.getGitCredentials().subscribe({
      next: (creds) => { this.credentials = creds; },
    });
    this.api.getApiKeys().subscribe({
      next: (keys) => { this.apiKeys = keys; },
    });
    this.api.getWorkspaces({ take: '100' }).subscribe({
      next: (res) => {
        this.workspaces = res.items;
        this.updateWorkspaceRepos();
      },
    });
  }

  addRepo(): void {
    this.repos.push({ url: '', branch: '', targetPath: '', credentialRef: '' });
  }

  removeRepo(i: number): void {
    this.repos.splice(i, 1);
  }

  updateWorkspaceRepos(): void {
    if (!this.selectedWorkspaceId) {
      this.workspaceRepos = [];
      return;
    }
    const ws = this.workspaces.find(w => w.id === this.selectedWorkspaceId);
    if (ws?.gitRepositories) {
      try {
        this.workspaceRepos = JSON.parse(ws.gitRepositories);
      } catch {
        this.workspaceRepos = [];
      }
    } else {
      this.workspaceRepos = [];
    }
  }

  onCodeAssistantChange(): void {
    if (this.selectedCodeAssistant) {
      this.excludeTemplateCodeAssistant = true;
    }
  }

  getToolLabel(tool: string): string {
    return this.codeAssistantTools.find(t => t.value === tool)?.label || tool;
  }

  getApiKeyEnv(tool: string): string {
    return this.codeAssistantTools.find(t => t.value === tool)?.apiKeyEnv || 'API_KEY';
  }

  getToolProvider(tool: string): string {
    return this.codeAssistantTools.find(t => t.value === tool)?.apiKeyProvider || 'Custom';
  }

  hasApiKeyForTool(tool: string): boolean {
    const provider = this.getToolProvider(tool);
    return this.apiKeys.some(k => k.provider === provider);
  }

  updateTemplateCodeAssistant(): void {
    if (!this.selectedTemplateId) {
      this.templateCodeAssistant = null;
      return;
    }
    const tmpl = this.templates.find(t => t.id === this.selectedTemplateId);
    if (tmpl?.codeAssistant) {
      try {
        this.templateCodeAssistant = JSON.parse(tmpl.codeAssistant);
      } catch {
        this.templateCodeAssistant = null;
      }
    } else {
      this.templateCodeAssistant = null;
    }
  }

  onSubmit(): void {
    if (!this.name || !this.selectedTemplateId) return;
    this.submitting = true;
    this.error = '';

    const gitRepos = this.repos
      .filter(r => r.url)
      .map(r => ({
        url: r.url,
        branch: r.branch || undefined,
        targetPath: r.targetPath || undefined,
        credentialRef: r.credentialRef || undefined,
      }));

    // Check for duplicate URLs
    const urls = gitRepos.map(r => r.url.toLowerCase());
    const dupes = urls.filter((u, i) => urls.indexOf(u) !== i);
    if (dupes.length > 0) {
      this.error = `Duplicate repository URL: ${dupes[0]}`;
      this.submitting = false;
      return;
    }

    const request: any = {
      name: this.name,
      templateId: this.selectedTemplateId,
      source: 'WebUi',
    };
    if (this.selectedProviderId) {
      request.providerId = this.selectedProviderId;
    }
    if (this.selectedWorkspaceId) {
      request.workspaceId = this.selectedWorkspaceId;
    }
    if (gitRepos.length > 0) {
      request.gitRepositories = gitRepos;
      if (this.skipUrlValidation) {
        request.skipUrlValidation = true;
      }
    }
    if (this.selectedCodeAssistant) {
      const tool = this.codeAssistantTools.find(t => t.value === this.selectedCodeAssistant);
      request.codeAssistant = {
        tool: this.selectedCodeAssistant,
        autoStart: false,
        apiKeyEnvVar: tool?.apiKeyEnv,
      };
      request.excludeTemplateCodeAssistant = this.excludeTemplateCodeAssistant;
    }

    this.api.createContainer(request).subscribe({
      next: (container) => {
        this.router.navigate(['/containers', container.id]);
      },
      error: (err) => {
        this.error = err?.error?.error || err?.message || 'Failed to create container';
        this.submitting = false;
      },
    });
  }
}
