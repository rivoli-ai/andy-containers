#!/bin/sh
# andy-containers — azure-bootstrap
#
# Opt-in corporate auth + artifactory wiring, intended to be sourced or
# exec'd from an image's entrypoint. andy-containers itself stays
# credential-agnostic: the caller (typically andy-issues' SandboxService)
# decides whether to inject these env vars per container.
#
# Inputs (all optional — no env → no action):
#
#   Azure resource login (independent of feed wiring):
#     AZURE_CLIENT_ID        — service principal app id
#     AZURE_CLIENT_SECRET    — service principal secret
#     AZURE_TENANT_ID        — AAD tenant id
#     AZURE_SUBSCRIPTION_ID  — optional subscription to default to
#
#   Azure DevOps artifactory feed wiring (independent of the login above):
#     AZURE_DEVOPS_PAT       — personal access token with "Packaging: read"
#     ARTIFACT_FEEDS_JSON    — JSON array of feed descriptors:
#                              [{ "name":"...", "type":"nuget|npm|pip",
#                                 "organization":"...", "project":"...",
#                                 "feedName":"..." }, ...]
#                              "project" is optional (org-scoped feeds).
#
# Behaviors:
#   1. No AZURE_* SP triple → skip the az login step silently.
#   2. No AZURE_DEVOPS_PAT or no ARTIFACT_FEEDS_JSON → skip feed wiring.
#   3. Idempotent — safe to re-source. Managed blocks in .npmrc /
#      pip.conf are bracketed by andy-artifactory markers and replaced
#      on each run; NuGet sources are removed-then-re-added.
#
# Exit codes:
#   0   — nothing to do, or all requested steps succeeded
#   1   — a requested step was missing a hard dependency (e.g. az CLI)
#         or failed unexpectedly
set -eu

log() { printf '[azure-bootstrap] %s\n' "$*"; }
warn() { printf '[azure-bootstrap] WARN: %s\n' "$*" >&2; }
die() { printf '[azure-bootstrap] ERROR: %s\n' "$*" >&2; exit 1; }

# ── 1. Azure resource login (opt-in) ──────────────────────────────────
if [ -n "${AZURE_CLIENT_ID:-}" ] \
   && [ -n "${AZURE_CLIENT_SECRET:-}" ] \
   && [ -n "${AZURE_TENANT_ID:-}" ]; then

    if ! command -v az >/dev/null 2>&1; then
        die "az CLI not found in PATH — install azure-cli in the image or omit AZURE_CLIENT_ID/SECRET/TENANT_ID."
    fi

    log "logging in as service principal $AZURE_CLIENT_ID (tenant $AZURE_TENANT_ID)"
    az login --service-principal \
        --username "$AZURE_CLIENT_ID" \
        --password "$AZURE_CLIENT_SECRET" \
        --tenant "$AZURE_TENANT_ID" \
        --output none

    if [ -n "${AZURE_SUBSCRIPTION_ID:-}" ]; then
        az account set --subscription "$AZURE_SUBSCRIPTION_ID"
        log "default subscription set to $AZURE_SUBSCRIPTION_ID"
    fi
else
    log "AZURE_* service-principal env not fully set; skipping az login."
fi

# ── 2. Artifactory feed wiring (opt-in, independent) ──────────────────
if [ -z "${AZURE_DEVOPS_PAT:-}" ] || [ -z "${ARTIFACT_FEEDS_JSON:-}" ]; then
    log "AZURE_DEVOPS_PAT or ARTIFACT_FEEDS_JSON not set; skipping feed wiring."
    exit 0
fi

if ! command -v python3 >/dev/null 2>&1; then
    warn "python3 not found — cannot parse ARTIFACT_FEEDS_JSON; skipping feed wiring."
    exit 0
fi

log "wiring Azure DevOps artifact feeds from ARTIFACT_FEEDS_JSON"

# Python does the JSON parse, URL construction, and file edits.
# Keeps the shell script readable and avoids fragile sed for INI/JSON.
AZURE_DEVOPS_PAT="$AZURE_DEVOPS_PAT" \
ARTIFACT_FEEDS_JSON="$ARTIFACT_FEEDS_JSON" \
python3 - <<'PYEOF'
import base64
import json
import os
import pathlib
import shutil
import subprocess
import sys

MARKER_BEGIN = "# >>> andy-artifactory >>>"
MARKER_END   = "# <<< andy-artifactory <<<"

try:
    feeds = json.loads(os.environ["ARTIFACT_FEEDS_JSON"])
except json.JSONDecodeError as e:
    print(f"[azure-bootstrap] ERROR: ARTIFACT_FEEDS_JSON is not valid JSON: {e}", file=sys.stderr)
    sys.exit(1)
if not isinstance(feeds, list):
    print("[azure-bootstrap] ERROR: ARTIFACT_FEEDS_JSON must be a JSON array.", file=sys.stderr)
    sys.exit(1)

pat   = os.environ["AZURE_DEVOPS_PAT"]
home  = pathlib.Path(os.environ.get("HOME", "/root"))

