---
coverage: IDE-layer (src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}) known issues, quirks & workarounds
---

# IDE — Known Issues

Layer-specific quirks for the IDE/Workspaces stack. Load when working under
`src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}`.
Cross-cutting issues live in `.github/memory/KNOWN_ISSUES.md`.

## MEF composition failures surface as test failures

**Affected area:** MEF-dependent IDE/Workspaces tests
**Description:** A missing/incorrect MEF export attribute often manifests as an
unrelated-looking test failure rather than a clear composition error.
**Workaround:** When IDE tests fail unexpectedly, check the export attributes
first (`[ExportLanguageService]`/`[ExportWorkspaceService]`, `[Shared]`,
`[ImportingConstructor]` + `[Obsolete(MefConstruction.ImportingConstructorMessage)]`).

## Daemon-mode `roslyn-language-server` has incomplete per-connection isolation

**Affected area:** `src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/` daemon mode (`--daemon`),
`src/LanguageServer/roslyn-language-server/`
**Description:** The daemon builds exactly one MEF `ExportProvider` and consumes one client's
`ServerConfiguration`, once, at startup. Every later client that connects to the shared daemon gets its own
`LanguageServerHost`. Global log routing is now fixed: `DaemonConnectionContext` (an
`AsyncLocal<LanguageServerHost?>` ambient context, set by `LanguageServerConnectionManager` per connection)
lets `GlobalLogMessageLogger.GetTargetServers` route a log call to just the connection it's attributable to,
instead of broadcasting to every client. **Still open:** every connection shares the same `IGlobalOptionService`
(a client's option writes silently affect every other connection), and the same per-session config
(`ExtensionLogDirectory`, `TelemetryLevel`, `SessionId`) from whichever client happened to launch the daemon.
Confirmed (via reflection over the actual restored assembly) that this isn't a `Microsoft.VisualStudio.Composition`
version gap — this repo already restores 18.9.15, ahead of the entire public nuget.org release history, and
it has no scoped/child-`ExportProvider` API to unlock.
**Workaround:** None yet for the option/config gaps; tracked as
[GoldMikeDev/roslyn#9](https://github.com/GoldMikeDev/roslyn/issues/9). Full design write-up, phased plan, and
current status: `docs/ide/specs/daemon-per-connection-isolation.md`.

## New loop-like statement kinds need registering in `IsContinuableConstruct`/`IsBreakableConstruct`

**Affected area:** `src/Workspaces/SharedUtilitiesAndExtensions/Compiler/CSharp/Extensions/SyntaxNodeExtensions.cs`
**Description:** `IsContinuableConstruct`/`IsBreakableConstruct` are shared helpers with their own
hardcoded `SyntaxKind` switch, consumed independently by `BreakKeywordRecommender`/
`ContinueKeywordRecommender` (keyword completion) and `LoopHighlighter` (keyword highlighting).
When this fork added `SyntaxKind.DoUntilStatement`, none of the other IDE-support work (classification,
formatting, outlining) touched this file, so `break`/`continue` silently weren't suggested and
weren't highlighted inside `do { } until (...)` loops until this helper was updated — with no error,
crash, or test failure to flag it.
**Workaround:** Any new loop-like statement kind must be added to `IsContinuableConstruct`'s switch
directly; it is not covered by any of the other per-feature registration points. See the
"Adding IDE Support for a New Statement/Expression SyntaxKind" checklist in
`.github/instructions/IDE.instructions.md`.

## Collapsed-region "..." is NOT Roslyn-rendered; Inline Hints is the real intra-text-adornment pattern

**Affected area:** anything needing to paint extra/resolved info inline over source text without
touching the buffer (e.g. `.github/memory/experimental-language-features.md`'s `*.` root-namespace
adornment)
**Description:** It's tempting to assume collapsed outlining regions (the "..." shown for a folded
`#region`/method body) are an example of a Roslyn-owned intra-text adornment to copy. They aren't —
Roslyn only supplies `BlockSpan`s (via `BlockStructureProvider`s) saying *where* things are
collapsible; the VS platform itself renders the collapsed ellipsis. The actual Roslyn-owned mechanism
for "show computed text inline without changing the buffer" is **Inline Hints**
(`src/Features/Core/Portable/InlineHints` → `src/EditorFeatures/Core/InlineHints`), which already
does this for inferred-type hints and parameter-name hints via WPF `IntraTextAdornmentTag`s.
**Workaround:** For a new intra-text adornment, add a language-service category to
`AbstractInlineHintsService`'s aggregation (see `IInlineRootNamespaceHintsService` for the pattern —
Core/Portable interface + `AbstractInlineHintsService.GetInlineHintsAsync` wiring + a C#-only, or
per-language, implementation) rather than reaching for outlining/collapsible-span APIs.
