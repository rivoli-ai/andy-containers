import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../core/services/api.service';
import { Organization, Team, Container, Template } from '../../core/models';
import { StatusBadgeComponent } from '../../shared/components/status-badge/status-badge.component';

@Component({
  selector: 'app-organization-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, StatusBadgeComponent],
  template: `
    <div class="max-w-4xl mx-auto space-y-6">
      <div *ngIf="loading" class="flex items-center justify-center py-12">
        <div class="text-surface-500 dark:text-surface-400">Loading organization...</div>
      </div>

      <div *ngIf="error" class="rounded-lg bg-red-50 dark:bg-red-900/20 p-4">
        <p class="text-red-800 dark:text-red-200">{{ error }}</p>
        <a routerLink="/organizations" class="mt-2 inline-block text-sm text-red-600 dark:text-red-400 underline">Back to list</a>
      </div>

      <ng-container *ngIf="organization && !loading">
        <!-- Header -->
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-3">
            <a routerLink="/organizations" class="text-surface-400 hover:text-surface-600 dark:hover:text-surface-300">
              <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
            </a>
            <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">{{ organization.name }}</h1>
          </div>
          <button (click)="confirmDelete()"
            class="px-3 py-2 text-sm font-medium rounded-lg border border-red-300 dark:border-red-800 text-red-600 dark:text-red-400 bg-white dark:bg-surface-800 hover:bg-red-50 dark:hover:bg-red-900/20">
            Delete
          </button>
        </div>

        <!-- Details Card -->
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
          <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100 mb-4">Details</h2>
          <dl class="space-y-3">
            <div *ngIf="organization.description" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Description</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100 text-right max-w-md">{{ organization.description }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Owner</dt>
              <dd class="text-sm text-surface-900 dark:text-surface-100 font-mono">{{ organization.ownerId }}</dd>
            </div>
            <div class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Created</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ organization.createdAt | date:'medium' }}</dd>
            </div>
            <div *ngIf="organization.updatedAt" class="flex justify-between">
              <dt class="text-sm text-surface-500 dark:text-surface-400">Updated</dt>
              <dd class="text-sm text-surface-600 dark:text-surface-300">{{ organization.updatedAt | date:'medium' }}</dd>
            </div>
          </dl>
        </div>

        <!-- Teams Card -->
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
          <div class="flex items-center justify-between mb-4">
            <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">Teams</h2>
            <button (click)="showTeamForm = !showTeamForm"
              class="px-3 py-1.5 text-xs font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700">
              Create Team
            </button>
          </div>

          <!-- Create Team Form -->
          <div *ngIf="showTeamForm" class="mb-4 p-4 rounded-lg border border-primary-200 dark:border-primary-800 bg-primary-50 dark:bg-primary-900/20 space-y-3">
            <div>
              <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Team Name</label>
              <input type="text" [(ngModel)]="newTeamName" placeholder="Team name"
                class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100" />
            </div>
            <div>
              <label class="block text-sm font-medium text-surface-700 dark:text-surface-300 mb-1">Description</label>
              <input type="text" [(ngModel)]="newTeamDescription" placeholder="Optional description"
                class="w-full rounded-lg border border-surface-300 dark:border-surface-600 bg-white dark:bg-surface-900 px-3 py-2 text-sm text-surface-900 dark:text-surface-100" />
            </div>
            <div *ngIf="teamError" class="text-sm text-red-600 dark:text-red-400">{{ teamError }}</div>
            <div class="flex gap-2">
              <button (click)="createTeam()" [disabled]="creatingTeam || !newTeamName.trim()"
                class="px-3 py-1.5 text-sm font-medium rounded-lg text-white bg-primary-600 hover:bg-primary-700 disabled:opacity-50">
                {{ creatingTeam ? 'Creating...' : 'Create' }}
              </button>
              <button (click)="cancelTeamCreate()"
                class="px-3 py-1.5 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-600 dark:text-surface-400">
                Cancel
              </button>
            </div>
          </div>

          <!-- Teams Table -->
          <div *ngIf="teams.length > 0" class="overflow-x-auto">
            <table class="min-w-full divide-y divide-surface-200 dark:divide-surface-700">
              <thead class="bg-surface-50 dark:bg-surface-800">
                <tr>
                  <th class="px-4 py-2 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Name</th>
                  <th class="px-4 py-2 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Description</th>
                  <th class="px-4 py-2 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Created</th>
                  <th class="px-4 py-2 text-left text-xs font-medium text-surface-500 dark:text-surface-400 uppercase tracking-wider">Actions</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-surface-200 dark:divide-surface-700">
                <tr *ngFor="let team of teams" class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
                  <td class="px-4 py-2 whitespace-nowrap text-sm font-medium text-surface-900 dark:text-surface-100">{{ team.name }}</td>
                  <td class="px-4 py-2 text-sm text-surface-600 dark:text-surface-300 max-w-xs truncate">{{ team.description || '--' }}</td>
                  <td class="px-4 py-2 whitespace-nowrap text-sm text-surface-600 dark:text-surface-300">{{ team.createdAt | date:'short' }}</td>
                  <td class="px-4 py-2 whitespace-nowrap">
                    <button (click)="confirmDeleteTeam(team)"
                      class="text-sm text-red-600 dark:text-red-400 hover:text-red-800 dark:hover:text-red-300">
                      Delete
                    </button>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <p *ngIf="teams.length === 0 && !teamsLoading"
            class="text-sm text-surface-400 dark:text-surface-500">No teams yet</p>
          <p *ngIf="teamsLoading" class="text-sm text-surface-400 dark:text-surface-500">Loading teams...</p>
        </div>

        <!-- Containers Card -->
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
          <div class="flex items-center justify-between mb-4">
            <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">Containers</h2>
          </div>
          <div *ngIf="containers.length > 0">
            <div *ngFor="let c of containers"
              class="flex items-center justify-between py-2 border-b border-surface-100 dark:border-surface-700 last:border-0">
              <a [routerLink]="['/containers', c.id]" class="text-sm font-medium text-primary-600 hover:text-primary-700">{{ c.name }}</a>
              <app-status-badge [status]="c.status"></app-status-badge>
            </div>
          </div>
          <p *ngIf="containers.length === 0 && !containersLoading"
            class="text-sm text-surface-400 dark:text-surface-500">No containers in this organization</p>
          <p *ngIf="containersLoading" class="text-sm text-surface-400 dark:text-surface-500">Loading containers...</p>
        </div>

        <!-- Templates Card -->
        <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
          <div class="flex items-center justify-between mb-4">
            <h2 class="text-lg font-medium text-surface-900 dark:text-surface-100">Templates</h2>
          </div>
          <div *ngIf="templates.length > 0">
            <div *ngFor="let t of templates"
              class="flex items-center justify-between py-2 border-b border-surface-100 dark:border-surface-700 last:border-0">
              <a [routerLink]="['/templates', t.id]" class="text-sm font-medium text-primary-600 hover:text-primary-700">{{ t.name }}</a>
              <span class="text-xs px-2 py-0.5 rounded bg-surface-100 dark:bg-surface-700 text-surface-600 dark:text-surface-300">{{ t.version }}</span>
            </div>
          </div>
          <p *ngIf="templates.length === 0 && !templatesLoading"
            class="text-sm text-surface-400 dark:text-surface-500">No templates in this organization</p>
          <p *ngIf="templatesLoading" class="text-sm text-surface-400 dark:text-surface-500">Loading templates...</p>
        </div>
      </ng-container>
    </div>
  `,
})
export class OrganizationDetailComponent implements OnInit {
  organization: Organization | null = null;
  loading = true;
  error = '';
  orgId = '';

