FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

# Desktop + VNC + noVNC + base dev tooling.
# Deliberately slim: no language SDKs baked in — users bring them via
# project setup or a follow-on template layer. This matches the
# devpilot default sandbox, which was a generic XFCE + Zed environment.
RUN apt-get update && apt-get install -y \
    xfce4 xfce4-terminal dbus-x11 \
    tigervnc-standalone-server tigervnc-common \
    novnc websockify \
    ca-certificates curl wget git openssh-server sudo openssl \
    tmux dtach locales vim \
    libfontconfig1 libgl1 libglib2.0-0 libvulkan1 mesa-vulkan-drivers \
    && locale-gen en_US.UTF-8 \
    && rm -rf /var/lib/apt/lists/*

ENV LANG=C.UTF-8 LC_ALL=C.UTF-8

# Zed — installed as root to /root/.local/zed.app, then symlinked
# onto PATH so it's reachable from any shell or the XFCE menu.
RUN curl -fsSL https://zed.dev/install.sh | bash && \
    ln -sf /root/.local/zed.app/bin/zed /usr/local/bin/zed

COPY --from=scripts setup-vnc.sh /tmp/setup-vnc.sh
COPY --from=scripts generate-ssl-cert.sh /tmp/generate-ssl-cert.sh
COPY --from=scripts start-vnc.sh /start.sh
RUN chmod +x /tmp/*.sh /start.sh && /tmp/setup-vnc.sh && /tmp/generate-ssl-cert.sh && rm -f /tmp/*.sh

RUN mkdir -p /run/sshd && \
    sed -i 's/#\?PermitRootLogin.*/PermitRootLogin yes/' /etc/ssh/sshd_config && \
    echo 'root:container' | chpasswd && ssh-keygen -A

EXPOSE 6080 22
CMD ["/start.sh"]
