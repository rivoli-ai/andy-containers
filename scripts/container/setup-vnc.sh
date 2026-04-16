#!/bin/sh
# Set up VNC password and xstartup for XFCE4.
# Run during Docker image build (not at runtime), as the user that will own
# the VNC session. Uses $HOME so it works for either root or a non-root user.
# VNC_PASSWORD env var overrides the default.
set -e

VNC_HOME="${HOME:-/root}"
VNC_PASSWORD="${VNC_PASSWORD:-container}"

mkdir -p "$VNC_HOME/.vnc"

echo "$VNC_PASSWORD" | vncpasswd -f > "$VNC_HOME/.vnc/passwd"
chmod 600 "$VNC_HOME/.vnc/passwd"

# Create xstartup for XFCE4 session (sources env vars before starting desktop).
# startxfce4 silently exits under tigervnc on Ubuntu 24.04, so we launch
# xfce4-session directly via dbus-launch with XDG_RUNTIME_DIR set.
cat > "$VNC_HOME/.vnc/xstartup" << 'EOF'
#!/bin/sh
unset SESSION_MANAGER
unset DBUS_SESSION_BUS_ADDRESS
RUNTIME_DIR="${XDG_RUNTIME_DIR:-/tmp/runtime-$(id -un)}"
mkdir -p "$RUNTIME_DIR" && chmod 700 "$RUNTIME_DIR"
export XDG_RUNTIME_DIR="$RUNTIME_DIR"
# Source env vars injected by provisioning
[ -f /etc/profile ] && . /etc/profile 2>/dev/null
for f in /etc/profile.d/*.sh; do [ -f "$f" ] && . "$f" 2>/dev/null; done
exec dbus-launch --exit-with-session xfce4-session
EOF
chmod +x "$VNC_HOME/.vnc/xstartup"

# Make xfce4-terminal source profile on each new window
mkdir -p "$VNC_HOME/.config/xfce4"
cat > "$VNC_HOME/.config/xfce4/xinitrc" << 'EOF2'
[ -f /etc/profile ] && . /etc/profile 2>/dev/null
for f in /etc/profile.d/*.sh; do [ -f "$f" ] && . "$f" 2>/dev/null; done
EOF2

# Link noVNC index (writes to system path; ignore failure if not root)
ln -sf /usr/share/novnc/vnc.html /usr/share/novnc/index.html 2>/dev/null || true

echo "VNC setup complete for $(id -un) (HOME=$VNC_HOME)"
