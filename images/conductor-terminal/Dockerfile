# rivoli-ai/conductor#1029 (M1.9.2). Base image every M1.9 code-assistant
# variant extends. Minimal Linux + shell tools + a non-root user; no LLM
# assistants pre-baked here — those live in the `:claude-code` and
# `:opencode` variants (M1.9.4 / M1.9.5) that FROM this image.
#
# Sizing target: < 200 MB compressed. The package list is intentionally
# narrow — anything an assistant CLI needs at install time lives in
# install-assistants.sh (M1.9.3), not in the base.

# Pinned to Ubuntu 22.04 LTS because every code-assistant CLI we ship
# (Claude Code, OpenCode, Codex CLI, Aider, etc.) publishes compatibility
# matrices against 22.04. 24.04 works for most but bumps glibc and would
# require re-validating every CLI's release notes.
FROM ubuntu:22.04

LABEL maintainer="Rivoli AI <dev@rivoli.ai>"
LABEL org.opencontainers.image.title="conductor-terminal-base"
LABEL org.opencontainers.image.description="Minimal Linux base for Conductor code-assistant container variants (#1029)"
LABEL org.opencontainers.image.source="https://github.com/rivoli-ai/andy-containers"
# Marker the andy-containers build pipeline keys off when surfacing
# this image in the catalog UI (and when M1.9.6's seeding code picks
# which images to register on first launch).
LABEL ai.rivoli.conductor.is-base="true"

ENV DEBIAN_FRONTEND=noninteractive \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8

# Core OS packages. Kept on a single layer so apt cache evicts in the
# same step + the layer stays small. `--no-install-recommends` avoids
# pulling in 80+ MB of doc/locale dependencies the assistants don't
# need.
#
# Why each package:
#   ca-certificates    — TLS for every curl/git operation
#   curl, wget         — assistant installers fetch via both
#   gnupg              — apt-key + signed installer scripts
#   git                — assistant CLIs operate on repos
#   bash, zsh          — shells the user expects to find
#   vim, nano          — minimum-viable editors
#   tmux               — session persistence across reconnects
#   sudo               — `conductor` user needs passwordless sudo
#   locales            — locale-gen for UTF-8 in TUIs
#   openssh-client     — git over SSH, ssh-keygen for host-key trust
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        wget \
        gnupg \
        git \
        bash \
        zsh \
        vim \
        nano \
        tmux \
        sudo \
        locales \
        openssh-client \
    && locale-gen en_US.UTF-8 \
    && rm -rf /var/lib/apt/lists/*

# Non-root user. uid 1000 matches the default macOS user mapping so
# bind-mounted volumes from Docker Desktop don't end up owned by root
# inside the container.
RUN useradd --create-home --uid 1000 --shell /bin/bash conductor \
    && echo "conductor ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/conductor \
    && chmod 0440 /etc/sudoers.d/conductor

# Workdir lives under /workspace, not /home/conductor — workspaces
# bind-mount the user's repo here in M1.9 follow-ups.
RUN mkdir -p /workspace \
    && chown conductor:conductor /workspace

# Entrypoint dir for Conductor-managed scripts. Owned by root so the
# user can't tamper with the install path; readable + executable.
RUN mkdir -p /opt/conductor \
    && chmod 0755 /opt/conductor

# install-assistants.sh ships in M1.9.3 (#1030) — when present, the
# entrypoint runs it conditional on $CONDUCTOR_INSTALL_ASSISTANTS.
COPY entrypoint.sh /opt/conductor/entrypoint.sh
RUN chmod 0755 /opt/conductor/entrypoint.sh

# Sensible shell defaults so the prompt looks reasonable in a fresh
# terminal session and the user isn't dropped into a POSIX-only env.
COPY conductor.bashrc /home/conductor/.bashrc
COPY conductor.tmux.conf /home/conductor/.tmux.conf
RUN chown conductor:conductor /home/conductor/.bashrc /home/conductor/.tmux.conf

USER conductor
WORKDIR /workspace

ENTRYPOINT ["/opt/conductor/entrypoint.sh"]
CMD ["bash", "-l"]
