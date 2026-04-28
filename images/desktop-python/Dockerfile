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

# code-server installs system-wide via the official script.
RUN curl -fsSL https://code-server.dev/install.sh | sh -s -- --method standalone

# Non-root sandbox user with passwordless sudo for sshd / system dbus.
RUN useradd -m -s /bin/bash sandbox && \
    echo "sandbox ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/sandbox && \
    chmod 440 /etc/sudoers.d/sandbox && \
    echo 'sandbox:container' | chpasswd

RUN mkdir -p /run/sshd && \
    sed -i 's/#\?PermitRootLogin.*/PermitRootLogin no/' /etc/ssh/sshd_config && \
    ssh-keygen -A

COPY --from=scripts generate-ssl-cert.sh /tmp/generate-ssl-cert.sh
COPY --from=scripts conductor-tmux.conf /etc/conductor-tmux.conf
RUN chmod +x /tmp/generate-ssl-cert.sh && /tmp/generate-ssl-cert.sh && rm -f /tmp/generate-ssl-cert.sh
COPY --from=scripts start-vnc.sh /start.sh
RUN chmod +x /start.sh

USER sandbox
WORKDIR /home/sandbox

COPY --from=scripts --chown=sandbox:sandbox setup-vnc.sh /tmp/setup-vnc.sh
RUN chmod +x /tmp/setup-vnc.sh && /tmp/setup-vnc.sh && rm -f /tmp/setup-vnc.sh

EXPOSE 6080 8080 22
CMD ["/start.sh"]
