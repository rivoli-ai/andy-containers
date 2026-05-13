# Default .bashrc for the `conductor` user inside conductor-terminal
# images. rivoli-ai/conductor#1029 (M1.9.2). Kept short — anything
# user-specific belongs in their shell-init dotfiles, which they can
# layer on top via a workspace mount.

# Source the system-wide bashrc first so distro-level aliases land.
if [[ -f /etc/bashrc ]]; then
    # shellcheck disable=SC1091
    . /etc/bashrc
fi

# Prompt: hostname, current dir, exit code on non-zero, two-line for
# better readability in long-path projects.
PS1='\[\e[36m\][\u@\h \w]\[\e[0m\]\n\$ '

# Sane defaults: vi-style editing is the assistant CLIs' assumption,
# colour `ls`, immediate history persistence so a crashed shell
# doesn't lose its scrollback.
set -o vi
alias ls='ls --color=auto'
export EDITOR=vim
export VISUAL=vim
shopt -s histappend
PROMPT_COMMAND='history -a'

# Conductor-managed env vars flow in from the runtime
# (ANDY_PROXY_BASE_URL, ANDY_SERVICE_TOKEN, etc. — set by
# andy-containers' #944 wiring). Expose them when present so an
# assistant CLI inheriting the user's environment picks them up.
[[ -n "${ANDY_PROXY_BASE_URL:-}" ]] && export ANDY_PROXY_BASE_URL
[[ -n "${ANDY_SERVICE_TOKEN:-}" ]]  && export ANDY_SERVICE_TOKEN
