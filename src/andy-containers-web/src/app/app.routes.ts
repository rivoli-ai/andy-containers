import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: '/dashboard', pathMatch: 'full' },
  { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard.component').then(m => m.DashboardComponent) },
  { path: 'containers', loadComponent: () => import('./features/containers/container-list/container-list.component').then(m => m.ContainerListComponent) },
  { path: 'containers/create', loadComponent: () => import('./features/containers/container-create/container-create.component').then(m => m.ContainerCreateComponent) },
  { path: 'containers/:id', loadComponent: () => import('./features/containers/container-detail/container-detail.component').then(m => m.ContainerDetailComponent) },
  { path: 'containers/:id/terminal', loadComponent: () => import('./features/containers/container-terminal/container-terminal.component').then(m => m.ContainerTerminalComponent) },
  { path: 'templates', loadComponent: () => import('./features/templates/template-list/template-list.component').then(m => m.TemplateListComponent) },
  { path: 'templates/:id', loadComponent: () => import('./features/templates/template-detail/template-detail.component').then(m => m.TemplateDetailComponent) },
  { path: 'providers', loadComponent: () => import('./features/providers/provider-list/provider-list.component').then(m => m.ProviderListComponent) },
  { path: 'workspaces', loadComponent: () => import('./features/workspaces/workspace-list/workspace-list.component').then(m => m.WorkspaceListComponent) },
  { path: 'workspaces/create', loadComponent: () => import('./features/workspaces/workspace-create/workspace-create.component').then(m => m.WorkspaceCreateComponent) },
  { path: 'workspaces/:id', loadComponent: () => import('./features/workspaces/workspace-detail/workspace-detail.component').then(m => m.WorkspaceDetailComponent) },
];
