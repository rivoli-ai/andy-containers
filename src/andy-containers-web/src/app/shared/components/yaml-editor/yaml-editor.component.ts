import {
  Component,
  Input,
  Output,
  EventEmitter,
  ElementRef,
  ViewChild,
  AfterViewInit,
  OnDestroy,
  OnChanges,
  SimpleChanges,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  EditorView,
  keymap,
  lineNumbers,
  highlightActiveLine,
  highlightSpecialChars,
  drawSelection,
} from '@codemirror/view';
import { EditorState } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import {
  syntaxHighlighting,
  defaultHighlightStyle,
  indentOnInput,
  bracketMatching,
  foldGutter,
  foldKeymap,
} from '@codemirror/language';
import { yaml } from '@codemirror/lang-yaml';
import { lintGutter, setDiagnostics } from '@codemirror/lint';
import { oneDark } from '@codemirror/theme-one-dark';

@Component({
  selector: 'app-yaml-editor',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div #editorContainer class="yaml-editor-container" [style.height]="height"></div>
  `,
  styles: [`
    .yaml-editor-container {
      min-height: 200px;
    }
  `],
})
export class YamlEditorComponent implements AfterViewInit, OnDestroy, OnChanges {
  @ViewChild('editorContainer') editorContainer!: ElementRef;

  @Input() value = '';
  @Input() readOnly = false;
  @Input() height = '500px';
  @Input() diagnostics: { line: number; severity: string; message: string }[] = [];

  @Output() valueChange = new EventEmitter<string>();
  @Output() save = new EventEmitter<void>();

  private view: EditorView | null = null;
  private suppressNextChange = false;

  ngAfterViewInit(): void {
    this.initEditor();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['value'] && this.view && !changes['value'].firstChange) {
      const currentValue = this.view.state.doc.toString();
      if (currentValue !== this.value) {
        this.suppressNextChange = true;
        this.view.dispatch({
          changes: { from: 0, to: this.view.state.doc.length, insert: this.value },
        });
      }
    }
    if (changes['diagnostics'] && this.view && !changes['diagnostics'].firstChange) {
      this.updateDiagnostics();
    }
  }

  ngOnDestroy(): void {
    if (this.view) {
      this.view.destroy();
      this.view = null;
    }
  }

  private initEditor(): void {
    const updateListener = EditorView.updateListener.of((update) => {
      if (update.docChanged) {
        if (this.suppressNextChange) {
          this.suppressNextChange = false;
          return;
        }
        if (!this.readOnly) {
          const val = update.state.doc.toString();
          this.valueChange.emit(val);
        }
      }
    });

    const saveKeymap = keymap.of([
      {
        key: 'Mod-s',
        run: () => {
          this.save.emit();
          return true;
        },
      },
    ]);

    const extensions = [
      lineNumbers(),
      highlightActiveLine(),
      highlightSpecialChars(),
      drawSelection(),
      indentOnInput(),
      bracketMatching(),
      foldGutter(),
      history(),
      lintGutter(),
      yaml(),
      oneDark,
      EditorView.lineWrapping,
      syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
      keymap.of([
        ...defaultKeymap,
        ...historyKeymap,
        ...foldKeymap,
        indentWithTab,
      ]),
      updateListener,
      saveKeymap,
      EditorView.theme({
        '&': { height: this.height },
        '.cm-scroller': { overflow: 'auto' },
        '.cm-content': {
          fontFamily: "'SF Mono', 'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
          fontSize: '0.85rem',
        },
        '.cm-gutters': {
          fontFamily: "'SF Mono', 'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
          fontSize: '0.85rem',
        },
      }),
    ];

    if (this.readOnly) {
      extensions.push(EditorState.readOnly.of(true));
      extensions.push(EditorView.editable.of(false));
    }

    const state = EditorState.create({
      doc: this.value || '',
      extensions,
    });

    this.view = new EditorView({
      state,
      parent: this.editorContainer.nativeElement,
    });

    if (this.diagnostics?.length) {
      this.updateDiagnostics();
    }
  }

  private updateDiagnostics(): void {
    if (!this.view) return;
    const cmDiags = (this.diagnostics || []).map((d) => {
      const lineNum = Math.max(0, (d.line || 1) - 1);
      const lineObj = this.view!.state.doc.line(
        Math.min(lineNum + 1, this.view!.state.doc.lines)
      );
      return {
        from: lineObj.from,
        to: lineObj.to,
        severity: d.severity as 'error' | 'warning' | 'info',
        message: d.message || '',
      };
    });
    this.view.dispatch(setDiagnostics(this.view.state, cmDiags));
  }
}