  // Teams
  teams: Team[] = [];
  teamsLoading = false;
  showTeamForm = false;
  creatingTeam = false;
  teamError = '';
  newTeamName = '';
  newTeamDescription = '';

  // Containers
  containers: Container[] = [];
  containersLoading = false;

  // Templates
  templates: Template[] = [];
  templatesLoading = false;

  constructor(
    private api: ContainersApiService,
    private route: ActivatedRoute,
    private router: Router,
  ) {}

  ngOnInit(): void {
    this.orgId = this.route.snapshot.paramMap.get('id')!;
    this.loadOrganization();
    this.loadTeams();
    this.loadContainers();
    this.loadTemplates();
  }

  loadOrganization(): void {
    this.loading = true;
    this.api.getOrganization(this.orgId).subscribe({
      next: (org) => {
        this.organization = org;
        this.loading = false;
      },
      error: () => {
        this.error = 'Failed to load organization';
        this.loading = false;
      },
    });
  }

  loadTeams(): void {
    this.teamsLoading = true;
    this.api.getTeams(this.orgId).subscribe({
      next: (teams) => {
        this.teams = teams;
        this.teamsLoading = false;
      },
      error: () => {
        this.teamsLoading = false;
      },
    });
  }

  loadContainers(): void {
    this.containersLoading = true;
    this.api.getContainers({ organizationId: this.orgId, take: '50' }).subscribe({
      next: (res) => {
        this.containers = res.items;
        this.containersLoading = false;
      },
      error: () => {
        this.containersLoading = false;
      },
    });
  }

  loadTemplates(): void {
    this.templatesLoading = true;
    this.api.getTemplates({ organizationId: this.orgId, take: '50' }).subscribe({
      next: (res) => {
        this.templates = res.items;
        this.templatesLoading = false;
      },
      error: () => {
        this.templatesLoading = false;
      },
    });
  }

  createTeam(): void {
    if (!this.newTeamName.trim()) return;
    this.creatingTeam = true;
    this.teamError = '';
    const data: { name: string; description?: string } = { name: this.newTeamName.trim() };
    if (this.newTeamDescription.trim()) {
      data.description = this.newTeamDescription.trim();
    }
    this.api.createTeam(this.orgId, data).subscribe({
      next: (team) => {
        this.teams.unshift(team);
        this.cancelTeamCreate();
        this.creatingTeam = false;
      },
      error: () => {
        this.teamError = 'Failed to create team';
        this.creatingTeam = false;
      },
    });
  }

  cancelTeamCreate(): void {
    this.showTeamForm = false;
    this.newTeamName = '';
    this.newTeamDescription = '';
    this.teamError = '';
  }

  confirmDeleteTeam(team: Team): void {
    if (!confirm(`Delete team "${team.name}"?`)) return;
    this.api.deleteTeam(this.orgId, team.id).subscribe({
      next: () => {
        this.teams = this.teams.filter(t => t.id !== team.id);
      },
      error: () => {
        this.error = 'Failed to delete team';
      },
    });
  }

  confirmDelete(): void {
    if (!this.organization) return;
    if (!confirm(`Delete organization "${this.organization.name}"? This cannot be undone.`)) return;
    this.api.deleteOrganization(this.organization.id).subscribe({
      next: () => this.router.navigate(['/organizations']),
      error: () => { this.error = 'Failed to delete organization'; },
    });
  }
}
