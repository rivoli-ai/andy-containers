FROM alpine:3.20

# Desktop + VNC + noVNC + dev tools
RUN apk add --no-cache \
    xfce4 xfce4-terminal dbus dbus-x11 \
    tigervnc novnc websockify \
    ca-certificates curl wget git openssh tmux dtach bash sudo \
    && mkdir -p /run/dbus

# .NET 10 via install script (not in Alpine repos yet)
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 && \
    ln -sf /root/.dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/root/.dotnet \
    PATH=$PATH:/root/.dotnet:/root/.dotnet/tools \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    LANG=C.UTF-8

# VNC setup
RUN mkdir -p /root/.vnc && \
    echo "container" | vncpasswd -f > /root/.vnc/passwd && \
    chmod 600 /root/.vnc/passwd && \
    printf '#!/bin/sh\nunset SESSION_MANAGER\nunset DBUS_SESSION_BUS_ADDRESS\nexec startxfce4\n' > /root/.vnc/xstartup && \
    chmod +x /root/.vnc/xstartup

RUN ln -sf /usr/share/novnc/vnc.html /usr/share/novnc/index.html 2>/dev/null || true

# SSH setup
RUN ssh-keygen -A && \
    echo 'PermitRootLogin yes' >> /etc/ssh/sshd_config && \
    echo 'root:container' | chpasswd

# Startup script
RUN printf '#!/bin/bash\nmkdir -p /run/dbus\ndbus-daemon --system --fork 2>/dev/null\n/usr/sbin/sshd\nvncserver :1 -geometry 1280x720 -depth 24 -localhost no &\nsleep 2\nwebsockify --web /usr/share/novnc 6080 localhost:5901 &\nexec sleep infinity\n' > /start.sh && chmod +x /start.sh

EXPOSE 6080 22

CMD ["/start.sh"]
