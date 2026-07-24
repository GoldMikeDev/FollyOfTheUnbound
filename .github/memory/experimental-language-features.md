---
coverage: This fork's experimental C# language features (do/until, mutate, inline expression declaration, if/catch/finally chains, '*.' root-namespace placeholder, null-conditional-coalescing statement, void-coalescing expression) — syntax shape, compiler status, and IDE support status
---

# Experimental Language Features ("Folly of the Unbound")

This fork prototypes several new C#-like constructs. The compiler side (parser, binder, lowering,
flow analysis) is real and verified end-to-end with real builds and executed test programs. IDE
support is being added incrementally; this file tracks what's done.

## The four statement/expression constructs

| Construct | `SyntaxKind` | Node type(s) | Keyword(s) | Notes |
|---|---|---|---|---|
| `do { } until (cond);` | `DoUntilStatement` | `DoUntilStatementSyntax` | `until` — real contextual `SyntaxKind.UntilKeyword` | Structurally identical to `DoStatementSyntax` with `until` instead of `while`. |
| `mutate x to Type;` | `MutateStatement` | `MutateStatementSyntax` | `mutate` — **not** a real `SyntaxKind`, matched by identifier text in the parser (`LanguageParser.ParseMutateStatement`); `to` — real contextual `SyntaxKind.ToKeyword` | No braces, single-line, semicolon-terminated — behaves like an ordinary statement for anything block/brace-related. |
| `if { cond-block } ifout = expr; { consequence } (catch/finally)*` | `IfCatchStatement`/`IfCatchArm` | `IfCatchStatementSyntax` (`Arms`, `Else`, `Catches`, `Finally`), `IfCatchArmSyntax` (`ElseKeyword`, `IfKeyword`, optional `Condition`/parens, optional `ConditionBlock`, `Consequence`) | `if`/`else`/`catch`/`finally` — all real, reused `SyntaxKind`s | Fuses if/else-if/else with try/catch/finally: `Catches`/`Finally` reuse the exact same `CatchClauseSyntax`/`FinallyClauseSyntax` nodes as ordinary `try`. `ifout` is not a keyword — it's a synthesized `bool?` local (`IfOutLocalSymbol`) referenced as a plain identifier. |
| inline expression declaration | `InlineExpressionDeclaration` | `InlineExpressionDeclarationSyntax` (`Expression`, `Identifier`) | none | Expression-level, no braces. |

No `LanguageVersion` gating exists for any of these — they're unconditionally enabled regardless of
declared `LangVersion`.

## IDE support status for the four constructs (see "Adding IDE Support for a New Statement/Expression SyntaxKind" in `.github/instructions/IDE.instructions.md`)

As of this writing, all four constructs have: classification, keyword completion, formatting rules,
outlining/brace-matching, keyword highlighting, and breakpoint spans.

**Deliberately not yet done (forward-looking, flagged as premature by the user):** code fixes /
refactorings that suggest replacing existing constructs with these new ones (e.g. suggesting
`do/until` in place of `do/while(!cond)`, or the if/catch chain in place of separate `if`+`try/catch`).
This would be a `CodeRefactoringProvider`/`CodeFixProvider`, not yet started.

**Not audited:** whether other IDE analyzers with exhaustive `SyntaxKind` switches (IDE0xxx style/
simplification analyzers) silently skip these new node kinds. Not yet checked.

## `receiver?.Call(...) ?? fallback;` — two overlapping features, same surface syntax

No new `SyntaxKind` or grammar for either: `a?.b() ?? c` already parses today as an ordinary
`CoalesceExpressionSyntax` whose `Left` is a `ConditionalAccessExpressionSyntax`; upstream Roslyn
rejects it at bind time because `??`'s left operand can't be `void`. Two independent fork commits
each taught the binder to accept this shape, at different levels, and the order they run in matters:

