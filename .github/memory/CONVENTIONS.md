---
coverage: Repo-wide code style, naming, immutability, resource & public-API rules (layer-specific conventions live in the instruction files)
---

# Conventions

Authoritative formatting lives in `.editorconfig`; path-scoped rules live in `.github/instructions/{Compiler,IDE,Razor}.instructions.md` and apply automatically to files under their globs. This file holds only **repo-wide** conventions.

**Layer-specific conventions live in the path-scoped instruction files — read the one for your area:**
- MEF service/analyzer exports, per-language services, code-fix patterns → `.github/instructions/IDE.instructions.md` (these do **not** apply to the compiler).
- Code generation, performance/pooling patterns → `.github/instructions/Compiler.instructions.md`.

## Naming Conventions

- **Namespaces:** `Microsoft.CodeAnalysis.[Language].[Area]` (e.g., `Microsoft.CodeAnalysis.CSharp.Formatting`).
- **Private fields:** `_camelCase`.
- **Types/methods/properties:** PascalCase. Interfaces prefixed `I`.

## Code Style

From `.editorconfig`:
- Indentation: 4 spaces for `*.cs`/`*.vb`; 2 spaces for project/XML/JSON/PS1/SH files. Never tabs.
- `*.cs`/`*.vb`: `insert_final_newline = true`, `charset = utf-8-bom`.
- **Blank lines must contain no whitespace** (no spaces/tabs) — this is a hard lint failure.
- **No trailing whitespace.**
- File-scoped namespaces and `var`/expression-body preferences are enforced via editorconfig analyzers — follow the file you are editing.

Running the formatter:
- `dotnet format whitespace --folder . --include <path>` (the `--folder .`/`--include` form avoids a slow design-time build).

## Patterns in Active Use

### Immutability (all layers)
```csharp
// Use With*/Add*/Replace* to produce new instances — never mutate.
// IDE/workspace: oldDocument.WithSyntaxRoot(newRoot)
// Compiler:      compilation.ReplaceSyntaxTree(oldTree, newTree)

// Always thread CancellationToken through async/semantic calls.
var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
var symbolInfo = semanticModel.GetSymbolInfo(expression, cancellationToken);
```

### Reviewable changes and completion

- Keep each change focused on one coherent concern. Split independently reviewable, validatable, mergeable, or revertible work instead of combining it into a broad diff.
- Judge change size by cognitive load and validation boundaries, not an arbitrary line count; generated and mechanical updates may be large while still representing one focused change.
- A change is complete only after applicable formatting, analyzers, affected builds, targeted tests, generated/resource/API updates, final diff review, and documentation freshness work are complete. The canonical ordered checklist is the **Definition of Done** in `.github/copilot-instructions.md`.

## Patterns Explicitly Avoided

- **No `TODO` or `TODO2` comments** — CI correctness leg flags `TODO`. Track follow-up work as a GitHub issue and link it in code (e.g. `// https://github.com/dotnet/roslyn/issues/NNNN`). Existing `TODO2` markers are a frozen baseline from when enforcement started, not a pattern to follow.
- **No `PROTOTYPE` comments in PRs targeting `main`** — CI enforces removal (they are allowed only on feature branches).
- **Do not hand-edit generated code** (`Syntax.xml`/`BoundNodes.xml`-derived `.cs`, `eng/common`, `*.xlf` content beyond the regen tool).
- **Do not break layering** — lower layers (Compilers) must not reference higher layers (Workspaces/Features/IDE). See `docs/Layering.md`.

## Resources, Localization & Public API

- Resource strings live in `.resx`, accessed via generated designer classes (`CSharpResources`, `FeaturesResources`, `AnalyzersResources`, …).
- After editing a `.resx`, run `dotnet msbuild <project.csproj> /t:UpdateXlf` to refresh the `.xlf` translation files.
- When adding/changing public APIs, update the project's `PublicAPI.Unshipped.txt` (the PublicApiAnalyzer / RS0016 enforces this).

## `folly.sh` / `folly.ps1` parity

`folly.sh` and `folly.ps1` implement the same commands (`attune`, `weave`, `cleanse`, `scry`, etc.) for bash and PowerShell respectively, and must stay in behavioral lockstep. When editing an action in one, make the equivalent change in the other in the same commit/PR — a bug fix, a new safety check, a changed message format, a retry, a test case — don't land it in only one language. A genuinely platform-specific fix (e.g. an NTFS ACE vs. Unix file permissions, `.dotnet/dotnet` vs. `.dotnet/dotnet.exe`) still needs the equivalent *behavior* added on the other side via whatever mechanism that platform actually has, not a silent omission. Same expectation for their manual test harnesses (`scripts/test-folly-cleanse.sh` / `scripts/test-folly-cleanse.ps1`, and any future `*.sh`/`*.ps1` harness pair) — see `TESTING_STRATEGY.md`.

## Disk-constrained sandboxes: never run a full solution build

A full build of this repo (`Roslyn.slnx`/`FollyOfTheUnbound.slnx`, restore + build, with all the
intermediate/obj output that produces) exceeds 34 GiB and will exhaust a disk-constrained sandbox's
storage allowance outright — it is not a "slow but eventually works" situation, it fails partway
through with no usable build output. In such an environment:
- Run `attune` (restore only) first, then build **individual solution filters** separately
  (`Compilers.slnf`, `Ide.slnf`, `Razor.slnf`, or a single project) rather than the full solution —
  see the Quick Reference commands in `CLAUDE.md`.
- **`cleanse` (delete `artifacts/`) between each separate solution/filter build**, not just at the
  end of a session — each filter's own build output is enough on its own to threaten the same
  storage ceiling if left alongside the next filter's. Treat `attune` → build filter A → `cleanse` →
  build filter B → `cleanse` → ... as the normal loop in these environments, not an occasional
  cleanup step.
- If a build appears to hang or is taking far longer than a filtered build should, suspect disk
  exhaustion (or an accidental full-solution build) before assuming the build itself is broken.

## Every open PR review thread must end resolved, not just addressed

A review comment that has been fixed (code changed, doc updated, test added — whatever the comment
asked for) must be marked resolved on GitHub (`resolve_review_thread`/equivalent) as part of the same
work, not left open for someone else to close later. This applies to every PR, current and future,
opened by any agent working in this repo. A thread you're intentionally *not* acting on (disagreeing
with the suggestion, deferring it, it's out of scope) still needs a reply explaining why — it does not
get resolved silently, but it also does not get left with no response at all. "I pushed a fix" and "I
left it alone, here's why" are both acceptable end states for a thread; "the fix is in the diff and the
thread is still open" is not.

## Language / Framework Constraints

- SDK and VS toolset pinned in `global.json`.
- Arcade-based build (`Microsoft.DotNet.Arcade.Sdk`); package versions centralized in `Directory.Packages.props`.

## Documentation Files

- New docs use **kebab-case** filenames (e.g., `roslyn-language-server-copilot-plugin.md`, not `Roslyn Language Server Copilot Plugin.md`).
- Place docs in the appropriate `docs/` subdirectory (`docs/contributing/`, `docs/compilers/`, `docs/features/`, …); general docs that don't fit a subdirectory go directly in `docs/`.
