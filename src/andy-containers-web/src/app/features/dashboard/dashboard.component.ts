import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ContainersApiService } from '../../core/services/api.service';
import { Container, Template, Provider, Workspace } from '../../core/models';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';
import { UptimePipe } from '../../shared/pipes/uptime.pipe';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink, StatusBadgeComponent, UptimePipe],
  template: `
    <!-- Loading state -->
    <div *ngIf="loading" class="flex items-center justify-center py-12">
      <div class="text-surface-500 dark:text-surface-400">Loading dashboard...</div>
    </div>

    <!-- Error state -->
    <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4 mb-6">
      <p class="text-red-800 dark:text-red-200">{{ error }}</p>
      <button (click)="loadData()" class="mt-2 text-sm text-red-600 dark:text-red-400 underline">Retry</button>
    </div>

    <div *ngIf="!loading && !error" class="space-y-6">
      <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Dashboard</h1>

      <!-- Stat cards with color-coded icons -->
      <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        <a routerLink="/containers" class="stat-card group">
          <div class="stat-icon bg-blue-100 dark:bg-blue-900/30 text-blue-600 dark:text-blue-400">
            <svg class="w-6 h-6" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18M9 21V9"/></svg>
          </div>
          <div>
            <div class="text-2xl font-bold text-primary-600 dark:text-primary-400">{{ containerCount }}</div>
            <div class="text-sm text-surface-500 dark:text-surface-400">Containers</div>
            <div class="text-xs text-primary-600 dark:text-primary-400 mt-0.5 opacity-0 group-hover:opacity-100 transition-opacity">View all &rarr;</div>
          </div>
        </a>
        <a routerLink="/providers" class="stat-card group">
          <div class="stat-icon bg-green-100 dark:bg-green-900/30 text-green-600 dark:text-green-400">
            <svg class="w-6 h-6" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M3 15a4 4 0 004 4h9a5 5 0 10-.1-9.999 5.002 5.002 0 10-9.78 2.096A4.001 4.001 0 003 15z"/></svg>
          </div>
          <div>
            <div class="text-2xl font-bold text-green-600 dark:text-green-400">{{ providerCount }}</div>
            <div class="text-sm text-surface-500 dark:text-surface-400">Providers</div>
            <div class="text-xs text-green-600 dark:text-green-400 mt-0.5 opacity-0 group-hover:opacity-100 transition-opacity">View all &rarr;</div>
          </div>
        </a>
        <a routerLink="/templates" class="stat-card group">
          <div class="stat-icon bg-purple-100 dark:bg-purple-900/30 text-purple-600 dark:text-purple-400">
            <svg class="w-6 h-6" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M4 5a1 1 0 011-1h14a1 1 0 011 1v2a1 1 0 01-1 1H5a1 1 0 01-1-1V5zM4 13a1 1 0 011-1h6a1 1 0 011 1v6a1 1 0 01-1 1H5a1 1 0 01-1-1v-6zM16 13a1 1 0 011-1h2a1 1 0 011 1v6a1 1 0 01-1 1h-2a1 1 0 01-1-1v-6z"/></svg>
          </div>
          <div>
            <div class="text-2xl font-bold text-purple-600 dark:text-purple-400">{{ templateCount }}</div>
            <div class="text-sm text-surface-500 dark:text-surface-400">Templates</div>
            <div class="text-xs text-purple-600 dark:text-purple-400 mt-0.5 opacity-0 group-hover:opacity-100 transition-opacity">View all &rarr;</div>
          </div>
        </a>
        <a routerLink="/workspaces" class="stat-card group">
          <div class="stat-icon bg-orange-100 dark:bg-orange-900/30 text-orange-600 dark:text-orange-400">
            <svg class="w-6 h-6" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10"/></svg>
          </div>
          <div>
            <div class="text-2xl font-bold text-orange-600 dark:text-orange-400">{{ workspaceCount }}</div>
            <div class="text-sm text-surface-500 dark:text-surface-400">Workspaces</div>
            <div class="text-xs text-orange-600 dark:text-orange-400 mt-0.5 opacity-0 group-hover:opacity-100 transition-opacity">View all &rarr;</div>
          </div>
        </a>
      </div>

      <!-- Recent containers -->
      <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800">
        <div class="flex items-center justify-between px-5 py-4 border-b border-surface-200 dark:border-surface-700">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">Recent Containers</h2>
          <a routerLink="/containers" class="text-sm text-primary-600 dark:text-primary-400 hover:underline">View all &rarr;</a>
        </div>
        <div class="overflow-x-auto">
          <table class="min-w-full divide-y divide-surface-200 dark:divide-surface-700">
            <thead class="bg-surface-50 dark:bg-surface-800">
              <tr>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Name</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Status</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Uptime</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Owner</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Created</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Host</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Endpoints</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Actions</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
              <tr *ngFor="let c of recentContainers" class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
                <td class="px-4 py-3 whitespace-nowrap text-sm">
                  <a [routerLink]="['/containers', c.id]" class="font-semibold text-primary-600 dark:text-primary-400 hover:underline">{{ c.name }}</a>
                  <div class="text-xs text-surface-400 dark:text-surface-500 font-mono">{{ c.id | slice:0:8 }}</div>
                </td>
                <td class="px-4 py-3 whitespace-nowrap">
                  <app-status-badge [status]="c.status"></app-status-badge>
                </td>
                <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">
                  <span *ngIf="c.status === 'Running' && c.startedAt">{{ c.startedAt | uptime }}</span>
                  <span *ngIf="c.status !== 'Running' || !c.startedAt" class="text-surface-400">--</span>
                </td>
                <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ c.ownerId }}</td>
                <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ c.createdAt | date:'short' }}</td>
                <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">
                  <span *ngIf="c.hostIp" class="font-mono">{{ c.hostIp }}</span>
                  <span *ngIf="!c.hostIp" class="text-surface-400">--</span>
                </td>
                <td class="px-4 py-3 whitespace-nowrap text-sm">
                  <div class="flex gap-2">
                    <a *ngIf="c.ideEndpoint" [href]="c.ideEndpoint" target="_blank"
                      class="endpoint-link">
                      <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4"/></svg>
                      IDE
                    </a>
                    <a *ngIf="c.vncEndpoint" [href]="c.vncEndpoint" target="_blank"
                      class="endpoint-link">
                      <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8m-4-4v4"/></svg>
                      VNC
                    </a>
                  </div>
                  <span *ngIf="!c.ideEndpoint && !c.vncEndpoint" class="text-surface-400">--</span>
                </td>
                <td class="px-4 py-3 whitespace-nowrap text-sm">
                  <div class="flex items-center gap-1">
                    <button *ngIf="c.status === 'Stopped'" (click)="startContainer(c)" title="Start"
                      class="p-1.5 rounded-lg hover:bg-green-100 dark:hover:bg-green-900/30 text-green-600 dark:text-green-400 transition-colors"
                      [disabled]="c._busy">
                      <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 20 20"><polygon points="6,4 16,10 6,16"/></svg>
                    </button>
                    <button *ngIf="c.status === 'Running'" (click)="stopContainer(c)" title="Stop"
                      class="p-1.5 rounded-lg hover:bg-yellow-100 dark:hover:bg-yellow-900/30 text-yellow-600 dark:text-yellow-400 transition-colors"
                      [disabled]="c._busy">
                      <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 20 20"><rect x="5" y="4" width="10" height="12" rx="1"/></svg>
                    </button>
                    <button (click)="destroyContainer(c)" title="Destroy"
                      class="p-1.5 rounded-lg hover:bg-red-100 dark:hover:bg-red-900/30 text-red-600 dark:text-red-400 transition-colors"
                      [disabled]="c._busy || c.status === 'Destroyed' || c.status === 'Destroying'">
                      <svg class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 20 20">
                        <path d="M6 6l8 8M14 6l-8 8"/>
                      </svg>
                    </button>
                  </div>
                </td>
              </tr>
              <tr *ngIf="recentContainers.length === 0">
                <td colspan="6" class="px-4 py-8 text-center text-surface-400 dark:text-surface-500">No containers yet</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
  `,
})
export class DashboardComponent implements OnInit {
  loading = true;
  error = '';
  containerCount = 0;
  providerCount = 0;
  templateCount = 0;
  workspaceCount = 0;
  recentContainers: (Container & { _busy?: boolean })[] = [];

