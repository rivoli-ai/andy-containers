FROM alpine:3.20

# Desktop + VNC + noVNC + dev tools
RUN apk add --no-cache \
    xfce4 xfce4-terminal dbus dbus-x11 \
    tigervnc novnc websockify \
    ca-certificates curl wget git openssh openssl tmux dtach bash sudo \
    dotnet8-sdk \
    && mkdir -p /run/dbus

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    LANG=C.UTF-8

# Setup scripts from shared scripts/container/
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
