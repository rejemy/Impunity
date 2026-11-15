# Standalone HTTPS and WSS

Implemented on 2026-11-15. The standalone server terminates TLS in Kestrel and serves the existing HTTP routes and `/ws` endpoint over HTTPS/WSS. Certificates reload without restarting listeners or disconnecting existing players. The Impunity wire protocol is unchanged.

## Configuration

See [`config.https.example.json`](../../ImpunityStandaloneServer/config.https.example.json) for a complete example. These settings supplement the existing game/world configuration:

```json
{
  "http_enabled": false,
  "http_port": 29653,
  "https": {
    "enabled": true,
    "port": 443,
    "certificate_path": "/etc/letsencrypt/live/impunity.example.com/fullchain.pem",
    "private_key_path": "/etc/letsencrypt/live/impunity.example.com/privkey.pem",
    "reload_interval_seconds": 60
  },
  "allowed_origins": ["https://game.example.com"],
  "webrtc": {
    "enabled": true,
    "udp_port": 29656,
    "public_address": "impunity.example.com"
  }
}
```

Existing configurations keep HTTP enabled on port 29653; HTTPS is disabled unless `https.enabled` is true. HTTP and HTTPS can coexist on different ports. At least one web listener must be enabled. Enabled web ports must be nonzero and distinct from one another and the raw TCP port. TLS applies to the web endpoints; the raw TCP/UDP transport remains separate.

Certificate paths may be absolute or relative to `config.json`. Use the stable Certbot `live` paths, including the full intermediate chain. The reload interval defaults to 60 seconds and accepts 1–86400 seconds. Endpoints, paths and origin policy are read at startup; certificate file contents are checked periodically. Clients connect directly to `https://impunity.example.com` and `wss://impunity.example.com/ws` (include the HTTPS port when it is not 443). The server does not redirect HTTP/WS traffic automatically.

`allowed_origins` lists the origins of pages allowed to use the server, including non-default ports. Entries must be HTTP(S) origins without credentials, paths, queries, fragments or wildcards. A trailing slash/default port is normalized. An explicitly empty list is rejected, avoiding the framework's interpretation of an empty WebSocket allowlist as unrestricted access.

The same explicit list configures HTTP CORS and WebSocket origin checks independently. HTTP GET and preflight requests are supported without cookie credentials. Disallowed HTTP origins receive no CORS permission; disallowed WebSocket origins cannot upgrade. Native clients without an `Origin` header remain supported. Literal `Origin: null` is rejected under an explicit list. Origins are not authentication; Impunity's world/password handshake still applies.

When `allowed_origins` is omitted, existing behavior is preserved: `server_hostname`, if present, supplies HTTP and HTTPS CORS origins, and WebSocket origins are unrestricted. An explicit list supersedes `server_hostname`. Include the game's own origin even for a same-origin deployment when using an explicit list.

## Certificate lifecycle

[`HttpsCertificateService`](../../ImpunityStandaloneServer/src/services/HttpsCertificateService.cs) loads and validates the initial certificate before game services start. It supports RSA and ECDSA private keys. An unreadable certificate, mismatched key, invalid validity window, inappropriate server-auth usage or invalid chain fails startup with an error.

Each poll reopens both configured paths, follows current symlinks and compares content hashes. Changed material is parsed and validated as a complete candidate. A second read detects files changing during loading. The fullchain intermediates are supplied explicitly to an immutable `SslStreamCertificateContext`; the server does not depend on downloading intermediates during a handshake. The server may use a private CA; clients retain responsibility for validating trust and hostname.

Kestrel's per-connection TLS callback reads the current context atomically. New full handshakes use the replacement. Established HTTPS/WSS connections and WebRTC peers continue without reconnection. Successful generations are retained until the web host stops, avoiding disposal races with handshakes and live TLS streams. Unchanged polls allocate no new certificate generation.

A bad replacement leaves the previous generation active and is retried. Repeated reload failures are logged at most once per five minutes. Successful loads log fingerprint and expiry. Certificates within 14 days of expiry produce a daily warning; retaining a certificate does not extend its validity. Private-key contents are never logged. Polling stops and certificate objects are released after the web host shuts down.

The implementation does not use Kestrel endpoint reconfiguration for renewal: that mechanism can restart listeners and interrupt clients. No Certbot deploy hook is required for the certificate reload itself.

## Local test certificates

From the repository root:

```bash
ImpunityStandaloneServer/tools/generate-test-certificate
```

