# Enterprise / Corporate Certificates

Place your corporate proxy or SSL-inspection root CA `.crt` / `.pem` files here.
They will be:

1. **Baked into images at build time** — the Dockerfile copies from this directory and runs `update-ca-certificates`.
2. **Mounted into containers at runtime** — `docker-compose.yml` bind-mounts this directory so you can update certs without rebuilding.

## Supported formats

- `.crt` (PEM-encoded, preferred)
- `.pem` (PEM-encoded)
- `.cer` (PEM or DER — PEM preferred)

## How to export your corporate root CA

### macOS
```bash
security find-certificate -a -c "YourCA" -p /Library/Keychains/System.keychain > certs/CorpRootCA.crt
```

### Windows (PowerShell)
```powershell
Get-ChildItem Cert:\LocalMachine\Root | Where-Object {$_.Subject -like "*YourCA*"} |
    ForEach-Object { [System.IO.File]::WriteAllText("certs\CorpRootCA.crt",
        "-----BEGIN CERTIFICATE-----`n" +
        [Convert]::ToBase64String($_.RawData, [System.Base64FormattingOptions]::InsertLineBreaks) +
        "`n-----END CERTIFICATE-----") }
```

### Linux
```bash
cp /usr/local/share/ca-certificates/CorpRootCA.crt certs/
```

## After adding certificates

Rebuild and restart:
```bash
docker compose build api
docker compose up -d api
```

Runtime-mounted certs are picked up on container restart (no rebuild needed).
