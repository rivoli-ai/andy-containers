import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { ContainersApiService } from '../../../core/services/api.service';
import { Container, ContainerStats, ContainerEvent, ContainerGitRepository, GitCloneMetadata, ConnectionInfo, ExecResult, CODE_ASSISTANT_TOOLS } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';
import { ContainerStatsBarComponent } from '../../../shared/components/container-stats-bar/container-stats-bar.component';
import { UptimePipe } from '../../../shared/pipes/uptime.pipe';
import { ContainerThumbnailComponent } from '../../../shared/components/container-thumbnail/container-thumbnail.component';

@Component({
  selector: 'app-container-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, StatusBadgeComponent, ContainerStatsBarComponent, UptimePipe, ContainerThumbnailComponent],
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
        <span *ngIf="container.status === 'Running' && container.startedAt" class="text-sm text-green-600 dark:text-green-400 font-medium">{{ container.startedAt | uptime }}</span>
        <app-container-stats-bar [containerId]="container.id" [isRunning]="container.status === 'Running'"></app-container-stats-bar>
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
          <dl class="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-2 items-baseline">
            <dt class="text-sm text-surface-500 dark:text-surface-400">Name</dt>
            <dd class="text-sm font-medium text-surface-900 dark:text-surface-100 break-words">{{ container.name }}</dd>

            <dt class="text-sm text-surface-500 dark:text-surface-400">Status</dt>
            <dd><app-status-badge [status]="container.status"></app-status-badge></dd>

            <dt class="text-sm text-surface-500 dark:text-surface-400">ID</dt>
            <dd class="text-sm font-mono text-surface-600 dark:text-surface-300 break-all">{{ container.id }}</dd>

            <ng-container *ngIf="container.externalId">
              <dt class="text-sm text-surface-500 dark:text-surface-400">External ID</dt>
              <dd class="text-sm font-mono text-surface-600 dark:text-surface-300">{{ container.externalId | slice:0:12 }}</dd>
            </ng-container>

            <dt class="text-sm text-surface-500 dark:text-surface-400">Owner</dt>
            <dd class="text-sm text-surface-900 dark:text-surface-100 break-words">{{ container.ownerId }}</dd>

            <dt class="text-sm text-surface-500 dark:text-surface-400">Template</dt>
            <dd class="text-sm text-surface-900 dark:text-surface-100 break-words">{{ container.template?.name || container.templateId | slice:0:8 }} <span *ngIf="container.template?.code" class="text-xs font-mono text-surface-400">({{ container.template?.code }})</span></dd>

            <ng-container *ngIf="container.template?.baseImage">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Base Image</dt>
              <dd class="text-sm font-mono text-surface-600 dark:text-surface-300 break-all">{{ container.template?.baseImage }}</dd>
            </ng-container>

            <dt class="text-sm text-surface-500 dark:text-surface-400">Provider</dt>
            <dd class="text-sm text-surface-900 dark:text-surface-100 break-words">{{ container.provider?.name || container.providerId | slice:0:8 }}</dd>

            <dt class="text-sm text-surface-500 dark:text-surface-400">Created</dt>
            <dd class="text-sm text-surface-600 dark:text-surface-300">{{ container.createdAt | date:'medium' }}</dd>

            <ng-container *ngIf="container.creationSource && container.creationSource !== 'Unknown'">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Source</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ container.creationSource }}</dd>
            </ng-container>

            <ng-container *ngIf="codeAssistantLabel">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Code Assistant</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ codeAssistantLabel }}</dd>
            </ng-container>

            <ng-container *ngIf="container.startedAt">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Started</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ container.startedAt | date:'medium' }}</dd>
            </ng-container>

            <ng-container *ngIf="container.status === 'Running' && container.startedAt">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Uptime</dt>
              <dd class="text-sm font-medium text-green-600 dark:text-green-400">{{ container.startedAt | uptime }}</dd>
            </ng-container>

            <ng-container *ngIf="container.stoppedAt">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Stopped</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ container.stoppedAt | date:'medium' }}</dd>
            </ng-container>
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

            <!-- Docker exec -->
            <div *ngIf="container.externalId" class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">Docker</span>
              <code class="flex-1 text-sm font-mono text-surface-700 dark:text-surface-300 truncate">docker exec -it {{ container.externalId | slice:0:12 }} /bin/sh</code>
              <button (click)="copyWithFeedback('docker exec -it ' + container.externalId + ' /bin/sh', 'docker')" title="Copy"
                class="copy-btn" [class.copied]="copiedField === 'docker'">
                <svg *ngIf="copiedField !== 'docker'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>
                <svg *ngIf="copiedField === 'docker'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
              </button>
            </div>

            <!-- SSH -->
            <div *ngIf="connectionInfo?.sshEndpoint" class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">SSH</span>
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2">
                  <code class="text-sm font-mono text-surface-700 dark:text-surface-300 truncate">{{ connectionInfo?.sshEndpoint }}</code>
                  <button (click)="copyWithFeedback(connectionInfo?.sshEndpoint, 'ssh')" title="Copy command"
                    class="copy-btn" [class.copied]="copiedField === 'ssh'">
                    <svg *ngIf="copiedField !== 'ssh'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>
                    <svg *ngIf="copiedField === 'ssh'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
                  </button>
                  <a [href]="getSshUrl()" title="Open in native terminal app" class="copy-btn">
                    <svg class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M18 13v6a2 2 0 01-2 2H5a2 2 0 01-2-2V8a2 2 0 012-2h6"/><polyline points="15 3 21 3 21 9"/><line x1="10" y1="14" x2="21" y2="3"/></svg>
                  </a>
                </div>
                <p class="text-xs text-surface-400 mt-0.5">Password: <code class="font-mono">container</code></p>
              </div>
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

            <!-- Embedded VNC Viewer (for GUI templates) -->
            <div *ngIf="isVncTemplate && (connectionInfo?.vncEndpoint || container.vncEndpoint)" class="mt-4 rounded-lg overflow-hidden border border-surface-200 dark:border-surface-700">
              <div class="flex items-center justify-between px-3 py-2 bg-surface-50 dark:bg-surface-900">
                <span class="text-xs font-medium text-surface-500">Remote Desktop</span>
                <a [href]="connectionInfo?.vncEndpoint || container.vncEndpoint" target="_blank" class="text-xs text-primary-600 hover:underline">Open in new tab</a>
              </div>
              <iframe [src]="sanitizedVncUrl" class="w-full" style="height: 500px; border: none;"></iframe>
            </div>

            <!-- IP Address -->
            <div *ngIf="connectionInfo?.ipAddress || container.hostIp" class="connect-row">
              <span class="connect-dot bg-green-500"></span>
              <span class="connect-label">IP</span>
              <code class="flex-1 text-xs font-mono text-surface-700 dark:text-surface-300">{{ connectionInfo?.ipAddress || container.hostIp }}</code>
              <button (click)="copyWithFeedback(connectionInfo?.ipAddress || container.hostIp, 'ip')" title="Copy"
                class="copy-btn" [class.copied]="copiedField === 'ip'">
                <svg *ngIf="copiedField !== 'ip'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>
                <svg *ngIf="copiedField === 'ip'" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
              </button>
            </div>

            <!-- Port Mappings -->
            <div *ngIf="connectionInfo?.portMappings" class="mt-3">
              <h4 class="text-xs font-medium text-surface-500 dark:text-surface-400 uppercase mb-2">Port Mappings</h4>
              <div *ngFor="let entry of portMappingEntries" class="connect-row">
                <span class="connect-dot bg-green-500"></span>
                <span class="connect-label">{{ getPortLabel(entry[0]) }}</span>
                <a [href]="'http://localhost:' + entry[1]" target="_blank"
                  class="flex-1 text-sm text-primary-600 dark:text-primary-400 hover:underline font-mono">
                  localhost:{{ entry[1] }}
                </a>
                <span class="text-xs text-surface-400">:{{ entry[0] }}</span>
              </div>
            </div>
          </div>
        </div>

        <!-- Terminal Preview (only when Running and not VNC) -->
        <div *ngIf="container.status === 'Running' && !isVncTemplate" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100 mb-3">Terminal Preview</h2>
          <app-container-thumbnail [containerId]="container.id" [isRunning]="true" size="lg"></app-container-thumbnail>
          <p class="text-xs text-surface-400 mt-2">Auto-refreshes every 30s. Open the terminal for an interactive session.</p>
        </div>

        <!-- Resources Card (only when Running) -->
        <div *ngIf="container.status === 'Running'" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100 mb-4">Resources</h2>

          <!-- Current usage from stats -->
          <div *ngIf="currentStats" class="grid grid-cols-3 gap-3 mb-4 p-3 rounded-lg bg-surface-50 dark:bg-surface-900">
            <div class="text-center">
              <p class="text-xs text-surface-400">CPU Usage</p>
              <p class="text-sm font-mono font-semibold"
                [class.text-green-600]="currentStats.cpuPercent <= 50"
                [class.text-yellow-600]="currentStats.cpuPercent > 50 && currentStats.cpuPercent <= 80"
                [class.text-red-600]="currentStats.cpuPercent > 80">{{ currentStats.cpuPercent }}%</p>
            </div>
            <div class="text-center">
              <p class="text-xs text-surface-400">Memory</p>
              <p class="text-sm font-mono font-semibold"
                [class.text-green-600]="currentStats.memoryPercent <= 50"
                [class.text-yellow-600]="currentStats.memoryPercent > 50 && currentStats.memoryPercent <= 80"
                [class.text-red-600]="currentStats.memoryPercent > 80">{{ formatBytes(currentStats.memoryUsageBytes) }} / {{ formatBytes(currentStats.memoryLimitBytes) }}</p>
            </div>
            <div class="text-center">
              <p class="text-xs text-surface-400">Disk</p>
              <p class="text-sm font-mono font-semibold" *ngIf="currentStats.diskUsageBytes > 0">{{ formatBytes(currentStats.diskUsageBytes) }}</p>
              <p class="text-sm text-surface-400" *ngIf="currentStats.diskUsageBytes === 0">--</p>
            </div>
          </div>

          <!-- Resize sliders -->
          <div class="space-y-3">
            <div>
              <div class="flex justify-between mb-1">
                <label class="text-xs text-surface-500 dark:text-surface-400">CPU Cores</label>
                <span class="text-xs text-surface-400">max {{ resizeLimits.maxCpu }}</span>
              </div>
              <div class="flex items-center gap-2">
                <input type="range" [(ngModel)]="resizeCpu" name="resizeCpu" min="1" [max]="resizeLimits.maxCpu" step="1"
                  class="flex-1 h-2 rounded-lg appearance-none bg-surface-200 dark:bg-surface-700 accent-primary-600" />
                <span class="text-sm font-mono w-8 text-right text-surface-700 dark:text-surface-300">{{ resizeCpu }}</span>
              </div>
            </div>
            <div>
              <div class="flex justify-between mb-1">
                <label class="text-xs text-surface-500 dark:text-surface-400">Memory (MB)</label>
                <span class="text-xs text-surface-400">max {{ resizeLimits.maxMemory }}</span>
              </div>
              <div class="flex items-center gap-2">
                <input type="range" [(ngModel)]="resizeMemory" name="resizeMemory" min="512" [max]="resizeLimits.maxMemory" step="512"
                  class="flex-1 h-2 rounded-lg appearance-none bg-surface-200 dark:bg-surface-700 accent-primary-600" />
                <span class="text-sm font-mono w-16 text-right text-surface-700 dark:text-surface-300">{{ resizeMemory }}</span>
              </div>
            </div>
            <div *ngIf="resizeError" class="text-xs text-red-600 dark:text-red-400">{{ resizeError }}</div>
            <div *ngIf="resizeSuccess" class="text-xs text-green-600 dark:text-green-400">Resources updated.</div>
            <button (click)="applyResize()" [disabled]="resizing"
              class="px-3 py-1.5 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700 disabled:opacity-50">
              {{ resizing ? 'Applying...' : 'Apply Changes' }}
            </button>
          </div>
          <p class="mt-2 text-xs text-surface-400">CPU and memory changes apply immediately without restart. Disk cannot be changed at runtime.</p>
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
          <div *ngIf="actionError" class="mb-3 rounded-lg bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 p-3">
            <p class="text-sm text-red-800 dark:text-red-200">{{ actionError }}</p>
          </div>
          <div class="flex flex-wrap gap-2">
            <button *ngIf="container.status === 'Stopped' || container.status === 'Failed'" (click)="startContainer()"
              [disabled]="actionBusy"
              class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-green-600 hover:bg-green-700 disabled:opacity-50 transition-colors">
              {{ actionBusy ? 'Starting...' : 'Start' }}
            </button>
            <button *ngIf="container.status === 'Running'" (click)="stopContainer()"
              [disabled]="actionBusy"
              class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-yellow-600 hover:bg-yellow-700 disabled:opacity-50 transition-colors">
              {{ actionBusy ? 'Stopping...' : 'Stop' }}
            </button>
            <button (click)="destroyContainer()"
              [disabled]="actionBusy || container.status === 'Destroyed' || container.status === 'Destroying'"
              class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-red-600 hover:bg-red-700 disabled:opacity-50 transition-colors">
              Destroy
            </button>
          </div>
        </div>
      </div>

      <!-- Repositories -->
      <div *ngIf="repositories.length > 0" class="space-y-4">
        <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">Repositories</h2>
        <div *ngFor="let repo of repositories" class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5">
          <div class="flex items-center justify-between mb-3">
            <div class="flex items-center gap-2 min-w-0">
              <span class="text-sm font-mono text-surface-900 dark:text-surface-100 truncate">{{ repo.url }}</span>
              <span *ngIf="repo.branch" class="shrink-0 text-xs px-1.5 py-0.5 rounded bg-surface-100 dark:bg-surface-700 text-surface-600 dark:text-surface-300">{{ repo.branch }}</span>
            </div>
            <span [ngClass]="getCloneStatusClasses(repo.cloneStatus)">
              <span *ngIf="repo.cloneStatus === 'Cloning'" class="inline-block w-3 h-3 mr-1 border-2 border-cyan-400 border-t-transparent rounded-full animate-spin"></span>
              {{ repo.cloneStatus }}
            </span>
          </div>
          <div class="text-xs text-surface-500 dark:text-surface-400 mb-2 font-mono">{{ repo.targetPath }}</div>
          <div *ngIf="repo.cloneError" class="text-xs text-red-600 dark:text-red-400 mb-2">{{ repo.cloneError }}</div>
          <div *ngIf="getMetadata(repo) as meta" class="grid grid-cols-2 sm:grid-cols-3 gap-x-4 gap-y-2 pt-3 border-t border-surface-100 dark:border-surface-700">
            <div *ngIf="meta.checkedOutBranch">
              <dt class="text-xs text-surface-400">Branch</dt>
              <dd class="text-sm text-surface-700 dark:text-surface-200">{{ meta.checkedOutBranch }}</dd>
            </div>
            <div *ngIf="meta.fileCount !== undefined && meta.fileCount !== null">
              <dt class="text-xs text-surface-400">Files</dt>
              <dd class="text-sm text-surface-700 dark:text-surface-200">{{ meta.fileCount | number }}</dd>
            </div>
            <div *ngIf="meta.diskUsageBytes !== undefined && meta.diskUsageBytes !== null">
              <dt class="text-xs text-surface-400">Size</dt>
              <dd class="text-sm text-surface-700 dark:text-surface-200">{{ formatBytes(meta.diskUsageBytes) }}</dd>
            </div>
            <div *ngIf="meta.lastCommitHash">
              <dt class="text-xs text-surface-400">Last Commit</dt>
              <dd class="text-sm font-mono text-surface-700 dark:text-surface-200">{{ meta.lastCommitHash }}</dd>
            </div>
            <div *ngIf="meta.lastCommitAuthor">
              <dt class="text-xs text-surface-400">Author</dt>
              <dd class="text-sm text-surface-700 dark:text-surface-200">{{ meta.lastCommitAuthor }}</dd>
            </div>
            <div *ngIf="meta.lastCommitDate">
              <dt class="text-xs text-surface-400">Date</dt>
              <dd class="text-sm text-surface-700 dark:text-surface-200">{{ meta.lastCommitDate | date:'medium' }}</dd>
            </div>
            <div *ngIf="meta.lastCommitMessage" class="col-span-2 sm:col-span-3">
              <dt class="text-xs text-surface-400">Message</dt>
              <dd class="text-sm text-surface-700 dark:text-surface-200 truncate">{{ meta.lastCommitMessage }}</dd>
            </div>
          </div>
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
  resizeCpu = 2;
  resizeMemory = 4096;
  resizing = false;
  resizeError = '';
  resizeSuccess = false;
  currentStats: ContainerStats | null = null;
  private statsTimer: any = null;
  actionBusy = false;
  actionError = '';
  copiedField = '';
  isStuck = false;
  codeAssistantLabel = '';

  private pollTimer: any = null;
  private eventPollTimer: any = null;
  private containerId = '';
  private copyTimeout: any = null;
  private createdTime: Date | null = null;

  constructor(
    private api: ContainersApiService,
    private route: ActivatedRoute,
    private router: Router,
    private sanitizer: DomSanitizer,
  ) {}

  ngOnInit(): void {
    this.containerId = this.route.snapshot.paramMap.get('id')!;
    this.loadContainer();
  }

  ngOnDestroy(): void {
    this.clearPoll();
    this.clearEventPoll();
    this.stopStatsPolling();
    if (this.copyTimeout) clearTimeout(this.copyTimeout);
  }

  get portMappingEntries(): [string, string][] {
    if (!this.connectionInfo?.portMappings) return [];
    return Object.entries(this.connectionInfo.portMappings);
  }

  get isVncTemplate(): boolean {
    return (this.container?.template as any)?.guiType === 'vnc';
  }

  get sanitizedVncUrl(): SafeResourceUrl {
    const url = this.connectionInfo?.vncEndpoint || this.container?.vncEndpoint || '';
    return this.sanitizer.bypassSecurityTrustResourceUrl(url);
  }

  loadContainer(): void {
    this.loading = !this.container;
    this.error = '';

    this.api.getContainer(this.containerId).subscribe({
      next: (c) => {
        this.container = c;
        this.loading = false;

        // Parse code assistant label
        if (c.codeAssistant) {
          try {
            const ca = JSON.parse(c.codeAssistant);
            this.codeAssistantLabel = CODE_ASSISTANT_TOOLS.find(t => t.value === ca.Tool)?.label || ca.Tool || '';
          } catch {
            this.codeAssistantLabel = '';
          }
        } else {
          this.codeAssistantLabel = '';
        }

        // Initialize resize sliders from template defaults
        if (c.template?.defaultResources) {
          try {
            const r = JSON.parse(c.template.defaultResources);
            this.resizeCpu = r.cpuCores ?? 2;
            this.resizeMemory = r.memoryMb ?? 4096;
          } catch {}
        }

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
          this.startStatsPolling();
        } else {
          this.stopStatsPolling();
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

  get resizeLimits(): { maxCpu: number; maxMemory: number } {
    const provider = this.container?.provider as any;
    if (provider?.capabilities) {
      try {
        const caps = JSON.parse(provider.capabilities);
        return { maxCpu: caps.maxCpuCores ?? 8, maxMemory: caps.maxMemoryMb ?? 16384 };
      } catch {}
    }
    return { maxCpu: 8, maxMemory: 16384 };
  }

  formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return (bytes / Math.pow(1024, i)).toFixed(i > 1 ? 1 : 0) + ' ' + units[i];
  }

  private startStatsPolling(): void {
    this.stopStatsPolling();
    if (this.container?.status !== 'Running') return;
    this.fetchStats();
    this.statsTimer = setInterval(() => this.fetchStats(), 5000);
  }

  private stopStatsPolling(): void {
    if (this.statsTimer) { clearInterval(this.statsTimer); this.statsTimer = null; }
  }

  private fetchStats(): void {
    this.api.getContainerStats(this.containerId).subscribe({
      next: (s) => { this.currentStats = s; },
      error: () => {},
    });
  }

  applyResize(): void {
    this.resizing = true;
    this.resizeError = '';
    this.resizeSuccess = false;
    this.api.resizeContainer(this.containerId, {
      cpuCores: this.resizeCpu,
      memoryMb: this.resizeMemory,
      diskGb: 20,
    }).subscribe({
      next: () => {
        this.resizing = false;
        this.resizeSuccess = true;
        setTimeout(() => { this.resizeSuccess = false; }, 3000);
      },
      error: (err) => {
        this.resizing = false;
        this.resizeError = err?.error?.error || 'Failed to resize container';
      },
    });
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
    this.actionError = '';
    this.api.startContainer(this.containerId).subscribe({
      next: () => { this.actionBusy = false; this.loadContainer(); },
      error: (err) => {
        this.actionBusy = false;
        this.actionError = err.error?.message || err.error || 'Failed to start container';
      },
    });
  }

  stopContainer(): void {
    this.actionBusy = true;
    this.actionError = '';
    this.api.stopContainer(this.containerId).subscribe({
      next: () => { this.actionBusy = false; this.loadContainer(); },
      error: (err) => {
        this.actionBusy = false;
        this.actionError = err.error?.message || err.error || 'Failed to stop container';
      },
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

  getPortLabel(containerPort: string): string {
    const labels: Record<string, string> = {
      '22': 'SSH', '80': 'HTTP', '443': 'HTTPS', '3000': 'Dev Server',
      '4200': 'Angular', '5000': '.NET', '5173': 'Vite', '5432': 'Postgres',
      '6080': 'VNC', '8080': 'IDE', '8443': 'API', '8888': 'Jupyter',
      '3306': 'MySQL', '6379': 'Redis', '27017': 'MongoDB',
    };
    return labels[containerPort] || `Port ${containerPort}`;
  }

  getSshUrl(): string {
    // Extract port from SSH endpoint like "ssh root@localhost -p 12345"
    const ep = this.connectionInfo?.sshEndpoint || '';
    const portMatch = ep.match(/-p\s+(\d+)/);
    const port = portMatch ? portMatch[1] : '22';
    // ssh:// URL scheme opens the native terminal on macOS and most Linux desktops
    return `ssh://root@localhost:${port}`;
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

  getMetadata(repo: ContainerGitRepository): GitCloneMetadata | null {
    if (!repo.cloneMetadata) return null;
    try {
      return JSON.parse(repo.cloneMetadata) as GitCloneMetadata;
    } catch {
      return null;
    }
  }


  getCloneStatusClasses(status: string): string[] {
    const base = ['inline-flex', 'items-center', 'px-3', 'py-1', 'rounded-sm', 'text-sm', 'font-medium'];
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
    const base = ['inline-flex', 'items-center', 'px-3', 'py-1', 'rounded-sm', 'text-sm', 'font-medium'];
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