  constructor(private api: ContainersApiService) {}

  ngOnInit(): void {
    this.loadData();
  }

  loadData(): void {
    this.loading = true;
    this.error = '';
    let completed = 0;
    const total = 4;
    const done = () => {
      completed++;
      if (completed >= total) this.loading = false;
    };

    this.api.getContainers({ take: '5' }).subscribe({
      next: (res) => {
        this.containerCount = res.totalCount;
        this.recentContainers = res.items;
        done();
      },
      error: () => { this.error = 'Failed to load containers'; done(); },
    });

    this.api.getProviders().subscribe({
      next: (res) => { this.providerCount = res.length; done(); },
      error: () => { this.error = 'Failed to load providers'; done(); },
    });

    this.api.getTemplates({ take: '1' }).subscribe({
      next: (res) => { this.templateCount = res.totalCount; done(); },
      error: () => { this.error = 'Failed to load templates'; done(); },
    });

    this.api.getWorkspaces({ take: '1' }).subscribe({
      next: (res) => { this.workspaceCount = res.totalCount; done(); },
      error: () => { this.error = 'Failed to load workspaces'; done(); },
    });
  }

  startContainer(c: Container & { _busy?: boolean }): void {
    c._busy = true;
    this.api.startContainer(c.id).subscribe({
      next: () => this.loadData(),
      error: () => { c._busy = false; },
    });
  }

  stopContainer(c: Container & { _busy?: boolean }): void {
    c._busy = true;
    this.api.stopContainer(c.id).subscribe({
      next: () => this.loadData(),
      error: () => { c._busy = false; },
    });
  }

  destroyContainer(c: Container & { _busy?: boolean }): void {
    if (!confirm(`Destroy container "${c.name}"?`)) return;
    c._busy = true;
    this.api.destroyContainer(c.id).subscribe({
      next: () => this.loadData(),
      error: () => { c._busy = false; },
    });
  }
}
