import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Container, ContainerEvent, ContainerGitRepository, ConnectionInfo, ExecResult } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';

@Component({
  selector: 'app-container-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, StatusBadgeComponent],
  template: `
    <!-- Loading -->
    <div *ngIf="loading" class="flex items-center justify-center py-12">
      <div class="text-surface-500 dark:text-surface-400">Loading container...</div>
    </div>

    <!-- Error -->
    <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
      <p class="text-red-800 dark:text-red-200">{{ error }}</p>
      <button (click)="loadContainer()" class="mt-2 text-sm text-red-600 dark:text-red-400 underline">Retry</button>
    </div>

    <div *ngIf="!loading && container" class="space-y-6">
      <!-- Header -->
      <div class="flex items-center gap-3">
        <a routerLink="/containers" class="text-surface-400 hover:text-surface-600 dark:hover:text-surface-300">
          <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
        </a>
        <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">{{ container.name }}</h1>
        <app-status-badge [status]="container.status"></app-status-badge>
      </div>

      <!-- Creating/Pending alert -->
      <div *ngIf="container.status === 'Creating' || container.status === 'Pending'"
        class="flex items-center gap-3 rounded-lg p-4"
        [class]="isStuck
          ? 'bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800'
          : 'bg-cyan-50 dark:bg-cyan-900/20 border border-cyan-200 dark:border-cyan-800'">
        <div *ngIf="!isStuck" class="animate-spin w-5 h-5 border-2 border-cyan-400 border-t-transparent rounded-full"></div>
        <svg *ngIf="isStuck" class="w-5 h-5 text-red-500" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"/></svg>
        <div>
          <p *ngIf="!isStuck" class="text-sm font-medium text-cyan-800 dark:text-cyan-200">
            Container is being provisioned... <span class="text-xs font-normal text-cyan-600 dark:text-cyan-400">(auto-refreshing)</span>
          </p>
          <p *ngIf="isStuck" class="text-sm font-medium text-red-800 dark:text-red-200">
            Container appears stuck in {{ container.status }} state. Check provider logs.
          </p>
        </div>
      </div>

      <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <!-- Overview Card -->
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100 mb-4">Overview</h2>
          <dl class="space-y-3">
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Name</dt>
              <dd class="text-sm font-medium text-surface-900 dark:text-surface-100">{{ container.name }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Status</dt>
              <dd><app-status-badge [status]="container.status"></app-status-badge></dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">ID</dt>
              <dd class="text-sm font-mono text-surface-600 dark:text-surface-300">{{ container.id }}</dd>
            </div>
            <div *ngIf="container.externalId" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">External ID</dt>
              <dd class="text-sm font-mono text-surface-600 dark:text-surface-300">{{ container.externalId }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Owner</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100">{{ container.ownerId }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Template</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100">{{ container.templateId | slice:0:8 }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Provider</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100">{{ container.providerId | slice:0:8 }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Created</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ container.createdAt | date:'medium' }}</dd>
            </div>
            <div *ngIf="container.creationSource && container.creationSource !== 'Unknown'" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Source</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ container.creationSource }}</dd>
            </div>
            <div *ngIf="container.startedAt" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Started</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ container.startedAt | date:'medium' }}</dd>
            </div>
            <div *ngIf="container.stoppedAt" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Stopped</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ container.stoppedAt | date:'medium' }}</dd>
            </div>
          </dl>
        </div>

        <!-- Connect Card (only when Running) -->
        <div *ngIf="container.status === 'Running'" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100 mb-4">Connect</h2>
          <div class="space-y-2">
            <!-- Terminal link -->
            <div class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">Terminal</span>
              <a [routerLink]="['/containers', container.id, 'terminal']"
                class="flex-1 text-sm font-medium text-primary-600 dark:text-primary-400 hover:underline">
                Open Web Terminal
              </a>
            </div>

            <!-- CLI exec -->
            <div class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">CLI</span>
              <code class="flex-1 text-xs font-mono text-surface-700 dark:text-surface-300 truncate">andy exec {{ container.id | slice:0:8 }} -- /bin/sh</code>
              <button (click)="copyWithFeedback('andy exec ' + container.id + ' -- /bin/sh', 'cli')" title="Copy"
                class="copy-btn" [class.copied]="copiedField === 'cli'">
                <svg *ngIf="copiedField !== 'cli'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>
                <svg *ngIf="copiedField === 'cli'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
              </button>
            </div>

            <!-- SSH -->
            <div *ngIf="connectionInfo?.sshEndpoint" class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">SSH</span>
              <code class="flex-1 text-xs font-mono text-surface-700 dark:text-surface-300 truncate">{{ connectionInfo?.sshEndpoint }}</code>
              <button (click)="copyWithFeedback(connectionInfo?.sshEndpoint, 'ssh')" title="Copy"
                class="copy-btn" [class.copied]="copiedField === 'ssh'">
                <svg *ngIf="copiedField !== 'ssh'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>
                <svg *ngIf="copiedField === 'ssh'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
              </button>
            </div>

            <!-- IDE -->
            <div *ngIf="connectionInfo?.ideEndpoint || container.ideEndpoint" class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">IDE</span>
              <a [href]="connectionInfo?.ideEndpoint || container.ideEndpoint" target="_blank"
                class="flex-1 text-sm text-primary-600 dark:text-primary-400 hover:underline truncate">
                {{ connectionInfo?.ideEndpoint || container.ideEndpoint }}
              </a>
            </div>

            <!-- VNC -->
            <div *ngIf="connectionInfo?.vncEndpoint || container.vncEndpoint" class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">VNC</span>
              <a [href]="connectionInfo?.vncEndpoint || container.vncEndpoint" target="_blank"
                class="flex-1 text-sm text-primary-600 dark:text-primary-400 hover:underline truncate">
                {{ connectionInfo?.vncEndpoint || container.vncEndpoint }}
              </a>
            </div>

            <!-- IP Address -->
            <div *ngIf="connectionInfo?.ipAddress" class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">IP</span>
              <code class="flex-1 text-xs font-mono text-surface-700 dark:text-surface-300">{{ connectionInfo?.ipAddress }}</code>
              <button (click)="copyWithFeedback(connectionInfo?.ipAddress, 'ip')" title="Copy"
                class="copy-btn" [class.copied]="copiedField === 'ip'">
                <svg *ngIf="copiedField !== 'ip'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>
                <svg *ngIf="copiedField === 'ip'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
              </button>
            </div>

            <!-- Port Mappings -->
            <div *ngIf="connectionInfo?.portMappings" class="mt-3">
              <h4 class="text-xs font-medium text-surface-500 dark:text-surface-400 uppercase mb-2">Port Mappings</h4>
              <div *ngFor="let entry of portMappingEntries" class="connect-row">
                <span class="connect-dot bg-gray-400"></span>
                <span class="connect-label">{{ entry[0] }}</span>
                <code class="flex-1 text-xs font-mono text-surface-700 dark:text-surface-300">{{ entry[1] }}</code>
              </div>
            </div>
          </div>
        </div>

        <!-- Quick Exec Card (only when Running) -->
        <div *ngIf="container.status === 'Running'" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100 mb-4">Quick Exec</h2>
          <div class="flex gap-2 mb-3">
            <input type="text" [(ngModel)]="execCommand" placeholder="Enter command, e.g. ls /"
              (keydown.enter)="runExec()"
              class="flex-1 rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100 font-mono focus:ring-2 focus:ring-primary-500" />
            <button (click)="runExec()" [disabled]="execRunning || !execCommand"
              class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700 disabled:opacity-50">
              {{ execRunning ? 'Running...' : 'Run' }}
            </button>
          </div>
          <div *ngIf="execResult" class="space-y-2">
            <div class="text-xs font-semibold"
              [class.text-green-600]="execResult.exitCode === 0"
              [class.dark:text-green-400]="execResult.exitCode === 0"
              [class.text-orange-600]="execResult.exitCode !== 0"
              [class.dark:text-orange-400]="execResult.exitCode !== 0">
              Exit code: {{ execResult.exitCode }}
            </div>
            <pre *ngIf="execResult.stdOut" class="text-xs bg-surface-50 dark:bg-surface-900 rounded-lg p-3 text-surface-800 dark:text-surface-200 overflow-x-auto whitespace-pre-wrap max-h-48 font-mono">{{ execResult.stdOut }}</pre>
            <pre *ngIf="execResult.stdErr" class="text-xs bg-red-50 dark:bg-red-900/20 rounded-lg p-3 text-red-700 dark:text-red-300 overflow-x-auto whitespace-pre-wrap max-h-48 font-mono">{{ execResult.stdErr }}</pre>
          </div>
        </div>

        <!-- Actions Card -->
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100 mb-4">Actions</h2>
          <div class="flex flex-wrap gap-2">
            <button *ngIf="container.status === 'Stopped' || container.status === 'Failed'" (click)="startContainer()"
              [disabled]="actionBusy"
              class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-green-600 hover:bg-green-700 disabled:opacity-50 transition-colors">
              Start
            </button>
            <button *ngIf="container.status === 'Running'" (click)="stopContainer()"
              [disabled]="actionBusy"
              class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-yellow-600 hover:bg-yellow-700 disabled:opacity-50 transition-colors">
              Stop
            </button>
            <button (click)="destroyContainer()"
              [disabled]="actionBusy || container.status === 'Destroyed' || container.status === 'Destroying'"
              class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-red-600 hover:bg-red-700 disabled:opacity-50 transition-colors">
              Destroy
            </button>
          </div>
        </div>
      </div>

      <!-- Repositories Card -->
      <div *ngIf="repositories.length > 0" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800">
        <div class="px-5 py-4 border-b border-surface-200 dark:border-surface-700">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">Repositories</h2>
        </div>
        <div class="overflow-x-auto">
          <table class="min-w-full divide-y divide-surface-200 dark:divide-surface-700">
            <thead class="bg-surface-50 dark:bg-surface-800">
              <tr>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">URL</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Branch</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Path</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Status</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
              <tr *ngFor="let repo of repositories" class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
                <td class="px-4 py-3 text-sm font-mono text-surface-700 dark:text-surface-300 truncate max-w-xs">{{ repo.url }}</td>
                <td class="px-4 py-3 text-sm text-surface-600 dark:text-surface-300">{{ repo.branch || 'default' }}</td>
                <td class="px-4 py-3 text-sm font-mono text-surface-600 dark:text-surface-300">{{ repo.targetPath }}</td>
                <td class="px-4 py-3 whitespace-nowrap">
                  <span [ngClass]="getCloneStatusClasses(repo.cloneStatus)">
                    <span *ngIf="repo.cloneStatus === 'Cloning'" class="inline-block w-3 h-3 mr-1 border-2 border-cyan-400 border-t-transparent rounded-full animate-spin"></span>
                    {{ repo.cloneStatus }}
                  </span>
                  <span *ngIf="repo.cloneError" class="block text-xs text-red-500 dark:text-red-400 mt-1 truncate max-w-xs" [title]="repo.cloneError">{{ repo.cloneError }}</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Events Card -->
      <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800">
        <div class="flex items-center justify-between px-5 py-4 border-b border-surface-200 dark:border-surface-700">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">Events</h2>
          <button (click)="loadEvents()" class="text-xs text-primary-600 dark:text-primary-400 hover:underline">Refresh</button>
        </div>
        <div class="overflow-x-auto">
          <table class="min-w-full divide-y divide-surface-200 dark:divide-surface-700">
            <thead class="bg-surface-50 dark:bg-surface-800">
              <tr>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Timestamp</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Event</th>
                <th class="px-4 py-3 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Details</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
              <tr *ngFor="let event of events" class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
                <td class="px-4 py-3 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ event.timestamp | date:'medium' }}</td>
                <td class="px-4 py-3 whitespace-nowrap">
                  <span [ngClass]="getEventBadgeClasses(event.eventType)">{{ event.eventType }}</span>
                </td>
                <td class="px-4 py-3 text-sm">
                  <code *ngIf="event.details" class="text-xs bg-surface-100 dark:bg-surface-900 rounded px-2 py-1 text-surface-600 dark:text-surface-400 font-mono">{{ event.details }}</code>
                  <span *ngIf="!event.details" class="text-surface-400">--</span>
                </td>
              </tr>
              <tr *ngIf="events.length === 0">
                <td colspan="3" class="px-4 py-6 text-center text-surface-400 dark:text-surface-500">No events recorded</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
  `,
})
export class ContainerDetailComponent implements OnInit, OnDestroy {
  loading = true;
  error = '';
  container: Container | null = null;
  events: ContainerEvent[] = [];
  repositories: ContainerGitRepository[] = [];
  connectionInfo: ConnectionInfo | null = null;
  execCommand = '';
  execResult: ExecResult | null = null;
  execRunning = false;
  actionBusy = false;
  copiedField = '';
  isStuck = false;

