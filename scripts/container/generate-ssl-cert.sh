#!/bin/sh
# Generate a self-signed SSL certificate for noVNC HTTPS
# Run during Docker image build
set -e

mkdir -p /etc/novnc/ssl

openssl req -x509 -nodes -days 3650 -newkey rsa:2048 \
    -keyout /etc/novnc/ssl/key.pem \
    -out /etc/novnc/ssl/cert.pem \
    -subj "/CN=localhost" \
    -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" \
    2>/dev/null

echo "SSL certificate generated at /etc/novnc/ssl/"
