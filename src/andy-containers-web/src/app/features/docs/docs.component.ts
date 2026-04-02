import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-docs',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="space-y-6">
      <h1 class="text-2xl font-semibold text-surface-900 dark:text-surface-100">Documentation</h1>

      <!-- Quick Links -->
      <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        <a *ngFor="let doc of docs" [href]="doc.url" target="_blank"
          class="group block rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-5 hover:shadow-md hover:border-primary-300 dark:hover:border-primary-600 hover:-translate-y-0.5 transition-all no-underline">
          <div class="flex items-center gap-3 mb-2">
            <svg class="w-5 h-5 text-primary-500 flex-shrink-0" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24">
              <path [attr.d]="doc.icon"/>
            </svg>
            <h3 class="text-base font-semibold text-primary-600 dark:text-primary-400 group-hover:underline">{{ doc.title }}</h3>
          </div>
          <p class="text-sm text-surface-500 dark:text-surface-400">{{ doc.description }}</p>
        </a>
      </div>

      <!-- Key Features -->
      <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
        <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-100 mb-4">Key Features</h2>
        <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div *ngFor="let feature of features" class="flex items-start gap-2">
            <svg class="w-4 h-4 text-green-500 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M5 13l4 4L19 7"/></svg>
            <div>
              <span class="text-sm font-medium text-surface-900 dark:text-surface-100">{{ feature.name }}</span>
              <span class="text-sm text-surface-500 dark:text-surface-400"> -- {{ feature.desc }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- Services -->
      <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
        <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-100 mb-4">Services</h2>
        <div class="overflow-x-auto">
          <table class="w-full text-sm">
            <thead>
              <tr class="border-b border-surface-200 dark:border-surface-700">
                <th class="text-left py-2 pr-4 font-medium text-surface-500 dark:text-surface-400">Service</th>
                <th class="text-left py-2 pr-4 font-medium text-surface-500 dark:text-surface-400">URL</th>
                <th class="text-left py-2 font-medium text-surface-500 dark:text-surface-400">Description</th>
              </tr>
            </thead>
            <tbody>
              <tr *ngFor="let svc of services" class="border-b border-surface-100 dark:border-surface-800 last:border-0">
                <td class="py-2 pr-4 font-medium text-surface-900 dark:text-surface-100">{{ svc.name }}</td>
                <td class="py-2 pr-4"><a [href]="svc.url" target="_blank" class="font-mono text-primary-600 dark:text-primary-400 hover:underline">{{ svc.url }}</a></td>
                <td class="py-2 text-surface-500 dark:text-surface-400">{{ svc.desc }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Background Workers -->
      <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-white dark:bg-surface-800 p-6">
        <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-100 mb-4">Background Workers</h2>
        <div class="space-y-2">
          <div *ngFor="let w of workers" class="flex items-start gap-2">
            <svg class="w-4 h-4 text-blue-500 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"/></svg>
            <div>
              <span class="text-sm font-mono font-medium text-surface-900 dark:text-surface-100">{{ w.name }}</span>
              <span class="text-sm text-surface-500 dark:text-surface-400"> -- {{ w.desc }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- External Links -->
      <div class="flex flex-wrap gap-3">
        <a href="https://github.com/rivoli-ai/andy-containers" target="_blank"
          class="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700 transition-colors no-underline">
          <svg class="w-4 h-4" viewBox="0 0 24 24" fill="currentColor"><path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z"/></svg>
          GitHub Repository
        </a>
        <a href="https://localhost:4200/swagger" target="_blank"
          class="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-lg border border-surface-300 dark:border-surface-600 text-surface-700 dark:text-surface-300 bg-white dark:bg-surface-800 hover:bg-surface-50 dark:hover:bg-surface-700 transition-colors no-underline">
          <svg class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4"/></svg>
          Swagger API
        </a>
      </div>
    </div>
  `,
})
export class DocsComponent {
  docs = [
    { title: 'Getting Started', description: 'Set up your development environment with Docker Compose', url: 'https://github.com/rivoli-ai/andy-containers/blob/main/docs/getting-started.md', icon: 'M13 10V3L4 14h7v7l9-11h-7z' },
    { title: 'Docker Setup', description: 'Ports, volumes, certificates, and service configuration', url: 'https://github.com/rivoli-ai/andy-containers/blob/main/docs/docker-setup.md', icon: 'M5 12h14M5 12a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v4a2 2 0 01-2 2M5 12a2 2 0 00-2 2v4a2 2 0 002 2h14a2 2 0 002-2v-4a2 2 0 00-2-2m-2-4h.01M17 16h.01' },
    { title: 'Architecture', description: 'System design, domain model, providers, and background workers', url: 'https://github.com/rivoli-ai/andy-containers/blob/main/docs/ARCHITECTURE.md', icon: 'M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10' },
    { title: 'Security', description: 'Authentication, RBAC permissions, API key encryption', url: 'https://github.com/rivoli-ai/andy-containers/blob/main/docs/SECURITY.md', icon: 'M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z' },
    { title: 'CLI Reference', description: 'Command-line tool with OAuth device flow authentication', url: 'https://github.com/rivoli-ai/andy-containers/blob/main/docs/cli-reference.md', icon: 'M8 9l3 3-3 3m5 0h3M5 20h14a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z' },
    { title: 'API Reference', description: 'REST API endpoints, MCP tools, and WebSocket terminal', url: 'https://github.com/rivoli-ai/andy-containers/blob/main/docs/api-reference.md', icon: 'M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4' },
  ];

  features = [
    { name: 'Container Lifecycle', desc: 'Create, start, stop, destroy, live resize' },
    { name: '12 Templates', desc: '4 VNC desktop + 8 CLI-based development environments' },
    { name: '10 Code Assistants', desc: 'Claude Code, Aider, OpenCode, Codex CLI, and more' },
    { name: 'Web Terminal', desc: 'xterm.js + tmux, 18 themes, per-container persistence' },
    { name: 'VNC Desktop', desc: 'XFCE4 + noVNC with HTTPS, embedded in UI' },
    { name: 'Multi-Provider API Keys', desc: 'Fallback chain across providers' },
    { name: 'Non-Root Containers', desc: 'Username derived from JWT claims' },
    { name: 'Image Build Tracking', desc: 'Build status, metadata, rebuild from UI' },
    { name: 'Git Integration', desc: 'Multi-repo cloning with credential management' },
    { name: 'CLI Tool', desc: 'OAuth device flow, full container management' },
    { name: 'MCP Tools', desc: 'Claude Desktop and Cursor integration' },
    { name: 'Auth & RBAC', desc: 'OAuth 2.0/OIDC + per-endpoint permissions' },
  ];

  services = [
    { name: 'Frontend', url: 'https://localhost:4200', desc: 'Angular 18 web UI' },
    { name: 'API (HTTPS)', url: 'https://localhost:5200', desc: 'REST / MCP API' },
    { name: 'PostgreSQL', url: 'localhost:5434', desc: 'Database (postgres:16-alpine)' },
    { name: 'Andy Auth', url: 'https://localhost:5001', desc: 'OAuth 2.0 / OIDC server' },
    { name: 'Andy RBAC API', url: 'https://localhost:7003', desc: 'RBAC permission server' },
    { name: 'Andy RBAC Web', url: 'https://localhost:5180', desc: 'RBAC admin UI' },
  ];

  workers = [
    { name: 'ContainerProvisioningWorker', desc: 'Channel-based queue for container creation and setup' },
    { name: 'ContainerStatusSyncWorker', desc: 'Periodic sync of container state with Docker' },
    { name: 'ProviderHealthCheckWorker', desc: 'Periodic health checks on infrastructure providers' },
    { name: 'ContainerScreenshotWorker', desc: 'Captures tmux terminal content for thumbnails' },
    { name: 'ImageBuildWorker', desc: 'Tracks and manages custom image build status' },
  ];
}
