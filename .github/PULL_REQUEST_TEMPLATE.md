<!--
Thanks for contributing to Fuse. Please keep the change scoped to one part of the pipeline
and describe what changed and why.
-->

## What changed

<!-- A short description of the change and the motivation. -->

## Verification

- [ ] `dotnet build Fuse.slnx -c Release`
- [ ] `dotnet test Fuse.slnx -c Release --no-build`
- [ ] `dotnet format Fuse.slnx --verify-no-changes`
- [ ] New public API has XML docs; new tests actually run (the count rises).

## Sign-off (required)

- [ ] Every commit is signed off with the Developer Certificate of Origin (`git commit -s`).

By signing off you certify the [DCO 1.1](../DCO.txt) statement. The DCO check fails any pull
request with a commit missing a matching `Signed-off-by:` trailer. See
[CONTRIBUTING.md](../CONTRIBUTING.md).
