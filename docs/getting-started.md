# Getting started

This tutorial is for a developer trying Fuse for the first time. It installs the tool, sets it up in a small sample repository, and shows what Fuse reports for an edit that breaks a caller, for an edit that fails a test, and what its hooks send the agent. Afterwards, run `fuse init` in your own repository and read [Harnesses](harnesses.md) for the harness you use.

Fuse keeps a repository's C# projects compiled in memory and compares the working tree, your files as they are on disk, with HEAD, the commit you have checked out. After an edit it reports only the compiler errors that edit introduced, including breaks in projects that depend on the edited one, and when tests run, it runs only the tests the change can reach.

You need the .NET 10 SDK, git, and access to NuGet to restore the sample's packages. The commands are for bash; on Windows, run them in Git Bash. The output below is from a run on Windows, and every path in it is relative to the repository.

## 1. Install Fuse

```bash
dotnet tool install -g Fuse
fuse --version
```

```text
5.1.0
```

`dotnet tool install -g` puts `fuse` in `~/.dotnet/tools` (`%USERPROFILE%\.dotnet\tools` on Windows), which has to be on your `PATH`, for you and for the agent harness that runs the hooks.

## 2. Create a sample repository

The sample has a library `Lib` with a class `Calc`, a console app `App` that calls it, and an xUnit test project `Lib.Tests`.

```bash
mkdir fuse-sample
cd fuse-sample
git init
dotnet new sln -n Sample
dotnet new classlib -n Lib -o Lib
dotnet new console -n App -o App
dotnet new xunit -n Lib.Tests -o Lib.Tests
dotnet sln add Lib App Lib.Tests
dotnet add App reference Lib
dotnet add Lib.Tests reference Lib
rm Lib/Class1.cs Lib.Tests/UnitTest1.cs
```

Create `Lib/Calc.cs`:

```csharp
namespace Lib;

public class Calc
{
    public int Add(int a, int b) => a + b;

    public int Multiply(int a, int b) => a * b;
}
```

Replace the contents of `App/Program.cs` with:

```csharp
using Lib;

Console.WriteLine(new Calc().Add(2, 3));
```

Create `Lib.Tests/CalcTests.cs`:

```csharp
namespace Lib.Tests;

public class CalcTests
{
    [Fact]
    public void Adds() => Assert.Equal(5, new Calc().Add(2, 3));

    [Fact]
    public void Multiplies() => Assert.Equal(6, new Calc().Multiply(2, 3));
}
```

Create `.gitignore`, so build output is not part of the working tree Fuse compares:

```text
bin/
obj/
```

## 3. Commit and build

Fuse compares with HEAD, so the repository needs a commit. It also needs restored projects, and it never restores on its own. `dotnet build` restores the packages and builds once, which gives the test project the build output that lets `fuse test` run without MSBuild later.

```bash
git add -A
git commit -m "Add the sample"
dotnet build
```

## 4. Register the hooks

```bash
fuse init
```

```text
wrote .claude/settings.json
fuse: hooks installed; your agent gets compiler errors after each edit, affected tests for `dotnet test`, and compact `dotnet build` output
```

The sample has no harness folder, so `fuse init` set up Claude Code, which it does by default. In a repository with `.cursor/`, `.gemini/`, `.codex/` or another harness's folder, it writes that harness's settings too; [Harnesses](harnesses.md) lists them and shows the file it wrote here.

Run a first check:

```bash
fuse check
```

```text
fuse: no errors introduced (0 file(s) checked)
```

This call started the engine, the background process that keeps the projects compiled for this repository. Later calls reuse it until it has been idle for 30 minutes.

## 5. Break a caller

Rename `Add` to `Plus` in `Lib/Calc.cs`, so the file reads:

```csharp
namespace Lib;

public class Calc
{
    public int Plus(int a, int b) => a + b;

    public int Multiply(int a, int b) => a * b;
}
```

Then check again and print the exit code:

```bash
fuse check
echo "exit code $?"
```

```text
App/Program.cs(3,30): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
  removed: public int Add(int a, int b)
Lib.Tests/CalcTests.cs(6,54): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
  removed: public int Add(int a, int b)
fuse: 2 error(s) introduced in 2 file(s) (App, Lib.Tests); Lib declarations changed, 2 dependent project(s) checked
exit code 1
```

