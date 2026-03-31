import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../core/services/api.service';
import { Organization } from '../../core/models';

@Component({
  selector: 'app-organization-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <div class="space-y-6">
      <!-- Header -->
      <div class="flex items-center justify-between">
        <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Organizations</h1>
        <div class="flex items-center gap-2">
          <button (click)="loadOrganizations()"
            class="inline-flex items-center px-3 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700">
            Refresh
          </button>
          <button (click)="showCreateForm = !showCreateForm"
            class="inline-flex items-center px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700">
            Create Organization
          </button>
        </div>
      </div>

      <!-- Create Form -->
      <div *ngIf="showCreateForm" class="rounded-lg border border-primary-200 dark:border-primary-800 bg-primary-50 dark:bg-primary-900/20 p-6 space-y-4">
        <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">New Organization</h2>
        <div>
          <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Name</label>
          <input type="text" [(ngModel)]="newName" placeholder="Organization name"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100" />
        </div>
        <div>
          <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Description</label>
          <textarea [(ngModel)]="newDescription" rows="2" placeholder="Optional description"
            class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100"></textarea>
        </div>
        <div *ngIf="createError" class="text-sm text-red-600 dark:text-red-400">{{ createError }}</div>
        <div class="flex gap-2">
          <button (click)="createOrganization()" [disabled]="creating || !newName.trim()"
            class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700 disabled:opacity-50">
            {{ creating ? 'Creating...' : 'Create' }}
          </button>
          <button (click)="cancelCreate()"
            class="px-4 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-600 dark:text-surface-400">
            Cancel
          </button>
        </div>
      </div>

      <!-- Loading -->
      <div *ngIf="loading" class="flex items-center justify-center py-12">
        <div class="text-surface-500 dark:text-surface-400">Loading organizations...</div>
      </div>

      <!-- Error -->
      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
        <button (click)="loadOrganizations()" class="mt-2 text-sm text-red-600 dark:text-red-400 underline">Retry</button>
      </div>

      <!-- Table -->
      <div *ngIf="!loading && !error" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 overflow-x-auto">
        <table class="min-w-full divide-y divide-surface-200 dark:divide-surface-700">
          <thead class="bg-surface-50 dark:bg-surface-800">
            <tr>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Name</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Description</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Owner</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Created</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Actions</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
            <tr *ngFor="let org of organizations" class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
              <td class="px-4 py-3 whitespace-nowrap">
                <a [routerLink]="['/organizations', org.id]"
                  class="text-sm font-medium text-primary-600 hover:text-primary-700 dark:text-primary-400 dark:hover:text-primary-300">
                  {{ org.name }}
                </a>
              </td>
              <td class="px-4 py-3 text-sm text-surface-600 dark:text-surface-300 max-w-xs truncate">{{ org.description || '--' }}</td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300 font-mono">{{ org.ownerId | slice:0:8 }}...</td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ org.createdAt | date:'short' }}</td>
              <td class="px-4 py-3 whitespace-nowrap">
                <button (click)="confirmDelete(org, $event)"
                  class="text-sm text-red-600 dark:text-red-400 hover:text-red-800 dark:hover:text-red-300">
                  Delete
                </button>
              </td>
            </tr>
            <tr *ngIf="organizations.length === 0">
              <td colspan="5" class="px-4 py-8 text-center text-surface-400 dark:text-surface-500">
                No organizations found.
                <button (click)="showCreateForm = true" class="text-primary-600 hover:text-primary-700 ml-1">Create one</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class OrganizationListComponent implements OnInit {
  loading = true;
  error = '';
  organizations: Organization[] = [];
  showCreateForm = false;
  creating = false;
  createError = '';
  newName = '';
  newDescription = '';

  constructor(private api: ContainersApiService, private router: Router) {}

  ngOnInit(): void {
    this.loadOrganizations();
  }

  loadOrganizations(): void {
    this.loading = true;
    this.error = '';
    this.api.getOrganizations().subscribe({
      next: (orgs) => {
        this.organizations = orgs;
        this.loading = false;
      },
      error: () => {
        this.error = 'Failed to load organizations';
        this.loading = false;
      },
    });
  }

  createOrganization(): void {
    if (!this.newName.trim()) return;
    this.creating = true;
    this.createError = '';
    const data: { name: string; description?: string } = { name: this.newName.trim() };
    if (this.newDescription.trim()) {
      data.description = this.newDescription.trim();
    }
    this.api.createOrganization(data).subscribe({
      next: (org) => {
        this.organizations.unshift(org);
        this.cancelCreate();
        this.creating = false;
      },
      error: () => {
        this.createError = 'Failed to create organization';
        this.creating = false;
      },
    });
  }

  cancelCreate(): void {
    this.showCreateForm = false;
    this.newName = '';
    this.newDescription = '';
    this.createError = '';
  }

  confirmDelete(org: Organization, event: Event): void {
    event.stopPropagation();
    if (!confirm(`Delete organization "${org.name}"? This cannot be undone.`)) return;
    this.api.deleteOrganization(org.id).subscribe({
      next: () => {
        this.organizations = this.organizations.filter(o => o.id !== org.id);
      },
      error: () => {
        this.error = 'Failed to delete organization';
      },
    });
  }
}
