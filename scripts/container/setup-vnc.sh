#!/bin/sh
# Set up VNC password and xstartup for XFCE4
# Run during Docker image build (not at runtime)
set -e

mkdir -p /root/.vnc

# Set VNC password
echo "container" | vncpasswd -f > /root/.vnc/passwd
chmod 600 /root/.vnc/passwd

# Create xstartup for XFCE4 session
cat > /root/.vnc/xstartup << 'EOF'
#!/bin/sh
unset SESSION_MANAGER
unset DBUS_SESSION_BUS_ADDRESS
exec startxfce4
EOF
chmod +x /root/.vnc/xstartup

# Link noVNC index
ln -sf /usr/share/novnc/vnc.html /usr/share/novnc/index.html 2>/dev/null || true

echo "VNC setup complete"
