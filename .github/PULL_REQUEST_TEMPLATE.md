## What changed

<!-- What the change does and why. -->

## AI assistance

<!-- Which AI tools you used and for what (code, tests, documentation, this description), or "none". See the AI policy: https://fuse.codes/docs/ai-policy -->

## Verification

- [ ] `dotnet build Fuse.slnx -c Release`
- [ ] `dotnet test --solution Fuse.slnx -c Release --no-build` (new tests run: the count rises)
- [ ] `dotnet format Fuse.slnx --verify-no-changes`
- [ ] For a change to checking, test selection or the path of a request: the result of the eval that [Evals](https://fuse.codes/docs/evals#what-to-run) names for it is attached

## Sign-off

- [ ] I have reviewed every change in this pull request and can explain it, as the [AI policy](https://fuse.codes/docs/ai-policy) asks.
- [ ] Every commit is signed off with the Developer Certificate of Origin: `git commit -s` adds the `Signed-off-by:` line that [DCO.txt](https://github.com/Litenova-Solutions/Fuse/blob/main/DCO.txt) describes, and `git rebase --signoff` adds it to commits already made.
