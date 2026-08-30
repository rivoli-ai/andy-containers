#!/usr/bin/env bash
# rivoli-ai/conductor#1030 (M1.9.3). Code-assistant CLI installer.
# Runs inside the conductor-terminal container family on first boot
# when `$CONDUCTOR_INSTALL_ASSISTANTS` is set (e.g. `claude-code` or
# `claude-code,opencode,aider`).
#
# This is the FALLBACK path. Pre-baked image variants (M1.9.4 /
# M1.9.5) bake the install at image-build time so a fresh container
# is ready instantly without internet. This script covers:
#
#   - multi-assistant requests (M1.6 picker eventually allows >1)
#   - assistants without a pre-baked variant yet (Aider, etc.)
#   - the dev/test path where you'd rather start from the base image
#     than rebuild a variant
#
# Contract:
#   - reads `$CONDUCTOR_INSTALL_ASSISTANTS`: comma-separated slugs
#     (claude-code,opencode,aider,...).
#   - per slug, runs the install command + writes one JSON-line per
#     event to `/var/log/conductor/install.log`.
#   - per-slug failure is logged and SKIPPED; the script keeps going
#     so a typo or one broken installer doesn't take down the whole
#     batch. Exit code: 0 if at least one slug succeeded, 1 if all
#     requested slugs failed, 0 for empty input.
#   - idempotent: re-running with the same env var is safe — slugs
#     whose CLI is already on $PATH log a "skipped" event and move on.
#
# This script is intentionally narrow on dependencies (`bash`,
# `curl`, `tee`, plus whatever the per-slug install needs) so it
# runs on the M1.9.2 base image as-shipped.

set -uo pipefail

# Log path. Default is /var/log/conductor/install.log so the host
# log-stream tailer (M1.5.3's banner) can find it without per-image
# config. Tests + dev runs that don't have /var/log writable can
# override with $CONDUCTOR_INSTALL_LOG to point at any path the
# current user can append to.
readonly INSTALL_LOG="${CONDUCTOR_INSTALL_LOG:-/var/log/conductor/install.log}"
LOG_DIR="$(dirname "$INSTALL_LOG")"
readonly LOG_DIR

# Resolved at startup. Either points at $INSTALL_LOG (if writable)
# or /dev/null (degraded path — events still flow to stderr). All
# per-installer `>>"$LOG_SINK"` redirects route through here so a
# read-only /var/log doesn't kill the install.
LOG_SINK="$INSTALL_LOG"

ensure_log_dir() {
    # The base image doesn't pre-create /var/log/conductor — do it
    # here with sudo because the script runs as the `conductor`
    # user, not root. Tests overriding $CONDUCTOR_INSTALL_LOG to a
    # writable temp path don't need sudo; mkdir suffices.
    if [[ ! -d "$LOG_DIR" ]]; then
        mkdir -p "$LOG_DIR" 2>/dev/null \
            || { command -v sudo >/dev/null 2>&1 \
                && sudo install -d -m 0755 -o conductor -g conductor "$LOG_DIR" 2>/dev/null; } \
            || true
    fi
    # Verify the log path is actually writable. If not, downgrade
    # the per-installer redirect target to /dev/null so the
    # commands still run; events keep flowing to stderr.
    if ! ( : >>"$LOG_SINK" ) 2>/dev/null; then
        LOG_SINK=/dev/null
    fi
}