This creates `ImpunityStandaloneServer/.test-certs/fullchain.pem`, `privkey.pem` and `ca.pem`. The certificate covers `localhost`, `127.0.0.1` and `::1`, lasts 30 days and is signed by a newly generated private test CA. The CA's private key is discarded. Files are private to the current user; the default directory is ignored by Git and excluded from server build/publish items. Existing files are never overwritten. An optional output directory and DNS hostname can be passed as the first and second arguments. OpenSSL with `-addext` support is required.

For local use, keep the existing game/world settings and set:

```json
{
  "https": {
    "enabled": true,
    "port": 8443,
    "certificate_path": ".test-certs/fullchain.pem",
    "private_key_path": ".test-certs/privkey.pem",
    "reload_interval_seconds": 5
  }
}
```

After starting the standalone server with that configuration, from the repository root:

```bash
curl --cacert ImpunityStandaloneServer/.test-certs/ca.pem https://localhost:8443/info
```

The generator does not install machine-wide or browser trust. Browsers need an explicitly trusted test CA, or a normally trusted Certbot certificate. The automated tests use a private CA scoped to each test client and retain certificate-chain and hostname checks.

## Client connections and discovery

The existing full-URL `MakeWebsocketRemoteConnection` and `MakeWebSocketClient` overloads accept WSS. The host/port convenience overloads still construct WS URLs. For the swarm demo, provide `?server=wss://impunity.example.com/ws`; automatic URL selection uses the page scheme but retains the demo's default port 29653.

`ImpunityHTTPServerFinder.GetServerWorlds` and `GetServerWorldStatus` accept a full HTTP(S) URL string without a path, for example `"https://impunity.example.com:8443"`. Returned `ServerInfo.WebSocketUrl` preserves that scheme and port (`wss://impunity.example.com:8443/ws`); `Hostname` and `Port` still describe the independent TCP endpoint. Use `WebSocketUrl` with the WebSocket connection factory in a browser. Bare hostnames are not accepted. Omitted ports use the scheme default (80 for HTTP, 443 for HTTPS); bracketed IPv6 URLs are supported.

## Certbot and public deployment

Certbot remains responsible for certificate issuance and renewal. The service account needs directory traversal and private-key read permissions across renewal, including the symlink targets under `archive`. A container must expose those targets as well as the `live` links. Arrange bind permissions or host port mapping for port 443.

Use a renewal challenge that does not stop the game server: DNS validation, an existing web server's webroot, or Certbot standalone on a free port 80. Impunity does not serve ACME challenge files or static WebGL assets; the game can remain on its existing HTTPS host. Its CSP, if configured, must allow the WSS destination.

WebRTC retains its own DTLS certificates. WSS protects the signaling that exchanges their fingerprints. It still needs its configured public UDP port to reach the same process as the WebSocket. `webrtc.public_address` is resolved once at startup; behind NAT, forward the same UDP port number. A TLS reverse proxy or CDN does not automatically forward RTC UDP. Restrictive networks use the existing WSS fallback; TURN is not implemented.

## Validation and limits

The automated HTTPS tests generate a private root, intermediate and RSA/ECDSA leaves. They exercise the actual standalone executable, including HTTPS-only and dual listeners, chain delivery, hostname/trust rejection, origin/preflight policy, invalid startup configuration, key rotation, missing/malformed/expired replacements, Certbot-style symlink retargeting, relative paths, concurrent TLS handshakes and unchanged polls. Both WSS-only and WSS-plus-WebRTC legs run the complete transport battery; renewal tests retain live entity replication and data channels, and fallback tests verify delivery over WSS.

Run them with:

```bash
./test.sh -c Release --filter "Category=Https"
```

A localhost smoke test also verified the certificate generator's output using its test CA and hostname validation, followed by clean server shutdown. Tests were run on macOS arm64. The WebGL C# target compiles; a real HTTPS-hosted Unity WebGL build, public NAT/UDP routing, Firefox/Safari and other host operating systems still need deployment validation. No certificate-validation bypass was added to shipped clients.

## References

- [Certbot certificate paths, PEM contents and permissions](https://eff-certbot.readthedocs.io/en/stable/using.html#where-are-my-certificates)
- [Certbot challenge methods](https://eff-certbot.readthedocs.io/en/stable/using.html#getting-certificates-and-choosing-plugins)
- [Kestrel endpoint configuration and reload behavior](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)
- [Kestrel per-connection TLS callback](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.https.tlshandshakecallbackoptions?view=aspnetcore-10.0)
- [.NET TLS certificate contexts and intermediate chains](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-best-practices)
- [ASP.NET Core WebSocket origin restrictions](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0#websocket-origin-restriction)
