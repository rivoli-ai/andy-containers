import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Template, ImageBuildRecord, TemplateBuildStatus } from '../../../core/models';

@Component({
  selector: 'app-template-list',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <div class="space-y-6">
      <!-- Header -->
      <div class="flex items-center justify-between">
        <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Templates</h1>
        <button (click)="loadTemplates()"
          class="inline-flex items-center px-3 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700">
          Refresh
        </button>
      </div>

      <!-- Loading -->
      <div *ngIf="loading" class="flex items-center justify-center py-12">
        <div class="text-surface-500 dark:text-surface-400">Loading templates...</div>
      </div>

      <!-- Error -->
      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
        <button (click)="loadTemplates()" class="mt-2 text-sm text-red-600 dark:text-red-400 underline">Retry</button>
      </div>

      <!-- Grid of template cards -->
      <div *ngIf="!loading && !error" class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        <a *ngFor="let t of templates" [routerLink]="['/templates', t.id]"
          class="group block rounded-lg border bg-white dark:bg-surface-800 p-5 hover:shadow-md hover:-translate-y-0.5 transition-all cursor-pointer no-underline border-l-4"
          [ngClass]="isCustomImage(t)
            ? 'border-l-amber-500 border-amber-200 dark:border-amber-800 hover:border-amber-300 dark:hover:border-amber-600'
            : 'border-l-sky-500 border-sky-200 dark:border-sky-800 hover:border-sky-300 dark:hover:border-sky-600'">
          <div class="flex items-start justify-between mb-2">
            <h3 class="text-base font-semibold text-primary-600 dark:text-primary-400 group-hover:underline">{{ t.name }}</h3>
            <div class="flex items-center gap-1.5">
              <span *ngIf="getBuildStatus(t.code) as status"
                class="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium whitespace-nowrap"
                [ngClass]="getBuildStatusClasses(status)">
                {{ getBuildStatusLabel(status) }}
              </span>
              <span class="text-xs text-surface-400 font-mono">{{ t.code }}</span>
            </div>
          </div>
          <p *ngIf="t.description" class="text-sm text-surface-500 dark:text-surface-400 mb-3 line-clamp-2">{{ t.description }}</p>
          <div class="space-y-2 text-xs text-surface-500 dark:text-surface-400">
            <div class="flex justify-between">
              <span>Version</span>
              <span class="font-medium text-surface-700 dark:text-surface-300">{{ t.version }}</span>
            </div>
            <div class="flex justify-between items-center">
              <span>Base Image</span>
              <div class="flex items-center gap-1.5">
                <span class="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium whitespace-nowrap"
                  [ngClass]="isCustomImage(t) ? 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300' : 'bg-sky-100 text-sky-700 dark:bg-sky-900/30 dark:text-sky-300'">
                  {{ isCustomImage(t) ? 'Custom' : 'Registry' }}
                </span>
                <span class="font-mono text-surface-700 dark:text-surface-300 truncate max-w-[150px]">{{ t.baseImage }}</span>
              </div>
            </div>
            <div *ngIf="getBuildRecord(t.code) as rec" class="flex justify-between">
              <span>Image Size</span>
              <span class="font-medium text-surface-700 dark:text-surface-300">{{ rec.imageSizeBytes ? formatBytes(rec.imageSizeBytes) : 'N/A' }}</span>
            </div>
            <div *ngIf="getBuildRecord(t.code) as rec" class="flex justify-between">
              <span>Platform</span>
              <span class="font-medium text-surface-700 dark:text-surface-300">{{ rec.os && rec.architecture ? rec.os + '/' + rec.architecture : 'N/A' }}</span>
            </div>
            <div *ngIf="getBuildRecord(t.code) as rec" class="flex justify-between">
              <span>Layers</span>
              <span class="font-medium text-surface-700 dark:text-surface-300">{{ rec.layerCount || 'N/A' }}</span>
            </div>
            <div class="flex justify-between">
              <span>IDE Type</span>
              <span class="font-medium text-surface-700 dark:text-surface-300">{{ t.ideType }}</span>
            </div>
            <div class="flex justify-between">
              <span>Scope</span>
              <span class="font-medium text-surface-700 dark:text-surface-300">{{ t.catalogScope }}</span>
            </div>
          </div>
          <div *ngIf="t.tags && t.tags.length > 0 || t.guiType === 'vnc'" class="flex flex-wrap gap-1 mt-3">
            <span *ngFor="let tag of t.tags"
              class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium"
              [ngClass]="getTagClasses(tag)">
              {{ tag }}
            </span>
            <span *ngIf="t.guiType === 'vnc'" class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300 ml-1">
              VNC Desktop
            </span>
          </div>
        </a>
      </div>

      <div *ngIf="!loading && !error && templates.length === 0" class="text-center py-12 text-surface-400 dark:text-surface-500">
        No templates found
      </div>
    </div>
  `,
})
export class TemplateListComponent implements OnInit {
  loading = true;
  error = '';
  templates: Template[] = [];
  buildStatuses: Record<string, TemplateBuildStatus> = {};
  buildRecords: Record<string, ImageBuildRecord> = {};

  constructor(private api: ContainersApiService) {}

  ngOnInit(): void {
    this.loadTemplates();
    this.loadBuildStatuses();
  }

  private static readonly TAG_COLORS: Record<string, string> = {
    // Languages & SDKs
    'dotnet':     'bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-300',
    'dotnet-10':  'bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-300',
    'python':     'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-300',
    'node':       'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-300',
    'angular':    'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-300',

    // Distros
    'alpine':     'bg-cyan-100 text-cyan-700 dark:bg-cyan-900/30 dark:text-cyan-300',
    'ubuntu':     'bg-orange-100 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300',

    // Categories
    'full-stack': 'bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300',
    'minimal':    'bg-sky-100 text-sky-700 dark:bg-sky-900/30 dark:text-sky-300',
    'ai':         'bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300',
    'agent':      'bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300',
    'devpilot':   'bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300',
    'vnc':        'bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300',
    'ui':         'bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300',
    'andy-cli':   'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300',
  };

  private static readonly DEFAULT_TAG = 'bg-surface-100 text-surface-600 dark:bg-surface-700 dark:text-surface-300';

  getTagClasses(tag: string): string {
    return (this.constructor as typeof TemplateListComponent).TAG_COLORS[tag]
      ?? (this.constructor as typeof TemplateListComponent).DEFAULT_TAG;
  }

  loadTemplates(): void {
    this.loading = true;
    this.error = '';
    this.api.getTemplates({ take: '100' }).subscribe({
      next: (res) => {
        this.templates = res.items;
        this.loading = false;
      },
      error: () => {
        this.error = 'Failed to load templates';
        this.loading = false;
      },
    });
  }

  loadBuildStatuses(): void {
    this.api.getImageBuildStatuses().subscribe({
      next: (records) => {
        this.buildStatuses = {};
        this.buildRecords = {};
        for (const r of records) {
          if (r.templateCode) {
            this.buildStatuses[r.templateCode] = r.status;
            this.buildRecords[r.templateCode] = r;
          }
        }
      },
      error: () => { /* ignore - build statuses are optional */ },
    });
  }

  getBuildStatus(code: string): TemplateBuildStatus | null {
    return this.buildStatuses[code] ?? null;
  }

  getBuildRecord(code: string): ImageBuildRecord | null {
    return this.buildRecords[code] ?? null;
  }

  formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return (bytes / Math.pow(1024, i)).toFixed(i > 1 ? 1 : 0) + ' ' + units[i];
  }

  getBuildStatusLabel(status: TemplateBuildStatus): string {
    switch (status) {
      case 'Built': return 'Image Built';
      case 'NotBuilt': return 'Not Built';
      case 'Building': return 'Building...';
      case 'Outdated': return 'Outdated';
      case 'Failed': return 'Build Failed';
      default: return '';
    }
  }

  getBuildStatusClasses(status: TemplateBuildStatus): string {
    switch (status) {
      case 'Built': return 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-300';
      case 'NotBuilt': return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-300';
      case 'Building': return 'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300';
      case 'Outdated': return 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-300';
      case 'Failed': return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-300';
      default: return 'bg-surface-100 text-surface-600 dark:bg-surface-700 dark:text-surface-300';
    }
  }

  isCustomImage(t: Template): boolean {
    return t.baseImage?.startsWith('andy-') ?? false;
  }
}
