FROM alpine:3.20

# Desktop + VNC + noVNC + dev tools
RUN apk add --no-cache \
    xfce4 xfce4-terminal dbus dbus-x11 \
    tigervnc novnc websockify \
    ca-certificates curl wget git openssh openssl tmux dtach bash sudo \
    && mkdir -p /run/dbus

# .NET 10 via install script (not in Alpine repos yet)
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 && \
    ln -sf /root/.dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/root/.dotnet \
    PATH=$PATH:/root/.dotnet:/root/.dotnet/tools \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    LANG=C.UTF-8

# Setup scripts
COPY --from=scripts setup-vnc.sh /tmp/setup-vnc.sh
COPY --from=scripts generate-ssl-cert.sh /tmp/generate-ssl-cert.sh
COPY --from=scripts start-vnc.sh /start.sh

RUN chmod +x /tmp/*.sh /start.sh && \
    /tmp/setup-vnc.sh && \
    /tmp/generate-ssl-cert.sh && \
    rm -f /tmp/*.sh

# SSH setup
RUN ssh-keygen -A && \
    echo 'PermitRootLogin yes' >> /etc/ssh/sshd_config && \
    echo 'root:container' | chpasswd

EXPOSE 6080 22
CMD ["/start.sh"]
