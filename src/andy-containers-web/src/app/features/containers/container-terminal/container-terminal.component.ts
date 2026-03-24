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
    <div #page class="terminal-page" [class.fullscreen]="isFullscreen">
      <!-- Header bar -->
      <div #header class="terminal-header">
        <div class="flex items-center gap-3">
          <a *ngIf="!isFullscreen" [routerLink]="['/containers', containerId]" class="text-gray-400 hover:text-gray-200">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
          </a>
          <span class="text-white font-medium">{{ container?.name || 'Terminal' }}</span>
          <app-status-badge *ngIf="container" [status]="container.status"></app-status-badge>
          <span *ngIf="connected" class="badge-connected">Connected</span>
          <span *ngIf="connecting" class="badge-connecting">Connecting...</span>
          <span *ngIf="!connected && !connecting && error" class="badge-error">Disconnected</span>
        </div>
        <div class="flex items-center gap-2">
          <button (click)="toggleCheatsheet()" class="header-btn" [class.active]="showCheatsheet">
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
      <div *ngIf="showCheatsheet" #cheatsheetEl class="cheatsheet">
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

      <!-- Terminal: height set explicitly via JS -->
      <div #terminalContainer class="terminal-container"></div>
    </div>
  `,
  styles: [`
    :host { display: block; overflow: hidden; }
    .terminal-page { background: #0d1117; overflow: hidden; }
    .terminal-page.fullscreen { position: fixed; inset: 0; z-index: 9999; }
    .terminal-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 6px 16px;
      border-bottom: 1px solid #21262d;
      background: #161b22;
    }
    .badge-connected, .badge-connecting, .badge-error {
      display: inline-flex; align-items: center;
      padding: 2px 8px; border-radius: 4px; font-size: 12px; font-weight: 500;
    }
    .badge-connected { background: rgba(63,185,80,0.15); color: #3fb950; }
    .badge-connecting { background: rgba(210,153,34,0.15); color: #d29922; }
    .badge-error { background: rgba(255,123,114,0.15); color: #ff7b72; }
    .header-btn {
      font-size: 12px; color: #8b949e; padding: 4px 8px; border-radius: 4px;
      border: 1px solid #30363d; background: transparent; cursor: pointer;
      display: inline-flex; align-items: center;
    }
    .header-btn:hover { color: #e6edf3; border-color: #484f58; }
    .header-btn.active { color: #58a6ff; border-color: #1f6feb; background: rgba(31,111,235,0.1); }
    .cheatsheet {
      padding: 8px 16px; background: #0d1117; border-bottom: 1px solid #21262d;
      font-size: 13px; color: #8b949e;
    }
    .cheatsheet kbd {
      font-family: inherit; padding: 1px 5px; border-radius: 3px;
      background: rgba(110,118,129,0.2); color: #58a6ff; font-size: 12px;
    }
    .terminal-container { overflow: hidden; }
  `],
})
export class ContainerTerminalComponent implements OnInit, AfterViewInit, OnDestroy {
  @ViewChild('page') page!: ElementRef<HTMLDivElement>;
  @ViewChild('header') headerEl!: ElementRef<HTMLDivElement>;
  @ViewChild('cheatsheetEl') cheatsheetEl?: ElementRef<HTMLDivElement>;
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
  private resizeListener: (() => void) | null = null;

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
    this.sizeTerminal();
    this.connect();

    // Resize on window resize
    this.resizeListener = () => this.sizeTerminal();
    window.addEventListener('resize', this.resizeListener);
  }

  ngOnDestroy(): void {
    if (this.resizeListener) {
      window.removeEventListener('resize', this.resizeListener);
    }
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
    setTimeout(() => this.sizeTerminal(), 0);
  }

  toggleCheatsheet(): void {
    this.showCheatsheet = !this.showCheatsheet;
    setTimeout(() => this.sizeTerminal(), 0);
  }

  /**
   * Calculate available height in pixels, set the terminal container to that
   * exact height, then fit xterm inside it. No CSS flex needed.
   */
  private sizeTerminal(): void {
    const navbarHeight = this.isFullscreen ? 0 : 64; // 4rem app navbar
    const pageHeight = this.isFullscreen ? window.innerHeight : (window.innerHeight - navbarHeight);
    const headerHeight = this.headerEl?.nativeElement.offsetHeight ?? 0;
    const cheatsheetHeight = this.showCheatsheet && this.cheatsheetEl
      ? this.cheatsheetEl.nativeElement.offsetHeight : 0;

    // Set page height explicitly
    this.page.nativeElement.style.height = pageHeight + 'px';

    // Terminal gets whatever is left
    const terminalHeight = pageHeight - headerHeight - cheatsheetHeight;
    this.terminalContainer.nativeElement.style.height = terminalHeight + 'px';
    this.terminalContainer.nativeElement.style.width = '100%';

    // Now fit xterm to the pixel-sized container
    this.fitAddon.fit();
  }

  private initTerminal(): void {
    this.fitAddon = new FitAddon();

    this.terminal = new Terminal({
      cursorBlink: true,
      fontSize: 16,
      lineHeight: 1.2,
      fontFamily: "'JetBrains Mono', 'Fira Code', 'Cascadia Code', Menlo, Monaco, 'Courier New', monospace",
      theme: {
        background: '#0d1117',
        foreground: '#e6edf3',
        cursor: '#e6edf3',
        selectionBackground: '#264f78',
        black: '#0d1117',
        red: '#ff7b72',
        green: '#3fb950',
        yellow: '#d29922',
        blue: '#58a6ff',
        magenta: '#bc8cff',
        cyan: '#39d353',
        white: '#e6edf3',
        brightBlack: '#484f58',
        brightRed: '#ffa198',
        brightGreen: '#56d364',
        brightYellow: '#e3b341',
        brightBlue: '#79c0ff',
        brightMagenta: '#d2a8ff',
        brightCyan: '#56d364',
        brightWhite: '#ffffff',
      },
      scrollback: 10000,
      allowProposedApi: true,
    });

    this.terminal.loadAddon(this.fitAddon);
    this.terminal.loadAddon(new WebLinksAddon());
    this.terminal.open(this.terminalContainer.nativeElement);

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

    // Ensure terminal is sized before connecting
    this.sizeTerminal();

    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${protocol}//${location.host}/api/containers/${this.containerId}/terminal`;

    const msg = this.hasConnectedBefore
      ? 'Reconnecting to session...'
      : 'Connecting to container...';
    this.terminal.writeln(`\x1b[33m${msg}\x1b[0m`);

    this.ws = new WebSocket(wsUrl);
    this.ws.binaryType = 'arraybuffer';

    this.ws.onopen = () => {
      // Send terminal dimensions as the first message — the server waits for
      // this before creating the PTY so the sizes match exactly
      const size = { cols: this.terminal.cols, rows: this.terminal.rows };
      this.ws!.send(JSON.stringify(size));

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
