import { Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewInit, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Container } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';
import { Terminal } from '@xterm/xterm';
import { WebglAddon } from '@xterm/addon-webgl';
import { WebLinksAddon } from '@xterm/addon-web-links';

@Component({
  selector: 'app-container-terminal',
  standalone: true,
  imports: [CommonModule, RouterLink, StatusBadgeComponent],
  template: `
    <div #page class="terminal-page" [class.fullscreen]="isFullscreen">
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
          <button (click)="toggleCheatsheet()" class="header-btn" [class.active]="showCheatsheet">tmux help</button>
          <button *ngIf="!connected && !connecting" (click)="connect()" class="header-btn">Reconnect</button>
          <button (click)="toggleFullscreen()" class="header-btn"
                  [title]="isFullscreen ? 'Exit full screen (Esc)' : 'Full screen (F11)'">
            {{ isFullscreen ? 'Exit' : 'Full screen' }}
          </button>
        </div>
      </div>

      <div *ngIf="showCheatsheet" #cheatsheetEl class="cheatsheet">
        <span class="text-gray-500 font-semibold">tmux (prefix: Ctrl+B):</span>
        <kbd>d</kbd> detach
        <kbd>c</kbd> new window
        <kbd>n/p</kbd> next/prev
        <kbd>%</kbd> split vert
        <kbd>"</kbd> split horiz
        <kbd>arrow</kbd> switch pane
        <kbd>[</kbd> scroll (q exit)
        <kbd>z</kbd> zoom pane &middot;
        <kbd>Ctrl+D</kbd> close
      </div>

      <div #terminalContainer class="terminal-container"></div>
    </div>
  `,
  styles: [`
    :host { display: block; overflow: hidden; background: #f6f8fa; }
    .terminal-page { position: relative; overflow: hidden; }
    .terminal-page.fullscreen { position: fixed; inset: 0; z-index: 9999; background: #161b22; }
    .terminal-header {
      display: flex; align-items: center; justify-content: space-between;
      padding: 6px 16px; border-bottom: 1px solid #21262d; background: #161b22;
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
      display: inline-flex; align-items: center; white-space: nowrap;
    }
    .header-btn:hover { color: #e6edf3; border-color: #484f58; }
    .header-btn.active { color: #58a6ff; border-color: #1f6feb; background: rgba(31,111,235,0.1); }
    .cheatsheet {
      padding: 6px 16px; background: #0d1117; border-bottom: 1px solid #21262d;
      font-size: 12px; color: #8b949e; white-space: nowrap; overflow-x: auto;
    }
    .cheatsheet kbd {
      font-family: inherit; padding: 1px 4px; border-radius: 3px; margin: 0 2px;
      background: rgba(110,118,129,0.2); color: #58a6ff; font-size: 11px;
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
  private ws: WebSocket | null = null;
  private resizeListener: (() => void) | null = null;
  // Measured character dimensions
  private charWidth = 0;
  private charHeight = 0;

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
    this.measureCharSize();
    const { cols, rows } = this.calcDimensions();
    this.initTerminal(cols, rows);
    this.connect();

    this.resizeListener = () => {
      // On window resize, we can't resize the PTY so just re-layout
      this.layoutTerminal();
    };
    window.addEventListener('resize', this.resizeListener);
  }

  ngOnDestroy(): void {
    if (this.resizeListener) window.removeEventListener('resize', this.resizeListener);
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
    if (event.key === 'F11') { event.preventDefault(); this.toggleFullscreen(); }
    if (event.key === 'Escape' && this.isFullscreen) { this.toggleFullscreen(); }
  }

  toggleFullscreen(): void {
    this.isFullscreen = !this.isFullscreen;
    setTimeout(() => this.layoutTerminal(), 0);
  }

  toggleCheatsheet(): void {
    this.showCheatsheet = !this.showCheatsheet;
    setTimeout(() => this.layoutTerminal(), 0);
  }

  /**
   * Measure the actual character cell size by rendering a test span.
   */
  private measureCharSize(): void {
    const span = document.createElement('span');
    span.style.fontFamily = "'JetBrains Mono', 'Fira Code', 'Cascadia Code', Menlo, Monaco, 'Courier New', monospace";
    span.style.fontSize = '16px';
    span.style.lineHeight = '1.2';
    span.style.position = 'absolute';
    span.style.visibility = 'hidden';
    span.style.whiteSpace = 'pre';
    span.textContent = 'WWWWWWWWWW'; // 10 chars
    document.body.appendChild(span);
    this.charWidth = span.offsetWidth / 10;
    this.charHeight = span.offsetHeight;
    document.body.removeChild(span);

    // Fallback if measurement failed
    if (this.charWidth < 1) this.charWidth = 9.6;
    if (this.charHeight < 1) this.charHeight = 19;
  }

  /**
   * Calculate cols/rows from available pixel space.
   */
  private calcDimensions(): { cols: number; rows: number; availWidth: number; availHeight: number } {
    const navbarHeight = this.isFullscreen ? 0 : 64;
    const pageHeight = this.isFullscreen ? window.innerHeight : (window.innerHeight - navbarHeight);
    const headerHeight = this.headerEl?.nativeElement.offsetHeight ?? 36;
    const cheatsheetHeight = this.showCheatsheet && this.cheatsheetEl
      ? this.cheatsheetEl.nativeElement.offsetHeight : 0;
    const padding = 8; // 4px padding on each side

    const availWidth = window.innerWidth - padding * 2;
    const availHeight = pageHeight - headerHeight - cheatsheetHeight - padding * 2;

    const cols = Math.max(40, Math.floor(availWidth / this.charWidth));
    const rows = Math.max(10, Math.floor(availHeight / this.charHeight));

    return { cols, rows, availWidth, availHeight };
  }

  /**
   * Set the page and terminal container to exact pixel sizes.
   */
  private layoutTerminal(): void {
    const navbarHeight = this.isFullscreen ? 0 : 64;
    const pageHeight = this.isFullscreen ? window.innerHeight : (window.innerHeight - navbarHeight);
    this.page.nativeElement.style.height = pageHeight + 'px';

    const { availHeight } = this.calcDimensions();
    this.terminalContainer.nativeElement.style.height = Math.max(100, availHeight) + 'px';
  }

  private initTerminal(cols: number, rows: number): void {
    // Set explicit pixel sizes before creating the terminal
    this.layoutTerminal();

    this.terminal = new Terminal({
      cursorBlink: true,
      fontSize: 16,
      lineHeight: 1.2,
      fontFamily: "'JetBrains Mono', 'Fira Code', 'Cascadia Code', Menlo, Monaco, 'Courier New', monospace",
      cols,
      rows,
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

    this.terminal.loadAddon(new WebLinksAddon());
    this.terminal.open(this.terminalContainer.nativeElement);

    // Try WebGL renderer for hardware acceleration, fall back to default
    try {
      this.terminal.loadAddon(new WebglAddon());
    } catch (e) {
      console.warn('WebGL renderer not available, using default', e);
    }

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

    const msg = this.hasConnectedBefore ? 'Reconnecting to session...' : 'Connecting to container...';
    this.terminal.writeln(`\x1b[33m${msg}\x1b[0m`);

    this.ws = new WebSocket(wsUrl);
    this.ws.binaryType = 'arraybuffer';

    this.ws.onopen = () => {
      // Send exact terminal dimensions — server creates PTY with matching size
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
