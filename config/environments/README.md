# EnvironmentProfile catalog (Epic X)

YAML files under `global/` are loaded by `EnvironmentProfileSeeder` at API
startup and upserted into `EnvironmentProfile` rows by `code`. The seeder is
**idempotent**: existing rows are never overwritten, so operator hand-edits
made via the catalog API (X3, once it lands) survive restarts. To force a
re-seed, delete the row in the catalog and restart.

## Schema

```yaml
code:           # Slug, unique across the catalog. Maps to EnvironmentProfile.Name.
display_name:   # Human-readable label.
kind:           # HeadlessContainer | Terminal | Desktop
base_image_ref: # OCI reference (e.g. ghcr.io/rivoli-ai/andy-headless:latest)

capabilities:
  network_allowlist: # Hostnames or "*" wildcards. [] means no egress.
    - "..."
  secrets_scope:     # None | RunScoped | WorkspaceScoped | OrganizationScoped
  has_gui:           # bool
  audit_mode:        # None | Standard | Strict
```

Field names use `snake_case` in YAML; the seeder maps them to the
`PascalCase` properties on `EnvironmentProfile` /
`EnvironmentCapabilities` automatically.

## Adding a profile

1. Drop a `your-profile.yaml` in `global/`.
2. Restart the API; the seeder loads it on boot.
3. Verify with `GET /containers/api/environments` once X3 ships, or by
   inspecting the `EnvironmentProfiles` table directly.

Malformed files are logged and skipped — the host never aborts startup
on a bad seed entry.
