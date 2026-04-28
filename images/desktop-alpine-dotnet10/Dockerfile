FROM alpine:3.20

# Desktop + VNC + noVNC + dev tools.
RUN apk add --no-cache \
    xfce4 xfce4-terminal dbus dbus-x11 \
    tigervnc novnc websockify \
    ca-certificates curl wget git openssh openssl tmux dtach bash sudo shadow \
    icu-libs libgcc libssl3 libstdc++ zlib krb5-libs \
    && mkdir -p /run/dbus

# Non-root sandbox user with passwordless sudo.
RUN adduser -D -s /bin/bash sandbox && \
    echo "sandbox ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/sandbox && \
    chmod 440 /etc/sudoers.d/sandbox && \
    echo 'sandbox:container' | chpasswd

RUN ssh-keygen -A && \
    sed -i 's/#\?PermitRootLogin.*/PermitRootLogin no/' /etc/ssh/sshd_config

COPY --from=scripts generate-ssl-cert.sh /tmp/generate-ssl-cert.sh
COPY --from=scripts conductor-tmux.conf /etc/conductor-tmux.conf
RUN chmod +x /tmp/generate-ssl-cert.sh && /tmp/generate-ssl-cert.sh && rm -f /tmp/generate-ssl-cert.sh
COPY --from=scripts start-vnc.sh /start.sh
RUN chmod +x /start.sh

USER sandbox
WORKDIR /home/sandbox

# .NET 10 via install script under sandbox's home (not in Alpine repos yet).
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
ENV DOTNET_ROOT=/home/sandbox/.dotnet \
    PATH=/home/sandbox/.dotnet:/home/sandbox/.dotnet/tools:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    LANG=C.UTF-8

COPY --from=scripts --chown=sandbox:sandbox setup-vnc.sh /tmp/setup-vnc.sh
RUN chmod +x /tmp/setup-vnc.sh && /tmp/setup-vnc.sh && rm -f /tmp/setup-vnc.sh

EXPOSE 6080 22
CMD ["/start.sh"]
