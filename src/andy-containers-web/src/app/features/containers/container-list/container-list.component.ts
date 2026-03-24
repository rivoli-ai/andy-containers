import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Container } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';

@Component({
  selector: 'app-container-list',
  standalone: true,
  imports: [CommonModule, RouterLink, StatusBadgeComponent],
  template: `
    <div class="space-y-6">
      <!-- Header -->
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Containers</h1>
          <p *ngIf="!loading" class="text-sm text-surface-500 dark:text-surface-400 mt-1">{{ totalCount }} container(s)</p>
        </div>
        <div class="flex items-center gap-2">
          <button (click)="loadContainers()"
            class="inline-flex items-center px-3 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700">
            Refresh
          </button>
          <a routerLink="/containers/create"
            class="inline-flex items-center px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700">
            New Container
          </a>
        </div>
      </div>

      <!-- Loading -->
      <div *ngIf="loading" class="flex items-center justify-center py-12">
        <div class="text-surface-500 dark:text-surface-400">Loading containers...</div>
      </div>

      <!-- Error -->
      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
        <button (click)="loadContainers()" class="mt-2 text-sm text-red-600 dark:text-red-400 underline">Retry</button>
      </div>

      <!-- Table -->
      <div *ngIf="!loading && !error" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 overflow-x-auto">
        <table class="min-w-full divide-y divide-surface-200 dark:divide-surface-700">
          <thead class="bg-surface-50 dark:bg-surface-800">
            <tr>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Name</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Status</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Owner</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Created</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Last Activity</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Host</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Endpoints</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Actions</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
            <tr *ngFor="let c of containers" class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
              <td class="px-4 py-3 whitespace-nowrap">
                <a [routerLink]="['/containers', c.id]" class="font-semibold text-primary-600 dark:text-primary-400 hover:underline">{{ c.name }}</a>
                <div class="text-xs text-surface-400 dark:text-surface-500 font-mono">{{ c.id | slice:0:8 }}</div>
              </td>
              <td class="px-4 py-3 whitespace-nowrap">
                <app-status-badge [status]="c.status"></app-status-badge>
              </td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ c.ownerId }}</td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ c.createdAt | date:'short' }}</td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">
                <span *ngIf="c.lastActivityAt">{{ c.lastActivityAt | date:'short' }}</span>
                <span *ngIf="!c.lastActivityAt" class="text-surface-400">--</span>
              </td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">
                <span *ngIf="c.hostIp" class="font-mono text-xs">{{ c.hostIp }}</span>
                <span *ngIf="!c.hostIp" class="text-surface-400">--</span>
              </td>
              <td class="px-4 py-3 whitespace-nowrap text-sm">
                <a *ngIf="c.ideEndpoint" [href]="c.ideEndpoint" target="_blank" class="text-primary-600 dark:text-primary-400 hover:underline mr-2">IDE</a>
                <a *ngIf="c.vncEndpoint" [href]="c.vncEndpoint" target="_blank" class="text-primary-600 dark:text-primary-400 hover:underline">VNC</a>
                <span *ngIf="!c.ideEndpoint && !c.vncEndpoint" class="text-surface-400">--</span>
              </td>
              <td class="px-4 py-3 whitespace-nowrap text-sm">
                <button *ngIf="c.status === 'Stopped'" (click)="startContainer(c)" title="Start"
                  class="p-1 rounded hover:bg-green-100 dark:hover:bg-green-900 text-green-600 dark:text-green-400 mr-1"
                  [disabled]="c._busy">
                  <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 20 20"><polygon points="6,4 16,10 6,16"/></svg>
                </button>
                <button *ngIf="c.status === 'Running'" (click)="stopContainer(c)" title="Stop"
                  class="p-1 rounded hover:bg-yellow-100 dark:hover:bg-yellow-900 text-yellow-600 dark:text-yellow-400 mr-1"
                  [disabled]="c._busy">
                  <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 20 20"><rect x="5" y="4" width="10" height="12" rx="1"/></svg>
                </button>
                <button (click)="destroyContainer(c)" title="Destroy"
                  class="p-1 rounded hover:bg-red-100 dark:hover:bg-red-900 text-red-600 dark:text-red-400"
                  [disabled]="c._busy || c.status === 'Destroyed' || c.status === 'Destroying'">
                  <svg class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 20 20">
                    <path d="M6 6l8 8M14 6l-8 8"/>
                  </svg>
                </button>
              </td>
            </tr>
            <tr *ngIf="containers.length === 0">
              <td colspan="8" class="px-4 py-8 text-center text-surface-400 dark:text-surface-500">No containers found</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class ContainerListComponent implements OnInit {
  loading = true;
  error = '';
  containers: (Container & { _busy?: boolean })[] = [];
  totalCount = 0;

  constructor(private api: ContainersApiService) {}

  ngOnInit(): void {
    this.loadContainers();
  }

  loadContainers(): void {
    this.loading = true;
    this.error = '';
    this.api.getContainers({ take: '100' }).subscribe({
      next: (res) => {
        this.containers = res.items;
        this.totalCount = res.totalCount;
        this.loading = false;
      },
      error: () => {
        this.error = 'Failed to load containers';
        this.loading = false;
      },
    });
  }

  startContainer(c: Container & { _busy?: boolean }): void {
    c._busy = true;
    this.api.startContainer(c.id).subscribe({
      next: () => this.loadContainers(),
      error: () => { c._busy = false; },
    });
  }

  stopContainer(c: Container & { _busy?: boolean }): void {
    c._busy = true;
    this.api.stopContainer(c.id).subscribe({
      next: () => this.loadContainers(),
      error: () => { c._busy = false; },
    });
  }

  destroyContainer(c: Container & { _busy?: boolean }): void {
    if (!confirm(`Destroy container "${c.name}"?`)) return;
    c._busy = true;
    this.api.destroyContainer(c.id).subscribe({
      next: () => this.loadContainers(),
      error: () => { c._busy = false; },
    });
  }
}
