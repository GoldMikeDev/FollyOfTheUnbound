# C#Unbound VSIX — Handoff Recap

Context for a fresh Claude Code session in the new VSIX repo. This doc captures decisions made
while working in the `FollyOfTheUnbound` repo so they don't need to be re-derived.

## Naming

- **This repo** (`FollyOfTheUnbound`): a fork of `dotnet/roslyn` that adds new C#-like language
  constructs. It is *not* being upstreamed — it's a personal/private compiler fork.
- **The language**: `C#Unbound` — a superset of C#, not a replacement. It compiles ordinary C# plus
  some new constructs.
- **The file extension**: `.csu` (maps to "C#Unbound"), distinct from `.cs`.
- **This new repo/project**: the VSIX that teaches Visual Studio to treat `.csu` files as
  `C#Unbound` instead of falling through to VS's in-box C# language service.

## Why a separate extension + separate repo

The user's dev setup is their regular, everyday **VS 2026 Insiders** — not the Roslyn
contributor "experimental hive" workflow. That means opening a plain `.cs` file will always be
served by VS's in-box C# language service (a separate binary from anything built in the
`FollyOfTheUnbound` repo), regardless of what compiler a project references. Referencing a custom
compiler package only changes *build-time* compilation, not the live editor experience.

Two options were considered:
1. **Full override** — build and install `FollyOfTheUnbound`'s VS setup VSIX into the real VS
   install, replacing in-box C# entirely for `.cs`. Simpler, but affects *every* `.cs` file on the
   machine (including unrelated/vanilla C# work), and VS updates can reset/conflict with the
   override.
2. **Separate extension** (chosen) — `.csu` gets its own content type and a VSIX that reuses
   `FollyOfTheUnbound`'s built `LanguageNames.CSharp` services, without ever touching how VS
   handles ordinary `.cs` files. More isolated, but requires real VSIX-level plumbing (content-type
   registration, extension mapping, editor-host wiring) that a Features-layer IDE change doesn't
   need.

The VSIX is a **separate project/repo**, not folded into `FollyOfTheUnbound`, so the language/
compiler fork keeps doing one thing (the language) and the VS integration is a separately-versioned
consumer of it — it depends on `FollyOfTheUnbound`'s built assemblies (the same way any
Roslyn-based VSIX depends on `Microsoft.CodeAnalysis.*` packages) rather than containing a copy of
its source.

## Do NOT rename `CSharp`/`C#` internals in `FollyOfTheUnbound`

Explicitly decided against renaming `LanguageNames.CSharp`, the `Microsoft.CodeAnalysis.CSharp`
namespace, or any of the thousands of `[ExportLanguageService(..., LanguageNames.CSharp)]`
MEF exports to something like `CSharpUnbound`, because:
- That string is load-bearing across virtually all of Workspaces/Features/Analyzers (which is
  unmodified code C#Unbound still needs — ordinary completion, most classification, refactorings,
  etc.). Renaming it would mean reimplementing all of that from scratch.
- `FollyOfTheUnbound` actively merges from upstream `dotnet/roslyn` (see git log:
  `Merge branch 'dotnet:main' into main`). A global rename would create permanent merge conflicts
  on every future sync, for no functional benefit.
- `C#Unbound` is a superset of C#, not a distinct language — the internals staying named `CSharp`
  is exactly what lets it inherit the entire existing C# IDE/compiler feature set for free.

**Conclusion:** `C#Unbound` branding lives at the edges only — the file extension, the VSIX/content
type name, docs, maybe eventually a distinctly-named compiler package/executable. Internals stay
`CSharp`/`C#`. This could be revisited later *if* the language ever evolves away from being a
C# superset in a major way, but not before.

## What's already built in `FollyOfTheUnbound` (as of this handoff)

Four new C#-like constructs, with real parser/binder/lowering (compiles to real IL, verified
end-to-end) and a full pass of IDE support already added at the `LanguageNames.CSharp` /
Features-layer level:

| Construct | `SyntaxKind` | Keyword(s) |
|---|---|---|
| `do { } until (cond);` | `DoUntilStatement` | `until` (real contextual keyword) |
| `mutate x to Type;` | `MutateStatement` | `mutate` (matched by identifier text, not a real `SyntaxKind`), `to` (real contextual keyword) |
| `if { ... } ifout = expr; { ... } (catch/finally)*` | `IfCatchStatement`/`IfCatchArm` | `if`/`else`/`catch`/`finally` (all reused real keywords); `ifout` is a synthesized `bool?` local, not a keyword |
| inline expression declaration | `InlineExpressionDeclaration` | none |

IDE surfaces covered: classification, keyword completion, formatting rules, outlining/brace
matching, keyword highlighting, breakpoint spans. See
`.github/memory/experimental-language-features.md` in this repo for the authoritative, up-to-date
version of this table and support status — this handoff doc is a snapshot, that memory file is
current.

Deliberately not yet done: code fixes/refactorings suggesting these constructs as replacements for
existing C# patterns (flagged by the user as premature/forward-looking).

## What the new VSIX repo needs to figure out

Not yet scoped in detail — starting points for the new session:
- How to reference `FollyOfTheUnbound`'s built output (local project reference during dev vs. a
  packaged/NuGet-style reference once more stable).
- Content-type definition + `FileExtensionToContentTypeDefinition` mapping `.csu` → a new content
  type (not simply reusing VS's built-in `"CSharp"` content type, since that's exactly what would
  cause collision with in-box C#).
- Editor-host wiring so that new content type resolves language services via
  `LanguageNames.CSharp` against `FollyOfTheUnbound`'s assemblies rather than any in-box copy.
- How MSBuild should treat `*.csu` files as compile items feeding the custom compiler at build time
  (separate from, but related to, the editor-side content-type work).
