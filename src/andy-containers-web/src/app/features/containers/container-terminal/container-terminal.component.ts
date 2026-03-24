import { Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewInit, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Container } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { WebLinksAddon } from '@xterm/addon-web-links';

@Component({
  selector: 'app-container-terminal',
  standalone: true,
  imports: [CommonModule, RouterLink, StatusBadgeComponent],
  template: `
    <div class="terminal-wrapper" [class.fullscreen]="isFullscreen">
      <!-- Header bar -->
      <div class="terminal-header">
        <div class="flex items-center gap-3">
          <a *ngIf="!isFullscreen" [routerLink]="['/containers', containerId]" class="text-gray-400 hover:text-gray-200">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
          </a>
          <span class="text-white font-medium">{{ container?.name || 'Terminal' }}</span>
          <app-status-badge *ngIf="container" [status]="container.status"></app-status-badge>
          <span *ngIf="connected" class="badge bg-green-900/30 text-green-400">Connected</span>
          <span *ngIf="connecting" class="badge bg-yellow-900/30 text-yellow-400">Connecting...</span>
          <span *ngIf="!connected && !connecting && error" class="badge bg-red-900/30 text-red-400">Disconnected</span>
        </div>
        <div class="flex items-center gap-2">
          <button (click)="showCheatsheet = !showCheatsheet"
                  class="header-btn"
                  [class.active]="showCheatsheet">
            tmux help
          </button>
          <button *ngIf="!connected && !connecting" (click)="connect()" class="header-btn">
            Reconnect
          </button>
          <button (click)="toggleFullscreen()" class="header-btn"
                  [title]="isFullscreen ? 'Exit full screen (Esc)' : 'Full screen (F11)'">
            <svg *ngIf="!isFullscreen" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24">
              <path d="M8 3H5a2 2 0 00-2 2v3m18 0V5a2 2 0 00-2-2h-3m0 18h3a2 2 0 002-2v-3M3 16v3a2 2 0 002 2h3"/>
            </svg>
            <svg *ngIf="isFullscreen" class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24">
              <path d="M8 3v3a2 2 0 01-2 2H3m18 0h-3a2 2 0 01-2-2V3m0 18v-3a2 2 0 012-2h3M3 16h3a2 2 0 012 2v3"/>
            </svg>
          </button>
        </div>
      </div>

      <!-- tmux cheatsheet -->
      <div *ngIf="showCheatsheet" class="cheatsheet">
        <div class="flex flex-wrap gap-x-6 gap-y-1">
          <span class="text-gray-500 font-semibold">tmux (prefix: Ctrl+B)</span>
          <span><kbd>Ctrl+B d</kbd> detach</span>
          <span><kbd>Ctrl+B c</kbd> new window</span>
          <span><kbd>Ctrl+B n/p</kbd> next/prev</span>
          <span><kbd>Ctrl+B %</kbd> split vert</span>
          <span><kbd>Ctrl+B "</kbd> split horiz</span>
          <span><kbd>Ctrl+B arrow</kbd> switch pane</span>
          <span><kbd>Ctrl+B [</kbd> scroll (q exit)</span>
          <span><kbd>Ctrl+B z</kbd> zoom pane</span>
          <span><kbd>Ctrl+D</kbd> close</span>
        </div>
      </div>

      <!-- Terminal -->
      <div #terminalContainer class="terminal-container"></div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      height: calc(100vh - 4rem);
      overflow: hidden;
    }
    .terminal-wrapper {
      display: flex;
      flex-direction: column;
      height: 100%;
      background-color: #1a1a2e;
      overflow: hidden;
    }
    .terminal-wrapper.fullscreen {
      position: fixed;
      inset: 0;
      z-index: 9999;
      height: 100vh;
    }
    .terminal-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 6px 16px;
      border-bottom: 1px solid #374151;
      background: #16213e;
      flex-shrink: 0;
    }
    .badge {
      display: inline-flex;
      align-items: center;
      padding: 2px 8px;
      border-radius: 4px;
      font-size: 12px;
      font-weight: 500;
    }
    .header-btn {
      font-size: 12px;
      color: #9ca3af;
      padding: 4px 8px;
      border-radius: 4px;
      border: 1px solid #4b5563;
      background: transparent;
      cursor: pointer;
      display: inline-flex;
      align-items: center;
    }
    .header-btn:hover { color: #e5e7eb; border-color: #6b7280; }
    .header-btn.active { color: #22d3ee; border-color: #0891b2; background: rgba(6,182,212,0.1); }
    .cheatsheet {
      padding: 8px 16px;
      background: #0d1b2a;
      border-bottom: 1px solid #374151;
      font-size: 13px;
      color: #9ca3af;
      flex-shrink: 0;
    }
    .cheatsheet kbd {
      font-family: inherit;
      padding: 1px 5px;
      border-radius: 3px;
      background: rgba(255,255,255,0.1);
      color: #22d3ee;
      font-size: 12px;
    }
    .terminal-container {
      flex: 1;
      min-height: 0;
      overflow: hidden;
    }
    ::ng-deep .terminal-container .xterm {
      height: 100%;
      padding: 4px;
    }
    ::ng-deep .terminal-container .xterm-viewport {
      overflow-y: auto !important;
    }
  `],
})
export class ContainerTerminalComponent implements OnInit, AfterViewInit, OnDestroy {
  @ViewChild('terminalContainer') terminalContainer!: ElementRef<HTMLDivElement>;