The edit was in `Lib`, but the errors are in `App` and `Lib.Tests`, the projects that depend on it. Fuse found them because the edit removed a declaration, `Add`, so it searched the dependent projects for code that uses it. The line under each error names the declaration change that broke that file, as HEAD had it. The last line counts the errors, names the projects they are in, and says how far the check reached. Exit code 1 means errors were introduced; [Commands](commands.md#fuse-check) describes every part of the output.

## 6. See what the hooks send the agent

The hooks run the same check when the agent works. To see what the agent would receive, send the hooks the JSON a harness sends. Claude Code runs the post-edit hook after an edit, with the edited file in the payload:

```bash
echo '{"tool_input":{"file_path":"Lib/Calc.cs"}}' | fuse hook claude post-edit; echo " [exit code $?]"
```

```text
App/Program.cs(3,30): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
  removed: public int Add(int a, int b)
Lib.Tests/CalcTests.cs(6,54): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)
  removed: public int Add(int a, int b)
fuse: 2 error(s) introduced in 2 file(s) (App, Lib.Tests); Lib declarations changed, 2 dependent project(s) checked [exit code 2]
```

Claude Code runs this hook in the background. Exit code 2 makes it wake the agent and show it this text. An edit that introduces no error prints nothing and exits with 0, so the agent hears from Fuse only when there is something to fix.

When the agent tries to finish, the stop hook checks every change and sends the agent back:

```bash
echo '{}' | fuse hook claude stop
```

```json
{"decision":"block","reason":"App/Program.cs(3,30): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)\n  removed: public int Add(int a, int b)\nLib.Tests/CalcTests.cs(6,54): error CS1061: 'Calc' does not contain a definition for 'Add' and no accessible extension method 'Add' accepting a first argument of type 'Calc' could be found (are you missing a using directive or an assembly reference?)\n  removed: public int Add(int a, int b)\nfuse: 2 error(s) introduced in 2 file(s) (App, Lib.Tests); Lib declarations changed, 2 dependent project(s) checked\nFix these errors before finishing; they are not in the last commit."}
```

Before the agent runs a shell command, the pre-shell hook rewrites `dotnet build` and `dotnet test` to their Fuse equivalents:

```bash
echo '{"tool_input":{"command":"dotnet test"}}' | fuse hook claude pre-shell
```

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"fuse test"}}}
```

## 7. Run the affected tests

Undo the rename, then make `Multiply` return a wrong result:

```bash
git restore Lib/Calc.cs
```

Edit the `Multiply` line in `Lib/Calc.cs` to read:

```csharp
    public int Multiply(int a, int b) => a * b + 1;
```

This edit changes only a method body, so it breaks no caller:

```bash
fuse check
```

```text
fuse: no errors introduced (1 file(s) checked)
```

It does break a test:

```bash
fuse test
echo "exit code $?"
```

```text
FAILED Lib.Tests.CalcTests.Multiplies
  Assert.Equal() Failure: Values differ
  Expected: 6
  Actual:   7
  at Lib.Tests.CalcTests.Multiplies() in Lib.Tests/CalcTests.cs:line 9
fuse: 1 failed, 0 passed in 2.9 s; ran 1 test(s) affected by your changes out of 2; fuse test --all runs everything; without MSBuild
exit code 1
```

Fuse ran one of the two tests, `Multiplies`, because it is the only one that reaches `Multiply`, and printed only the failure. `without MSBuild` means it compiled the changed `Lib` from memory into a copy of the test project's build output instead of building with MSBuild. When the agent runs `dotnet test`, the pre-shell hook turns it into this command.

## 8. Clean up

```bash
git restore Lib/Calc.cs
```

The engine exits on its own after 30 idle minutes. Delete the `fuse-sample` folder when you are done; Fuse's own files for it are in your local application data, as [Troubleshooting](troubleshooting.md#where-the-logs-are) describes.

## Next steps

- Run `fuse init` in your own repository, and read [Harnesses](harnesses.md) for what it writes for your harness.
- [Commands](commands.md) describes every command and message.
- [Limits](limits.md) lists what a check cannot see, before you rely on it.
