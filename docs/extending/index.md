---
title: Extending Fuse
description: Add languages, templates, format reducers, and pattern detectors through the capability model.
---

Fuse keeps language and format behavior in plugins resolved by file extension, so extending it means adding a capability rather than changing the core pipeline. This section is for contributors. Read [Capability and Plugin Model](../architecture/capability-model.md) first for the design these guides build on.

## Pages

- [Adding a Language Plugin](language-plugin.md): register reduction, skeleton, dependency, and other capabilities for a language.
- [Adding a Template](template.md): declare default extensions and exclusions for a project type.
- [Adding a Format Reducer](format-reducer.md): reduce a non-language file type.
- [Adding a Pattern Detector](pattern-detector.md): detect a convention and contribute to the pattern summary.

## Next

Start with [Adding a Language Plugin](language-plugin.md).
