#!/bin/sh
set -e

# Trust custom CA certificates mounted at runtime under /usr/local/share/ca-certificates/custom/
if ls /usr/local/share/ca-certificates/custom/*.crt 1>/dev/null 2>&1 || \
   ls /usr/local/share/ca-certificates/custom/*.pem 1>/dev/null 2>&1; then
    echo "Trusting custom CA certificates..."
    update-ca-certificates 2>/dev/null || true
fi

exec "$@"