# JSON-line emit. The host log collector (Conductor's container
# log-stream tailer, M1.5.3's banner) parses this format. Schema:
#   {"ts": "<iso8601>", "level": "info|warn|error", "slug": "<tool>",
#    "event": "<phase>", "message": "..."}
#
# `event` values:
#   "start"     — beginning the install for this slug
#   "skipped"   — CLI already on $PATH, no-op
#   "installed" — install command succeeded
#   "failed"    — install command exited non-zero
#   "unknown"   — slug not in the dispatch table
#   "summary"   — terminal record of overall result
log_event() {
    local level="$1" slug="$2" event="$3" message="$4"
    local ts
    ts="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    # Escape backslashes + quotes in the message so the JSON line stays
    # well-formed even if a curl install printed an "unmatched quote"
    # error.
    local escaped="${message//\\/\\\\}"
    escaped="${escaped//\"/\\\"}"
    local line
    line="$(printf '{"ts":"%s","level":"%s","slug":"%s","event":"%s","message":"%s"}' \
        "$ts" "$level" "$slug" "$event" "$escaped")"
    # Always emit to stderr (the container log stream picks this up
    # regardless of file-system state). Best-effort append to the
    # log file too; if the directory is read-only or doesn't exist
    # the append silently fails — stderr still has the event.
    printf '%s\n' "$line" >&2
    printf '%s\n' "$line" >>"$LOG_SINK" 2>/dev/null || true
}

# Best-effort apt-get installer used by the npm + pip bootstraps
# below. Returns 0 if the package is present after the call.
apt_ensure() {
    local pkg="$1"
    if command -v "${2:-$pkg}" >/dev/null 2>&1; then
        return 0
    fi
    if ! command -v sudo >/dev/null 2>&1; then
        return 1
    fi
    sudo apt-get update -qq >/dev/null 2>&1 || true
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$pkg" >/dev/null 2>&1
    command -v "${2:-$pkg}" >/dev/null 2>&1
}

ensure_nodejs() {
    if command -v npm >/dev/null 2>&1; then return 0; fi
    apt_ensure nodejs node && apt_ensure npm npm
}

ensure_pip() {
    if command -v pip3 >/dev/null 2>&1 || command -v pip >/dev/null 2>&1; then
        return 0
    fi
    apt_ensure python3-pip pip3
}

# ----------------------------------------------------------------
# Per-assistant installers. Each returns 0 on success, non-zero on
# failure. Each MUST handle the already-installed case (log skipped
# and return 0). Don't `set -e` inside these — we want to handle
# specific exit codes per command.
# ----------------------------------------------------------------

install_claude_code() {
    if command -v claude >/dev/null 2>&1; then
        log_event info claude-code skipped "claude already on PATH"
        return 0
    fi
    ensure_nodejs || {
        log_event error claude-code failed "nodejs/npm could not be installed"
        return 1
    }
    log_event info claude-code start "npm install -g @anthropic-ai/claude-code"
    if sudo npm install -g @anthropic-ai/claude-code >>"$LOG_SINK" 2>&1; then
        log_event info claude-code installed "claude-code installed via npm"
        return 0
    fi
    log_event error claude-code failed "npm install exited non-zero — see $INSTALL_LOG"
    return 1
}

install_codex_cli() {
    if command -v codex >/dev/null 2>&1; then
        log_event info codex-cli skipped "codex already on PATH"
        return 0
    fi
    ensure_nodejs || {
        log_event error codex-cli failed "nodejs/npm could not be installed"
        return 1
    }
    log_event info codex-cli start "npm install -g @openai/codex"
    if sudo npm install -g @openai/codex >>"$LOG_SINK" 2>&1; then
        log_event info codex-cli installed "codex installed via npm"
        return 0
    fi
    log_event error codex-cli failed "npm install exited non-zero"
    return 1
}

install_aider() {
    if command -v aider >/dev/null 2>&1; then
        log_event info aider skipped "aider already on PATH"
        return 0
    fi
    ensure_pip || {
        log_event error aider failed "python3-pip could not be installed"
        return 1
    }
    log_event info aider start "pip install aider-chat"
    local pip_cmd
    if command -v pip3 >/dev/null 2>&1; then
        pip_cmd=pip3
    else
        pip_cmd=pip
    fi
    if "$pip_cmd" install --user aider-chat >>"$LOG_SINK" 2>&1; then
        log_event info aider installed "aider installed via $pip_cmd"
        return 0
    fi
    log_event error aider failed "pip install exited non-zero"
    return 1
}

