# Contributing

Read [AGENTS.md](AGENTS.md) first: it covers the layout, the gates and the rules the code follows.

1. Branch from `main`.
2. Make the change with tests, and run the gates:

   ```bash
   dotnet build Fuse.slnx -c Release
   dotnet test --solution Fuse.slnx -c Release --no-build
   dotnet format Fuse.slnx --verify-no-changes
   ```

3. If the change touches checking or test selection, run the relevant eval (`dotnet run --project evals/Fuse.Evals -c Release -- correctness <repo>`) and include its result.
4. Sign off every commit with the Developer Certificate of Origin (`git commit -s`); see [DCO.txt](DCO.txt). The DCO check fails a pull request with an unsigned commit.
5. Open a pull request describing what changed and why.
