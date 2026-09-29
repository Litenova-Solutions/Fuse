# AI policy

This page is for anyone who contributes to Fuse with the help of an AI tool, including a coding agent that edits files or runs commands. Fuse is built for coding agents and is developed with them, so AI-assisted contributions are welcome. The policy asks one thing: a person stands behind every contribution, as if they had written it themselves.

It applies to issues, pull requests, review comments, discussions and security reports.

## A person is responsible

- Every contribution comes from a person who has read it and answers for it. Maintainers judge a contribution by what it does, not by how it was written.
- You understand every line of your pull request and can explain and change it when a reviewer asks. That an agent wrote a line does not answer a question about it.
- A pull request passes the gates in [Contributing](../CONTRIBUTING.md), its new tests run, and every example and number in its documentation follows [AGENTS.md](../AGENTS.md). Output that an agent says it ran counts only when you ran it too.
- Keep a pull request to one change. Split generated work that touches unrelated parts of the code.

## Sign-off

The `Signed-off-by:` line certifies the [Developer Certificate of Origin](../DCO.txt): that you have the right to submit the change under the Apache-2.0 license. Only a person can give that certification. Add the line only to a change you have reviewed, whether you run `git commit -s` or your agent runs it for you, and never sign off in an agent's name.

Check that generated code does not copy code under a license that is not compatible with Apache-2.0. When you recognize code from another project, say where it comes from.

## Disclosure

Say in the pull request description which AI tools you used and for what: code, tests, documentation, or the description itself. The pull request template has a section for it. Completion of a single line in an editor needs no mention.

In a commit that an AI tool helped write, add an `Assisted-by:` trailer naming the tool and the model:

```text
Assisted-by: Claude Code (claude-opus-5-5)
```

## Autonomous agents

An agent must not open issues, pull requests or comments that no person reviewed before they were posted. Maintainers close such contributions without review.

## Issues and security reports

Reproduce a bug before you report it, and include the output of `fuse --version` and the output Fuse printed, copied from your terminal rather than described by a tool. Do not submit a vulnerability report that an AI tool produced unless you have confirmed the vulnerability yourself; [Security](../SECURITY.md) describes how to report one.

## Agents working in this repository

[AGENTS.md](../AGENTS.md) is the guide for coding agents: the layout, the build, test and format commands, and the rules the code and the documentation follow. Claude Code reads it through `CLAUDE.md`, and most other harnesses read `AGENTS.md` directly. An agent follows it the same way a person does.

## Maintainers

Maintainers use AI tools under the same rules. A maintainer may close a contribution that does not follow this policy, and says which part it does not follow.
