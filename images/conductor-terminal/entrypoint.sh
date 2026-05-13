#!/usr/bin/env bash
# rivoli-ai/conductor#1029 (M1.9.2). Entrypoint for the conductor-terminal
# image family. Two responsibilities, kept narrow on purpose:
#
#   1. When $CONDUCTOR_INSTALL_ASSISTANTS is set non-empty AND
#      /opt/conductor/install-assistants.sh exists, run it once before
#      handing control to the user. The install script is shipped by
#      M1.9.3 (#1030); pre-baked `:claude-code` / `:opencode` variants
#      (M1.9.4 / M1.9.5) bake the install into their image layers and
#      leave the env var unset.
#
#   2. exec the CMD verbatim (bash by default). `exec` is load-bearing
#      so the shell becomes PID 1 + receives SIGTERM cleanly when
#      Conductor stops the container.
#
# The script is intentionally permissive: a failed install logs a
# warning but does NOT abort the entrypoint. The container is still
# reachable for the user to debug, matching the M1.5.3 contract that
# install failures surface on Container.CodeAssistantStatus rather
# than killing the container.

set -uo pipefail

readonly LOG_PREFIX="[conductor-terminal]"

log() {
    printf '%s %s\n' "$LOG_PREFIX" "$*" >&2
}

run_install_assistants_if_requested() {
    local marker="${CONDUCTOR_INSTALL_ASSISTANTS:-}"
    if [[ -z "$marker" ]]; then
        return 0
    fi

    local installer=/opt/conductor/install-assistants.sh
    if [[ ! -x "$installer" ]]; then
        log "CONDUCTOR_INSTALL_ASSISTANTS=$marker requested but $installer is missing or not executable — skipping (the M1.9.3 install script may not be shipped in this image variant)"
        return 0
    fi

    log "running install-assistants.sh (CONDUCTOR_INSTALL_ASSISTANTS=$marker)"
    if ! "$installer"; then
        log "install-assistants.sh exited non-zero — continuing into shell so the user can debug"
    fi
}

run_install_assistants_if_requested

if [[ "$#" -eq 0 ]]; then
    # No CMD passed (`docker run image` with no command). Drop to login bash.
    exec bash -l
fi

exec "$@"
