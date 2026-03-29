import { Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewInit, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Container } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';
import { ContainerStatsBarComponent } from '../../../shared/components/container-stats-bar/container-stats-bar.component';
import { UptimePipe } from '../../../shared/pipes/uptime.pipe';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { WebglAddon } from '@xterm/addon-webgl';
import { WebLinksAddon } from '@xterm/addon-web-links';

@Component({
  selector: 'app-container-terminal',
  standalone: true,
  imports: [CommonModule, RouterLink, StatusBadgeComponent, ContainerStatsBarComponent, UptimePipe],
  template: `
    <div class="terminal-page" [class.fullscreen]="isFullscreen">
      <div class="terminal-header">
        <div class="flex items-center gap-3">
          <a *ngIf="!isFullscreen" [routerLink]="['/containers', containerId]" class="text-gray-400 hover:text-gray-200">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
          </a>
          <span class="text-white font-medium">{{ container?.name || 'Terminal' }}</span>
          <app-status-badge *ngIf="container" [status]="container.status"></app-status-badge>
          <span *ngIf="connected" class="badge-connected">Connected</span>
          <span *ngIf="connecting" class="badge-connecting">Connecting...</span>
          <span *ngIf="!connected && !connecting && error" class="badge-error">Disconnected</span>
          <span *ngIf="connected && container?.startedAt" class="uptime-badge">{{ container?.startedAt | uptime }}</span>
        </div>
        <div class="flex items-center gap-2">
          <app-container-stats-bar [containerId]="containerId" [isRunning]="connected" variant="terminal-overlay"></app-container-stats-bar>
          <span class="header-divider"></span>
          <button (click)="decreaseFontSize()" class="header-btn" title="Decrease font (Ctrl+-)">A-</button>
          <span class="font-size-label">{{ fontSize }}px</span>
          <button (click)="increaseFontSize()" class="header-btn" title="Increase font (Ctrl+=)">A+</button>
          <button *ngIf="connected" (click)="resetColors()" class="header-btn" title="Reset terminal colors">Reset</button>
          <button *ngIf="!connected && !connecting" (click)="connect()" class="header-btn">Reconnect</button>
          <button (click)="toggleFullscreen()" class="header-btn"
                  [title]="isFullscreen ? 'Exit full screen (Esc)' : 'Full screen (F11)'">
            {{ isFullscreen ? 'Exit' : 'Full screen' }}
          </button>
        </div>
      </div>

      <div #terminalContainer class="terminal-container"></div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      /* Break out of parent <main class="p-8"> padding (2rem = 32px each side) */
      margin: -2rem;
      /* Width: fill the main area including the negated padding */
      width: calc(100% + 4rem);
      /* Height: viewport minus header (64px) */
      height: calc(100vh - 64px);
      overflow: hidden;
      background: #0d1117;
    }
    .terminal-page {
      display: flex;
      flex-direction: column;
      height: 100%;
      overflow: hidden;
    }
    .terminal-page.fullscreen {
      position: fixed;
      inset: 0;
      z-index: 9999;
      background: #0d1117;
      height: 100vh;
    }
    .terminal-header {
      flex: 0 0 auto;
      display: flex; align-items: center; justify-content: space-between;
      padding: 8px 16px; border-bottom: 1px solid #21262d; background: #161b22;
      font-size: 14px;
    }
    .badge-connected, .badge-connecting, .badge-error {
      display: inline-flex; align-items: center;
      padding: 2px 8px; border-radius: 4px; font-size: 13px; font-weight: 500;
    }
    .uptime-badge {
      display: inline-flex; align-items: center;
      padding: 2px 8px; border-radius: 4px; font-size: 13px; font-weight: 500;
      color: #8b949e;
    }
    .badge-connected { background: rgba(63,185,80,0.15); color: #3fb950; }
    .badge-connecting { background: rgba(210,153,34,0.15); color: #d29922; }
    .badge-error { background: rgba(255,123,114,0.15); color: #ff7b72; }
    .header-btn {
      font-size: 14px; color: #8b949e; padding: 4px 10px; border-radius: 4px;
      border: 1px solid #30363d; background: transparent; cursor: pointer;
      display: inline-flex; align-items: center; white-space: nowrap;
    }
    .header-btn:hover { color: #e6edf3; border-color: #484f58; }
    .header-btn.active { color: #58a6ff; border-color: #1f6feb; background: rgba(31,111,235,0.1); }
    .header-divider {
      width: 1px; height: 20px; background: #30363d; margin: 0 4px;
    }
    .font-size-label {
      font-size: 12px; color: #8b949e; min-width: 36px; text-align: center;
    }
    .terminal-container {
      flex: 1 1 auto;
      overflow: hidden;
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
  fontSize = 16;
  private readonly minFontSize = 10;
  private readonly maxFontSize = 28;
  private readonly defaultFontSize = 16;
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
    // Delay connect slightly so the DOM has fully laid out and FitAddon
    // calculates correct cols/rows from the actual container pixel size
    setTimeout(() => {
      this.fitAddon.fit();
      this.connect();
    }, 50);
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
    if (event.key === 'F11') { event.preventDefault(); this.toggleFullscreen(); }
    if (event.key === 'Escape' && this.isFullscreen) { this.toggleFullscreen(); }
    // Ctrl+= or Ctrl++ to increase font size
    if (event.ctrlKey && (event.key === '=' || event.key === '+')) {
      event.preventDefault(); this.increaseFontSize();
    }
    // Ctrl+- to decrease font size
    if (event.ctrlKey && event.key === '-') {
      event.preventDefault(); this.decreaseFontSize();
    }
    // Ctrl+0 to reset font size
    if (event.ctrlKey && event.key === '0') {
      event.preventDefault(); this.resetFontSize();
    }
  }

  toggleFullscreen(): void {
    this.isFullscreen = !this.isFullscreen;
  }

  increaseFontSize(): void {
    if (this.fontSize < this.maxFontSize) {
      this.fontSize += 2;
      this.applyFontSize();
    }
  }

  decreaseFontSize(): void {
    if (this.fontSize > this.minFontSize) {
      this.fontSize -= 2;
      this.applyFontSize();
    }
  }

  resetFontSize(): void {
    this.fontSize = this.defaultFontSize;
    this.applyFontSize();
  }

  resetColors(): void {
    if (this.ws?.readyState === WebSocket.OPEN) {
      // Send ANSI reset + clear: \ec resets terminal state, \e[0m resets colors,
      // then 'reset' command restores full terminal config
      this.ws.send('reset\n');
    }
  }

  private applyFontSize(): void {
    if (this.terminal) {
      this.terminal.options.fontSize = this.fontSize;
      this.fitAddon.fit();
    }
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

    try {
      this.terminal.loadAddon(new WebglAddon());
    } catch (e) {
      console.warn('WebGL renderer not available, using default', e);
    }

    // Let FitAddon calculate cols/rows from the container's actual pixel size
    this.fitAddon.fit();

    // Re-fit terminal when the container element resizes (fullscreen, window resize)
    this.resizeObserver = new ResizeObserver(() => {
      this.fitAddon.fit();
    });
    this.resizeObserver.observe(this.terminalContainer.nativeElement);

    this.terminal.onData((data) => {
      if (this.ws?.readyState === WebSocket.OPEN) {
        this.ws.send(data);
      }
    });

    // Send resize events to the server so tmux/stty can update
    this.terminal.onResize(({ cols, rows }) => {
      if (this.ws?.readyState === WebSocket.OPEN) {
        this.ws.send(JSON.stringify({ type: 'resize', cols, rows }));
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
      // Send the FitAddon-calculated dimensions to the server
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
