# Fuse Feedback from the Traceability Completion Run

Written 2026-10-29 from the traceability completion run on NightHall (`D:\Personal-Projects\Nighthall`, branch `v1`), where the judge repacked a local LiteSpec package between builds. The earlier note from the same day (`traceability-run-2026-10-29.md`) already covers warning counts and building only changed projects; this note adds one item.

**F-1. Name the remedy when a locked package's content changed.**
Evidence: after `dotnet pack` rewrote `LiteSpec.Analyzers 3.0.0` into the local feed and the 3.0.0 folder was cleared from the NuGet cache (the project's documented step after a LiteSpec change), `fuse build apps/api/src/NightHall.WebApi` stopped with `error NU1403: Package content hash validation failed for LiteSpec.Analyzers.3.0.0. The package is different than the last restore.` for every project, and nothing more. The fix is `dotnet restore NightHall.slnx --force-evaluate`, which rewrites the eight `packages.lock.json` files, and the judge had to know that.
Proposal: when the only errors are NU1403, print the one command that refreshes the lock files for the solution being built (and which lock files it will change), or offer `fuse restore --refresh-locks` that runs it and lists the changed lock files, so a same-version local repack is one step instead of a diagnosis.