def feed_url(kind, org, project, feed_name):
    """Construct the Azure DevOps Artifacts URL for a feed."""
    scope = f"{org}/{project}" if project else org
    if kind == "nuget":
        return f"https://pkgs.dev.azure.com/{scope}/_packaging/{feed_name}/nuget/v3/index.json"
    if kind == "npm":
        return f"https://pkgs.dev.azure.com/{scope}/_packaging/{feed_name}/npm/registry/"
    if kind == "pip":
        return f"https://pkgs.dev.azure.com/{scope}/_packaging/{feed_name}/pypi/simple/"
    raise ValueError(f"unknown feed type: {kind}")

def replace_managed_block(path, new_lines, prelude=""):
    """Rewrite path, replacing any existing andy-artifactory block."""
    path.parent.mkdir(parents=True, exist_ok=True)
    existing = path.read_text() if path.exists() else prelude
    out, skip = [], False
    for line in existing.splitlines():
        if line.strip() == MARKER_BEGIN: skip = True; continue
        if line.strip() == MARKER_END:   skip = False; continue
        if not skip: out.append(line)
    out.append(MARKER_BEGIN)
    out.extend(new_lines)
    out.append(MARKER_END)
    path.write_text("\n".join(out).rstrip("\n") + "\n")

npm_lines = []
pip_lines = []
nuget_sources = []  # list of (name, url)

for f in feeds:
    try:
        name     = f["name"]
        kind     = f["type"].lower()
        org      = f["organization"]
        project  = f.get("project") or None
        feed_nm  = f["feedName"]
    except KeyError as e:
        print(f"[azure-bootstrap] WARN: skipping feed {f!r} — missing {e}", file=sys.stderr)
        continue

    try:
        url = feed_url(kind, org, project, feed_nm)
    except ValueError as e:
        print(f"[azure-bootstrap] WARN: {e} (feed {name!r})", file=sys.stderr)
        continue

    if kind == "npm":
        # Azure DevOps npm uses basic auth with base64(user:PAT).
        # "user" is a literal placeholder; Azure DevOps ignores it.
        auth = base64.b64encode(f"andy:{pat}".encode()).decode()
        host_and_path = url.split("//", 1)[1]
        npm_lines.append(f"# feed: {name}")
        npm_lines.append(f"registry={url}")
        npm_lines.append(f"//{host_and_path}:_password={auth}")
        npm_lines.append(f"//{host_and_path}:username=andy")
        npm_lines.append(f"//{host_and_path}:email=andy@local")
        npm_lines.append(f"//{host_and_path}:always-auth=true")
    elif kind == "pip":
        # pip picks up extra-index-url from [global] in pip.conf
        authed = url.replace("https://", f"https://andy:{pat}@", 1)
        pip_lines.append(f"# feed: {name}")
        pip_lines.append(f"extra-index-url = {authed}")
    elif kind == "nuget":
        nuget_sources.append((name, url))

# Write ~/.npmrc
if npm_lines:
    replace_managed_block(home / ".npmrc", npm_lines)
    print(f"[azure-bootstrap] wrote {len([l for l in npm_lines if l.startswith('registry=')])} npm feed(s) to {home / '.npmrc'}")

# Write ~/.config/pip/pip.conf (must have a [global] section for extra-index-url)
if pip_lines:
    pip_path = home / ".config" / "pip" / "pip.conf"
    # Ensure a [global] section exists before inserting the managed block.
    existing = pip_path.read_text() if pip_path.exists() else ""
    if "[global]" not in existing:
        existing = "[global]\n" + existing
    tmp = existing.splitlines()
    # Insert after the [global] line (and after any previous managed block
    # already inside [global]) by rewriting with the stripped prelude first.
    stripped_out, skip = [], False
    for line in tmp:
        if line.strip() == MARKER_BEGIN: skip = True; continue
        if line.strip() == MARKER_END:   skip = False; continue
        if not skip: stripped_out.append(line)
    # Inject managed block after the [global] header
    final = []
    injected = False
    for line in stripped_out:
        final.append(line)
        if not injected and line.strip() == "[global]":
            final.append(MARKER_BEGIN)
            final.extend(pip_lines)
            final.append(MARKER_END)
            injected = True
    pip_path.parent.mkdir(parents=True, exist_ok=True)
    pip_path.write_text("\n".join(final).rstrip("\n") + "\n")
    print(f"[azure-bootstrap] wrote {len([l for l in pip_lines if l.startswith('extra-index-url')])} pip feed(s) to {pip_path}")

# Register NuGet sources via dotnet CLI (the idiomatic idempotent path)
if nuget_sources:
    if not shutil.which("dotnet"):
        print("[azure-bootstrap] WARN: dotnet not in PATH — skipping NuGet feed registration.", file=sys.stderr)
    else:
        for name, url in nuget_sources:
            # remove-if-present, then add. NuGet has no upsert.
            subprocess.run(
                ["dotnet", "nuget", "remove", "source", name],
                capture_output=True)
            result = subprocess.run(
                ["dotnet", "nuget", "add", "source", url,
                 "--name", name,
                 "--username", "andy",
                 "--password", pat,
                 "--store-password-in-clear-text"],
                capture_output=True, text=True)
            if result.returncode != 0:
                print(f"[azure-bootstrap] WARN: failed to add NuGet source {name}: {result.stderr}",
                      file=sys.stderr)
            else:
                print(f"[azure-bootstrap] registered NuGet source {name}")

PYEOF

log "done."
