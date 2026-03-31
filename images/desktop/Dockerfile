FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

# Install desktop environment + VNC + noVNC
RUN apt-get update && apt-get install -y --no-install-recommends \
    xfce4 xfce4-terminal dbus-x11 \
    tigervnc-standalone-server tigervnc-common \
    novnc websockify \
    ca-certificates curl wget git openssh-server sudo \
    && rm -rf /var/lib/apt/lists/*

# VNC setup
RUN mkdir -p /root/.vnc && \
    echo "container" | vncpasswd -f > /root/.vnc/passwd && \
    chmod 600 /root/.vnc/passwd

# noVNC setup - link to standard path
RUN ln -sf /usr/share/novnc/vnc.html /usr/share/novnc/index.html

# Startup script
RUN printf '#!/bin/bash\n\
vncserver :1 -geometry 1280x720 -depth 24 -localhost no &\n\
sleep 2\n\
websockify --web /usr/share/novnc 6080 localhost:5901 &\n\
exec sleep infinity\n' > /start-vnc.sh && chmod +x /start-vnc.sh

EXPOSE 6080 22

CMD ["/start-vnc.sh"]