  private pollTimer: any = null;
  private eventPollTimer: any = null;
  private containerId = '';
  private copyTimeout: any = null;
  private createdTime: Date | null = null;

  constructor(
    private api: ContainersApiService,
    private route: ActivatedRoute,
    private router: Router,
  ) {}

  ngOnInit(): void {
    this.containerId = this.route.snapshot.paramMap.get('id')!;
    this.loadContainer();
  }

  ngOnDestroy(): void {
    this.clearPoll();
    this.clearEventPoll();
    if (this.copyTimeout) clearTimeout(this.copyTimeout);
  }

  get portMappingEntries(): [string, string][] {
    if (!this.connectionInfo?.portMappings) return [];
    return Object.entries(this.connectionInfo.portMappings);
  }

  loadContainer(): void {
    this.loading = !this.container;
    this.error = '';

    this.api.getContainer(this.containerId).subscribe({
      next: (c) => {
        this.container = c;
        this.loading = false;

        // Check if stuck (>2 min in Creating/Pending)
        if (c.status === 'Creating' || c.status === 'Pending') {
          const created = new Date(c.createdAt);
          this.isStuck = (Date.now() - created.getTime()) > 120000;
        } else {
          this.isStuck = false;
        }

        this.loadEvents();
        this.loadRepositories();

        if (c.status === 'Running') {
          this.loadConnectionInfo();
        }

        if (c.status === 'Pending' || c.status === 'Creating') {
          this.startPoll();
        } else {
          this.clearPoll();
        }
      },
      error: () => {
        this.error = 'Failed to load container';
        this.loading = false;
      },
    });
  }

