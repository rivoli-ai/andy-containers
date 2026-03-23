import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ContainersApiService } from '../../../core/services/api.service';
import { Provider } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';

@Component({
  selector: 'app-provider-list',
  standalone: true,
  imports: [CommonModule, StatusBadgeComponent],
  template: `
    <div class="space-y-6">
      <!-- Header -->
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Providers</h1>
          <p *ngIf="!loading" class="text-xs text-surface-400 dark:text-surface-500 mt-1">Auto-refreshes every 30s</p>
        </div>
        <button (click)="loadProviders()"
          class="inline-flex items-center px-3 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700">
          Refresh
        </button>
      </div>

      <!-- Loading -->
      <div *ngIf="loading" class="flex items-center justify-center py-12">
        <div class="text-surface-500 dark:text-surface-400">Loading providers...</div>
      </div>

      <!-- Error -->
      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
        <button (click)="loadProviders()" class="mt-2 text-sm text-red-600 dark:text-red-400 underline">Retry</button>
      </div>

      <!-- Table -->
      <div *ngIf="!loading && !error" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 overflow-x-auto">
        <table class="min-w-full divide-y divide-surface-200 dark:divide-surface-700">
          <thead class="bg-surface-50 dark:bg-surface-800">
            <tr>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Name</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Code</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Type</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Region</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Status</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Enabled</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Last Checked</th>
              <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Actions</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
            <tr *ngFor="let p of providers" class="hover:bg-surface-50 dark:hover:bg-surface-700/50"
              [class.opacity-60]="!p.isEnabled">
              <td class="px-4 py-3 whitespace-nowrap text-sm font-medium text-surface-900 dark:text-surface-100">{{ p.name }}</td>
              <td class="px-4 py-3 whitespace-nowrap text-sm font-mono text-surface-600 dark:text-surface-300">{{ p.code }}</td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ p.type }}</td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ p.region || '--' }}</td>
              <td class="px-4 py-3 whitespace-nowrap">
                <app-status-badge [status]="p.healthStatus"></app-status-badge>
              </td>
              <td class="px-4 py-3 whitespace-nowrap text-sm">
                <span *ngIf="p.isEnabled" class="text-green-600 dark:text-green-400 font-medium">Yes</span>
                <span *ngIf="!p.isEnabled" class="text-surface-400">No</span>
              </td>
              <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">
                <span *ngIf="p.lastHealthCheck">{{ p.lastHealthCheck | date:'medium' }}</span>
                <span *ngIf="!p.lastHealthCheck" class="text-surface-400 italic">Never</span>
              </td>
              <td class="px-4 py-3 whitespace-nowrap text-sm">
                <button (click)="checkHealth(p)" [disabled]="p._checking"
                  class="px-3 py-1 text-xs font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700 disabled:opacity-50 transition-colors">
                  {{ p._checking ? 'Checking...' : 'Check Health' }}
                </button>
              </td>
            </tr>
            <tr *ngIf="providers.length === 0">
              <td colspan="8" class="px-4 py-8 text-center text-surface-400 dark:text-surface-500">No providers found</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class ProviderListComponent implements OnInit, OnDestroy {
  loading = true;
  error = '';
  providers: (Provider & { _checking?: boolean })[] = [];
  private refreshTimer: any = null;

  constructor(private api: ContainersApiService) {}

  ngOnInit(): void {
    this.loadProviders();
    // Auto-refresh every 30 seconds to reflect background health check updates
    this.refreshTimer = setInterval(() => this.silentRefresh(), 30000);
  }

  ngOnDestroy(): void {
    if (this.refreshTimer) {
      clearInterval(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  loadProviders(): void {
    this.loading = true;
    this.error = '';
    this.api.getProviders().subscribe({
      next: (res) => {
        this.providers = res;
        this.loading = false;
      },
      error: () => {
        this.error = 'Failed to load providers';
        this.loading = false;
      },
    });
  }

  /** Refresh without showing loading spinner */
  private silentRefresh(): void {
    this.api.getProviders().subscribe({
      next: (res) => {
        // Preserve _checking state
        this.providers = res.map((p) => {
          const existing = this.providers.find((e) => e.id === p.id);
          return { ...p, _checking: existing?._checking };
        });
      },
    });
  }

  checkHealth(p: Provider & { _checking?: boolean }): void {
    p._checking = true;
    this.api.checkProviderHealth(p.id).subscribe({
      next: (result) => {
        p.healthStatus = result.status;
        p._checking = false;
        this.silentRefresh();
      },
      error: () => {
        p._checking = false;
      },
    });
  }
}
