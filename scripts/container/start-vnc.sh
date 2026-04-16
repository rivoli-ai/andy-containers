#!/bin/sh
# Start VNC desktop environment with noVNC web client.
# Used as CMD in desktop container images.
# Works as root or as a non-root user with passwordless sudo (sshd / system
# dbus / nginx-style services need root, the rest run as the invoking user).
set -e

# sudo wrapper: no-op when already root, sudo -n otherwise. Avoids hangs if
# sudo isn't configured for non-interactive use.
if [ "$(id -u)" = "0" ]; then
    SUDO=""
else
    SUDO="sudo -n"
fi

# Start dbus system bus (required by XFCE4 indirectly via polkit etc.)
$SUDO mkdir -p /run/dbus 2>/dev/null || true
$SUDO dbus-daemon --system --fork 2>/dev/null || true

# Start SSH server
$SUDO /usr/sbin/sshd 2>/dev/null || true

# Source env vars for the session (including vars injected during provisioning).
# Disable set -e: busybox ash propagates set -e from sourced scripts whose
# `[ test ] && cmd` chain fails (e.g. Alpine's dotnet.sh re-sourced after
# /etc/profile already ran its profile.d loop), which would kill start.sh.
set +e
[ -f /etc/profile ] && . /etc/profile 2>/dev/null
for f in /etc/profile.d/*.sh; do [ -f "$f" ] && . "$f" 2>/dev/null; done
set -e

# Start VNC server.
# Ubuntu's tigervncserver wrapper honors ~/.vnc/xstartup. Alpine's vncserver
# perl wrapper ignores it and forces `startxfce4`, which fails — so on Alpine
# we drive Xvnc directly and run our xstartup ourselves.
if command -v apk >/dev/null 2>&1; then
    Xvnc :1 -geometry 1280x720 -depth 24 -rfbport 5901 \
        -SecurityTypes VncAuth -PasswordFile "$HOME/.vnc/passwd" \
        -desktop "${HOSTNAME:-vnc}" 2>"$HOME/.vnc/Xvnc.log" &
    sleep 1
    DISPLAY=:1 sh "$HOME/.vnc/xstartup" >"$HOME/.vnc/xstartup.log" 2>&1 &
else
    vncserver :1 -geometry 1280x720 -depth 24 -localhost no 2>/dev/null &
fi

sleep 3

# Start websockify (noVNC web client proxy).
# Plain HTTP by default — browsers don't carry self-signed cert exceptions
# from the HTML page over to the wss:// WebSocket, so TLS makes the page
# load but breaks the actual VNC connection. Set NOVNC_TLS=true when serving
# beyond loopback.
if [ "${NOVNC_TLS:-false}" = "true" ] && [ -f /etc/novnc/ssl/cert.pem ] && [ -f /etc/novnc/ssl/key.pem ]; then
    websockify --cert /etc/novnc/ssl/cert.pem --key /etc/novnc/ssl/key.pem \
        --web /usr/share/novnc 6080 localhost:5901 &
else
    websockify --web /usr/share/novnc 6080 localhost:5901 &
fi

# Keep container alive
exec sleep infinity
