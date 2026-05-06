# BASE_IMAGE defaults to the locally-built `andy-containers-base` so existing
# `docker build images/full-stack` invocations keep working. CI overrides it
# with the registry-published base via `--build-arg BASE_IMAGE=...`.
ARG BASE_IMAGE=andy-containers-base:latest
FROM ${BASE_IMAGE}

LABEL description="Andy Containers - Full Stack (.NET 8, Python 3.12, Node 20, Angular 18)"

USER root

# .NET 8 SDK
RUN wget https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && rm /tmp/dotnet-install.sh

ENV DOTNET_ROOT=/usr/share/dotnet
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# Python 3.12
RUN add-apt-repository ppa:deadsnakes/ppa \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
    python3.12 \
    python3.12-venv \
    python3.12-dev \
    python3-pip \
    && update-alternatives --install /usr/bin/python3 python3 /usr/bin/python3.12 1 \
    && update-alternatives --install /usr/bin/python python /usr/bin/python3.12 1 \
    && rm -rf /var/lib/apt/lists/*

# Node.js 20 LTS
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && npm install -g npm@latest \
    && rm -rf /var/lib/apt/lists/*

# Angular CLI
RUN npm install -g @angular/cli

# code-server (VSCode in browser)
RUN curl -fsSL https://code-server.dev/install.sh | sh

# Cleanup
RUN apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/*

USER dev

# Verify installations
RUN dotnet --version \
    && python --version \
    && node --version \
    && npm --version \
    && ng version 2>/dev/null || true \
    && code-server --version

EXPOSE 8080 3000 5000

CMD ["code-server", "--bind-addr", "0.0.0.0:8080", "--auth", "none"]
