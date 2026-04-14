# In-container scripts

Scripts in this directory are copied into images at build time via Docker
BuildKit's `--build-context scripts=scripts/container` mechanism. They are
intended to be consumed either as **build-time one-shots** (baked into a
Dockerfile `RUN /tmp/setup-vnc.sh`) or as **runtime entry helpers** (sourced
or `exec`d from an image's `CMD`).

## Scripts

| Script | When it runs | Purpose |
|---|---|---|
| `setup-vnc.sh` | build-time | Installs VNC password + xstartup for XFCE4 sessions. |
| `generate-ssl-cert.sh` | build-time | Generates a self-signed cert for noVNC's websockify wrapper. |
| `start-vnc.sh` | runtime (CMD) | Starts Xvnc + websockify. Entry point for desktop images. |
| `install-opencode.sh` | build-time | Installs the OpenCode AI coding assistant. |
| `azure-bootstrap.sh` | runtime (early) | Opt-in Azure login + artifactory feed wiring. See below. |

## `azure-bootstrap.sh`

Runs at container startup to log into Azure and register Azure DevOps
Artifacts feeds for NuGet / npm / pip. andy-containers itself stays
credential-agnostic — the caller (typically andy-issues' `SandboxService`)
decides whether to inject the env vars per-container.

### Env-var contract

All env vars are optional. If the relevant triple is absent, that half of
the script is a silent no-op. The two halves are independent.

**Azure resource login:**

| Var | Purpose |
|---|---|
| `AZURE_CLIENT_ID` | Service principal app id |
| `AZURE_CLIENT_SECRET` | Service principal secret |
| `AZURE_TENANT_ID` | AAD tenant id |
| `AZURE_SUBSCRIPTION_ID` | *(optional)* default subscription |

Requires `az` CLI to be present in the image — if it isn't, the script
exits non-zero so the failure is visible rather than silently swallowed.

**Azure DevOps artifactory feeds:**

| Var | Purpose |
|---|---|
| `AZURE_DEVOPS_PAT` | PAT with `Packaging: read` scope |
| `ARTIFACT_FEEDS_JSON` | JSON array of feed descriptors (see below) |

Feed descriptor shape (matches what `andy-issues` emits):

```json
[
  { "name": "internal-nuget", "type": "nuget",
    "organization": "contoso", "project": "platform",
    "feedName": "shared-packages" },
  { "name": "internal-npm", "type": "npm",
    "organization": "contoso",
    "feedName": "shared-npm" }
]
```

`project` is optional — omit for organization-scoped feeds.

Requires `python3` in the image for JSON parsing. NuGet registration
additionally requires `dotnet` to be on `PATH`.

### Idempotency

Safe to re-source. Managed blocks in `~/.npmrc` and `~/.config/pip/pip.conf`
are bracketed by `# >>> andy-artifactory >>>` / `# <<<` markers and
replaced on each run. NuGet sources are removed-then-re-added.

### Integration

Images that want opt-in enterprise feed access should source the script
from their entrypoint:

```dockerfile
COPY --from=scripts azure-bootstrap.sh /usr/local/bin/azure-bootstrap
RUN chmod +x /usr/local/bin/azure-bootstrap
```

And in the container's start script, before the IDE/agent launches:

```sh
/usr/local/bin/azure-bootstrap || true  # opt-in; failures shouldn't block boot
```

Images that don't need it simply don't `COPY` the script — there is no
runtime cost to opting out.
