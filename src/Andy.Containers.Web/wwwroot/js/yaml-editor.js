// yaml-editor.js - CodeMirror 6 YAML editor via JS interop

const editors = new Map();

let cmModules = null;

async function loadCodeMirror() {
    if (cmModules) return cmModules;

    const [
        { EditorView, keymap, lineNumbers, highlightActiveLine, highlightSpecialChars, drawSelection, rectangularSelection },
        { EditorState },
        { defaultKeymap, history, historyKeymap, indentWithTab },
        { syntaxHighlighting, defaultHighlightStyle, indentOnInput, bracketMatching, foldGutter, foldKeymap },
        { yaml },
        { linter, lintGutter, setDiagnostics },
        { oneDark }
    ] = await Promise.all([
        import('https://esm.sh/@codemirror/view@6'),
        import('https://esm.sh/@codemirror/state@6'),
        import('https://esm.sh/@codemirror/commands@6'),
        import('https://esm.sh/@codemirror/language@6'),
        import('https://esm.sh/@codemirror/lang-yaml@6'),
        import('https://esm.sh/@codemirror/lint@6'),
        import('https://esm.sh/@codemirror/theme-one-dark@6')
    ]);

    cmModules = {
        EditorView, keymap, lineNumbers, highlightActiveLine, highlightSpecialChars,
        drawSelection, rectangularSelection, EditorState,
        defaultKeymap, history, historyKeymap, indentWithTab,
        syntaxHighlighting, defaultHighlightStyle, indentOnInput, bracketMatching,
        foldGutter, foldKeymap, yaml, linter, lintGutter, setDiagnostics, oneDark
    };
    return cmModules;
}

window.yamlEditor = {
    init: async function (elementId, dotNetRef, options) {
        const cm = await loadCodeMirror();
        const container = document.getElementById(elementId);
        if (!container) return;

        let debounceTimer = null;

        const updateListener = cm.EditorView.updateListener.of((update) => {
            if (update.docChanged && !options.readOnly) {
                clearTimeout(debounceTimer);
                debounceTimer = setTimeout(() => {
                    const value = update.state.doc.toString();
                    dotNetRef.invokeMethodAsync('OnEditorChanged', value);
                }, 300);
            }
        });

        const saveKeymap = cm.keymap.of([{
            key: 'Mod-s',
            run: () => {
                dotNetRef.invokeMethodAsync('OnSaveRequested');
                return true;
            }
        }]);

        const extensions = [
            cm.lineNumbers(),
            cm.highlightActiveLine(),
            cm.highlightSpecialChars(),
            cm.drawSelection(),
            cm.indentOnInput(),
            cm.bracketMatching(),
            cm.foldGutter(),
            cm.history(),
            cm.lintGutter(),
            cm.yaml(),
            cm.syntaxHighlighting(cm.defaultHighlightStyle, { fallback: true }),
            cm.keymap.of([
                ...cm.defaultKeymap,
                ...cm.historyKeymap,
                ...cm.foldKeymap,
                cm.indentWithTab
            ]),
            updateListener,
            saveKeymap,
            cm.EditorView.theme({
                '&': { height: options.height || '500px' },
                '.cm-scroller': { overflow: 'auto' },
                '.cm-content': { fontFamily: "'SF Mono', 'Cascadia Code', 'Fira Code', monospace", fontSize: '0.85rem' },
                '.cm-gutters': { fontFamily: "'SF Mono', 'Cascadia Code', 'Fira Code', monospace", fontSize: '0.85rem' }
            })
        ];

        if (options.readOnly) {
            extensions.push(cm.EditorState.readOnly.of(true));
            extensions.push(cm.EditorView.editable.of(false));
        }

        const state = cm.EditorState.create({
            doc: options.value || '',
            extensions
        });

        const view = new cm.EditorView({
            state,
            parent: container
        });

        editors.set(elementId, { view, dotNetRef });
    },

    setValue: function (elementId, value) {
        const entry = editors.get(elementId);
        if (!entry) return;
        const { view } = entry;
        view.dispatch({
            changes: { from: 0, to: view.state.doc.length, insert: value }
        });
    },

    getValue: function (elementId) {
        const entry = editors.get(elementId);
        if (!entry) return '';
        return entry.view.state.doc.toString();
    },

    setDiagnostics: async function (elementId, diagnostics) {
        const entry = editors.get(elementId);
        if (!entry) return;
        const cm = await loadCodeMirror();
        const { view } = entry;

        const cmDiagnostics = diagnostics.map(d => {
            const line = Math.max(0, (d.line || 1) - 1);
            const lineObj = view.state.doc.line(Math.min(line + 1, view.state.doc.lines));
            return {
                from: lineObj.from,
                to: lineObj.to,
                severity: d.severity || 'error',
                message: d.message || ''
            };
        });

        view.dispatch(cm.setDiagnostics(view.state, cmDiagnostics));
    },

    dispose: function (elementId) {
        const entry = editors.get(elementId);
        if (!entry) return;
        entry.view.destroy();
        editors.delete(elementId);
    }
};
