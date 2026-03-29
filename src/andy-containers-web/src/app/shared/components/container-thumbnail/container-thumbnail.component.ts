import { Component, Input, OnInit, OnDestroy, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';

@Component({
  selector: 'app-container-thumbnail',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="thumbnail-wrapper" [style.width.px]="width" [style.height.px]="height"
      [style.background]="themeBackground" [class.clickable]="isRunning" (click)="openTerminal()">
      <!-- Loading -->
      <div *ngIf="loading" class="thumbnail-placeholder">
        <div class="animate-pulse bg-surface-700 rounded w-full h-full"></div>
      </div>

      <!-- No preview -->
      <div *ngIf="!loading && !ansiText && isRunning" class="thumbnail-placeholder">
        <span class="text-surface-500 text-xs">No preview</span>
      </div>

      <!-- Not running -->
      <div *ngIf="!loading && !isRunning" class="thumbnail-placeholder">
        <span class="text-surface-600 text-xs">Stopped</span>
      </div>

      <!-- Terminal preview -->
      <div *ngIf="!loading && ansiText" class="thumbnail-terminal" [title]="'Click to open terminal'">
        <pre class="thumbnail-text" [style.color]="themeForeground">{{ ansiText }}</pre>
      </div>
    </div>
  `,
  styles: [`
    .thumbnail-wrapper {
      position: relative;
      overflow: hidden;
      border-radius: 4px;
      border: 1px solid #30363d;
      background: #0d1117;
    }
    .thumbnail-wrapper.clickable {
      cursor: pointer;
      transition: border-color 0.15s, box-shadow 0.15s;
    }
    .thumbnail-wrapper.clickable:hover {
      border-color: #58a6ff;
      box-shadow: 0 0 0 1px #58a6ff;
    }
    .thumbnail-placeholder {
      display: flex;
      align-items: center;
      justify-content: center;
      width: 100%;
      height: 100%;
      background: #161b22;
    }
    .thumbnail-terminal {
      width: 100%;
      height: 100%;
      overflow: hidden;
      cursor: pointer;
    }
    .thumbnail-text {
      margin: 0;
      padding: 2px 4px;
      font-family: 'JetBrains Mono', 'Fira Code', monospace;
      font-size: 3.5px;
      line-height: 1.2;
      color: #e6edf3;
      white-space: pre;
      overflow: hidden;
      background: transparent;
    }
  `],
})
export class ContainerThumbnailComponent implements OnInit, OnDestroy, OnChanges {
  @Input() containerId = '';
  @Input() isRunning = false;
  @Input() size: 'sm' | 'md' | 'lg' = 'sm';

  ansiText: string | null = null;
  capturedAt: string | null = null;
  loading = false;
  private timer: any = null;

  get width(): number {
    return this.size === 'lg' ? 400 : this.size === 'md' ? 240 : 160;
  }
  get height(): number {
    return this.size === 'lg' ? 250 : this.size === 'md' ? 150 : 100;
  }

  private static readonly THEME_COLORS: Record<string, { bg: string; fg: string }> = {
    'GitHub Dark': { bg: '#0d1117', fg: '#e6edf3' },
    'Dracula': { bg: '#282a36', fg: '#f8f8f2' },
    'Monokai': { bg: '#272822', fg: '#f8f8f2' },
    'Solarized Dark': { bg: '#002b36', fg: '#839496' },
    'Nord': { bg: '#2e3440', fg: '#d8dee9' },
    'One Dark': { bg: '#282c34', fg: '#abb2bf' },
    'Catppuccin Mocha': { bg: '#1e1e2e', fg: '#cdd6f4' },
    'Gruvbox Dark': { bg: '#282828', fg: '#ebdbb2' },
    'Ocean Blue': { bg: '#0a1929', fg: '#b2bac2' },
    'Deep Sea': { bg: '#001b2e', fg: '#a8d8ea' },
    'Forest': { bg: '#0b1a0b', fg: '#b5d6b2' },
    'Aurora': { bg: '#0d0f1a', fg: '#c8d6e5' },
    'Midnight Purple': { bg: '#13001a', fg: '#d4b8e0' },
    'Cyberpunk': { bg: '#0a0a1a', fg: '#0ff0fc' },
    'Solarized Light': { bg: '#fdf6e3', fg: '#657b83' },
    'GitHub Light': { bg: '#ffffff', fg: '#24292f' },
    'Catppuccin Latte': { bg: '#eff1f5', fg: '#4c4f69' },
    'One Light': { bg: '#fafafa', fg: '#383a42' },
  };

  get themeBackground(): string {
    const name = localStorage.getItem(`andy.terminalTheme.${this.containerId}`)
      || localStorage.getItem('andy.terminalTheme') || 'GitHub Dark';
    return ContainerThumbnailComponent.THEME_COLORS[name]?.bg || '#0d1117';
  }

  get themeForeground(): string {
    const name = localStorage.getItem(`andy.terminalTheme.${this.containerId}`)
      || localStorage.getItem('andy.terminalTheme') || 'GitHub Dark';
    return ContainerThumbnailComponent.THEME_COLORS[name]?.fg || '#e6edf3';
  }

  constructor(private api: ContainersApiService, private router: Router) {}

  openTerminal(): void {
    if (this.isRunning && this.containerId) {
      this.router.navigate(['/containers', this.containerId, 'terminal']);
    }
  }

  ngOnInit(): void {
    this.startPolling();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['isRunning'] || changes['containerId']) {
      this.stopPolling();
      if (this.isRunning && this.containerId) {
        this.startPolling();
      } else if (!this.isRunning) {
        this.ansiText = null;
      }
    }
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  private startPolling(): void {
    if (!this.isRunning || !this.containerId) return;
    this.loading = true;
    this.fetchScreenshot();
    this.timer = setInterval(() => this.fetchScreenshot(), 30000);
  }

  private stopPolling(): void {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
  }

  private fetchScreenshot(): void {
    this.api.getContainerScreenshot(this.containerId).subscribe({
      next: (s) => {
        this.loading = false;
        if (s.available && s.ansiText) {
          // Strip ANSI escape sequences for the pre-based rendering
          this.ansiText = this.stripAnsi(s.ansiText);
          this.capturedAt = s.capturedAt || null;
        }
      },
      error: () => { this.loading = false; },
    });
  }

  private stripAnsi(text: string): string {
    // Remove ANSI escape sequences for plain text display
    return text.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, '').replace(/\x1b\][^\x07]*\x07/g, '');
  }
}
