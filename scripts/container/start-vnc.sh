#!/bin/sh
# Start VNC desktop environment with noVNC web client
# Used as CMD in desktop container images
set -e

export HOME=/root
export USER=root

# Start dbus (required by XFCE4)
mkdir -p /run/dbus
dbus-daemon --system --fork 2>/dev/null || true

# Start SSH server
/usr/sbin/sshd 2>/dev/null || true

# Source env vars for the session (including vars injected during provisioning)
[ -f /etc/profile ] && . /etc/profile 2>/dev/null || true
for f in /etc/profile.d/*.sh; do [ -f "$f" ] && . "$f" 2>/dev/null; done

# Start VNC server
# Alpine uses simpler syntax, Ubuntu supports more flags
if command -v apk >/dev/null 2>&1; then
    vncserver :1 2>/dev/null &
else
    vncserver :1 -geometry 1280x720 -depth 24 -localhost no 2>/dev/null &
fi

sleep 3

# Start websockify (noVNC web client proxy)
# Use HTTPS if certs are available
if [ -f /etc/novnc/ssl/cert.pem ] && [ -f /etc/novnc/ssl/key.pem ]; then
    websockify --cert /etc/novnc/ssl/cert.pem --key /etc/novnc/ssl/key.pem \
        --web /usr/share/novnc 6080 localhost:5901 &
else
    websockify --web /usr/share/novnc 6080 localhost:5901 &
fi

# Keep container alive
exec sleep infinity
