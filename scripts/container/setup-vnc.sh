#!/bin/sh
# Set up VNC password and xstartup for XFCE4
# Run during Docker image build (not at runtime)
set -e

mkdir -p /root/.vnc

# Set VNC password
echo "container" | vncpasswd -f > /root/.vnc/passwd
chmod 600 /root/.vnc/passwd

# Create xstartup for XFCE4 session (sources env vars before starting desktop)
cat > /root/.vnc/xstartup << 'EOF'
#!/bin/sh
unset SESSION_MANAGER
unset DBUS_SESSION_BUS_ADDRESS
# Source env vars injected by provisioning
[ -f /etc/profile ] && . /etc/profile 2>/dev/null
for f in /etc/profile.d/*.sh; do [ -f "$f" ] && . "$f" 2>/dev/null; done
exec startxfce4
EOF
chmod +x /root/.vnc/xstartup

# Make xfce4-terminal source profile on each new window
mkdir -p /root/.config/xfce4
cat > /root/.config/xfce4/xinitrc << 'EOF2'
[ -f /etc/profile ] && . /etc/profile 2>/dev/null
for f in /etc/profile.d/*.sh; do [ -f "$f" ] && . "$f" 2>/dev/null; done
EOF2

# Link noVNC index
ln -sf /usr/share/novnc/vnc.html /usr/share/novnc/index.html 2>/dev/null || true

echo "VNC setup complete"
