#!/usr/bin/env bash
# Installs the Fuse tool packages in <feed> into a tool path and checks the installed command end to end, the way a
# harness runs it: the version, whether the command is the native client, an introduced error in a fresh repository,
# the pre-shell and post-edit hooks, the MCP server, and that every call reached one engine of the same build.
#
# Usage: .github/scripts/check-installed-tool.sh <feed> <version> <native|managed>
# Runs in bash on Linux, macOS and Windows (Git Bash). Needs the .NET SDK and git.
set -euo pipefail

feed=$(cd "${1:?feed}" && pwd)
version=${2:?version}
expect=${3:?native or managed}
work=$(mktemp -d)
tools="$work/tools"

dotnet tool install --tool-path "$tools" Fuse --version "$version" --add-source "$feed"
fuse="$tools/fuse"
[ -e "$fuse.exe" ] && fuse="$fuse.exe"
if [ ! -e "$fuse" ]; then
    echo "check: dotnet tool install created no fuse command in $tools:"; ls -la "$tools"; exit 1
fi

reported=$("$fuse" --version)
if [ "$reported" != "$version" ]; then echo "check: fuse --version printed '$reported', expected '$version'"; exit 1; fi

# The native client is megabytes; an apphost shim or the managed entry point is a few hundred kilobytes at most.
target=$fuse
if [ -L "$fuse" ]; then target="$tools/$(readlink "$fuse")"; fi
size=$(wc -c < "$target" | tr -d ' ')
if [ "$expect" = native ] && [ "$size" -lt 5000000 ]; then echo "check: $target is $size bytes; expected the native client"; exit 1; fi
if [ "$expect" = managed ] && [ "$size" -ge 5000000 ]; then echo "check: $target is $size bytes; expected no native client"; exit 1; fi
echo "check: the fuse command is $target ($size bytes, $expect)"

repo="$work/repo"
mkdir -p "$repo" && cd "$repo"
git init -q
git config user.email "ci@fuse.invalid"
git config user.name "Fuse CI"
dotnet new classlib -n Lib -o Lib >/dev/null
printf 'bin/\nobj/\n' > .gitignore
git add -A && git commit -qm init
dotnet restore Lib >/dev/null
echo 'namespace Lib; public class Broken { public int M() => missing; }' > Lib/Broken.cs

for attempt in 1 2; do
    set +e
    output=$("$fuse" check 2>&1)
    code=$?
    set -e
    echo "$output"
    if [ "$code" != "1" ]; then echo "check: fuse check exited $code, expected 1 (error introduced)"; exit 1; fi
done

# Git Bash, the shell Claude Code runs hooks in on Windows, finds the command by name on the PATH.
rewritten=$(echo '{"tool_input":{"command":"dotnet test"}}' | PATH="$tools:$PATH" fuse hook claude pre-shell)
case "$rewritten" in *'"command":"fuse test"'*) ;; *) echo "check: pre-shell answered '$rewritten'"; exit 1 ;; esac

file=$(cd Lib && pwd)/Broken.cs
if command -v cygpath >/dev/null 2>&1; then file=$(cygpath -m "$file"); fi
set +e
report=$(printf '{"tool_input":{"file_path":"%s"},"cwd":"%s"}' "$file" "$(dirname "$file")" | "$fuse" hook claude post-edit 2>&1 >/dev/null)
code=$?
set -e
if [ "$code" != "2" ] || [[ "$report" != *CS0103* ]]; then echo "check: post-edit exited $code with '$report'"; exit 1; fi

# An MCP host keeps standard input open for the session; the server stops at its end without answering what is pending.
tools_list=$( { printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"ci","version":"1"}}}' \
    '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'; sleep 5; } | "$fuse" mcp)
case "$tools_list" in *fuse_check*) ;; *) echo "check: the MCP server listed '$tools_list'"; exit 1 ;; esac

# One engine served every call: an engine that saw a request of another build id would have restarted.
repo_path=$(pwd -P)
if command -v cygpath >/dev/null 2>&1; then repo_path=$(cygpath -w -l "$repo_path"); fi
log=$(for file in "${LOCALAPPDATA:-/nonexistent}"/fuse/repos/*/engine.log "$HOME"/.local/share/fuse/repos/*/engine.log \
    "$HOME/Library/Application Support"/fuse/repos/*/engine.log; do
    if [ -f "$file" ] && grep -qF "started for $repo_path (pid" "$file"; then cat "$file"; fi
done)
starts=$(echo "$log" | grep -cF "started for $repo_path (pid" || true)
if [ "$starts" != "1" ] || echo "$log" | grep -qF "differs; exiting"; then
    echo "check: the engine for $repo_path started $starts time(s), expected once for this build:"
    echo "$log"
    exit 1
fi

echo "check: the installed fuse $version passed"
