import { Component, Input, OnInit, OnDestroy, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ContainersApiService } from '../../../core/services/api.service';
import { ContainerStats } from '../../../core/models';

const DEFAULT_POLL_INTERVAL = 5000;
const SETTINGS_KEY = 'andy.statsPollingInterval';

@Component({
  selector: 'app-container-stats-bar',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div *ngIf="stats && isRunning" class="stats-bar" [class]="variant">
      <div class="stat" [title]="'CPU: ' + stats.cpuPercent + '%'">
        <span class="stat-label">CPU</span>
        <span class="stat-value" [class.text-red-400]="stats.cpuPercent > 80"
              [class.text-yellow-400]="stats.cpuPercent > 50 && stats.cpuPercent <= 80"
              [class.text-green-400]="stats.cpuPercent <= 50">{{ stats.cpuPercent }}%</span>
      </div>
      <div class="stat" [title]="formatBytes(stats.memoryUsageBytes) + ' / ' + formatBytes(stats.memoryLimitBytes)">
        <span class="stat-label">RAM</span>
        <span class="stat-value" [class.text-red-400]="stats.memoryPercent > 80"
              [class.text-yellow-400]="stats.memoryPercent > 50 && stats.memoryPercent <= 80"
              [class.text-green-400]="stats.memoryPercent <= 50">{{ formatBytes(stats.memoryUsageBytes) }}</span>
        <span class="stat-pct">({{ stats.memoryPercent }}%)</span>
      </div>
      <div class="stat" *ngIf="stats.diskUsageBytes > 0" [title]="formatBytes(stats.diskUsageBytes) + (stats.diskLimitBytes > 0 ? ' / ' + formatBytes(stats.diskLimitBytes) : '')">
        <span class="stat-label">Disk</span>
        <span class="stat-value">{{ formatBytes(stats.diskUsageBytes) }}</span>
        <span class="stat-pct" *ngIf="stats.diskPercent > 0">({{ stats.diskPercent }}%)</span>
      </div>
    </div>
  `,
  styles: [`
    .stats-bar {
      display: inline-flex; align-items: center; gap: 12px; font-size: inherit;
    }
    .stats-bar.compact { gap: 8px; }
    .stats-bar.terminal-overlay {
      background: rgba(22,27,34,0.85); backdrop-filter: blur(4px);
      padding: 4px 12px; border-radius: 4px; border: 1px solid #30363d;
      font-size: 13px;
    }
    .stat { display: inline-flex; align-items: center; gap: 4px; }
    .stat-label { color: #8b949e; font-weight: 500; }
    .stat-value { font-family: 'JetBrains Mono', monospace; font-weight: 600; }
    .stat-pct { color: #8b949e; font-size: 0.9em; }
    /* Light mode for non-terminal contexts */
    :host-context(.dark) .stat-label, :host-context(.dark) .stat-pct { color: #8b949e; }
    .stats-bar:not(.terminal-overlay) .stat-label { color: #6b7280; }
    .stats-bar:not(.terminal-overlay) .stat-pct { color: #9ca3af; }
  `],
})
export class ContainerStatsBarComponent implements OnInit, OnDestroy, OnChanges {
  @Input() containerId = '';
  @Input() isRunning = false;
  @Input() variant: 'default' | 'compact' | 'terminal-overlay' = 'default';

  stats: ContainerStats | null = null;
  private timer: any = null;

  constructor(private api: ContainersApiService) {}

  ngOnInit(): void {
    this.startPolling();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['isRunning'] || changes['containerId']) {
      this.stopPolling();
      if (this.isRunning && this.containerId) {
        this.startPolling();
      } else {
        this.stats = null;
      }
    }
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  static getPollingInterval(): number {
    try {
      const stored = localStorage.getItem(SETTINGS_KEY);
      if (stored) {
        const val = parseInt(stored, 10);
        if (val >= 1000 && val <= 60000) return val;
      }
    } catch {}
    return DEFAULT_POLL_INTERVAL;
  }

  static setPollingInterval(ms: number): void {
    localStorage.setItem(SETTINGS_KEY, String(ms));
  }

  formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return (bytes / Math.pow(1024, i)).toFixed(i > 1 ? 1 : 0) + ' ' + units[i];
  }

  private startPolling(): void {
    if (!this.isRunning || !this.containerId) return;
    this.fetchStats();
    this.timer = setInterval(() => this.fetchStats(), ContainerStatsBarComponent.getPollingInterval());
  }

  private stopPolling(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private fetchStats(): void {
    this.api.getContainerStats(this.containerId).subscribe({
      next: (s) => { this.stats = s; },
      error: () => { /* silently ignore — container may have stopped */ },
    });
  }
}