- **Null-conditional-coalescing statement** (`BoundConditionalCoalesceStatement`, added first). Detected
  in `Binder_Statements.BindExpressionStatement` *before* the expression binder ever sees the
  `CoalesceExpressionSyntax`: if `node.Expression` is a `CoalesceExpressionSyntax` whose `Left` is a
  `ConditionalAccessExpressionSyntax` that speculatively binds to `void`, it's bound as a statement
  (`Access` + `FallbackStatement`) instead of an expression — for **any** receiver type, value or
  reference. Lowered in `LocalRewriter_ConditionalAccess.cs` by threading a `whenNullOpt` fallback
  through `RewriteConditionalAccess` (reusing `BoundLoweredConditionalAccess`'s existing `WhenNullOpt`
  slot). Flow analysis: `AbstractFlowPass.VisitConditionalCoalesceStatement` visits both branches
  unconditionally (conservative, not flow-splitting).
- **Void-coalescing expression** (`BoundVoidCoalesceExpression`, added second, in `Binder_Operators.
  BindNullCoalescingOperator`). Same trigger shape, but reached only when ordinary `??` expression
  binding sees a void-typed `BoundConditionalAccess` on the left — and only accepts **reference-type**
  receivers, reporting `ERR_VoidCoalesceRequiresReferenceTypeReceiver` (CS9400) otherwise. Lowered
  separately in `LocalRewriter_VoidCoalesceExpression.cs` to an `if (receiver != null) { ... } else
  { fallback; }`.

**Known gap, not yet resolved:** because `Binder_Statements` intercepts the shape first and
unconditionally (any receiver type), `BindVoidCoalesceExpression`'s own path — and in particular its
reference-type-only restriction and CS9400 diagnostic — is likely unreachable when the coalesce
appears directly as an expression statement, which is the only context `IsValidStatementExpression`
allows it in. It would only be reachable if a caller binds the `CoalesceExpressionSyntax` outside
`BindExpressionStatement`'s interception (e.g. speculative binding, or future non-statement contexts).
If touching either feature, check the other — they compete for the same syntax shape, and the
statement-level binder currently wins.

## `*.` root-namespace placeholder qualifier

Motivation: shared-source files (e.g. `AddonModules`) want a namespace segment that resolves to
whatever project consumes them (`ToolBox.AddonModules`, `SteeleTerm.AddonModules`, ...) without a
manual per-project search-and-replace. Mirrors VB.NET's `RootNamespace` project option, but VB wraps
*every* declaration in the file implicitly; this fork instead requires an explicit `*.` placeholder
as the leftmost segment of a written `namespace` declaration.

- **Syntax:** `namespace *.AddonModules { }` / `namespace *.AddonModules;` (file-scoped). `*` is the
  real `SyntaxKind.AsteriskToken`; no new keyword. Represented by a new `RootNamespaceQualifierSyntax`
  node (`AsteriskToken`, `DotToken`) — a **new optional field on `BaseNamespaceDeclarationSyntax`**,
  not a `NameSyntax` subtype. (`NameSyntax.GetUnqualifiedName()` is abstract and every existing
  consumer assumes a `NameSyntax` resolves to one real identifier; making the placeholder a
  `NameSyntax` would have broken that contract across the binder and IDE. `Name` itself stays an
  ordinary, required `NameSyntax` — e.g. just `AddonModules` — untouched by every other consumer.)
  `*` is only recognized in `LanguageParser.ParseNamespaceDeclarationCore`, not the shared
  `ParseQualifiedName`, so it can't leak into `using` directives or type references.
- **Compiler option:** `CSharpCompilationOptions.RootNamespace` (string, `WithRootNamespace`,
  `GetRootNamespaceParts()` splits on `.`), `/rootnamespace:` command-line switch
  (`CSharpCommandLineParser.cs`), `Csc.cs` task property, `RootNamespace="$(RootNamespace)"` wired
  into `Microsoft.CSharp.Core.targets`'s `<Csc>` invocation — mirrors `Vbc.cs`/VB's targets exactly.
