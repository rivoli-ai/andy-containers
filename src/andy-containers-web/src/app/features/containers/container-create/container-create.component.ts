import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Template, Provider } from '../../../core/models';

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
          <select id="template" [(ngModel)]="selectedTemplateId" name="template" required
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

        <!-- Git Repository URL (optional) -->
        <div>
          <label for="gitRepo" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Git Repository URL <span class="text-surface-400">(optional)</span></label>
          <input id="gitRepo" type="text" [(ngModel)]="gitRepoUrl" name="gitRepo"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500"
            placeholder="https://github.com/user/repo.git" />
          <div *ngIf="gitRepoUrl" class="mt-2 flex items-center gap-2">
            <input id="skipValidation" type="checkbox" [(ngModel)]="skipUrlValidation" name="skipValidation"
              class="rounded border-surface-300 dark:border-surface-600 text-primary-600 focus:ring-primary-500" />
            <label for="skipValidation" class="text-xs text-surface-500 dark:text-surface-400">
              Skip URL validation <span class="text-surface-400">(for repos behind firewalls only accessible from the container)</span>
            </label>
          </div>
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
  gitRepoUrl = '';
  skipUrlValidation = false;
  templates: Template[] = [];
  providers: Provider[] = [];
  submitting = false;
  error = '';

  constructor(private api: ContainersApiService, private router: Router) {}

  ngOnInit(): void {
    this.api.getTemplates({ take: '100' }).subscribe({
      next: (res) => { this.templates = res.items; },
    });
    this.api.getProviders().subscribe({
      next: (res) => { this.providers = res; },
    });
  }

  onSubmit(): void {
    if (!this.name || !this.selectedTemplateId) return;
    this.submitting = true;
    this.error = '';

    const request: any = {
      name: this.name,
      templateId: this.selectedTemplateId,
    };
    if (this.selectedProviderId) {
      request.providerId = this.selectedProviderId;
    }
    if (this.gitRepoUrl) {
      request.gitRepository = { url: this.gitRepoUrl };
      if (this.skipUrlValidation) {
        request.skipUrlValidation = true;
      }
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
