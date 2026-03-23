import { Component, OnInit, OnDestroy, ViewChild, ElementRef, AfterViewChecked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ContainersApiService } from '../../../core/services/api.service';
import { Container } from '../../../core/models';
import { StatusBadgeComponent } from '../../../shared/components/status-badge/status-badge.component';

interface TerminalEntry {
  type: 'command' | 'stdout' | 'stderr' | 'exit';
  text: string;
  cwd?: string;
}

@Component({
  selector: 'app-container-terminal',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, StatusBadgeComponent],
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
        </div>
        <button (click)="clearOutput()" class="text-xs text-gray-400 hover:text-gray-200 px-2 py-1 rounded border border-gray-600 hover:border-gray-500">
          Clear
        </button>
      </div>

      <!-- Output area -->
      <div #outputArea class="flex-1 overflow-y-auto px-4 py-2 font-mono text-sm" (click)="focusInput()">
        <div *ngFor="let entry of output" class="leading-relaxed">
          <div *ngIf="entry.type === 'command'" class="flex gap-2">
            <span class="text-cyan-400">{{ entry.cwd || '~' }} $</span>
            <span class="text-white">{{ entry.text }}</span>
          </div>
          <pre *ngIf="entry.type === 'stdout'" class="text-gray-200 whitespace-pre-wrap m-0">{{ entry.text }}</pre>
          <pre *ngIf="entry.type === 'stderr'" class="text-red-400 whitespace-pre-wrap m-0">{{ entry.text }}</pre>
          <div *ngIf="entry.type === 'exit'" class="text-orange-400 text-xs">exit code: {{ entry.text }}</div>
        </div>
      </div>

      <!-- Input line -->
      <div class="flex items-center gap-2 px-4 py-3 border-t border-gray-700 bg-[#16213e]">
        <span class="text-cyan-400 font-mono text-sm">{{ cwd }} $</span>
        <input #cmdInput type="text" [(ngModel)]="currentCommand"
          (keydown.enter)="executeCommand()"
          (keydown.arrowUp)="historyUp($event)"
          (keydown.arrowDown)="historyDown($event)"
          (keydown)="onKeydown($event)"
          class="flex-1 bg-transparent border-none outline-none text-white font-mono text-sm caret-white"
          placeholder="Enter command..."
          [disabled]="running"
          autocomplete="off" spellcheck="false" />
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    input::placeholder { color: #4a5568; }
  `],
})
export class ContainerTerminalComponent implements OnInit, AfterViewChecked {
  @ViewChild('outputArea') outputArea!: ElementRef<HTMLDivElement>;
  @ViewChild('cmdInput') cmdInput!: ElementRef<HTMLInputElement>;

  containerId = '';
  container: Container | null = null;
  output: TerminalEntry[] = [];
  currentCommand = '';
  running = false;
  cwd = '~';

  private commandHistory: string[] = [];
  private historyIndex = -1;
  private shouldScroll = false;

  constructor(
    private api: ContainersApiService,
    private route: ActivatedRoute,
  ) {}

  ngOnInit(): void {
    this.containerId = this.route.snapshot.paramMap.get('id')!;
    this.api.getContainer(this.containerId).subscribe({
      next: (c) => { this.container = c; },
    });
    setTimeout(() => this.focusInput(), 100);
  }

  ngAfterViewChecked(): void {
    if (this.shouldScroll && this.outputArea) {
      const el = this.outputArea.nativeElement;
      el.scrollTop = el.scrollHeight;
      this.shouldScroll = false;
    }
  }

  focusInput(): void {
    this.cmdInput?.nativeElement?.focus();
  }

  executeCommand(): void {
    const cmd = this.currentCommand.trim();
    if (!cmd || this.running) return;

    this.commandHistory.push(cmd);
    this.historyIndex = this.commandHistory.length;

    this.output.push({ type: 'command', text: cmd, cwd: this.cwd });
    this.currentCommand = '';
    this.running = true;
    this.shouldScroll = true;

    // Track cd commands for prompt display
    if (cmd.startsWith('cd ')) {
      const target = cmd.substring(3).trim();
      if (target === '~' || target === '') {
        this.cwd = '~';
      } else if (target === '..') {
        const parts = this.cwd.split('/');
        if (parts.length > 1) {
          parts.pop();
          this.cwd = parts.join('/') || '/';
        }
      } else if (target.startsWith('/')) {
        this.cwd = target;
      } else {
        this.cwd = this.cwd === '~' ? `~/${target}` : `${this.cwd}/${target}`;
      }
    }

    this.api.execCommand(this.containerId, cmd).subscribe({
      next: (result) => {
        if (result.stdOut) {
          this.output.push({ type: 'stdout', text: result.stdOut });
        }
        if (result.stdErr) {
          this.output.push({ type: 'stderr', text: result.stdErr });
        }
        if (result.exitCode !== 0) {
          this.output.push({ type: 'exit', text: String(result.exitCode) });
        }
        this.running = false;
        this.shouldScroll = true;
        this.focusInput();
      },
      error: () => {
        this.output.push({ type: 'stderr', text: 'Failed to execute command' });
        this.running = false;
        this.shouldScroll = true;
        this.focusInput();
      },
    });
  }

  historyUp(event: Event): void {
    event.preventDefault();
    if (this.historyIndex > 0) {
      this.historyIndex--;
      this.currentCommand = this.commandHistory[this.historyIndex];
    }
  }

  historyDown(event: Event): void {
    event.preventDefault();
    if (this.historyIndex < this.commandHistory.length - 1) {
      this.historyIndex++;
      this.currentCommand = this.commandHistory[this.historyIndex];
    } else {
      this.historyIndex = this.commandHistory.length;
      this.currentCommand = '';
    }
  }

  onKeydown(event: KeyboardEvent): void {
    // Ctrl+L to clear
    if (event.ctrlKey && event.key === 'l') {
      event.preventDefault();
      this.clearOutput();
    }
  }

  clearOutput(): void {
    this.output = [];
  }
}
