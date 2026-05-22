---
title: Templates
order: 3
tags: [templates, yaml]
---

# Templates

**Templates** are declarative YAML definitions that describe how to build and configure container images within Andy Containers.

## Why Templates?

Instead of pushing pre-built images, templates let you:

- Define build steps in version-controlled YAML.
- Parameterize base images, environment variables, and build arguments.
- Reproduce builds deterministically across workspaces.
- Share common patterns through the template gallery.

## Template Structure

```yaml
apiVersion: containers.andy.ai/v1
kind: Template
metadata:
  name: python-data-science
  workspace: ml-pipeline
spec:
  baseImage: python:3.11-slim
  build:
    - run: apt-get update && apt-get install -y build-essential
    - run: pip install --no-cache-dir numpy pandas scikit-learn jupyter
  env:
    PYTHONDONTWRITEBYTECODE: "1"
    PYTHONUNBUFFERED: "1"
  ports:
    - 8888
  volumes:
    - name: data
      mountPath: /data
```

## Build Context

Templates support inline build steps as well as external context:

- `git.url` — Clone a repository and use its Dockerfile.
- `archive.url` — Fetch a tarball to use as the build context.
- `inline` — Define steps directly inside the template YAML.

## Variables

Use `${VAR_NAME}` syntax to reference variables supplied at build time:

```yaml
spec:
  baseImage: ${BASE_IMAGE:-ubuntu:22.04}
```

Variables are resolved from:

1. Build-time parameters passed to the API.
2. Workspace-level defaults.
3. Hard-coded fallback values.

## Template Gallery

Organization owners can publish templates to a shared gallery. Members of the organization can then instantiate gallery templates into their own workspaces without rewriting YAML.