install_opencode() {
    if command -v opencode >/dev/null 2>&1; then
        log_event info opencode skipped "opencode already on PATH"
        return 0
    fi
    log_event info opencode start "downloading opencode release tarball"

    local arch
    arch="$(uname -m | sed 's/aarch64/arm64/' | sed 's/x86_64/x86_64/')"
    local url="https://github.com/opencode-ai/opencode/releases/latest/download/opencode-linux-${arch}.tar.gz"

    local tmp
    tmp="$(mktemp -d)"
    if ! curl -fsSL -o "$tmp/oc.tar.gz" "$url" >>"$LOG_SINK" 2>&1; then
        log_event error opencode failed "curl failed for $url"
        rm -rf "$tmp"
        return 1
    fi
    if ! tar xzf "$tmp/oc.tar.gz" -C "$tmp" >>"$LOG_SINK" 2>&1; then
        log_event error opencode failed "tar xzf failed"
        rm -rf "$tmp"
        return 1
    fi
    if ! sudo install -m 0755 "$tmp/opencode" /usr/local/bin/opencode-bin >>"$LOG_SINK" 2>&1; then
        log_event error opencode failed "install to /usr/local/bin failed"
        rm -rf "$tmp"
        return 1
    fi
    rm -rf "$tmp"

    # OpenCode is multi-provider — write a tiny wrapper that
    # synthesises its config file from the runtime env vars so the
    # binary picks the right backend (defaults to OpenAI via the
    # andy-models proxy — #944 wires that into the container env).
    sudo tee /usr/local/bin/opencode >/dev/null <<'WRAP'
#!/bin/sh
. /etc/profile 2>/dev/null
for f in /etc/profile.d/*.sh; do [ -f "$f" ] && . "$f" 2>/dev/null; done
M=${LLM_MODEL:-gpt-4o}
K=${OPENAI_API_KEY:-}
cat > "$HOME/.opencode.json" <<CONF
{"providers":{"openai":{"apiKey":"$K"}},"agents":{"coder":{"model":"$M","maxTokens":5000},"task":{"model":"$M","maxTokens":5000},"title":{"model":"$M","maxTokens":80}}}
CONF
exec /usr/local/bin/opencode-bin "$@"
WRAP
    sudo chmod 0755 /usr/local/bin/opencode

    log_event info opencode installed "opencode installed to /usr/local/bin"
    return 0
}

# Best-effort fallback. The full set is in andy-containers'
# CodeAssistantInstallService; what we surface here is the M1.6
# priority list. Unknown slugs log a warning and exit 0 so the
# overall script doesn't fail on a typo.
install_unknown() {
    local slug="$1"
    log_event warn "$slug" unknown "no installer for slug — see CodeAssistantInstallService for the full list of supported tools"
}

# ----------------------------------------------------------------
# Dispatch
# ----------------------------------------------------------------

main() {
    local raw="${CONDUCTOR_INSTALL_ASSISTANTS:-}"
    if [[ -z "$raw" ]]; then
        return 0
    fi

    ensure_log_dir

    local total=0 ok=0 failed=0 unknown=0
    local IFS=','
    # shellcheck disable=SC2206
    local slugs=($raw)
    unset IFS

    for slug in "${slugs[@]}"; do
        slug="${slug#"${slug%%[![:space:]]*}"}"
        slug="${slug%"${slug##*[![:space:]]}"}"
        [[ -z "$slug" ]] && continue
        total=$((total + 1))

        case "$slug" in
            claude-code)  install_claude_code  && ok=$((ok + 1)) || failed=$((failed + 1)) ;;
            codex-cli)    install_codex_cli    && ok=$((ok + 1)) || failed=$((failed + 1)) ;;
            aider)        install_aider        && ok=$((ok + 1)) || failed=$((failed + 1)) ;;
            opencode)     install_opencode     && ok=$((ok + 1)) || failed=$((failed + 1)) ;;
            *)            install_unknown "$slug"; unknown=$((unknown + 1)) ;;
        esac
    done

    log_event info "$raw" summary \
        "total=$total ok=$ok failed=$failed unknown=$unknown"

    # Exit code: 0 if at least one slug succeeded OR all unknown;
    # 1 only if every recognised slug failed.
    if (( failed > 0 && ok == 0 )); then
        return 1
    fi
    return 0
}

main "$@"
