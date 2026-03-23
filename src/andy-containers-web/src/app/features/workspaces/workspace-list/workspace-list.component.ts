import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Workspace } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';

@Component({
  selector: 'app-workspace-list',
  standalone: true,
  imports: [CommonModule, RouterLink, StatusBadgeComponent],
  template: `
    <div class="space-y-6">
      <!-- Header -->
      <div class="flex items-center justify-between">
        <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Workspaces</h1>
        <div class="flex items-center gap-2">
          <button (click)="loadWorkspaces()"
            class="inline-flex items-center px-3 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700">
            Refresh
          </button>
          <button
            class="inline-flex items-center px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700">
            New Workspace
          </button>
        </div>
      </div>

      <!-- Loading -->
      <div *ngIf="loading" class="flex items-center justify-center py-12">
        <div class="text-surface-500 dark:text-surface-400">Loading workspaces...</div>
      </div>

      <!-- Error -->
      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
        <button (click)="loadWorkspaces()" class="mt-2 text-sm text-red-600 dark:text-red-400 underline">Retry</button>
      </div>

      <!-- Table -->
      <div *ngIf="!loading && !error" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 overflow-x-auto">
        <table class="min-w-full divide-y divide-surface-200 dark:divide-surface-700">
          <thead class="bg-surface-50 dark:bg-surface-800">
            <tr>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Name</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Description</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Owner</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Status</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Created</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
            <tr *ngFor="let w of workspaces" class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
              <td class="px-4 py-3 whitespace-nowrap text-sm font-medium text-surface-900 dark:text-surface-100">{{ w.name }}</td>
              <td class="px-4 py-3 text-sm text-surface-600 dark:text-surface-300 max-w-xs truncate">{{ w.description || '--' }}</td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ w.ownerId }}</td>
              <td class="px-4 py-3 whitespace-nowrap">
                <app-status-badge [status]="w.status"></app-status-badge>
              </td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ w.createdAt | date:'short' }}</td>
            </tr>
            <tr *ngIf="workspaces.length === 0">
              <td colspan="5" class="px-4 py-8 text-center text-surface-400 dark:text-surface-500">No workspaces found</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class WorkspaceListComponent implements OnInit {
  loading = true;
  error = '';
  workspaces: Workspace[] = [];

  constructor(private api: ContainersApiService) {}

  ngOnInit(): void {
    this.loadWorkspaces();
  }

  loadWorkspaces(): void {
    this.loading = true;
    this.error = '';
    this.api.getWorkspaces({ take: '100' }).subscribe({
      next: (res) => {
        this.workspaces = res.items;
        this.loading = false;
      },
      error: () => {
        this.error = 'Failed to load workspaces';
        this.loading = false;
      },
    });
  }
}
