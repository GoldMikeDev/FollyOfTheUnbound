---
coverage: This fork's experimental C# language features (do/until, mutate, inline expression declaration, if/catch/finally chains, '*.' root-namespace placeholder, null-conditional-coalescing statement, void-coalescing expression, escape statement) — syntax shape, compiler status, and IDE support status
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
| inline expression declaration | `InlineExpressionDeclaration` | `InlineExpressionDeclarationSyntax` (`Expression`, `Identifier`) | none | Expression-level, no braces. Restricted to `new-expression identifier` (`ObjectCreationExpressionSyntax`, `ImplicitObjectCreationExpressionSyntax`, `AnonymousObjectCreationExpressionSyntax`, `ArrayCreationExpressionSyntax`, `ImplicitArrayCreationExpressionSyntax`), naming a `new`-expression result so it survives past the end of the expression -- e.g. `new Widget() w` instead of `var w = new Widget();`. Not a general `expr identifier` production, but there is no positional suppression either: it applies anywhere one of those `new`-expression kinds can appear as the left operand, including attribute-argument lists, array-rank/fixed-buffer-size lists, and indexer bracketed-argument lists (e.g. `[Attr(new C() x)]`, `items[new C() x]`) -- the type restriction alone is what keeps it from colliding with unrelated grammar in those positions (bare identifiers like `var`/`int` never match a `new`-expression kind). Skipped when the creation expression already contains diagnostics (broken parses aren't extended into a declaration); see `LanguageParser.IsInlineDeclarationContext` and its call site in `ParseExpressionContinued`. |

No `LanguageVersion` gating exists for any of these — they're unconditionally enabled regardless of
declared `LangVersion`.

## `escape;` — jump to the end of the nearest enclosing block

`SyntaxKind.EscapeStatement`, `EscapeStatementSyntax` (`AttributeLists`, `EscapeKeyword`,
`SemicolonToken`). `escape` is matched by identifier text in the parser (same trick as `mutate`:
`LanguageParser.TryParseStatementStartingWithIdentifier` checks `CurrentToken.ValueText == "escape"`
**and** that the very next token is `;`, so `escape();` and `escape = 1;` still parse as ordinary
expression statements using `escape` as an identifier — no real `SyntaxKind.EscapeKeyword` exists).

**Semantics:** jumps to just before the closing brace of the *nearest* enclosing `{ }` block —
including a `catch` or `finally` block's own body, and a `try` block's body (where `finally` still
runs normally afterward, since the jump target is inside the `try`'s own protected region, not past
it). Unlike `break`/`continue`, it never searches past the nearest block to find a loop/switch — it
always targets the closest enclosing `BlockSyntax`, full stop. A method body is itself a block, so
`escape;` at the top level of a method just runs to the end of the method (no dedicated diagnostic
needed for "no enclosing block" — that case is unreachable in practice, since every statement
position is lexically inside some block).

**Implementation — deliberately lowering-free:** rather than a new `BoundNode` kind with custom
`LocalRewriter`/codegen handling, `escape;` binds directly to an ordinary `BoundGotoStatement`
targeting a `GeneratedLabelSymbol` that is lazily allocated per block:
- `Binder.EscapeLabel` (new virtual property, delegates to `Next` by default, `null` at
  `BuckStopsHereBinder`) is overridden in `BlockBinder` to lazily allocate and always return *its
  own* label (no further delegation — this is what gives `escape;` "nearest enclosing block, not
  nearest enclosing loop" semantics, the opposite of `GetBreakLabel`/`GetContinueLabel`'s search).
- `Binder_Statements.BindBlockParts` appends a `BoundLabeledStatement` (wrapping a no-op
  `BoundNoOpStatement`) as the block's last statement, but **only if** `BlockBinder.
  EscapeLabelIfAllocated` is non-null — i.e. only if some `escape;` inside that exact block actually
  asked for the label. An unused block never gets the extra synthetic label statement.
- `Binder_Statements.BindEscapeStatement` binds `escape;` to `new BoundGotoStatement(node,
  this.EscapeLabel, null, null)`.

Because the label always sits at the end of the *same* lexical block as any `escape;` that targets
it, this is never a jump across a protected-region boundary from the CLR's point of view — it is
lexically indistinguishable from a plain forward `goto` within one block. That means all the usual
machinery (existing goto/label lowering in `LocalRewriter`, `ControlFlowPass` unreachable-code
detection after an unconditional jump, the `ControlFlowGraphBuilder`) already handles it correctly
with **zero new lowering code**. This is why escaping a `try`/`catch`/`finally` body "just works":
it's not special-cased, it's a consequence of the label placement.

**One real bug found and fixed along the way:** `GeneratedLabelSymbol.Locations` inherited
`LabelSymbol`'s base `throw new NotSupportedException()` — harmless for `BreakLabel`/`ContinueLabel`
because those are only ever referenced structurally (as fields on `BoundWhileStatement` etc.), never
as the direct target of a real `BoundGotoStatement`. `ControlFlowPass.VisitGotoStatement` calls
`node.Label.GetFirstLocation()` for its using-declaration forward/backward-jump check, which crashed
(`FailFast`) the first time `escape;` lowered to a real goto targeting a `GeneratedLabelSymbol`.
Fixed by adding an optional `Location?` to `GeneratedLabelSymbol`'s constructor (defaults to `null`
for existing callers, which still get an empty `Locations` array instead of a throw — strictly safer,
nothing depended on the throw) and having `BlockBinder` pass the block's `CloseBraceToken` location
when allocating the escape label. **If any future feature also makes a `GeneratedLabelSymbol` the
direct target of a `BoundGotoStatement` (as opposed to a break/continue-style structural reference),
it needs a real location the same way.**

**Verified end-to-end** by compiling+running a real program with the freshly-built `csc` (plain block,
nested block — confirms "nearest only" — `try`/`finally` where `finally` still runs, and `catch`),
not just unit tests: all four cases print exactly the expected output and skip the statements after
`escape;`. Compiler tests: `EscapeStatementParsingTests`
(`src/Compilers/CSharp/Test/Syntax/Parsing/`) and `EscapeStatementTests`
(`src/Compilers/CSharp/Test/CSharp15/`, using `CompileAndVerify(..., expectedOutput: ...)` the same
way `LabeledBreakContinueEmitTests` does).

**IDE:** keyword completion only (`EscapeKeywordRecommender`, mirroring `MutateKeywordRecommender`'s
identifier-text-based approach since there's no real `SyntaxKind.EscapeKeyword` for
`AbstractSyntacticSingleKeywordRecommender` to key off), plus an explicit (already-correct-by-default)
`SyntaxKind.EscapeStatement` case in `BreakpointSpans.cs`'s whole-statement-span fallback list.
Classification, keyword highlighting, and outlining were not added (same limitation `mutate` already
has, since `EscapeKeyword`'s token kind is `IdentifierToken`, not a real keyword `SyntaxKind`).

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
  (`Access` + `FallbackStatement`) instead of an expression — **reference-type receivers only** since
  the ordering fix below; a value-type (`Nullable<T>`) receiver is left unintercepted here so it falls
  through to the void-coalescing expression path instead. Lowered in `LocalRewriter_ConditionalAccess.cs`
  by threading a `whenNullOpt` fallback
  through `RewriteConditionalAccess` (reusing `BoundLoweredConditionalAccess`'s existing `WhenNullOpt`
  slot). Flow analysis: `AbstractFlowPass.VisitConditionalCoalesceStatement` visits both branches
  unconditionally (conservative, not flow-splitting).
- **Void-coalescing expression** (`BoundVoidCoalesceExpression`, added second, in `Binder_Operators.
  BindNullCoalescingOperator`). Same trigger shape, but reached only when ordinary `??` expression
  binding sees a void-typed `BoundConditionalAccess` on the left — and only accepts **reference-type**
  receivers, reporting `ERR_VoidCoalesceRequiresReferenceTypeReceiver` (CS10004) otherwise. Lowered
  separately in `LocalRewriter_VoidCoalesceExpression.cs` to an `if (receiver != null) { ... } else
  { fallback; }`.

**Ordering gap — resolved.** `Binder_Statements.BindExpressionStatement`'s interception now only fires
for reference-type receivers (`speculativeAccess.Receiver.Type is { IsReferenceType: true }`), matching
the restriction `BindVoidCoalesceExpression` already enforced. A value-type (`Nullable<T>`) receiver is
no longer intercepted at the statement level and instead falls through to ordinary `??` expression
binding, which reaches `BindNullCoalescingOperator` → `BindVoidCoalesceExpression` and correctly reports
`ERR_VoidCoalesceRequiresReferenceTypeReceiver` (CS10004) — previously unreachable in the common case.
`IsValidStatementExpression`'s existing `BoundKind.VoidCoalesceExpression` special-case (treats it as
always statement-valid) means this fallthrough doesn't also spuriously report `ERR_IllegalStatement`.
Reference-type receivers are unaffected — they still take the statement-level `BoundConditionalCoalesceStatement`
path, which both features' `IOperation` results already converged on anyway (`CSharpOperationFactory`
builds the same `VoidCoalesceOperation` either way). If touching either feature, still check the other —
they compete for the same syntax shape, just split now by receiver type instead of one unconditionally
shadowing the other. **Verified**: `Microsoft.CodeAnalysis.CSharp.csproj` builds clean, and
`VoidCoalesceTests.cs` (`src/Compilers/CSharp/Test/Semantic/Semantics/`) has passing compiler tests
for both cases — a reference-type receiver still binds with no diagnostics via the statement-level
path, and a `Nullable<T>` receiver reports `ERR_VoidCoalesceRequiresReferenceTypeReceiver`.

**Two `--testIOperation` gaps found and fixed via `.\folly scry --testIOperation` (both in
`VoidCoalesceTests`, unrelated to each other):**
1. `TestOperationVisitor` (`src/Compilers/Test/Core/Compilation/TestOperationVisitor.cs`) had no
   `VisitVoidCoalesce` override for `IVoidCoalesceOperation`, so `--testIOperation` walked into
   `DefaultVisit` and threw `NotImplementedException`. Fixed by adding an override next to
   `VisitCoalesce`, asserting `OperationKind.VoidCoalesce` and validating `Access`/`WhenNull` as the
   two `ChildOperations`.
2. `ControlFlowGraphBuilder.VisitVoidCoalesce` (`src/Compilers/Core/Portable/Operations/
   ControlFlowGraphBuilder.cs`) never closed the receiver-chain's capture spill region before
   branching to the `WhenNull` block. `VisitConditionalAccessTestExpression`'s `PopStackFrame` (shared
   with `VisitConditionalAccess`) merges the spill region into whatever region was current, but leaves
   it *open* — for a bare `a?.M();` statement this is harmless because the `WhenNull` block is empty
   and gets elided, letting the region's last block resolve to wherever the receiver capture was
   actually used. `VoidCoalesceExpression`'s `WhenNull` is a real fallback statement, so that trailing
   block survives and, if the capture region were left open across the branch, would silently become
   the region's *last* block — even though it never references the capture — tripping
   `ControlFlowGraphVerifier`'s "capture used before leaving its region" check
   (`Capture [n] is not used in region [Rx] before leaving it after block [Bx]`). Fixed by capturing
   `resultCaptureRegion = CurrentRegionRequired` up front and calling `LeaveRegionsUpTo
   (resultCaptureRegion)` right after finishing the `WhenNotNull` chain, before creating the `WhenNull`
   block — mirroring `VisitConditionalAccess`'s non-statement-level (captured-value) path. **Verified**:
   `VoidCoalesceTests` pass (2/2), plus the broader `FlowAnalysis`/`ConditionalAccess`/`IOperation`
   filter in `Microsoft.CodeAnalysis.CSharp.Semantic.UnitTests` (103/103) on net10.0 in this
   environment (net472 can't run under Linux/Mono here — same fix applies, untested on that TFM).

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
