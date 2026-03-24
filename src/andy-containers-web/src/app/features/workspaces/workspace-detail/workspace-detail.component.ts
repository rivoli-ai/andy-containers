import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Workspace } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';

@Component({
  selector: 'app-workspace-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, StatusBadgeComponent],
  template: `
    <div class="max-w-4xl mx-auto space-y-6">
      <!-- Loading -->
      <div *ngIf="loading" class="flex items-center justify-center py-12">
        <div class="text-surface-500 dark:text-surface-400">Loading workspace...</div>
      </div>

      <!-- Error -->
      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
        <a routerLink="/workspaces" class="mt-2 inline-block text-sm text-red-600 dark:text-red-400 underline">Back to list</a>
      </div>

      <ng-container *ngIf="workspace && !loading">
        <!-- Header -->
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-3">
            <a routerLink="/workspaces" class="text-surface-400 hover:text-surface-600 dark:hover:text-surface-300">
              <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
            </a>
            <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">{{ workspace.name }}</h1>
            <app-status-badge [status]="workspace.status"></app-status-badge>
          </div>
          <div class="flex items-center gap-2">
            <button *ngIf="!editing" (click)="editing = true"
              class="px-3 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700">
              Edit
            </button>
            <button (click)="confirmDelete()"
              class="px-3 py-2 text-sm font-medium rounded-lg border border-red-300 dark:border-red-800 text-red-600 dark:text-red-400 bg-white dark:bg-surface-800 hover:bg-red-50 dark:hover:bg-red-900/20">
              Delete
            </button>
          </div>
        </div>

        <!-- Edit Form -->
        <div *ngIf="editing" class="rounded-lg border border-primary-200 dark:border-primary-800 bg-primary-50 dark:bg-primary-900/20 p-6 space-y-4">
          <div>
            <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Name</label>
            <input type="text" [(ngModel)]="editName"
              class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100" />
          </div>
          <div>
            <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Description</label>
            <textarea [(ngModel)]="editDescription" rows="2"
              class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100"></textarea>
          </div>
          <div *ngIf="workspace.gitRepositoryUrl">
            <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Git Branch</label>
            <input type="text" [(ngModel)]="editGitBranch"
              class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100" />
          </div>
          <div class="flex gap-2">
            <button (click)="saveEdit()" [disabled]="saving"
              class="px-3 py-1.5 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700 disabled:opacity-50">
              {{ saving ? 'Saving...' : 'Save' }}
            </button>
            <button (click)="editing = false"
              class="px-3 py-1.5 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-600 dark:text-surface-400">
              Cancel
            </button>
          </div>
        </div>

        <!-- Details Card -->
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100 mb-4">Details</h2>
          <dl class="space-y-3">
            <div *ngIf="workspace.description" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Description</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100 text-right max-w-md">{{ workspace.description }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Owner</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100">{{ workspace.ownerId }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Created</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ workspace.createdAt | date:'medium' }}</dd>
            </div>
            <div *ngIf="workspace.updatedAt" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Updated</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ workspace.updatedAt | date:'medium' }}</dd>
            </div>
            <div *ngIf="workspace.lastAccessedAt" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Last accessed</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ workspace.lastAccessedAt | date:'medium' }}</dd>
            </div>
            <div *ngIf="workspace.gitRepositoryUrl" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Git Repository</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100 font-mono">{{ workspace.gitRepositoryUrl }}</dd>
            </div>
            <div *ngIf="workspace.gitBranch" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Branch</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100 font-mono">{{ workspace.gitBranch }}</dd>
            </div>
          </dl>
        </div>

        <!-- Containers Card -->
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
          <div class="flex items-center justify-between mb-4">
            <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">Containers</h2>
            <a [routerLink]="['/containers/create']" [queryParams]="{ workspaceId: workspace.id }"
              class="px-3 py-1.5 text-xs font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700">
              Create Container
            </a>
          </div>
          <div *ngIf="workspace.containers && workspace.containers.length > 0">
            <div *ngFor="let c of workspace.containers"
              class="flex items-center justify-between py-2 border-b border-surface-100 dark:border-surface-700 last:border-0">
              <a [routerLink]="['/containers', c.id]" class="text-sm font-medium text-primary-600 hover:text-primary-700">{{ c.name }}</a>
              <app-status-badge [status]="c.status"></app-status-badge>
            </div>
          </div>
          <p *ngIf="!workspace.containers || workspace.containers.length === 0"
            class="text-sm text-surface-400 dark:text-surface-500">No containers yet</p>
        </div>
      </ng-container>
    </div>
  `,
})
export class WorkspaceDetailComponent implements OnInit {
  workspace: Workspace | null = null;
  loading = true;
  error = '';
  editing = false;
  saving = false;
  editName = '';
  editDescription = '';
  editGitBranch = '';

  constructor(
    private api: ContainersApiService,
    private route: ActivatedRoute,
    private router: Router,
  ) {}

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id')!;
    this.api.getWorkspace(id).subscribe({
      next: (ws) => {
        this.workspace = ws;
        this.editName = ws.name;
        this.editDescription = ws.description || '';
        this.editGitBranch = ws.gitBranch || '';
        this.loading = false;
      },
      error: () => {
        this.error = 'Failed to load workspace';
        this.loading = false;
      },
    });
  }

  saveEdit(): void {
    if (!this.workspace) return;
    this.saving = true;
    const data: any = {};
    if (this.editName !== this.workspace.name) data.name = this.editName;
    if (this.editDescription !== (this.workspace.description || '')) data.description = this.editDescription;
    if (this.editGitBranch !== (this.workspace.gitBranch || '')) data.gitBranch = this.editGitBranch;

    this.api.updateWorkspace(this.workspace.id, data).subscribe({
      next: (ws) => {
        this.workspace = ws;
        this.editing = false;
        this.saving = false;
      },
      error: () => {
        this.saving = false;
      },
    });
  }

  confirmDelete(): void {
    if (!this.workspace) return;
    if (!confirm(`Delete workspace "${this.workspace.name}"?`)) return;
    this.api.deleteWorkspace(this.workspace.id).subscribe({
      next: () => {
        this.router.navigate(['/workspaces']);
      },
      error: () => {
        this.error = 'Failed to delete workspace';
      },
    });
  }
}
