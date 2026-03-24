import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';

@Component({
  selector: 'app-workspace-create',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <div class="max-w-2xl mx-auto space-y-6">
      <div class="flex items-center gap-3">
        <a routerLink="/workspaces" class="text-surface-400 hover:text-surface-600 dark:hover:text-surface-300">
          <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
        </a>
        <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Create Workspace</h1>
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
            placeholder="my-workspace" />
        </div>

        <!-- Description -->
        <div>
          <label for="description" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Description <span class="text-surface-400">(optional)</span></label>
          <textarea id="description" [(ngModel)]="description" name="description" rows="3"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500"
            placeholder="What this workspace is for..."></textarea>
        </div>

        <!-- Git Repository URL -->
        <div>
          <label for="gitUrl" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Git Repository URL <span class="text-surface-400">(optional)</span></label>
          <input id="gitUrl" type="text" [(ngModel)]="gitRepositoryUrl" name="gitUrl"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500"
            placeholder="https://github.com/user/repo.git" />
        </div>

        <!-- Git Branch -->
        <div *ngIf="gitRepositoryUrl">
          <label for="gitBranch" class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Branch <span class="text-surface-400">(optional)</span></label>
          <input id="gitBranch" type="text" [(ngModel)]="gitBranch" name="gitBranch"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 focus:ring-2 focus:ring-primary-500 focus:border-primary-500"
            placeholder="main" />
        </div>

        <!-- Actions -->
        <div class="flex items-center justify-end gap-3 pt-3 border-t border-surface-200 dark:border-surface-700">
          <a routerLink="/workspaces"
            class="px-4 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700">
            Cancel
          </a>
          <button type="submit" [disabled]="submitting || !name"
            class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700 disabled:opacity-50 disabled:cursor-not-allowed">
            {{ submitting ? 'Creating...' : 'Create Workspace' }}
          </button>
        </div>
      </form>
    </div>
  `,
})
export class WorkspaceCreateComponent {
  name = '';
  description = '';
  gitRepositoryUrl = '';
  gitBranch = '';
  submitting = false;
  error = '';

  constructor(private api: ContainersApiService, private router: Router) {}

  onSubmit(): void {
    if (!this.name) return;
    this.submitting = true;
    this.error = '';

    const request: any = { name: this.name };
    if (this.description) request.description = this.description;
    if (this.gitRepositoryUrl) {
      request.gitRepositoryUrl = this.gitRepositoryUrl;
      if (this.gitBranch) request.gitBranch = this.gitBranch;
    }

    this.api.createWorkspace(request).subscribe({
      next: (ws) => {
        this.router.navigate(['/workspaces', ws.id]);
      },
      error: (err) => {
        this.error = err?.error?.error || err?.message || 'Failed to create workspace';
        this.submitting = false;
      },
    });
  }
}
