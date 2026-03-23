import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Subject } from 'rxjs';
import { debounceTime, takeUntil } from 'rxjs/operators';
import { ContainersApiService } from '../../../core/services/api.service';
import { Template, TemplateDefinition, ValidationResult } from '../../../core/models';
import { YamlEditorComponent } from '../../../shared/components/yaml-editor/yaml-editor.component';

@Component({
  selector: 'app-template-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, FormsModule, YamlEditorComponent],
  template: `
    <div class="space-y-6">
      <!-- Header -->
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <a routerLink="/templates"
            class="inline-flex items-center px-2 py-1 text-sm text-surface-500 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200">
            <svg class="w-4 h-4 mr-1" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 19l-7-7 7-7"/></svg>
            Back
          </a>
          <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100" *ngIf="template">{{ template.name }}</h1>
        </div>
      </div>

      <!-- Loading -->
      <div *ngIf="loading" class="flex items-center justify-center py-12">
        <div class="text-surface-500 dark:text-surface-400">Loading template...</div>
      </div>

      <!-- Error -->
      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
        <button (click)="load()" class="mt-2 text-sm text-red-600 dark:text-red-400 underline">Retry</button>
      </div>

      <div *ngIf="!loading && !error && template">
        <!-- Tab Bar -->
        <div class="flex border-b-2 border-surface-200 dark:border-surface-700 mb-6">
          <button (click)="activeTab = 'details'"
            class="px-5 py-3 text-sm font-semibold border-b-2 -mb-0.5 transition-colors"
            [class]="activeTab === 'details'
              ? 'border-primary-500 text-primary-600 dark:text-primary-400'
              : 'border-transparent text-surface-500 dark:text-surface-400 hover:text-surface-700 dark:hover:text-surface-200'">
            <svg class="w-4 h-4 inline-block mr-1.5 -mt-0.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/></svg>
            Details
          </button>
          <button (click)="switchToEditTab()"
            class="px-5 py-3 text-sm font-semibold border-b-2 -mb-0.5 transition-colors relative"
            [class]="activeTab === 'edit'
              ? 'border-primary-500 text-primary-600 dark:text-primary-400'
              : 'border-transparent text-surface-500 dark:text-surface-400 hover:text-surface-700 dark:hover:text-surface-200'">
            <svg class="w-4 h-4 inline-block mr-1.5 -mt-0.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"/></svg>
            Edit YAML
            <span *ngIf="hasUnsavedChanges" class="absolute top-1.5 -right-0.5 w-2 h-2 bg-amber-500 rounded-full"></span>
          </button>
        </div>

        <!-- Details Tab -->
        <div *ngIf="activeTab === 'details'" class="space-y-6">
          <!-- Overview Card -->
          <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
            <div class="flex items-start justify-between mb-4">
              <div>
                <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-100">{{ template.name }}</h2>
                <p *ngIf="template.description" class="text-sm text-surface-500 dark:text-surface-400 mt-1">{{ template.description }}</p>
              </div>
              <span class="px-2.5 py-0.5 rounded-full text-xs font-semibold bg-primary-100 text-primary-700 dark:bg-primary-900/30 dark:text-primary-300">
                {{ template.version }}
              </span>
            </div>

            <div class="grid grid-cols-1 sm:grid-cols-2 gap-4 text-sm">
              <div>
                <span class="text-surface-500 dark:text-surface-400 text-xs font-medium uppercase">Code</span>
                <p class="font-mono text-surface-900 dark:text-surface-100">{{ template.code }}</p>
              </div>
              <div>
                <span class="text-surface-500 dark:text-surface-400 text-xs font-medium uppercase">ID</span>
                <p class="font-mono text-surface-900 dark:text-surface-100 text-xs break-all">{{ template.id }}</p>
              </div>
              <div>
                <span class="text-surface-500 dark:text-surface-400 text-xs font-medium uppercase">Base Image</span>
                <p class="font-mono text-surface-900 dark:text-surface-100">{{ template.baseImage }}</p>
              </div>
              <div>
                <span class="text-surface-500 dark:text-surface-400 text-xs font-medium uppercase">IDE Type</span>
                <p class="text-surface-900 dark:text-surface-100">{{ template.ideType }}</p>
              </div>
              <div>
                <span class="text-surface-500 dark:text-surface-400 text-xs font-medium uppercase">Scope</span>
                <p>
                  <span class="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-semibold"
                    [ngClass]="getScopeBadgeClass(template.catalogScope)">
                    {{ template.catalogScope }}
                  </span>
                </p>
              </div>
              <div>
                <span class="text-surface-500 dark:text-surface-400 text-xs font-medium uppercase">Published</span>
                <p class="text-surface-900 dark:text-surface-100">{{ template.isPublished ? 'Yes' : 'No' }}</p>
              </div>
            </div>

            <div *ngIf="template.tags && template.tags.length > 0" class="flex flex-wrap gap-1.5 mt-4">
              <span *ngFor="let tag of template.tags"
                class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-surface-100 text-surface-600 dark:bg-surface-700 dark:text-surface-300">
                {{ tag }}
              </span>
            </div>
          </div>

          <!-- Definition YAML Card -->
          <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
            <div class="flex items-center justify-between mb-4">
              <h3 class="text-base font-semibold text-surface-900 dark:text-surface-100">Definition YAML</h3>
              <div class="flex gap-2">
                <button *ngIf="!definition && !defLoading" (click)="loadDefinition()"
                  class="inline-flex items-center px-3 py-1.5 text-xs font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700 transition-colors">
                  <svg class="w-3.5 h-3.5 mr-1.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4"/></svg>
                  Load YAML
                </button>
                <button *ngIf="definition" (click)="downloadYaml()"
                  class="inline-flex items-center px-3 py-1.5 text-xs font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700 transition-colors">
                  <svg class="w-3.5 h-3.5 mr-1.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"/></svg>
                  Download
                </button>
              </div>
            </div>

            <div *ngIf="defLoading" class="text-sm text-surface-500 dark:text-surface-400">Loading definition...</div>
            <div *ngIf="defError" class="text-sm text-red-600 dark:text-red-400">{{ defError }}</div>

            <app-yaml-editor *ngIf="definition"
              [value]="definition.content"
              [readOnly]="true"
              height="500px">
            </app-yaml-editor>

            <p *ngIf="!definition && !defLoading && !defError" class="text-sm text-surface-400 dark:text-surface-500">
              Click "Load YAML" to view the template definition file.
            </p>
          </div>
        </div>

        <!-- Edit YAML Tab -->
        <div *ngIf="activeTab === 'edit'" class="space-y-4">
          <!-- Toolbar -->
          <div class="flex items-center gap-3 flex-wrap">
            <button (click)="saveYaml()" [disabled]="saving || !hasUnsavedChanges"
              class="inline-flex items-center px-4 py-2 text-sm font-medium rounded-lg bg-primary-600 text-white hover:bg-primary-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all">
              <svg class="w-4 h-4 mr-1.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M8 7H5a2 2 0 00-2 2v9a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-3m-1 4l-3 3m0 0l-3-3m3 3V4"/></svg>
              {{ saving ? 'Saving...' : 'Save' }}
            </button>
            <button (click)="revertChanges()" [disabled]="!hasUnsavedChanges"
              class="inline-flex items-center px-3 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors">
              <svg class="w-4 h-4 mr-1.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"/></svg>
              Revert
            </button>
            <button (click)="downloadYaml()"
              class="inline-flex items-center px-3 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700 transition-colors">
              <svg class="w-4 h-4 mr-1.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"/></svg>
              Download
            </button>

            <span *ngIf="hasUnsavedChanges" class="text-xs font-semibold text-amber-600 dark:text-amber-400 ml-2">
              <span class="inline-block w-1.5 h-1.5 rounded-full bg-amber-500 mr-1 align-middle"></span>
              Unsaved changes
            </span>
            <span *ngIf="saveSuccess" class="text-xs font-semibold text-green-600 dark:text-green-400 ml-2">
              <svg class="w-3.5 h-3.5 inline-block mr-0.5 -mt-0.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
              Saved successfully
            </span>
            <span *ngIf="saveError" class="text-xs font-semibold text-red-600 dark:text-red-400 ml-2">
              <svg class="w-3.5 h-3.5 inline-block mr-0.5 -mt-0.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M6 18L18 6M6 6l12 12"/></svg>
              {{ saveError }}
            </span>
          </div>

          <!-- Split view: Editor + Validation -->
          <div class="grid grid-cols-1 lg:grid-cols-5 gap-4">
            <!-- Editor (60%) -->
            <div class="lg:col-span-3">
              <app-yaml-editor
                [value]="editYaml"
                [readOnly]="false"
                [diagnostics]="editorDiagnostics"
                height="500px"
                (valueChange)="onYamlChanged($event)"
                (save)="saveYaml()">
              </app-yaml-editor>
            </div>

            <!-- Validation Panel (40%) -->
            <div class="lg:col-span-2">
              <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-4 h-[500px] overflow-y-auto">
                <h4 class="text-sm font-semibold text-surface-700 dark:text-surface-300 mb-3">Validation</h4>

                <!-- Loading -->
                <div *ngIf="validating" class="flex items-center gap-2 text-sm text-surface-500 dark:text-surface-400">
                  <div class="animate-spin w-4 h-4 border-2 border-primary-400 border-t-transparent rounded-full"></div>
                  Validating...
                </div>

                <!-- No result yet -->
                <div *ngIf="!validating && !validationResult" class="text-sm text-surface-400 dark:text-surface-500">
                  <svg class="w-5 h-5 inline-block mr-1.5 -mt-0.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/></svg>
                  Start typing to see validation results
                </div>

                <!-- Result -->
                <div *ngIf="!validating && validationResult">
                  <!-- Summary -->
                  <div class="flex items-center gap-2 mb-3 pb-3 border-b border-surface-200 dark:border-surface-700">
                    <span *ngIf="validationResult.isValid" class="text-sm font-semibold text-green-600 dark:text-green-400">
                      <svg class="w-4 h-4 inline-block mr-0.5 -mt-0.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
                      Valid
                    </span>
                    <span *ngIf="validationResult.errors.length > 0" class="text-sm font-semibold text-red-600 dark:text-red-400">
                      <svg class="w-4 h-4 inline-block mr-0.5 -mt-0.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M6 18L18 6M6 6l12 12"/></svg>
                      {{ validationResult.errors.length }} error(s)
                    </span>
                    <span *ngIf="validationResult.warnings.length > 0" class="text-sm font-semibold text-amber-600 dark:text-amber-400">
                      <svg class="w-4 h-4 inline-block mr-0.5 -mt-0.5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"/></svg>
                      {{ validationResult.warnings.length }} warning(s)
                    </span>
                  </div>

                  <!-- Errors -->
                  <div *ngIf="validationResult.errors.length > 0" class="mb-3">
                    <div *ngFor="let err of validationResult.errors"
                      class="flex items-start gap-2 p-2 rounded-md bg-red-50 dark:bg-red-900/10 mb-1.5 text-xs">
                      <svg class="w-3.5 h-3.5 text-red-500 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M6 18L18 6M6 6l12 12"/></svg>
                      <div>
                        <span *ngIf="err.line" class="font-mono text-red-500">Line {{ err.line }}: </span>
                        <span *ngIf="err.field" class="font-semibold text-red-700 dark:text-red-300">{{ err.field }}: </span>
                        <span class="text-red-600 dark:text-red-400">{{ err.message }}</span>
                      </div>
                    </div>
                  </div>

                  <!-- Warnings -->
                  <div *ngIf="validationResult.warnings.length > 0">
                    <div *ngFor="let warn of validationResult.warnings"
                      class="flex items-start gap-2 p-2 rounded-md bg-amber-50 dark:bg-amber-900/10 mb-1.5 text-xs">
                      <svg class="w-3.5 h-3.5 text-amber-500 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"/></svg>
                      <div>
                        <span *ngIf="warn.line" class="font-mono text-amber-500">Line {{ warn.line }}: </span>
                        <span *ngIf="warn.field" class="font-semibold text-amber-700 dark:text-amber-300">{{ warn.field }}: </span>
                        <span class="text-amber-600 dark:text-amber-400">{{ warn.message }}</span>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class TemplateDetailComponent implements OnInit, OnDestroy {
  loading = true;
  error = '';
  template: Template | null = null;

  definition: TemplateDefinition | null = null;
  defLoading = false;
  defError = '';

  activeTab = 'details';

  // Edit state
  originalYaml = '';
  editYaml = '';
  hasUnsavedChanges = false;
  saving = false;
  saveSuccess = false;
  saveError = '';

  // Validation
  validationResult: ValidationResult | null = null;
  validating = false;
  editorDiagnostics: { line: number; severity: string; message: string }[] = [];

  private templateId = '';
  private destroy$ = new Subject<void>();
  private yamlChange$ = new Subject<string>();

  constructor(
    private route: ActivatedRoute,
    private api: ContainersApiService,
  ) {}

  ngOnInit(): void {
    this.templateId = this.route.snapshot.paramMap.get('id') || '';
    this.load();

    // Debounced validation (500ms after last change)
    this.yamlChange$.pipe(
      debounceTime(500),
      takeUntil(this.destroy$),
    ).subscribe((yaml) => {
      this.validateYaml(yaml);
    });
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  load(): void {
    this.loading = true;
    this.error = '';
    this.api.getTemplate(this.templateId).subscribe({
      next: (t) => {
        this.template = t;
        this.loading = false;
        this.loadDefinition();
      },
      error: (err) => {
        this.error = err.message || 'Failed to load template';
        this.loading = false;
      },
    });
  }

  loadDefinition(): void {
    this.defLoading = true;
    this.defError = '';
    this.api.getTemplateDefinition(this.templateId).subscribe({
      next: (def) => {
        this.definition = def;
        this.originalYaml = def.content;
        this.editYaml = def.content;
        this.hasUnsavedChanges = false;
        this.defLoading = false;
      },
      error: () => {
        this.defError = 'No definition file found for this template.';
        this.defLoading = false;
      },
    });
  }

  switchToEditTab(): void {
    this.activeTab = 'edit';
    this.saveSuccess = false;
    this.saveError = '';
  }

  onYamlChanged(value: string): void {
    this.editYaml = value;
    this.hasUnsavedChanges = this.editYaml !== this.originalYaml;
    this.saveSuccess = false;
    this.saveError = '';
    this.yamlChange$.next(value);
  }

  private validateYaml(yaml: string): void {
    if (!yaml?.trim()) return;
    this.validating = true;
    this.api.validateTemplateYaml(yaml).subscribe({
      next: (result) => {
        this.validationResult = result;
        this.validating = false;

        // Update editor diagnostics
        this.editorDiagnostics = [
          ...result.errors.map((e) => ({
            line: e.line || 1,
            severity: 'error',
            message: `${e.field ? e.field + ': ' : ''}${e.message}`,
          })),
          ...result.warnings.map((w) => ({
            line: w.line || 1,
            severity: 'warning',
            message: `${w.field ? w.field + ': ' : ''}${w.message}`,
          })),
        ];
      },
      error: () => {
        this.validating = false;
        this.editorDiagnostics = [];
      },
    });
  }

  saveYaml(): void {
    if (!this.hasUnsavedChanges || this.saving) return;
    this.saving = true;
    this.saveSuccess = false;
    this.saveError = '';

    this.api.updateTemplateDefinition(this.templateId, this.editYaml).subscribe({
      next: (updated) => {
        this.template = updated;
        this.originalYaml = this.editYaml;
        this.hasUnsavedChanges = false;
        this.saveSuccess = true;
        this.saving = false;
        this.loadDefinition();
      },
      error: (err) => {
        this.saveError = err.error?.errors
          ? 'Validation failed. Fix errors and try again.'
          : err.message || 'Failed to save';
        this.saving = false;
      },
    });
  }

  revertChanges(): void {
    this.editYaml = this.originalYaml;
    this.hasUnsavedChanges = false;
    this.saveSuccess = false;
    this.saveError = '';
    this.validationResult = null;
    this.editorDiagnostics = [];
  }

  downloadYaml(): void {
    const content = this.activeTab === 'edit' ? this.editYaml : (this.definition?.content || '');
    const filename = `${this.template?.code || 'template'}.yaml`;
    const blob = new Blob([content], { type: 'text/yaml' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  }

  getScopeBadgeClass(scope: string): string {
    switch (scope?.toLowerCase()) {
      case 'global': return 'scope-badge-global';
      case 'organization': return 'scope-badge-organization';
      case 'team': return 'scope-badge-team';
      case 'user': return 'scope-badge-user';
      default: return 'bg-gray-100 text-gray-600 dark:bg-gray-700 dark:text-gray-400';
    }
  }
}
