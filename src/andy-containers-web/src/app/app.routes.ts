import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  { path: '', redirectTo: '/dashboard', pathMatch: 'full' },
  { path: 'login', loadComponent: () => import('./features/auth/login.component').then(m => m.LoginComponent) },
  { path: 'callback', loadComponent: () => import('./features/auth/callback.component').then(m => m.CallbackComponent) },
  { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard.component').then(m => m.DashboardComponent), canActivate: [authGuard] },
  { path: 'containers', loadComponent: () => import('./features/containers/container-list/container-list.component').then(m => m.ContainerListComponent), canActivate: [authGuard] },
  { path: 'containers/create', loadComponent: () => import('./features/containers/container-create/container-create.component').then(m => m.ContainerCreateComponent), canActivate: [authGuard] },
  { path: 'containers/:id', loadComponent: () => import('./features/containers/container-detail/container-detail.component').then(m => m.ContainerDetailComponent), canActivate: [authGuard] },
  { path: 'containers/:id/terminal', loadComponent: () => import('./features/containers/container-terminal/container-terminal.component').then(m => m.ContainerTerminalComponent), canActivate: [authGuard] },
  { path: 'templates', loadComponent: () => import('./features/templates/template-list/template-list.component').then(m => m.TemplateListComponent), canActivate: [authGuard] },
  { path: 'templates/:id', loadComponent: () => import('./features/templates/template-detail/template-detail.component').then(m => m.TemplateDetailComponent), canActivate: [authGuard] },
  { path: 'providers', loadComponent: () => import('./features/providers/provider-list/provider-list.component').then(m => m.ProviderListComponent), canActivate: [authGuard] },
  { path: 'workspaces', loadComponent: () => import('./features/workspaces/workspace-list/workspace-list.component').then(m => m.WorkspaceListComponent), canActivate: [authGuard] },
  { path: 'workspaces/create', loadComponent: () => import('./features/workspaces/workspace-create/workspace-create.component').then(m => m.WorkspaceCreateComponent), canActivate: [authGuard] },
  { path: 'workspaces/:id', loadComponent: () => import('./features/workspaces/workspace-detail/workspace-detail.component').then(m => m.WorkspaceDetailComponent), canActivate: [authGuard] },
  { path: 'settings', loadComponent: () => import('./features/settings/settings.component').then(m => m.SettingsComponent), canActivate: [authGuard] },
  { path: 'docs', loadComponent: () => import('./features/docs/docs.component').then(m => m.DocsComponent), canActivate: [authGuard] },
];
