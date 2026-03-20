#!/usr/bin/env bash
# NOTE: SSH is now auto-provisioned when creating containers with SshEnabled=true.
# This script remains as a fallback/debugging tool for manual SSH setup.
# See stories #3, #10, #14 for the automated SSH provisioning flow.
set -euo pipefail

CONTAINER_ID="${1:-}"
PASSWORD="${2:-changeme}"

if [ -z "$CONTAINER_ID" ]; then
    echo "Usage: $0 <container-id> [password]"
    echo ""
    echo "Sets up SSH in an Apple container and connects to it."
    echo ""
    echo "Examples:"
    echo "  $0 apple-test"
    echo "  $0 apple-test mypassword"
    echo ""
    echo "Running containers:"
    container list 2>/dev/null || echo "  (container CLI not available)"
    exit 1
fi

# Get container IP
ADDR=$(container list --format json 2>/dev/null | python3 -c "
import sys, json
data = json.load(sys.stdin)
for c in data:
    if c.get('id') == '$CONTAINER_ID' or c.get('name') == '$CONTAINER_ID':
        nets = c.get('networks', [])
        if nets:
            addr = nets[0].get('address', '')
            print(addr.split('/')[0])
            break
" 2>/dev/null)

if [ -z "$ADDR" ]; then
    echo "Error: Could not find container '$CONTAINER_ID' or it has no IP address."
    echo ""
    echo "Running containers:"
    container list
    exit 1
fi

echo "Container: $CONTAINER_ID ($ADDR)"
echo "Setting up SSH..."

# Install and configure SSH
container exec "$CONTAINER_ID" sh -c "
    apt-get update -qq && apt-get install -y -qq openssh-server > /dev/null 2>&1
    mkdir -p /run/sshd /root/.ssh
    sed -i 's/#*PermitRootLogin.*/PermitRootLogin yes/' /etc/ssh/sshd_config
    echo 'root:$PASSWORD' | chpasswd
    /usr/sbin/sshd 2>/dev/null || true
    echo 'SSH ready'
"

echo "Connecting to $ADDR..."
echo "Password: $PASSWORD"
echo ""

exec ssh -o StrictHostKeyChecking=accept-new "root@$ADDR"
