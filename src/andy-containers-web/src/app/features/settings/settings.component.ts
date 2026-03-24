import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ContainersApiService } from '../../core/services/api.service';
import { ApiKeyCredential, ApiKeyChangeEntry, API_KEY_PROVIDERS } from '../../core/models';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="max-w-3xl mx-auto space-y-6">
      <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Settings</h1>

      <!-- API Keys Section -->
      <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800">
        <div class="flex items-start justify-between p-5 border-b border-surface-200 dark:border-surface-700">
          <div>
            <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">API Keys</h2>
            <p class="text-sm text-surface-500 dark:text-surface-400 mt-0.5">Manage API keys for AI code assistants. Keys are encrypted and never displayed.</p>
          </div>
          <button (click)="showAddForm = true" *ngIf="!showAddForm"
            class="ml-4 shrink-0 px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700">
            Add API Key
          </button>
        </div>

        <!-- Add Key Form -->
        <div *ngIf="showAddForm" class="p-5 border-b border-surface-200 dark:border-surface-700 bg-surface-50 dark:bg-surface-900">
          <div class="space-y-3">
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Provider *</label>
                <select [(ngModel)]="newKey.provider" name="provider" (ngModelChange)="onProviderChange()"
                  class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-800 px-3 py-2 text-sm">
                  <option value="">Select provider...</option>
                  <option *ngFor="let p of providers" [value]="p.value">{{ p.label }}</option>
                </select>
              </div>
              <div>
                <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Label *</label>
                <input type="text" [(ngModel)]="newKey.label" name="label" placeholder="e.g., my-anthropic-key"
                  class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-800 px-3 py-2 text-sm" />
              </div>
            </div>
            <div>
              <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">API Key *</label>
              <input type="password" [(ngModel)]="newKey.apiKey" name="apiKey" placeholder="sk-..."
                class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-800 px-3 py-2 text-sm font-mono" />
            </div>
            <div>
              <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Environment Variable <span class="text-surface-400">(auto-detected)</span></label>
              <input type="text" [(ngModel)]="newKey.envVarName" name="envVarName"
                class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-800 px-3 py-2 text-sm font-mono" />
            </div>
            <div *ngIf="addError" class="text-sm text-red-600 dark:text-red-400">{{ addError }}</div>
            <div class="flex items-center gap-2 justify-end">
              <button (click)="showAddForm = false; addError = ''"
                class="px-4 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300">
                Cancel
              </button>
              <button (click)="addKey()" [disabled]="addingKey || !newKey.provider || !newKey.label || !newKey.apiKey"
                class="px-4 py-2 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700 disabled:opacity-50">
                {{ addingKey ? 'Validating...' : 'Add & Validate' }}
              </button>
            </div>
          </div>
        </div>

        <!-- Keys Table -->
        <div *ngIf="keys.length > 0" class="divide-y divide-surface-200 dark:divide-surface-700">
          <div *ngFor="let key of keys" class="p-4">
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-3">
                <div>
                  <div class="flex items-center gap-2">
                    <span class="text-sm font-medium text-surface-900 dark:text-surface-100">{{ key.label }}</span>
                    <span [ngClass]="getStatusClasses(key)" class="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium">
                      {{ key.isValid ? 'Valid' : (key.lastValidatedAt ? 'Invalid' : 'Untested') }}
                    </span>
                  </div>
                  <div class="flex items-center gap-3 mt-1 text-xs text-surface-500 dark:text-surface-400">
                    <span>{{ key.provider }}</span>
                    <span class="font-mono">{{ key.envVarName }}</span>
                    <span class="font-mono text-surface-400">{{ key.maskedValue }}</span>
                  </div>
                  <div class="flex items-center gap-3 mt-0.5 text-xs text-surface-400">
                    <span *ngIf="key.lastValidatedAt">Validated: {{ key.lastValidatedAt | date:'short' }}</span>
                    <span *ngIf="key.lastUsedAt">Used: {{ key.lastUsedAt | date:'short' }}</span>
                  </div>
                </div>
              </div>
              <div class="flex items-center gap-2">
                <button (click)="validateKey(key)" [disabled]="key === validatingKey"
                  class="px-3 py-1.5 text-xs font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-600 dark:text-surface-300 hover:bg-surface-50 dark:hover:bg-surface-700">
                  {{ key === validatingKey ? 'Checking...' : 'Re-validate' }}
                </button>
                <button (click)="toggleHistory(key)"
                  class="px-3 py-1.5 text-xs font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-600 dark:text-surface-300 hover:bg-surface-50 dark:hover:bg-surface-700">
                  History
                </button>
                <button (click)="startEdit(key)"
                  class="px-3 py-1.5 text-xs font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-600 dark:text-surface-300 hover:bg-surface-50 dark:hover:bg-surface-700">
                  Edit
                </button>
                <button (click)="deleteKey(key)"
                  class="px-3 py-1.5 text-xs font-medium rounded-lg text-red-600 dark:text-red-400 border border-red-200 dark:border-red-800 hover:bg-red-50 dark:hover:bg-red-900/20">
                  Delete
                </button>
              </div>
            </div>

            <!-- Edit inline -->
            <div *ngIf="editingKey === key" class="mt-3 p-3 rounded-lg bg-surface-50 dark:bg-surface-900 space-y-2">
              <div class="grid grid-cols-2 gap-2">
                <input type="text" [(ngModel)]="editLabel" placeholder="Label"
                  class="rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-800 px-3 py-1.5 text-sm" />
                <input type="password" [(ngModel)]="editApiKey" placeholder="New API key (leave empty to keep)"
                  class="rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-800 px-3 py-1.5 text-sm font-mono" />
              </div>
              <div class="flex gap-2 justify-end">
                <button (click)="editingKey = null" class="px-3 py-1.5 text-xs rounded-lg border border-surface-300 dark:border-surface-600">Cancel</button>
                <button (click)="saveEdit(key)" class="px-3 py-1.5 text-xs rounded-lg text-white bg-primary-600 hover:bg-primary-700">Save</button>
              </div>
            </div>

            <!-- History timeline -->
            <div *ngIf="historyKey === key && history.length > 0" class="mt-3 p-3 rounded-lg bg-surface-50 dark:bg-surface-900">
              <div class="space-y-2 max-h-48 overflow-y-auto">
                <div *ngFor="let entry of history" class="flex items-start gap-2 text-xs">
                  <span [ngClass]="getActionClasses(entry.action)" class="inline-flex items-center px-1.5 py-0.5 rounded-sm font-medium min-w-[70px] justify-center">
                    {{ entry.action }}
                  </span>
                  <span class="text-surface-500 dark:text-surface-400">{{ entry.timestamp | date:'medium' }}</span>
                  <span *ngIf="entry.details" class="text-surface-600 dark:text-surface-300 truncate">{{ entry.details }}</span>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div *ngIf="keys.length === 0 && !showAddForm" class="p-8 text-center text-surface-400 dark:text-surface-500">
          <p class="text-sm">No API keys stored yet.</p>
          <p class="text-xs mt-1">Add keys to automatically inject them into containers with AI code assistants.</p>
        </div>
      </div>
    </div>
  `,
})
export class SettingsComponent implements OnInit {
  keys: ApiKeyCredential[] = [];
  providers = API_KEY_PROVIDERS;
  showAddForm = false;
  addingKey = false;
  addError = '';
  newKey = { provider: '', label: '', apiKey: '', envVarName: '' };

  editingKey: ApiKeyCredential | null = null;
  editLabel = '';
  editApiKey = '';

  validatingKey: ApiKeyCredential | null = null;
  historyKey: ApiKeyCredential | null = null;
  history: ApiKeyChangeEntry[] = [];

  confirmDeleteKey: ApiKeyCredential | null = null;

  constructor(private api: ContainersApiService) {}

  ngOnInit(): void {
    this.loadKeys();
  }

  loadKeys(): void {
    this.api.getApiKeys().subscribe({
      next: (keys) => { this.keys = keys; },
    });
  }

  onProviderChange(): void {
    const p = this.providers.find(p => p.value === this.newKey.provider);
    if (p) this.newKey.envVarName = p.defaultEnvVar;
  }

  addKey(): void {
    this.addingKey = true;
    this.addError = '';
    this.api.createApiKey({
      label: this.newKey.label,
      provider: this.newKey.provider,
      apiKey: this.newKey.apiKey,
      envVarName: this.newKey.envVarName || undefined,
    }).subscribe({
      next: () => {
        this.showAddForm = false;
        this.newKey = { provider: '', label: '', apiKey: '', envVarName: '' };
        this.addingKey = false;
        this.loadKeys();
      },
      error: (err) => {
        this.addError = err?.error?.error || 'Failed to add key';
        this.addingKey = false;
      },
    });
  }

  validateKey(key: ApiKeyCredential): void {
    this.validatingKey = key;
    this.api.validateApiKey(key.id).subscribe({
      next: (result) => {
        key.isValid = result.isValid;
        key.lastValidatedAt = new Date().toISOString();
        this.validatingKey = null;
      },
      error: () => { this.validatingKey = null; },
    });
  }

  startEdit(key: ApiKeyCredential): void {
    this.editingKey = key;
    this.editLabel = key.label;
    this.editApiKey = '';
  }

  saveEdit(key: ApiKeyCredential): void {
    this.api.updateApiKey(key.id, {
      label: this.editLabel || undefined,
      apiKey: this.editApiKey || undefined,
    }).subscribe({
      next: () => {
        this.editingKey = null;
        this.loadKeys();
      },
    });
  }

  deleteKey(key: ApiKeyCredential): void {
    if (!confirm(`Delete API key "${key.label}"? This cannot be undone.`)) return;
    this.api.deleteApiKey(key.id).subscribe({
      next: () => { this.loadKeys(); },
    });
  }

  toggleHistory(key: ApiKeyCredential): void {
    if (this.historyKey === key) {
      this.historyKey = null;
      this.history = [];
      return;
    }
    this.historyKey = key;
    this.api.getApiKeyHistory(key.id).subscribe({
      next: (h) => { this.history = h; },
    });
  }

  getStatusClasses(key: ApiKeyCredential): string[] {
    if (key.isValid) return ['bg-green-100', 'text-green-800', 'dark:bg-green-900/30', 'dark:text-green-400'];
    if (key.lastValidatedAt) return ['bg-red-100', 'text-red-800', 'dark:bg-red-900/30', 'dark:text-red-400'];
    return ['bg-gray-100', 'text-gray-600', 'dark:bg-gray-700', 'dark:text-gray-400'];
  }

  getActionClasses(action: string): string[] {
    switch (action) {
      case 'created': return ['bg-green-100', 'text-green-800', 'dark:bg-green-900/30', 'dark:text-green-400'];
      case 'updated': return ['bg-blue-100', 'text-blue-800', 'dark:bg-blue-900/30', 'dark:text-blue-400'];
      case 'validated': return ['bg-cyan-100', 'text-cyan-800', 'dark:bg-cyan-900/30', 'dark:text-cyan-400'];
      case 'used': return ['bg-purple-100', 'text-purple-800', 'dark:bg-purple-900/30', 'dark:text-purple-400'];
      case 'deleted': return ['bg-red-100', 'text-red-800', 'dark:bg-red-900/30', 'dark:text-red-400'];
      default: return ['bg-gray-100', 'text-gray-600', 'dark:bg-gray-700', 'dark:text-gray-400'];
    }
  }
}