- **Binder substitution — the part that does NOT mirror VB:** VB's root-namespace machinery
  (`VisualBasicCompilation._rootNamespaces`) is VB-only; there is no shared `SyntaxAndDeclarationManager`
  equivalent in VB at all (`CommonSyntaxAndDeclarationManager` in Core/Portable is C#-only). So the
  binder-side wiring is C#-native: `RootNamespace`'s parts are threaded through
  `CSharpCompilation.WithOptions` (extends the existing `reuseSyntaxAndDeclarationManager` check with
  a `RootNamespace` comparison — this hook already existed, unused, before this feature) →
  `SyntaxAndDeclarationManager` → `DeclarationTreeBuilder.ForTree`, which wraps the declaration in one
  extra `SingleNamespaceDeclaration` layer per dot-separated `RootNamespace` part when
  `RootNamespaceQualifier` is present. If `RootNamespace` is unset, this is a hard **error**
  (`ErrorCode.ERR_RootNamespaceQualifierRequiresRootNamespace`, CS9399) — not a silent no-op like VB's
  equivalent unset-case. This was an explicit user decision (VB's silent fallback can mask a forgotten
  `<RootNamespace>` in a consuming project).
- **A second, independent decomposition site had to be fixed too:** `BinderFactory.BinderFactoryVisitor.MakeNamespaceBinder`
  (member-binding/name-resolution path) does its **own** separate walk of a namespace declaration's
  `Name` to find its container symbol — entirely separate from `DeclarationTreeBuilder`. It didn't
  know about `RootNamespaceQualifier` and crashed with a null-container assert (`InContainerBinder`)
  the first time a member was bound inside a `*.`-qualified namespace. Fixed via a new
  `MakeRootNamespaceContainerBinder` helper that descends through `RootNamespace`'s parts before
  `MakeNamespaceBinder` processes the written `Name`. **If a future change touches namespace
  declarations again, check both sites** — this is the same category of gap as the `GetUnqualifiedName()`/
  `NamespaceSymbol.GetNestedNamespace` sync note already in `DeclarationTreeBuilder.cs`.
- **IDE — intra-text adornment:** shows the resolved namespace painted over the `*` (buffer
  untouched), via a **new third category in the shared Inline Hints pipeline**
  (`IInlineRootNamespaceHintsService` in Core/Portable InlineHints, wired into
  `AbstractInlineHintsService.GetInlineHintsAsync` alongside the existing parameter-name/type-hint
  categories; C#-only implementation `CSharpInlineRootNamespaceHintsService`, VB exports nothing so
  it no-ops there). Chosen over a bespoke standalone tagger specifically to reuse the existing
  tooltip/caching/re-tagging machinery and match the established pattern, at the cost of touching one
  shared Core/Portable file also used by VB.
  Verified with an automated `<WpfFact>` test (`CSharpInlineRootNamespaceHintsTests.vb`, 4 cases:
  single-segment, multi-segment, no-qualifier, file-scoped) proving the computed hint span/text is
  correct. **Not verified:** actual WPF rendering in a live Visual Studio window — that needs a real
  VS session, which isn't available in this environment.
- **PublicAPI.Unshipped.txt:** the new type/members/`SyntaxFactory` overloads are recorded, but note
  this feature also *changes* the signature of already-shipped members (`NamespaceDeclarationSyntax.Update`,
  `SyntaxFactory.NamespaceDeclaration`/`FileScopedNamespaceDeclaration`'s full-arg overloads — each
  gained a `rootNamespaceQualifier` parameter). That's normally a breaking API change requiring a
  Shipped→Unshipped removal entry; deliberately not reconciled here since (a) the PublicAPI analyzer
  isn't actually wired into this build (confirmed: zero `RS00xx` diagnostics across several full
  rebuilds during this work), and (b) this fork isn't upstreamed, so the ledger's real purpose
  (protecting external NuGet consumers across releases) doesn't apply.
- **End-to-end verified** (not just unit tests) by compiling+running real programs with the
  freshly-built `csc`: single-segment `RootNamespace`, multi-segment `RootNamespace`, and the
  missing-`RootNamespace` error — all confirmed via actual emitted IL (`typeof(x).Namespace` at
  runtime), not just source-level resolution.
- **Not started:** code fixes/refactorings, and the same "not audited against exhaustive `SyntaxKind`
  switches" caveat as the four statement/expression constructs above.
