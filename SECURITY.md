# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 4.1.x   | Yes       |
| 4.0.x   | Yes       |

Security fixes ship in patch releases on the current minor version. Upgrade to the latest 4.1.x release.

## Reporting a Vulnerability

**Do not open a public GitHub issue for security reports.**

Email **security@litenova.com** with:

- A description of the issue and the impact you believe it has
- Steps to reproduce, or a minimal proof of concept if you have one
- The Fuse version (`fuse --version`) and your OS

We aim to acknowledge reports within 3 business days and will coordinate disclosure timing with you.

## Scope

In scope:

- The `fuse` CLI and MCP server (`fuse mcp serve`, `fuse host`)
- Remote code execution or arbitrary file write outside the declared workspace root
- Secret leakage in indexed output or logs

Out of scope:

- Issues in third-party MCP clients (for example Cursor, Claude Code, Copilot, Windsurf, Cline)
- Denial of service from indexing very large repositories on under-provisioned machines (document operational limits instead)

## Safe Defaults

Fuse indexes and serves code locally. It does not send your repository to a remote model as part of indexing. Verify MCP client configuration if you use cloud-hosted models alongside Fuse tools.

## Local-Trust Host IPC

`fuse host` exposes a JSON-RPC surface over a named pipe (Windows) or Unix domain socket (Linux and macOS). Fuse treats this as **local-trust IPC**, not a network-authenticated service.

**Any local user who can open the pipe can call RPC** once they hold the session token from `fuse/handshake`. On a typical developer workstation, any process running as your OS user can connect to local pipes and sockets. The predictable per-repository endpoint name is intentional (clients find the daemon without an out-of-band address), not a secret.

Three mechanisms reduce casual abuse without pretending the endpoint is internet-hardened:

| Layer | Mechanism | Limit |
|-------|-----------|-------|
| Endpoint | Pipe or socket name derived from the repository root | Does not hide the endpoint from other same-user processes |
| Handshake | Random session token required on every method after `fuse/handshake` | Not a substitute for login; a same-user peer can observe the handshake |
| Served root | Every RPC with a `root` argument must match the daemon's `--directory` | Blocks pivoting to a different repository path through the RPC surface |

On Windows, set `FUSE_HOST_RESTRICT_PIPE=1` before starting `fuse host` to restrict the named-pipe ACL to the current user only (off by default). Unix domain sockets remain governed by filesystem permissions under the system temp directory.

For the full threat model, exposure table, and operational guidance, see [Host RPC threat model](https://fuse.codes/docs/internals/host-rpc) (`site/content/docs/internals/host-rpc.mdx` in the repository).
