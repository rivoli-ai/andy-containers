import { Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewInit, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
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
  imports: [CommonModule, FormsModule, RouterLink, StatusBadgeComponent, ContainerStatsBarComponent, UptimePipe],
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
          <select [(ngModel)]="currentThemeName" (ngModelChange)="applyTheme($event)" class="theme-select" title="Terminal theme"
            [style.background]="getThemeBg(currentThemeName)" [style.color]="getThemeFg(currentThemeName)">
            <option *ngFor="let t of themeNames" [value]="t"
              [style.background]="getThemeBg(t)" [style.color]="getThemeFg(t)">{{ t }}</option>
          </select>
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
    .theme-select {
      font-size: 13px; color: #e6edf3; padding: 3px 8px; border-radius: 4px;
      border: 1px solid #30363d; background: #161b22; cursor: pointer;
      outline: none;
    }
    .theme-select:hover { border-color: #484f58; }
    .theme-select:focus { border-color: #1f6feb; }
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
  static readonly THEMES: Record<string, any> = {
    'GitHub Dark': {
      background: '#0d1117', foreground: '#e6edf3', cursor: '#e6edf3', selectionBackground: '#264f78',
      black: '#0d1117', red: '#ff7b72', green: '#3fb950', yellow: '#d29922',
      blue: '#58a6ff', magenta: '#bc8cff', cyan: '#39d353', white: '#e6edf3',
      brightBlack: '#484f58', brightRed: '#ffa198', brightGreen: '#56d364', brightYellow: '#e3b341',
      brightBlue: '#79c0ff', brightMagenta: '#d2a8ff', brightCyan: '#56d364', brightWhite: '#ffffff',
    },
    'Dracula': {
      background: '#282a36', foreground: '#f8f8f2', cursor: '#f8f8f2', selectionBackground: '#44475a',
      black: '#21222c', red: '#ff5555', green: '#50fa7b', yellow: '#f1fa8c',
      blue: '#bd93f9', magenta: '#ff79c6', cyan: '#8be9fd', white: '#f8f8f2',
      brightBlack: '#6272a4', brightRed: '#ff6e6e', brightGreen: '#69ff94', brightYellow: '#ffffa5',
      brightBlue: '#d6acff', brightMagenta: '#ff92df', brightCyan: '#a4ffff', brightWhite: '#ffffff',
    },
    'Monokai': {
      background: '#272822', foreground: '#f8f8f2', cursor: '#f8f8f2', selectionBackground: '#49483e',
      black: '#272822', red: '#f92672', green: '#a6e22e', yellow: '#f4bf75',
      blue: '#66d9ef', magenta: '#ae81ff', cyan: '#a1efe4', white: '#f8f8f2',
      brightBlack: '#75715e', brightRed: '#f92672', brightGreen: '#a6e22e', brightYellow: '#f4bf75',
      brightBlue: '#66d9ef', brightMagenta: '#ae81ff', brightCyan: '#a1efe4', brightWhite: '#f9f8f5',
    },
    'Solarized Dark': {
      background: '#002b36', foreground: '#839496', cursor: '#839496', selectionBackground: '#073642',
      black: '#073642', red: '#dc322f', green: '#859900', yellow: '#b58900',
      blue: '#268bd2', magenta: '#d33682', cyan: '#2aa198', white: '#eee8d5',
      brightBlack: '#586e75', brightRed: '#cb4b16', brightGreen: '#586e75', brightYellow: '#657b83',
      brightBlue: '#839496', brightMagenta: '#6c71c4', brightCyan: '#93a1a1', brightWhite: '#fdf6e3',
    },
    'Nord': {
      background: '#2e3440', foreground: '#d8dee9', cursor: '#d8dee9', selectionBackground: '#434c5e',
      black: '#3b4252', red: '#bf616a', green: '#a3be8c', yellow: '#ebcb8b',
      blue: '#81a1c1', magenta: '#b48ead', cyan: '#88c0d0', white: '#e5e9f0',
      brightBlack: '#4c566a', brightRed: '#bf616a', brightGreen: '#a3be8c', brightYellow: '#ebcb8b',
      brightBlue: '#81a1c1', brightMagenta: '#b48ead', brightCyan: '#8fbcbb', brightWhite: '#eceff4',
    },
    'One Dark': {
      background: '#282c34', foreground: '#abb2bf', cursor: '#528bff', selectionBackground: '#3e4451',
      black: '#282c34', red: '#e06c75', green: '#98c379', yellow: '#e5c07b',
      blue: '#61afef', magenta: '#c678dd', cyan: '#56b6c2', white: '#abb2bf',
      brightBlack: '#5c6370', brightRed: '#e06c75', brightGreen: '#98c379', brightYellow: '#e5c07b',
      brightBlue: '#61afef', brightMagenta: '#c678dd', brightCyan: '#56b6c2', brightWhite: '#ffffff',
    },
    'Catppuccin Mocha': {
      background: '#1e1e2e', foreground: '#cdd6f4', cursor: '#f5e0dc', selectionBackground: '#45475a',
      black: '#45475a', red: '#f38ba8', green: '#a6e3a1', yellow: '#f9e2af',
      blue: '#89b4fa', magenta: '#f5c2e7', cyan: '#94e2d5', white: '#bac2de',
      brightBlack: '#585b70', brightRed: '#f38ba8', brightGreen: '#a6e3a1', brightYellow: '#f9e2af',
      brightBlue: '#89b4fa', brightMagenta: '#f5c2e7', brightCyan: '#94e2d5', brightWhite: '#a6adc8',
    },
    'Gruvbox Dark': {
      background: '#282828', foreground: '#ebdbb2', cursor: '#ebdbb2', selectionBackground: '#504945',
      black: '#282828', red: '#cc241d', green: '#98971a', yellow: '#d79921',
      blue: '#458588', magenta: '#b16286', cyan: '#689d6a', white: '#a89984',
      brightBlack: '#928374', brightRed: '#fb4934', brightGreen: '#b8bb26', brightYellow: '#fabd2f',
      brightBlue: '#83a598', brightMagenta: '#d3869b', brightCyan: '#8ec07c', brightWhite: '#ebdbb2',
    },
    // Light themes
    'Solarized Light': {
      background: '#fdf6e3', foreground: '#657b83', cursor: '#657b83', selectionBackground: '#eee8d5',
      black: '#073642', red: '#dc322f', green: '#859900', yellow: '#b58900',
      blue: '#268bd2', magenta: '#d33682', cyan: '#2aa198', white: '#eee8d5',
      brightBlack: '#586e75', brightRed: '#cb4b16', brightGreen: '#586e75', brightYellow: '#657b83',
      brightBlue: '#839496', brightMagenta: '#6c71c4', brightCyan: '#93a1a1', brightWhite: '#fdf6e3',
    },
    'GitHub Light': {
      background: '#ffffff', foreground: '#24292f', cursor: '#24292f', selectionBackground: '#dafbe1',
      black: '#24292f', red: '#cf222e', green: '#116329', yellow: '#4d2d00',
      blue: '#0969da', magenta: '#8250df', cyan: '#1b7c83', white: '#6e7781',
      brightBlack: '#57606a', brightRed: '#a40e26', brightGreen: '#1a7f37', brightYellow: '#633c01',
      brightBlue: '#218bff', brightMagenta: '#a475f9', brightCyan: '#3192aa', brightWhite: '#8c959f',
    },
    'Catppuccin Latte': {
      background: '#eff1f5', foreground: '#4c4f69', cursor: '#dc8a78', selectionBackground: '#ccd0da',
      black: '#5c5f77', red: '#d20f39', green: '#40a02b', yellow: '#df8e1d',
      blue: '#1e66f5', magenta: '#ea76cb', cyan: '#179299', white: '#acb0be',
      brightBlack: '#6c6f85', brightRed: '#d20f39', brightGreen: '#40a02b', brightYellow: '#df8e1d',
      brightBlue: '#1e66f5', brightMagenta: '#ea76cb', brightCyan: '#179299', brightWhite: '#bcc0cc',
    },
    'One Light': {
      background: '#fafafa', foreground: '#383a42', cursor: '#526fff', selectionBackground: '#e5e5e6',
      black: '#383a42', red: '#e45649', green: '#50a14f', yellow: '#c18401',
      blue: '#4078f2', magenta: '#a626a4', cyan: '#0184bc', white: '#a0a1a7',
      brightBlack: '#696c77', brightRed: '#e45649', brightGreen: '#50a14f', brightYellow: '#c18401',
      brightBlue: '#4078f2', brightMagenta: '#a626a4', brightCyan: '#0184bc', brightWhite: '#ffffff',
    },
  };
  @ViewChild('terminalContainer') terminalContainer!: ElementRef<HTMLDivElement>;

  containerId = '';
  container: Container | null = null;
  connected = false;
  connecting = false;
  error = '';
  isFullscreen = false;
  fontSize = 16;
  currentThemeName = 'GitHub Dark';
  themeNames = Object.keys(ContainerTerminalComponent.THEMES);
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
    this.currentThemeName = localStorage.getItem('andy.terminalTheme') || 'GitHub Dark';
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

  getThemeBg(name: string): string {
    return ContainerTerminalComponent.THEMES[name]?.background || '#0d1117';
  }

  getThemeFg(name: string): string {
    return ContainerTerminalComponent.THEMES[name]?.foreground || '#e6edf3';
  }

  applyTheme(name: string): void {
    const theme = ContainerTerminalComponent.THEMES[name];
    if (theme && this.terminal) {
      this.terminal.options.theme = theme;
      this.currentThemeName = name;
      localStorage.setItem('andy.terminalTheme', name);
      // Update page background to match
      const host = this.terminalContainer?.nativeElement?.closest('.terminal-page') as HTMLElement;
      if (host) host.style.background = theme.background;
    }
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

    const theme = ContainerTerminalComponent.THEMES[this.currentThemeName] || ContainerTerminalComponent.THEMES['GitHub Dark'];
    this.terminal = new Terminal({
      cursorBlink: true,
      fontSize: this.fontSize,
      lineHeight: 1.2,
      fontFamily: "'JetBrains Mono', 'Fira Code', 'Cascadia Code', Menlo, Monaco, 'Courier New', monospace",
      theme,
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
