# syntax=docker/dockerfile:1
# rivoli-ai/andy-tasks#390 — pre-baked agent-run image.
#
# The `andy-cli-dev` template used to git-clone + `dotnet publish` andy-cli
# inside EVERY provisioned container (>5 minutes per container), which forced
# the Workspaces:AndyContainers:ProvisionReadyTimeout stopgap bump to 900s.
# This image pays that cost ONCE at image-build time: the Docker provider
# lazily builds `andy-agent-cli:latest` from this Dockerfile on first use
# (same machinery as the andy-desktop-* images), and every container after
# that reaches Ready in seconds.
#
# The seeded post_create script keys its fast path on the /etc/andy/prebaked
# marker + `command -v andy-cli`; when this image is unavailable (non-Docker
# providers fall back to ubuntu:24.04) the script source-builds andy-cli
# exactly as before, so nothing regresses.
#
# Refresh: the andy-cli clone layer is cached by Docker. To pick up a newer
# andy-cli, rebuild with --no-cache (or `docker rmi andy-agent-cli:latest`
# and let the provider rebuild), optionally pinning ANDY_CLI_GIT_REF.

FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    NUGET_CERT_REVOCATION_MODE=off

# Base packages — the same set the generic PostCreateScript installs at
# provision time, plus libicu-dev (the .NET SDK FailFasts without ICU:
# "Couldn't find a valid ICU package").
RUN apt-get update -qq \
 && apt-get install -y -qq --no-install-recommends \
      git curl wget ca-certificates openssh-server dtach tmux locales \
      libicu-dev unzip \
 && locale-gen en_US.UTF-8 >/dev/null \
 && rm -rf /var/lib/apt/lists/*

# GitHub CLI — apt-keyring method, mirroring
# ContainerToolProvisioner.GitHubCliInstallScript (conductor#2242: agents
# need `gh` for PR creation).
RUN mkdir -p -m 755 /etc/apt/keyrings \
 && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
      -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
 && chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
 && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
      > /etc/apt/sources.list.d/github-cli.list \
 && apt-get update -qq \
 && apt-get install -y -qq gh \
 && rm -rf /var/lib/apt/lists/*

# .NET 8 SDK — same official install script the legacy provision-time chain
# used. The SDK stays in the image on purpose: this is also the interactive
# "Andy CLI Development" template, and the published andy-cli output is
# framework-dependent.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh \
      | bash -s -- --channel 8.0 --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet

# Environment parity with the legacy provision-time script.
RUN { echo 'LANG=C.UTF-8'; \
      echo 'LC_ALL=C.UTF-8'; \
      echo 'DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0'; \
      echo 'NUGET_CERT_REVOCATION_MODE=off'; \
      echo 'DOTNET_NUGET_SIGNATURE_VERIFICATION=false'; } >> /etc/environment \
 && echo 'export DOTNET_ROOT=/usr/share/dotnet' >> /root/.bashrc \
 && echo 'export PATH=$PATH:/usr/share/dotnet:/root/.dotnet/tools' >> /root/.bashrc \
 && echo '[ -f ~/.bashrc ] && . ~/.bashrc' >> /root/.bash_profile

# andy-cli — cloned + published ONCE here instead of inside every container.
# `andy-cli --version` fails the image build loudly if the binary is broken,
# so a bad image can never be tagged (the L0 guarantee).
ARG ANDY_CLI_GIT_REF=main
RUN git clone --depth 1 --branch "$ANDY_CLI_GIT_REF" \
      https://github.com/rivoli-ai/andy-cli.git /opt/andy-cli-src \
 && dotnet publish /opt/andy-cli-src/src/Andy.Cli/Andy.Cli.csproj -c Release -o /opt/andy-cli \
 && ln -sf /opt/andy-cli/andy-cli /usr/local/bin/andy-cli \
 && rm -rf /opt/andy-cli-src /root/.nuget/packages \
 && andy-cli --version

# Marker the seeded post_create fast path keys on (DataSeeder.AndyCliScriptsJson).
RUN mkdir -p /etc/andy && date -u +%Y-%m-%dT%H:%M:%SZ > /etc/andy/prebaked

CMD ["sleep", "infinity"]
