import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Template } from '../../../core/models';

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
          class="group block rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5 hover:shadow-md hover:border-primary-300 dark:hover:border-primary-600 hover:-translate-y-0.5 transition-all cursor-pointer no-underline">
          <div class="flex items-start justify-between mb-2">
            <h3 class="text-base font-semibold text-primary-600 dark:text-primary-400 group-hover:underline">{{ t.name }}</h3>
            <span class="text-xs text-surface-400 font-mono">{{ t.code }}</span>
          </div>
          <p *ngIf="t.description" class="text-sm text-surface-500 dark:text-surface-400 mb-3 line-clamp-2">{{ t.description }}</p>
          <div class="space-y-2 text-xs text-surface-500 dark:text-surface-400">
            <div class="flex justify-between">
              <span>Version</span>
              <span class="font-medium text-surface-700 dark:text-surface-300">{{ t.version }}</span>
            </div>
            <div class="flex justify-between">
              <span>Base Image</span>
              <span class="font-mono text-surface-700 dark:text-surface-300 truncate ml-2 max-w-[200px]">{{ t.baseImage }}</span>
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
          <div *ngIf="t.tags && t.tags.length > 0" class="flex flex-wrap gap-1 mt-3">
            <span *ngFor="let tag of t.tags"
              class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-surface-100 text-surface-600 dark:bg-surface-700 dark:text-surface-300">
              {{ tag }}
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

  constructor(private api: ContainersApiService) {}

  ngOnInit(): void {
    this.loadTemplates();
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
}
