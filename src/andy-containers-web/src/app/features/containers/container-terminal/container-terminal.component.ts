import { Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewInit } from '@angular/core';
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
    <div class="flex flex-col h-[calc(100vh-4rem)]" style="background-color: #1a1a2e;">
      <!-- Header bar -->
      <div class="flex items-center justify-between px-4 py-2 border-b border-gray-700 bg-[#16213e]">
        <div class="flex items-center gap-3">
          <a [routerLink]="['/containers', containerId]" class="text-gray-400 hover:text-gray-200">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path d="M15 19l-7-7 7-7"/></svg>
          </a>
          <span class="text-white font-medium">{{ container?.name || 'Terminal' }}</span>
          <app-status-badge *ngIf="container" [status]="container.status"></app-status-badge>
          <span *ngIf="connected" class="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-green-900/30 text-green-400">Connected</span>
          <span *ngIf="connecting" class="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-yellow-900/30 text-yellow-400">Connecting...</span>
          <span *ngIf="!connected && !connecting && error" class="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-red-900/30 text-red-400">Disconnected</span>
        </div>
        <div class="flex items-center gap-2">
          <button *ngIf="!connected && !connecting" (click)="connect()" class="text-xs text-gray-400 hover:text-gray-200 px-2 py-1 rounded border border-gray-600 hover:border-gray-500">
            Reconnect
          </button>
        </div>
      </div>

      <!-- Terminal -->
      <div #terminalContainer class="flex-1 overflow-hidden p-1"></div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    ::ng-deep .xterm { height: 100%; }
    ::ng-deep .xterm-viewport { overflow-y: auto !important; }
  `],
})
export class ContainerTerminalComponent implements OnInit, AfterViewInit, OnDestroy {
  @ViewChild('terminalContainer') terminalContainer!: ElementRef<HTMLDivElement>;

  containerId = '';
  container: Container | null = null;
  connected = false;
  connecting = false;
  error = '';

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
    this.ws?.close();
    this.terminal?.dispose();
  }

  private initTerminal(): void {
    this.fitAddon = new FitAddon();

    this.terminal = new Terminal({
      cursorBlink: true,
      fontSize: 14,
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

    this.terminal.writeln('\x1b[33mConnecting to container...\x1b[0m');

    this.ws = new WebSocket(wsUrl);
    this.ws.binaryType = 'arraybuffer';

    this.ws.onopen = () => {
      this.connecting = false;
      this.connected = true;
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
      this.terminal.writeln(`\r\n\x1b[33mSession ended (code: ${event.code})\x1b[0m`);
    };

    this.ws.onerror = () => {
      this.connecting = false;
      this.connected = false;
      this.error = 'Connection failed';
      this.terminal.writeln('\r\n\x1b[31mFailed to connect. Is the container running with SSH enabled?\x1b[0m');
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
