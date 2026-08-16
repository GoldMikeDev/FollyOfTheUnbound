# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

This is a fork of [dotnet/roslyn](https://github.com/dotnet/roslyn), the C#/VB compiler platform, used to prototype experimental C# language features ("Folly of the Unbound"). The upstream repo already ships a thorough AI-agent knowledge base — **read it before doing anything else**:

1. **[`.github/copilot-instructions.md`](.github/copilot-instructions.md)** — canonical repo-wide entry point: project overview, directory layout, build/test commands, code style, and the memory-first orientation protocol.
2. **[`.github/memory/INDEX.md`](.github/memory/INDEX.md)** — loading map for the knowledge base (`ARCHITECTURE.md`, `CONVENTIONS.md`, `FILE_MAP.md`, `API_MAP.md`, `KNOWN_ISSUES.md`, `TESTING_STRATEGY.md`, plus per-area `known-issues/` and `testing/` files). Load only what's relevant to the task.
3. **Path-scoped rules**, auto-applied by directory: [`.github/instructions/Compiler.instructions.md`](.github/instructions/Compiler.instructions.md) (`src/{Compilers,Dependencies,ExpressionEvaluator,Tools}`), [`IDE.instructions.md`](.github/instructions/IDE.instructions.md) (`src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}`), [`Razor.instructions.md`](.github/instructions/Razor.instructions.md) (`src/Razor`).
4. **Skills** in [`.github/skills/`](.github/skills/) are auto-discovered (e.g. `code-review`, `ci-analysis`, `new-compiler-feature`, `run-toolset-tests`, `update-agent-docs`) — check there before writing ad hoc tooling.

## Orientation protocol

1. Read `.github/copilot-instructions.md`, then `.github/memory/INDEX.md` and any memory files relevant to the task.
2. Read the path-scoped instruction file for the area being edited — it applies automatically and carries directory-level detail and conventions for that layer.
3. **After changing code, run the `update-agent-docs` skill** to keep `.github/memory/` current (this is a hard obligation in `copilot-instructions.md`, not optional cleanup).

## Quick reference (see `.github/copilot-instructions.md` for full detail)

Build/test specific projects during development rather than the whole repo:
```bash
dotnet build Compilers.slnf      # compilers only
dotnet build Ide.slnf            # IDE only
dotnet build Razor.slnf          # Razor compiler & tooling only
dotnet test <path/to/Specific.UnitTests.csproj> --filter "FullyQualifiedName~MyTestClass"
```
Full build/test (`./build.sh` / `Build.cmd`, `./test.sh` / `Test.cmd`) is for final validation only — it's slow.

Other entry points: `dotnet run --file eng/generate-compiler-code.cs` (regenerate Syntax/BoundNodes code after grammar changes), `dotnet msbuild <proj> /t:UpdateXlf` (refresh `.xlf` after `.resx` edits).

## Claude Code Remote session naming

When running as a Claude Code Remote session (`claude.ai/code`), rename the session with `set_session_title` as soon as it creates a pull request, starts work on a specific numbered PR (even one it didn't open), or starts work on a specific numbered GitHub issue. Use the pattern:

`FotU <Issue|PR> #<n>[-<n>|, #<n>...] [& <Issue|PR> #<n>...]`

- Prefix with `FotU `.
- Group issue numbers and PR numbers separately; state the `Issue`/`PR` keyword once per group.
- Consecutive numbers of the same type collapse into a range (`#30-31`); non-consecutive numbers are comma-separated (`#21-25, #28`).
- Join multiple groups with `&`.
- If the session already touched other issues/PRs, merge the new number(s) into the existing title instead of overwriting it, re-collapsing into ranges where the merge makes numbers consecutive.
- **Always call `get_session` first to read the current title before calling `set_session_title`.** Never assume the current title (from memory, from context, or "this looks like a fresh session") — a title set without checking first risks silently overwriting and losing every issue/PR number a prior rename recorded.
- Examples: `FotU PR #34`, `FotU Issue #8 & PR #27`, `FotU PR #34-35`, `FotU Issue #29 & PR #30-31, #33`.
- `set_session_title` requires the real session ID (`session_...`) — there is no "current session" shorthand, and passing one (e.g. `"current"`) fails. Before the first rename in a session, look the ID up yourself: call `list_sessions` (`mine: true`) and match the running entry whose `session_context.outcomes[].git_repository.git_info.branches` contains the branch this session is developing on. Do this proactively, not only after a failed rename attempt.

## `folly.sh` / `folly.ps1` parity

See [`.github/memory/CONVENTIONS.md`](.github/memory/CONVENTIONS.md#follysh--follyps1-parity): the two scripts (and their `scripts/test-folly-cleanse.{sh,ps1}` harnesses) must be edited together, not drift.

## Merging pull requests

See [`.github/copilot-instructions.md`](.github/copilot-instructions.md#merging-pull-requests): never squash/rebase-merge, always a real merge commit (`merge_method: merge`), no exceptions.

## This fork's work: experimental language features

Recent branch work adds new C#-like control-flow constructs (`do/until`, `mutate`, inline expression declarations, `if/catch/finally` chains with block conditions). This kind of work touches the compiler's front-to-back pipeline — parser (`src/Compilers/CSharp/Portable/Parser/`), syntax model (generated from `Syntax.xml`, requires regeneration via `eng/generate-compiler-code.cs`), binder/lowering, and often IDE features (formatting, completion, classification) that pattern-match on syntax kinds. When adding or changing a construct:
- Check the `new-compiler-feature` skill in `.github/skills/` first — it likely encodes the checklist for wiring a feature through parser → binder → lowering → IDE.
- Verify changes end-to-end with a real build and an executed test program, not just unit tests — this fork's history shows real build/runtime bugs slipping past unit tests alone for control-flow features.
- Regenerate syntax code after any `Syntax.xml` change; don't hand-edit generated files.
