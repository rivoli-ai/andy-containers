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

# Non-root sandbox user. Zed refuses to run as root, and running the desktop
# as a regular user is defense-in-depth even if the container is isolated.
# Passwordless sudo lets start-vnc.sh bring up root-only services (sshd,
# system dbus) without prompting.
RUN useradd -m -s /bin/bash sandbox && \
    echo "sandbox ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/sandbox && \
    chmod 440 /etc/sudoers.d/sandbox && \
    echo 'sandbox:container' | chpasswd

# SSH: password login for sandbox, root login disabled.
RUN mkdir -p /run/sshd && \
    sed -i 's/#\?PermitRootLogin.*/PermitRootLogin no/' /etc/ssh/sshd_config && \
    ssh-keygen -A

# SSL cert for noVNC HTTPS (writes to /etc/novnc — must run as root).
COPY --from=scripts generate-ssl-cert.sh /tmp/generate-ssl-cert.sh
RUN chmod +x /tmp/generate-ssl-cert.sh && /tmp/generate-ssl-cert.sh && rm -f /tmp/generate-ssl-cert.sh

# start-vnc.sh is the container CMD — install system-wide so it's reachable.
COPY --from=scripts start-vnc.sh /start.sh
RUN chmod +x /start.sh

USER sandbox
WORKDIR /home/sandbox

# Zed installed under sandbox's home so it runs as that user.
RUN curl -fsSL https://zed.dev/install.sh | bash
ENV PATH="/home/sandbox/.local/bin:${PATH}"

# VNC password + xstartup under /home/sandbox/.vnc (setup-vnc.sh honors $HOME).
COPY --from=scripts --chown=sandbox:sandbox setup-vnc.sh /tmp/setup-vnc.sh
RUN chmod +x /tmp/setup-vnc.sh && /tmp/setup-vnc.sh && rm -f /tmp/setup-vnc.sh

# Desktop launcher for Zed (appears on the XFCE desktop).
RUN mkdir -p /home/sandbox/Desktop && \
    printf '%s\n' \
        '[Desktop Entry]' \
        'Type=Application' \
        'Name=Zed' \
        'Exec=/home/sandbox/.local/bin/zed' \
        'Terminal=false' \
        'Categories=Development;IDE;' \
        > /home/sandbox/Desktop/zed.desktop && \
    chmod +x /home/sandbox/Desktop/zed.desktop

EXPOSE 6080 22
CMD ["/start.sh"]
