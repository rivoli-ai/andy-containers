FROM ubuntu:24.04

LABEL maintainer="Rivoli AI <dev@rivoli.ai>"
LABEL description="Andy Containers - Base development environment"

ENV DEBIAN_FRONTEND=noninteractive
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8

# Core system packages
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    wget \
    gnupg \
    lsb-release \
    software-properties-common \
    apt-transport-https \
    git \
    vim \
    nano \
    htop \
    jq \
    unzip \
    zip \
    sudo \
    locales \
    openssh-client \
    && rm -rf /var/lib/apt/lists/*

# Create dev user
RUN useradd -m -s /bin/bash -G sudo dev \
    && echo "dev ALL=(ALL) NOPASSWD:ALL" >> /etc/sudoers.d/dev

# Set locale
RUN locale-gen en_US.UTF-8

ENV HOME=/home/dev
WORKDIR /home/dev

USER dev