  containerId = '';
  container: Container | null = null;
  connected = false;
  connecting = false;
  error = '';
  isFullscreen = false;
  showCheatsheet = false;
  private hasConnectedBefore = false;

  private terminal!: Terminal;
  private fitAddon!: FitAddon;
  private ws: WebSocket | null = null;
  private resizeObserver: ResizeObserver | null = null;

  constructor(
    private api: ContainersApiService,
    private route: ActivatedRoute,
  ) {}

  ngOnInit(): void {
    this.containerId = this.route.snapshot.paramMap.get('id')!;
    this.api.getContainer(this.containerId).subscribe({
      next: (c) => { this.container = c; },
    });
  }

  ngAfterViewInit(): void {
    this.initTerminal();
    this.connect();
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    if (this.ws) {
      this.ws.onmessage = null;
      this.ws.onclose = null;
      this.ws.onerror = null;
      this.ws.close();
      this.ws = null;
    }
    this.terminal?.dispose();
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'F11') {
      event.preventDefault();
      this.toggleFullscreen();
    }
    if (event.key === 'Escape' && this.isFullscreen) {
      this.toggleFullscreen();
    }
  }

  toggleFullscreen(): void {
    this.isFullscreen = !this.isFullscreen;
    setTimeout(() => this.fitAddon.fit(), 50);
  }

  private initTerminal(): void {
    this.fitAddon = new FitAddon();

    this.terminal = new Terminal({
      cursorBlink: true,
      fontSize: 16,
      lineHeight: 1.2,
      letterSpacing: 0,
      fontFamily: "'JetBrains Mono', 'Fira Code', 'Cascadia Code', Menlo, Monaco, 'Courier New', monospace",
      theme: {
        background: '#1a1a2e',
        foreground: '#e0e0e0',
        cursor: '#e0e0e0',
        selectionBackground: '#3a3a5e',
        black: '#1a1a2e',
        red: '#ff6b6b',
        green: '#51cf66',
        yellow: '#ffd43b',
        blue: '#5c7cfa',
        magenta: '#cc5de8',
        cyan: '#22b8cf',
        white: '#e0e0e0',
        brightBlack: '#4a4a6e',
        brightRed: '#ff8787',
        brightGreen: '#69db7c',
        brightYellow: '#ffe066',
        brightBlue: '#748ffc',
        brightMagenta: '#da77f2',
        brightCyan: '#3bc9db',
        brightWhite: '#ffffff',
      },
      scrollback: 10000,
      allowProposedApi: true,
    });

    this.terminal.loadAddon(this.fitAddon);
    this.terminal.loadAddon(new WebLinksAddon());
    this.terminal.open(this.terminalContainer.nativeElement);

    // Fit terminal to container size
    setTimeout(() => this.fitAddon.fit(), 0);

    // Watch for resize
    this.resizeObserver = new ResizeObserver(() => {
      this.fitAddon.fit();
    });
    this.resizeObserver.observe(this.terminalContainer.nativeElement);

    // Send keystrokes to WebSocket
    this.terminal.onData((data) => {
      if (this.ws?.readyState === WebSocket.OPEN) {
        this.ws.send(data);
      }
    });
  }

  connect(): void {
    if (this.connecting || this.connected) return;
    this.connecting = true;
    this.error = '';

    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${protocol}//${location.host}/api/containers/${this.containerId}/terminal`;

    const msg = this.hasConnectedBefore
      ? 'Reconnecting to session...'
      : 'Connecting to container...';
    this.terminal.writeln(`\x1b[33m${msg}\x1b[0m`);

    this.ws = new WebSocket(wsUrl);
    this.ws.binaryType = 'arraybuffer';

    this.ws.onopen = () => {
      this.connecting = false;
      this.connected = true;
      this.hasConnectedBefore = true;
    };

    this.ws.onmessage = (event) => {
      if (event.data instanceof ArrayBuffer) {
        this.terminal.write(new Uint8Array(event.data));
      } else {
        this.terminal.write(event.data);
      }
    };

    this.ws.onclose = (event) => {
      this.connecting = false;
      this.connected = false;
      if (event.code !== 1000) {
        this.terminal.writeln(`\r\n\x1b[33mSession ended (code: ${event.code})\x1b[0m`);
      }
    };

    this.ws.onerror = () => {
      this.connecting = false;
      this.connected = false;
      this.error = 'Connection failed';
      this.terminal.writeln('\r\n\x1b[31mFailed to connect. Is the container running?\x1b[0m');
    };
  }
}