  loadEvents(): void {
    this.api.getContainerEvents(this.containerId).subscribe({
      next: (events) => { this.events = events; },
    });
  }

  private loadConnectionInfo(): void {
    this.api.getConnectionInfo(this.containerId).subscribe({
      next: (info) => { this.connectionInfo = info; },
    });
  }

  private startPoll(): void {
    this.clearPoll();
    this.pollTimer = setInterval(() => this.loadContainer(), 3000);
  }

  private clearPoll(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  runExec(): void {
    if (!this.execCommand || this.execRunning) return;
    this.execRunning = true;
    this.execResult = null;
    this.api.execCommand(this.containerId, this.execCommand).subscribe({
      next: (result) => {
        this.execResult = result;
        this.execRunning = false;
      },
      error: () => {
        this.execResult = { exitCode: -1, stdErr: 'Failed to execute command' };
        this.execRunning = false;
      },
    });
  }

  startContainer(): void {
    this.actionBusy = true;
    this.api.startContainer(this.containerId).subscribe({
      next: () => { this.actionBusy = false; this.loadContainer(); },
      error: () => { this.actionBusy = false; },
    });
  }

  stopContainer(): void {
    this.actionBusy = true;
    this.api.stopContainer(this.containerId).subscribe({
      next: () => { this.actionBusy = false; this.loadContainer(); },
      error: () => { this.actionBusy = false; },
    });
  }

  destroyContainer(): void {
    if (!confirm(`Destroy container "${this.container?.name}"?`)) return;
    this.actionBusy = true;
    this.api.destroyContainer(this.containerId).subscribe({
      next: () => { this.router.navigate(['/containers']); },
      error: () => { this.actionBusy = false; },
    });
  }

  copyWithFeedback(text: string | null | undefined, field: string): void {
    if (!text) return;
    navigator.clipboard.writeText(text);
    this.copiedField = field;
    if (this.copyTimeout) clearTimeout(this.copyTimeout);
    this.copyTimeout = setTimeout(() => { this.copiedField = ''; }, 2000);
  }

  private loadRepositories(): void {
    this.api.getContainerRepositories(this.containerId).subscribe({
      next: (repos) => {
        this.repositories = repos;
        // Auto-refresh events while any repo is cloning or pending
        const hasActiveClone = repos.some(r => r.cloneStatus === 'Pending' || r.cloneStatus === 'Cloning');
        if (hasActiveClone) {
          this.startEventPoll();
        } else {
          this.clearEventPoll();
        }
      },
    });
  }

  private startEventPoll(): void {
    if (this.eventPollTimer) return; // Already polling
    this.eventPollTimer = setInterval(() => {
      this.loadEvents();
      this.loadRepositories();
    }, 5000);
  }

  private clearEventPoll(): void {
    if (this.eventPollTimer) {
      clearInterval(this.eventPollTimer);
      this.eventPollTimer = null;
    }
  }

  getCloneStatusClasses(status: string): string[] {
    const base = ['inline-flex', 'items-center', 'px-2', 'py-0.5', 'rounded-full', 'text-xs', 'font-semibold'];
    switch (status) {
      case 'Cloned':
        return [...base, 'bg-green-100', 'text-green-800', 'dark:bg-green-900/30', 'dark:text-green-400'];
      case 'Cloning':
        return [...base, 'bg-cyan-100', 'text-cyan-800', 'dark:bg-cyan-900/30', 'dark:text-cyan-400'];
      case 'Pending':
        return [...base, 'bg-gray-100', 'text-gray-600', 'dark:bg-gray-700', 'dark:text-gray-400'];
      case 'Failed':
        return [...base, 'bg-red-100', 'text-red-800', 'dark:bg-red-900/30', 'dark:text-red-400'];
      case 'Pulling':
        return [...base, 'bg-yellow-100', 'text-yellow-800', 'dark:bg-yellow-900/30', 'dark:text-yellow-300'];
      default:
        return [...base, 'bg-gray-100', 'text-gray-600', 'dark:bg-gray-700', 'dark:text-gray-400'];
    }
  }

  getEventBadgeClasses(eventType: string): string[] {
    const base = ['inline-flex', 'items-center', 'px-2', 'py-0.5', 'rounded-full', 'text-xs', 'font-semibold'];
    const lower = eventType?.toLowerCase() || '';
    if (lower.includes('failed') || lower.includes('error')) {
      return [...base, 'bg-red-100', 'text-red-800', 'dark:bg-red-900/30', 'dark:text-red-400'];
    }
    if (lower.includes('started') || lower.includes('created') || lower.includes('cloned') || lower.includes('pulled')) {
      return [...base, 'bg-green-100', 'text-green-800', 'dark:bg-green-900/30', 'dark:text-green-400'];
    }
    if (lower.includes('stopped')) {
      return [...base, 'bg-yellow-100', 'text-yellow-800', 'dark:bg-yellow-900/30', 'dark:text-yellow-300'];
    }
    if (lower.includes('destroyed')) {
      return [...base, 'bg-gray-100', 'text-gray-500', 'dark:bg-gray-800', 'dark:text-gray-500'];
    }
    if (lower.includes('pending') || lower.includes('creating')) {
      return [...base, 'bg-cyan-100', 'text-cyan-800', 'dark:bg-cyan-900/30', 'dark:text-cyan-400'];
    }
    return [...base, 'bg-gray-100', 'text-gray-600', 'dark:bg-gray-700', 'dark:text-gray-400'];
  }
}
