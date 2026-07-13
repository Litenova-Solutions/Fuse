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
