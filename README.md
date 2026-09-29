# Fuse

Near-instant compiler feedback for AI coding agents on .NET.

Fuse keeps your solution compiled in memory with Roslyn and checks each edit against it, up to 10.0x faster than `dotnet build`. The agent gets only the errors its edit introduced, including breaks in other projects, and when it runs `dotnet test`, only the tests the change can reach run and only failures are printed.

Fuse needs the .NET 10 SDK. Install the tool, then run `fuse init` in your repository to register its hooks with your agent:

```bash
dotnet tool install -g Fuse
fuse init
```

![Fuse's time as a share of the dotnet command it replaces](https://raw.githubusercontent.com/Litenova-Solutions/Fuse/main/site/benefits.svg)

## Learn more

- [Getting started](https://github.com/Litenova-Solutions/Fuse/blob/main/docs/getting-started.md): set Fuse up in a sample repository and see what it reports.
- [Commands](https://github.com/Litenova-Solutions/Fuse/blob/main/docs/commands.md): every command, its output and its messages.
- [Harnesses](https://github.com/Litenova-Solutions/Fuse/blob/main/docs/harnesses.md): what `fuse init` sets up for Claude Code, Cursor, Gemini CLI, Codex, GitHub Copilot CLI, OpenCode and VS Code.
- [How it works](https://github.com/Litenova-Solutions/Fuse/blob/main/docs/how-it-works.md): how Fuse checks an edit, selects tests and runs them.
- [Results](https://github.com/Litenova-Solutions/Fuse/blob/main/docs/results.md): measured speed and accuracy on four repositories.
- [Contributing](https://github.com/Litenova-Solutions/Fuse/blob/main/docs/contributing.md): how to build, test and submit a change.
- [All documentation](https://github.com/Litenova-Solutions/Fuse/blob/main/docs/README.md)

Website: [fuse.codes](https://fuse.codes). License: Apache-2.0, see [LICENSE](https://github.com/Litenova-Solutions/Fuse/blob/main/LICENSE).
