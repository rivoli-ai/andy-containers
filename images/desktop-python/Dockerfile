FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y \
    xfce4 xfce4-terminal dbus-x11 \
    tigervnc-standalone-server tigervnc-common \
    novnc websockify \
    ca-certificates curl wget git openssh-server sudo openssl \
    tmux dtach locales \
    python3 python3-pip python3-venv python3-tk \
    && locale-gen en_US.UTF-8 \
    && ln -sf /usr/bin/python3 /usr/bin/python \
    && ln -sf /usr/bin/pip3 /usr/bin/pip \
    && rm -rf /var/lib/apt/lists/*

ENV LANG=C.UTF-8 LC_ALL=C.UTF-8

RUN curl -fsSL https://code-server.dev/install.sh | sh -s -- --method standalone

COPY --from=scripts setup-vnc.sh /tmp/setup-vnc.sh
COPY --from=scripts generate-ssl-cert.sh /tmp/generate-ssl-cert.sh
COPY --from=scripts start-vnc.sh /start.sh
RUN chmod +x /tmp/*.sh /start.sh && /tmp/setup-vnc.sh && /tmp/generate-ssl-cert.sh && rm -f /tmp/*.sh

RUN mkdir -p /run/sshd && \
    sed -i 's/#\?PermitRootLogin.*/PermitRootLogin yes/' /etc/ssh/sshd_config && \
    echo 'root:container' | chpasswd && ssh-keygen -A

EXPOSE 6080 8080 22
CMD ["/start.sh"]
