FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

# Desktop environment + VNC + noVNC
RUN apt-get update && apt-get install -y \
    xfce4 xfce4-terminal dbus-x11 \
    tigervnc-standalone-server tigervnc-common \
    novnc websockify \
    ca-certificates curl wget git openssh-server sudo \
    tmux dtach locales \
    && locale-gen en_US.UTF-8 \
    && rm -rf /var/lib/apt/lists/*

ENV LANG=C.UTF-8 LC_ALL=C.UTF-8

# .NET 8 SDK
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 && \
    ln -sf /root/.dotnet/dotnet /usr/local/bin/dotnet
ENV DOTNET_ROOT=/root/.dotnet \
    PATH=$PATH:/root/.dotnet:/root/.dotnet/tools \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Code editor (code-server)
RUN curl -fsSL https://code-server.dev/install.sh | sh -s -- --method standalone

# VNC setup: password + xstartup for XFCE4
RUN mkdir -p /root/.vnc && \
    echo "container" | vncpasswd -f > /root/.vnc/passwd && \
    chmod 600 /root/.vnc/passwd && \
    printf '#!/bin/sh\nunset SESSION_MANAGER\nunset DBUS_SESSION_BUS_ADDRESS\nexec startxfce4\n' > /root/.vnc/xstartup && \
    chmod +x /root/.vnc/xstartup

# noVNC link
RUN ln -sf /usr/share/novnc/vnc.html /usr/share/novnc/index.html

# SSH setup
RUN mkdir -p /run/sshd && \
    sed -i 's/#\?PermitRootLogin.*/PermitRootLogin yes/' /etc/ssh/sshd_config && \
    echo 'root:container' | chpasswd && \
    ssh-keygen -A

# Self-signed cert for noVNC HTTPS
RUN mkdir -p /etc/novnc/ssl && \
    openssl req -x509 -nodes -days 3650 -newkey rsa:2048 \
      -keyout /etc/novnc/ssl/key.pem -out /etc/novnc/ssl/cert.pem \
      -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"

# Startup script: dbus + VNC + noVNC (HTTPS) + SSH + code-server
RUN printf '#!/bin/bash\nmkdir -p /run/dbus\ndbus-daemon --system --fork 2>/dev/null\n/usr/sbin/sshd\nvncserver :1 -geometry 1280x720 -depth 24 -localhost no &\nsleep 2\nwebsockify --cert /etc/novnc/ssl/cert.pem --key /etc/novnc/ssl/key.pem --web /usr/share/novnc 6080 localhost:5901 &\ncode-server --bind-addr 0.0.0.0:8080 --auth none &\nexec sleep infinity\n' > /start.sh && chmod +x /start.sh

EXPOSE 6080 8080 22

CMD ["/start.sh"]
