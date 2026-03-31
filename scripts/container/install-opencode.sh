#!/bin/sh
# Install OpenCode AI coding assistant
# Downloads the binary from GitHub releases and creates a wrapper
# that writes .opencode.json config before each launch
set -e

ARCH=$(uname -m | sed 's/aarch64/arm64/' | sed 's/x86_64/x86_64/')

echo "Installing OpenCode for ${ARCH}..."

cd /tmp
curl -fsSL -o oc.tar.gz \
    "https://github.com/opencode-ai/opencode/releases/latest/download/opencode-linux-${ARCH}.tar.gz"
tar xzf oc.tar.gz
mv opencode /usr/local/bin/opencode-bin
chmod +x /usr/local/bin/opencode-bin
rm -f oc.tar.gz LICENSE README.md

# Create wrapper that writes config before each launch
cat > /usr/local/bin/opencode << 'WRAPPER'
#!/bin/sh
M=${LLM_MODEL:-gpt-4o}
cat > "$HOME/.opencode.json" << CONF
{
  "providers": {
    "openai": {
      "apiKey": "env:OPENAI_API_KEY"
    }
  },
  "agents": {
    "coder": { "model": "$M", "maxTokens": 5000 },
    "task": { "model": "$M", "maxTokens": 5000 },
    "title": { "model": "$M", "maxTokens": 80 }
  }
}
CONF
exec /usr/local/bin/opencode-bin "$@"
WRAPPER
chmod +x /usr/local/bin/opencode

echo "OpenCode $(opencode-bin -v 2>&1 | tail -1) installed"
