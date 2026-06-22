#!/bin/sh
# Fuse installer for Linux x64.
#
#   curl -fsSL https://fuse.codes/install.sh | sh
#
# Downloads the latest self-contained Fuse binary from GitHub Releases, verifies
# its checksum, and installs it to ~/.local/bin (override with FUSE_INSTALL_DIR).
# No .NET SDK is required. To pin a version, set FUSE_VERSION=v2.0.0.
#
# .NET developers can instead use: dotnet tool install -g Fuse
set -eu

repo="Litenova-Solutions/Fuse"
bindir="${FUSE_INSTALL_DIR:-$HOME/.local/bin}"

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Linux) os="linux" ;;
  Darwin)
    echo "macOS is not packaged as a binary yet. Install with: dotnet tool install -g Fuse" >&2
    exit 1 ;;
  *)
    echo "Unsupported OS: $os. Install with: dotnet tool install -g Fuse" >&2
    exit 1 ;;
esac

case "$arch" in
  x86_64 | amd64) arch="x64" ;;
  *)
    echo "Unsupported architecture: $arch. Install with: dotnet tool install -g Fuse" >&2
    exit 1 ;;
esac

version="${FUSE_VERSION:-}"
if [ -z "$version" ]; then
  version="$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" \
    | grep '"tag_name"' | head -n1 | cut -d '"' -f4)"
fi
if [ -z "$version" ]; then
  echo "Could not determine the latest version. Set FUSE_VERSION and retry." >&2
  exit 1
fi

num="${version#v}"
asset="fuse-${num}-${os}-${arch}.tar.gz"
base="https://github.com/$repo/releases/download/$version"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $asset ($version) ..."
curl -fsSL "$base/$asset" -o "$tmp/$asset"
curl -fsSL "$base/SHA256SUMS.txt" -o "$tmp/SHA256SUMS.txt"

echo "Verifying checksum ..."
( cd "$tmp" && grep " ${asset}\$" SHA256SUMS.txt | sha256sum -c - >/dev/null )

tar -xzf "$tmp/$asset" -C "$tmp"
mkdir -p "$bindir"
install -m 0755 "$tmp/fuse" "$bindir/fuse"

echo "Installed fuse $version to $bindir/fuse"
case ":$PATH:" in
  *":$bindir:"*) ;;
  *) echo "Add $bindir to your PATH to run 'fuse'." ;;
esac
