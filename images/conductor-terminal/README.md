# conductor-terminal — base image

rivoli-ai/conductor#1029 (M1.9.2). Minimal Linux base for Conductor's
code-assistant container variants. Every M1.9 variant
(`:claude-code`, `:opencode`, future tools) extends this image.

## What's inside

- Ubuntu 22.04 LTS
- Shells: `bash`, `zsh`
- VCS / network: `git`, `curl`, `wget`, `ca-certificates`, `gnupg`, `openssh-client`
- Editors: `vim`, `nano`
- Session: `tmux` (config tuned for Conductor's terminal pane)
- Non-root user: `conductor` (uid 1000), passwordless sudo, workdir `/workspace`
- Entrypoint: `/opt/conductor/entrypoint.sh` — runs `install-assistants.sh` (M1.9.3) when `$CONDUCTOR_INSTALL_ASSISTANTS` is set, otherwise drops to bash

## Files

| File | Purpose |
| --- | --- |
| `Dockerfile` | Image source — what `docker build` consumes |
| `spec.yaml` | Template spec consumed by andy-containers' `POST /api/templates/from-yaml`. Must stay in lockstep with `Dockerfile`. |
| `entrypoint.sh` | PID 1 entrypoint. Conditionally runs the install script, then `exec`s the CMD (default `bash -l`). |
| `conductor.bashrc` | Default `.bashrc` for the `conductor` user. Vi-mode, coloured `ls`, ANDY_* env-var propagation. |
| `conductor.tmux.conf` | Default tmux config: mouse on, 50k scrollback, true-colour, base-index 1. |
| `build.sh` | Wrapper. Runs `docker build` + `docker save` → `images/_out/conductor-terminal-base.tar`. |

## Build

```bash
images/conductor-terminal/build.sh
# → images/_out/conductor-terminal-base.tar
```

Add `--tag custom:tag` to retag, or `--out-dir path/` to change output dir.

## Size target

< 200 MB compressed (the tarball `build.sh` produces). The package
list is intentionally narrow. Anything an assistant CLI needs at
install time belongs in `install-assistants.sh` (M1.9.3), not here.

## Lifecycle in Conductor

1. **M1.9.2 (this)** — base image.
2. **M1.9.3 (#1030)** — `install-assistants.sh` lives at
   `/opt/conductor/install-assistants.sh` inside variant images.
3. **M1.9.4 / M1.9.5 (#1031 / #1032)** — `:claude-code` / `:opencode`
   variants pre-bake the install at image-build time so a fresh
   container is ready instantly. Variants `FROM conductor-terminal:base`.
4. **M1.9.6 (#1014)** — bundled tarballs ship inside the Conductor
   app and seed into the embedded zot registry on first launch.
