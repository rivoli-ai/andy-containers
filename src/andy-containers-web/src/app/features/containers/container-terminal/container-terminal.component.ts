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
    <div [class]="isFullscreen ? 'fixed inset-0 z-50 flex flex-col' : 'flex flex-col h-[calc(100vh-4rem)]'"
         style="background-color: #1a1a2e;">
      <!-- Header bar -->
      <div class="flex items-center justify-between px-4 py-1.5 border-b border-gray-700 bg-[#16213e] shrink-0">
        <div class="flex items-center gap-3">
          <a *ngIf="!isFullscreen" [routerLink]="['/containers', containerId]" class="text-gray-400 hover:text-gray-200">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
          </a>
          <span class="text-white font-medium text-sm">{{ container?.name || 'Terminal' }}</span>
          <app-status-badge *ngIf="container" [status]="container.status"></app-status-badge>
          <span *ngIf="connected" class="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-green-900/30 text-green-400">Connected</span>
          <span *ngIf="connecting" class="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-yellow-900/30 text-yellow-400">Connecting...</span>
          <span *ngIf="!connected && !connecting && error" class="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-red-900/30 text-red-400">Disconnected</span>
        </div>
        <div class="flex items-center gap-2">
          <button (click)="showCheatsheet = !showCheatsheet"
                  class="text-xs px-2 py-1 rounded border"
                  [class]="showCheatsheet ? 'text-cyan-400 border-cyan-600 bg-cyan-900/20' : 'text-gray-400 hover:text-gray-200 border-gray-600 hover:border-gray-500'">
            tmux help
          </button>
          <button *ngIf="!connected && !connecting" (click)="connect()"
                  class="text-xs text-gray-400 hover:text-gray-200 px-2 py-1 rounded border border-gray-600 hover:border-gray-500">
            Reconnect
          </button>
          <button (click)="toggleFullscreen()"
                  class="text-xs text-gray-400 hover:text-gray-200 px-2 py-1 rounded border border-gray-600 hover:border-gray-500"
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
      <div *ngIf="showCheatsheet" class="px-4 py-2 bg-[#0d1b2a] border-b border-gray-700 text-xs text-gray-400 shrink-0">
        <div class="flex flex-wrap gap-x-6 gap-y-1">
          <span class="text-gray-500 font-medium">tmux shortcuts (prefix: Ctrl+B)</span>
          <span><kbd class="text-cyan-400">Ctrl+B</kbd> <kbd class="text-cyan-400">d</kbd> detach</span>
          <span><kbd class="text-cyan-400">Ctrl+B</kbd> <kbd class="text-cyan-400">c</kbd> new window</span>
          <span><kbd class="text-cyan-400">Ctrl+B</kbd> <kbd class="text-cyan-400">n</kbd>/<kbd class="text-cyan-400">p</kbd> next/prev window</span>
          <span><kbd class="text-cyan-400">Ctrl+B</kbd> <kbd class="text-cyan-400">%</kbd> split vertical</span>
          <span><kbd class="text-cyan-400">Ctrl+B</kbd> <kbd class="text-cyan-400">"</kbd> split horizontal</span>
          <span><kbd class="text-cyan-400">Ctrl+B</kbd> <kbd class="text-cyan-400">arrow</kbd> switch pane</span>
          <span><kbd class="text-cyan-400">Ctrl+B</kbd> <kbd class="text-cyan-400">[</kbd> scroll mode (q to exit)</span>
          <span><kbd class="text-cyan-400">Ctrl+B</kbd> <kbd class="text-cyan-400">z</kbd> zoom pane</span>
          <span><kbd class="text-cyan-400">Ctrl+D</kbd> close pane/window</span>
        </div>
      </div>

      <!-- Terminal -->
      <div #terminalContainer class="flex-1 min-h-0 overflow-hidden"></div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    ::ng-deep .xterm { height: 100%; padding: 4px; }
    ::ng-deep .xterm-viewport { overflow-y: auto !important; }
    kbd { font-family: inherit; padding: 1px 4px; border-radius: 3px; background: rgba(255,255,255,0.08); }
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
    // Refit terminal after layout change
    setTimeout(() => {
      this.fitAddon.fit();
      this.sendResize();
    }, 50);
  }

  private initTerminal(): void {
    this.fitAddon = new FitAddon();

    this.terminal = new Terminal({
      cursorBlink: true,
      fontSize: 15,
      lineHeight: 1.15,
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
      this.sendResize();
    });
    this.resizeObserver.observe(this.terminalContainer.nativeElement);

    // Send keystrokes to WebSocket
    this.terminal.onData((data) => {
      if (this.ws?.readyState === WebSocket.OPEN) {
        this.ws.send(data);
      }
    });

    this.terminal.onResize(() => {
      this.sendResize();
    });
  }

  connect(): void {
    if (this.connecting || this.connected) return;
    this.connecting = true;
    this.error = '';

    // Build WebSocket URL — use same host (works through Angular proxy or direct)
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
      // Send actual terminal size immediately
      this.sendResize();
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

  private sendResize(): void {
    if (this.ws?.readyState === WebSocket.OPEN) {
      const cols = this.terminal.cols;
      const rows = this.terminal.rows;
      // Send resize escape sequence
      this.ws.send(`\x1b[R${cols};${rows}`);
    }
  }
}
