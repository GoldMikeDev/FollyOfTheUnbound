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
`LanguageParser.TryParseStatementStartingWithIdentifier` checks `CurrentToken.Text == "escape"`
(the *raw* token text, not `ValueText` — see the third-review-round `@escape` fix below, since
`ValueText` is the same `"escape"` for both `escape` and `@escape`) **and** that the very next token
is `;`, so `escape();`, `escape = 1;` and `@escape;` all still parse as ordinary expression
statements using `escape` as an identifier — no real `SyntaxKind.EscapeKeyword` exists).

**Semantics:** jumps to just before the closing brace of the *nearest* enclosing `{ }` block —
including a `catch` or `finally` block's own body, and a `try` block's body (where `finally` still
runs normally afterward, since the jump target is inside the `try`'s own protected region, not past
it). Unlike `break`/`continue`, it never searches past the nearest block to find a loop/switch — it
always targets the closest enclosing `BlockSyntax`, full stop. An ordinary method body is itself a
block, so `escape;` at the top level of a regular method just runs to the end of the method with no
dedicated "no enclosing block" diagnostic needed there.

The null-target case (`Binder.GetEscapeLabel(SyntaxNode)` never overridden by a `BlockBinder`, so it
bottoms out at `BuckStopsHereBinder.GetEscapeLabel => null`) **is** reachable, though: global/top-level statements and
script code bind through `SimpleProgramBinder`/`BuckStopsHereBinder`, not through a `BlockBinder`, so
an `escape;` written directly in top-level statements has no enclosing `BlockSyntax` binder at all and
correctly reports `ERR_NoBreakOrCont` (see the IDE section below, which intentionally excludes `escape`
from keyword completion at that scope for the same reason). The defensive `Error(diagnostics,
ErrorCode.ERR_NoBreakOrCont, node)` branch in `Binder_Statements.BindEscapeStatement` is exercised by
that case, not dead code.

**Implementation — deliberately lowering-free:** rather than a new `BoundNode` kind with custom
`LocalRewriter`/codegen handling, `escape;` binds directly to an ordinary `BoundGotoStatement`
targeting a `GeneratedLabelSymbol` that is lazily allocated per block:
- `Binder.GetEscapeLabel(SyntaxNode escapeStatementSyntax)` (a method, not a property — mirroring
  `GetBreakLabel`/`GetContinueLabel`'s parameterized pattern so the callee can tell *which* syntax
  node is asking; see the third-review-round speculative-binding fix below for why that parameter
  exists) delegates to `Next` by default, `null` at `BuckStopsHereBinder`. Overridden in
  `BlockBinder.GetEscapeLabel`: if `escapeStatementSyntax` is a genuine descendant of the block (a
  manual `.Parent` walk, not a `Span`/position check), lazily allocates and caches *its own* label
  (no further delegation — this is what gives `escape;` "nearest enclosing block, not nearest
  enclosing loop" semantics, the opposite of `GetBreakLabel`/`GetContinueLabel`'s search); otherwise
  (a free-standing/speculative node) returns a fresh, uncached label without touching the block's
  cached state.
- `Binder_Statements.BindBlockParts` appends a `BoundLabeledStatement` (wrapping a no-op
  `BoundNoOpStatement`) as the block's last statement, but **only if** `BlockBinder.
  EscapeLabelIfAllocated` is non-null — i.e. only if some `escape;` inside that exact block actually
  asked for the label. An unused block never gets the extra synthetic label statement.
- `Binder_Statements.BindEscapeStatement` binds `escape;` to `new BoundGotoStatement(node,
  this.GetEscapeLabel(node), null, null)`.

Because the label always sits at the end of the *same* lexical block as any `escape;` that targets
it, this is never a jump across a protected-region boundary from the CLR's point of view — it is
lexically indistinguishable from a plain forward `goto` within one block. That means all the usual
machinery (existing goto/label lowering in `LocalRewriter`, `ControlFlowPass` unreachable-code
detection after an unconditional jump, the `ControlFlowGraphBuilder`) already handles it correctly
with **zero new lowering code**. This is why escaping a `try`/`catch`/`finally` body "just works":
it's not special-cased, it's a consequence of the label placement.

**One exception that did need special-casing (PR #96 review, fixed):** a `using var`/`await using`
declaration in the same block as an `escape;` that precedes it breaks the "lexically indistinguishable
from a plain goto" story above in two ways, because `LocalRewriter.VisitPossibleUsingDeclaration`
wraps *every* statement textually after a using declaration -- including the synthesized escape
label, since it's appended as the block's last statement -- inside the try/finally generated for that
declaration:
- `ControlFlowPass.VisitGotoStatement` treats the escape goto exactly like a user `goto` jumping
  forward over a using declaration to a same-block label and reports `ERR_GoToForwardJumpOverUsingVar`
  -- even though `escape;` is structurally the same kind of legitimate forward jump `break`/`return`
  already make past a later using declaration, just spelled as a raw goto instead of a dedicated
  bound node. Fixed by special-casing `node.Syntax is EscapeStatementSyntax` in
  `VisitGotoStatement` to skip the using-declaration jump checks entirely (only a forward jump is
  possible for `escape;`, since its target is always later in the same block).
- Even with that diagnostic suppressed, the *label* would still end up nested inside the
  using-declaration's generated try, making the goto (which originates before the try) an illegal
  jump into protected code. Fixed in `LocalRewriter.VisitPossibleUsingDeclaration`
  (`LocalRewriter_Block.cs`): the compiler-generated escape label -- always the *original*
  (pre-lowering) block statement list's last entry -- is detected against the **raw** `statements[^1]`
  (via `IsCompilerGeneratedEscapeLabel`, matching `WasCompilerGenerated`/`GeneratedLabelSymbol`/
  `BoundNoOpStatement`) before it gets lowered and swept into the trailing-statements list that
  becomes the using declaration's try body. When present, it's carried out through a new
  `extraTrailingStatement` out-parameter and the caller (`VisitStatementSubList`) appends it as a
  genuine sibling statement in whatever list it's building at that exact nesting level -- never
  wrapped inside another node. Because every nesting level funnels its own trailing statements
  through the same out-parameter, this hoists the label past *every* enclosing using declaration in
  the block, not just the innermost one, and lands it as a plain sibling of the generated try/finally
  in the enclosing `BoundBlock`'s statement list -- structurally identical to an ordinary
  `goto End; try { ... } finally { ... } End: ;`.
  - **Two mistakes caught along the way, both by hand-compiling repro programs with the fork's own
    `csc` and inspecting the crash/output rather than trusting the reasoning in isolation:**
    1. An earlier version of this fix checked the *already-lowered* `builder[^1]` instead of the raw
       `statements[^1]`. Lowering a labeled statement rewrites `BoundLabeledStatement` into a
       `BoundStatementList` (see `LocalRewriter_LabeledStatement.MakeLabeledStatement`), so by the
       time the check ran it could never match -- the label silently stayed nested inside the try
       after all, with the ControlFlowPass diagnostic now (wrongly) suppressed and no compile error
       to reveal it. The result was a real `ILBuilder` assertion failure at emit time
       (`BasicBlock.RewriteBranchesAcrossExceptionHandlers`: `BranchBlock?.EnclosingHandler == null`,
       i.e. "cannot branch into a handler") that manifested as `dotnet test` hanging (the test host's
       failure/crash handling did not fail fast). Caught by isolating the specific failing test with
       `timeout` and reproducing it directly against `csc.dll` outside the test host.
    2. An earlier version of the sibling-append also tried wrapping `[usingStatement,
       trailingEscapeLabel]` in a `BoundStatementList` returned from the `UsingLocalDeclarations`
       case, rather than appending both directly into the caller's output list via the out-parameter.
       That *also* hit the same `ILBuilder` assertion, even once the raw-vs-lowered detection bug
       above was fixed -- nesting the pair inside a `BoundStatementList` node (rather than making them
       true flat siblings in the enclosing block's own `Statements` array) was apparently enough to
       confuse the exception-handler-region bookkeeping. The final fix avoids constructing any wrapper
       node at all for this pair.

The synthesized label statement and its no-op body are both marked `WasCompilerGenerated = true`
(the escape's own `BoundGotoStatement` is not -- it corresponds to real `escape;` syntax), so
`GetOperation` doesn't expose the synthetic label/empty-statement pair as fake source-level
`ILabeledOperation`/`IEmptyOperation` nodes to analyzers.

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

**IDE:** keyword completion (`EscapeKeywordRecommender`, mirroring `MutateKeywordRecommender`'s
identifier-text-based approach since there's no real `SyntaxKind.EscapeKeyword` for
`AbstractSyntacticSingleKeywordRecommender` to key off) -- but, unlike `mutate`, **not** offered at
global/top-level-statement scope: top-level statements bind through `SimpleProgramBinder`, whose
chain bottoms out at `BuckStopsHereBinder.GetEscapeLabel => null`, so an `escape;` inserted there can
never bind and always fails with `ERR_NoBreakOrCont`. An explicit `SyntaxKind.EscapeStatement` case
was added to `BreakpointSpans.cs`'s whole-statement-span fallback list, and (initially missing, fixed
in PR #96 review) to `SyntaxComparer.ClassifyStatementSyntax` (EnC active-statement tracking) as a
leaf `Label.GotoStatement`, and to `LookupPosition.GetFirstIncludedToken`/`GetFirstExcludedToken` (a
missing case there made any `SemanticModel` lookup whose position is inside `escape;`, e.g.
`LookupSymbols`, throw `UnexpectedValue`). Basic keyword classification was also added: `escape` is
now registered in `ClassificationHelpers.IsActualContextualKeyword` (same pattern as `mutate`), so it
colors as `ClassificationTypeNames.Keyword` instead of a plain identifier. Note this gives it the
same *plain* keyword coloring `mutate` gets, not `ControlKeyword` -- `IsControlKeyword`/
`IsControlStatementKind` key off the token's actual `SyntaxKind`, and since `escape`'s (like
`mutate`'s) token kind is still `IdentifierToken`, not a real keyword `SyntaxKind`, that path can
never be reached by either construct; a `ControlStatementKind` case for `EscapeStatement` would be a
no-op. Keyword highlighting and outlining were not added (same limitation `mutate` already has).

**Fourth review round on PR #96 (3 more Codex findings on commit `737227b8`, both named production
findings confirmed real and fixed, plus a proactive sweep):**
- **`ContainsTopLevelEscapeStatement()`'s walk didn't know about this fork's own `do`/`until`
  construct.** `GetEmbeddedStatement()` (the shared helper it falls through to for unrecognized
  statement kinds) had cases for every *standard* embedded-statement owner (`do`/`while`/`for`/
  `foreach`/etc.) but none for `DoUntilStatementSyntax` — so `if (condition) do escape; until (done);`
  (a do-until as an *unbraced* embedded statement of an `if`) wasn't recognized as containing a
  top-level `escape;`, and AddBraces would have silently retargeted it. Fixed by adding a
  `DoUntilStatementSyntax n => n.Statement` case to `GetEmbeddedStatement()` (in the shared
  `SyntaxNodeExtensions.cs`) and a `DoUntilStatementSyntax` case to `IsEmbeddedStatementOwner()`,
  right next to `DoStatementSyntax` — `DoUntilStatementSyntax` is structurally identical to
  `DoStatementSyntax` (`until` instead of `while`) and exposes its body the same way, via `.Statement`.
  Test: `AddBracesTests.DoNotWrapIfBodyContainingTopLevelEscapeStatementInsideDoUntil`.
- **`UseSimpleUsingStatementDiagnosticAnalyzer` (IDE0063, "convert to using declaration") removes a
  block, retargeting an `escape;` inside it — the inverse of the AddBraces/ConvertForEachToFor hazard.**
  All four prior fixes guarded actions that *add* a block; converting `using (var d = Get()) { ... }`
  to `using var d = Get(); ...` *removes* the block that used to wrap the body, so a top-level
  `escape;` in that body gets silently retargeted to whatever larger scope now contains it (e.g. an
  enclosing `while` loop — turning an infinite-loop-avoidance `escape;` into a real infinite loop, or
  vice versa). Fixed in `UsingStatementDoesNotInvolveJumps` (`UseSimpleUsingStatementDiagnosticAnalyzer.
  cs`), the same helper that already walks the innermost using's direct body statements checking
  `IsGotoOrLabeledStatement` for the analogous goto/label hazard: added a
  `statement.ContainsTopLevelEscapeStatement()` check per direct statement, reusing the *existing*
  helper as-is rather than adding a block-specific variant — `ContainsTopLevelEscapeStatement()`
  already takes a `StatementSyntax` and stops at any nested `BlockSyntax` it encounters, which is
  exactly right here: called once per statement *inside* the block being removed, it correctly leaves
  alone an `escape;` that's already nested in its own inner block (unaffected by removing the
  *outer* using's block) while still catching one reached through a chain of further unbraced
  embedded statements (nested `if`/`for`/`while`/do-until/etc.) directly inside the removed block.
  Tests: `TestMissingWithTopLevelEscapeInBody`, `TestMissingWithTopLevelEscapeInBodyThroughUnbracedIf`,
  and the control case `TestOfferedWhenEscapeStatementIsInsideItsOwnNestedBlock` (still offered/fixes
  normally).
- **Proactive sweep for the same hazard shape elsewhere** (grepped `SyntaxFactory.Block(` across
  `src/Analyzers/CSharp` and `src/Features/CSharp`, both the add-a-block and remove-a-block
  directions):
  - **Checked, confirmed clean:**
    - `CSharpUseLabeledJumpStatementsCodeFixProvider.ReplaceLoop` wraps a labeled loop (`label: while
      (...) { ... }`) in a new `SyntaxFactory.Block(...)` when the loop's own parent isn't already a
      block/switch-section/top-level-statement. Looks identical to the AddBraces/ConvertForEachToFor
      shape at a glance, but isn't: the loop being wrapped always already has a *braced* body by the
      time this runs (both `TryGetGotoBreakPattern`/`TryGetGotoContinuePattern` and the flag-pattern
      path require a labeled empty statement as the last statement of a `BlockSyntax` body to detect
      the pattern at all — confirmed by the continue-path's own unconditional
      `(BlockSyntax)newLoop.GetEmbeddedStatement()!` cast a few lines above). Wrapping the *outer*
      labeled-loop statement in a new block cannot retarget an `escape;` that's already nested inside
      the loop's own inner block body.
    - `AssignOutParametersAboveReturnCodeFixProvider.AddAssignmentStatements` wraps `exprOrStatement`
      (always the flagged `return` statement itself, or an expression standing in for one) together
      with newly generated assignment statements in a new block when its parent is an embedded-
      statement owner. `exprOrStatement` is the statement being wrapped, not a container of other
      statements that could itself hold an unrelated `escape;` — no hazard.
    - The `UseExpressionBody`/`UseExpressionBodyForLambda` families (block body ⇄ `=>` expression
      body) only ever fire when the block body is a single expression-convertible `return`/`throw`
      statement (or, for void members, a single expression statement) — `escape;` is a statement, not
      an expression, and can't appear inside a body those analyzers consider convertible, so there's
      no block-removal path an `escape;` could ever be caught by.
    - `CSharpRemoveAsyncModifierCodeFixProvider.ConvertToBlockBody` and
      `CSharpRemoveUnreachableCodeCodeFixProvider` also construct new blocks, but the former wraps a
      brand-new statement derived from an expression body (no pre-existing statement content to
      retarget) and the latter replaces genuinely unreachable code with an *empty* block (nothing to
      retarget, and unreachable code past an unconditional jump like `escape;`'s goto is exactly the
      kind of code `ControlFlowPass` already flags separately).
  - **Found but ambiguous, left as an open question (not fixed this round):**
    `CSharpMethodExtractor.CSharpCodeGenerator.CallSiteContainerRewriter.ReplaceStatementIfNeeded`
    (Extract Method) has a narrow path (`// replace one statement with multiple statements (see bug
    # 6310)`) that replaces a *single* embedded-statement-owner's body (e.g. a `do`/`while`/`for`
    loop's unbraced `Statement`) with `SyntaxFactory.Block(statements)` when the extraction leaves
    behind more than one statement in that position (the extracted call plus leftover code). If the
    original unbraced body was itself a top-level `escape;` (or led to one through further unbraced
    statements) that got selected for extraction such that residual statements remain, this is the
    same block-introduction hazard. It's left undetermined rather than fixed because: (a) it's unclear
    whether Extract Method's selection/analysis pipeline can ever select *through* an `escape;` in the
    first place without already rejecting the selection for other control-flow reasons (jumping out of
    a would-be-extracted region is exactly the kind of thing Extract Method's existing jump/control-
    flow analysis is supposed to reject before this rewriter ever runs, but that hasn't been traced
    end-to-end here) — if it does reject such selections already, this code path may be unreachable
    for `escape;` specifically; (b) confirming the actual reachability requires a live repro through
    the Extract Method command's full analysis pipeline (selection validity, `AnalyzeAsync`, control
    flow), which is a substantially larger investigation than the pattern-matched fixes above.
    Whoever next touches Extract Method or revisits this sweep should check whether Extract Method's
    control-flow analysis already rejects selections containing `escape;`, and if not, apply the same
    `ContainsTopLevelEscapeStatement()`-based guard here.
  - **Not deeply investigated (skipped per the sweep's own time-budget guidance, judged unlikely):**
    `ConvertSwitchStatementToExpression` (switches aren't `escape`'s domain — a `switch` block isn't a
    `BlockSyntax` `escape;` could target in the first place), `InlineTemporaryVariable`,
    lock/fixed-statement-specific simplifications beyond what's already covered by the embedded-
    statement-owner cases above. No dedicated "remove unnecessary braces" analyzer exists in this
    fork or upstream Roslyn to check.

**Third review round on PR #96 (5 more Codex findings on commit `f7e424ad`, all confirmed real, fixed):**
- **Split/Merge Nested If refactorings had the same block-retargeting hazard as round 2's AddBraces/
  ConvertForEachToFor fix.** `SplitIntoNestedIfStatements` (`if (a && b) S;` → `if (a) { if (b) S; }`)
  introduces a new `BlockSyntax` around the outer `if`'s original body via `IIfLikeStatementGenerator.
  WithStatementInBlock` -- if that body is/leads to a top-level `escape;`, the new block silently
  retargets it. `MergeNestedIfStatements` (the reverse) does the opposite: it *removes* the block that
  wraps a nested `if`, which retargets an `escape;` that used to target that removed block. Fixed with
  two new virtual hooks, both defaulting to "safe" and overridden only in the CSharp-specific providers
  (the Core/Portable abstract classes are shared with VB and can't reference the CSharp-only
  `ContainsTopLevelEscapeStatement()` extension):
  - `AbstractSplitIfStatementCodeRefactoringProvider.IsSafeToRefactor(ifOrElseIf)`, checked in the
    shared `ComputeRefactoringsAsync` before registering the action at all. Overridden in
    `CSharpSplitIntoNestedIfStatementsCodeRefactoringProvider` to check `((IfStatementSyntax)ifOrElseIf).
    Statement.ContainsTopLevelEscapeStatement()`.
  - `AbstractMergeNestedIfStatementsCodeRefactoringProvider.IsSafeToMerge(innerIfStatement)`, checked in
    `CanBeMergedAsync` (had to change from `static` to an instance method to call the virtual hook) for
    both the "merge up" and "merge down" directions, which share that one implementation. Overridden in
    `CSharpMergeNestedIfStatementsCodeRefactoringProvider` to check `((IfStatementSyntax)innerIfStatement).
    ContainsTopLevelEscapeStatement()` -- reusing the helper's own `IfStatementSyntax` case (which already
    walks `.Statement` and `.Else`) directly on the nested `if`, since *it* is the node whose wrapping
    block is being removed.
- **AddBraces still *offered* the code fix (IDE0011) for a hazardous diagnostic, even though round 2
  made the rewrite a no-op when applied.** Triggering the action then visibly did nothing, and the
  diagnostic stayed "fixable" forever. Fixed in `CSharpAddBracesCodeFixProvider.RegisterCodeFixesAsync`:
  it now checks every diagnostic in `context.Diagnostics` for the hazard (via the same `GetEmbeddedStatement
  ()`/`ContainsTopLevelEscapeStatement()` pair `FixAllAsync` already used) and skips registering the fix
  entirely if any diagnostic in the context has it, while `FixAllAsync`'s existing per-diagnostic no-op
  guard still lets Fix All fix the *other*, safe diagnostics in the same operation.
- **`@escape;` parsed as the escape statement instead of an ordinary identifier expression.** The
  parser's detection (`LanguageParser.TryParseStatementStartingWithIdentifier`) checked `CurrentToken.
  ValueText == "escape"` -- the *unescaped* semantic text, which is `"escape"` for both `escape` and
  `@escape`. But `@identifier` is specifically how source opts a contextual keyword *out* of its special
  interpretation (same as `@if`, `@mutate`, etc.), so `@escape;` should parse as a bare identifier-expression
  statement, not `EscapeStatementSyntax`. Fixed by checking `CurrentToken.Text` (the raw token text,
  which differs for `@escape`) instead of `ValueText`, matching how the classifier already distinguishes
  the two for this exact reason. (`mutate` has the same `ValueText`-based check and likely the same
  latent bug for `@mutate;` -- not in scope of this fix, flagged here for whoever touches `mutate` next.)
- **Speculative binding of a synthetic `escape;` mutated the real, long-lived `BlockBinder`'s lazy
  label state.** `SemanticModel.TryGetSpeculativeSemanticModel(position, StatementSyntax, ...)` for a
  synthetic `escape;` reuses the *real* enclosing binder chain (`this.GetEnclosingBinder(position)` in
  `MethodBodySemanticModel.TryGetSpeculativeSemanticModelCore`), wrapping it in a new speculative
  `ExecutableCodeBinder`. The old `BlockBinder.EscapeLabel` getter unconditionally lazily allocated and
  cached `_lazyEscapeLabel` via `Interlocked.CompareExchange` on `this` -- with no way to tell whether
  `this` was answering for a real `escape;` actually in the block's source, or a free-standing
  speculative node that merely happens to reuse the same binder chain. A later real bind of that exact
  block (same `BlockBinder` instance, since it's found via the compilation-shared per-tree binder cache)
  would then see `EscapeLabelIfAllocated` non-null and append an unused synthesized label statement,
  which `ControlFlowPass` would flag with a spurious `WRN_UnreferencedLabel`.
  - **Fix:** `Binder.EscapeLabel` (a property, delegating to `Next`) became `Binder.GetEscapeLabel
    (SyntaxNode escapeStatementSyntax)` (a method, mirroring `GetBreakLabel`/`GetContinueLabel`'s
    parameterized pattern) so the callee can tell *which* syntax node is asking. `BlockBinder.
    GetEscapeLabel` now first checks `IsDescendantOfThisBlock(escapeStatementSyntax)` (a manual `.Parent`
    walk comparing reference identity against `_block`, not a `Span`/position check -- a speculative
    node is a free-standing, unattached syntax tree that was never made a descendant of the real tree,
    so this is robust regardless of what absolute offsets the parser happened to assign it): if the node
    isn't a real descendant, it returns a **fresh, uncached** `GeneratedLabelSymbol` without touching
    `_lazyEscapeLabel` at all; only a genuine descendant allocates/caches as before. `Binder_Statements.
    BindEscapeStatement` now calls `this.GetEscapeLabel(node)` instead of `this.EscapeLabel`.
    `BuckStopsHereBinder.GetEscapeLabel` still just returns `null`.
  - **Verification note (matters for anyone re-validating this):** a straightforward repro through
    `Compilation.GetDiagnostics()` or even `SemanticModel.GetDiagnostics()` called *after* the
    speculative bind does **not** observe the corruption in this codebase, empirically -- each of those
    entry points turns out to construct its own independent binder chain for the method body rather than
    reusing the one `SemanticModel.GetEnclosingBinder` (and thus the speculative bind) obtained and
    cached. That's an accidental insulation of *those two specific call paths*, not evidence the bug is
    harmless: the real, mutated `BlockBinder` instance is reachable and demonstrably corrupted (confirmed
    by instrumenting the old code -- a foreign/speculative node did allocate onto the real block's shared
    instance), and any other consumer that reuses `SemanticModel.GetEnclosingBinder`'s result more
    directly (which is exactly what the speculative-binding code itself does, and plausibly what some
    IDE-side caching could do too) would see it. The regression test
    (`EscapeStatementTests.Escape_SpeculativeBind_DoesNotMutateRealBlockBinder`) therefore exercises the
    mechanism directly at the binder level -- obtains the real `BlockBinder` via `SemanticModel.
    GetEnclosingBinder` (the same API the speculative-binding code path uses internally), calls
    `GetEscapeLabel` with a free-standing `escape;` node the way the speculative code does, and asserts
    `EscapeLabelIfAllocated` stays `null` on the real binder -- plus a control case confirming a genuine
    descendant `escape;` still allocates and caches correctly. Confirmed failing (compile error against
    the pre-fix `EscapeLabel` property API, and via direct instrumentation of the old getter showing it
    allocate on the real block's shared instance for a foreign node) before the fix, passing after.

**Second review round on PR #96 (3 more Codex findings, all confirmed real, fixed):**
- **IDE refactorings that wrap an embedded statement in a new block can silently retarget an `escape;`
  inside it.** Because `escape;`'s target is purely syntactic ("nearest enclosing `BlockSyntax`"),
  any refactoring that introduces a *new* `BlockSyntax` around an unbraced embedded statement changes
  what a top-level `escape;` inside that statement targets, even though such refactorings (add braces,
  convert `foreach` to `for`) are supposed to be behavior-preserving. Two call sites were affected:
  `CSharpAddBracesCodeFixProvider.FixAllAsync` (`src/Analyzers/CSharp/CodeFixes/AddBraces/`) and
  `CSharpConvertForEachToForCodeRefactoringProvider.GetForLoopBody`
  (`src/Features/CSharp/Portable/ConvertForEachToFor/`). Fixed with a shared helper,
  `StatementSyntax.ContainsTopLevelEscapeStatement()` (in the shared `SyntaxNodeExtensions.cs` under
  `src/Workspaces/SharedUtilitiesAndExtensions/Compiler/CSharp/Extensions/`), which walks through a
  chain of *further* unbraced embedded statements (nested `if`/`else`, `for`, `while`, etc. -- anything
  `GetEmbeddedStatement()` recognizes, plus `IfStatementSyntax.Else`) looking for a top-level
  `escape;`, and stops (returns `false`) the moment it hits an already-present `BlockSyntax`, since an
  `escape;` already inside its own block is unaffected by wrapping an ancestor statement. AddBraces
  uses this to leave the diagnostic's node unwrapped (returns `currentStatement` unchanged) when the
  hazard applies; ConvertForEachToFor uses it in `ValidLocation` to not offer the refactoring at all
  for such a `foreach` (its body always needs to become a block to host the new index/item variable,
  so there's no safe partial fix like AddBraces has). Tests: `AddBracesTests` (do-not-wrap /
  do-not-wrap-when-nested / control-case-still-wraps-when-escape-already-in-its-own-block) and
  `ConvertForEachToForTests` (do-not-offer / do-not-offer-when-nested / still-offers-when-escape-
  already-in-its-own-block).
- `PublicAPI.Unshipped.txt` was missing the generated
  `EscapeStatementSyntax.AddAttributeLists(params AttributeListSyntax[])` fluent method entry (the
  generator emits one `Add*` method per list-typed property, same as every other statement syntax
  type) -- added.
- `KeywordCompletionProvider`'s registry is alphabetically ordered; `EscapeKeywordRecommender` had
  been inserted before `Equals`/`Error` instead of after -- moved to sit between `Error` and `Event`.

## IDE support status for the four constructs (see "Adding IDE Support for a New Statement/Expression SyntaxKind" in `.github/instructions/IDE.instructions.md`)

As of this writing, all four constructs have: classification, keyword completion, formatting rules,
outlining/brace-matching, keyword highlighting, and breakpoint spans.

**Deliberately not yet done (forward-looking, flagged as premature by the user):** code fixes /
refactorings that suggest replacing existing constructs with these new ones (e.g. suggesting
`do/until` in place of `do/while(!cond)`, or the if/catch chain in place of separate `if`+`try/catch`).
This would be a `CodeRefactoringProvider`/`CodeFixProvider`, not yet started.

**Not audited:** whether other IDE analyzers with exhaustive `SyntaxKind` switches (IDE0xxx style/
simplification analyzers) silently skip these new node kinds. Not yet checked.

**PR #99 (10 Codex findings deferred from PR #97's review, all fixed) — mostly `if/catch` and `mutate`
gaps beyond the "as of this writing" baseline above:**
- `LocalBinderFactory.VisitIfCatchStatement` now propagates `BinderFlags.InTryBlockOfTryCatch` to the
  chain's arms/trailing-else when it has catches (mirroring `VisitTryStatement`) — without this, a
  `yield return` inside an if/catch arm with a following `catch` missed CS1626 and crashed the
  iterator rewriter downstream.
- `CSharpMiscellaneousReducer.SimplifyBlock` no longer simplifies a block-condition arm's
  `ConditionBlock`/`Consequence` to an embedded statement — both are required blocks for that arm
  shape, so simplifying either produced unparseable syntax.
- `NullableWalker.VisitMutateStatement` no longer always inherits the source local's flow state for
  the mutated local. `BoundMutateStatement.ConversionExpression` is just a reference to the original
  local (see `Binder_MutateStatement`), not the real lowered conversion
  (`LocalRewriter_MutateStatement.BuildLoweredConversion`), so which state to use now depends on that
  method's actual lowering strategy: same-underlying-type mutation (`string? -> string`) and
  reference-to-reference mutation where the target isn't `string` (`object? -> string[]`, lowered
  through a plain checked cast, which preserves the source reference including its null-ness) both
  still track the source; everything else (anything `-> string` via `.ToString()`, `string ->`
  primitive via `Type.Parse`, numeric/bool paths — all producing a genuinely new value) falls back to
  the target type's own declared nullable annotation.
- `CSharpEditAndContinueAnalyzer` gained `DoUntilStatement` cases in `FindStatementAndPartner`,
  `TryGetActiveSpan`, and `FindContainingStatementPart`, mirroring the existing `DoStatement` ones —
  EnC's active-statement span for a do/until loop's condition now stays on `until (...)` instead of
  resolving to the body's opening brace.
- `UntilKeywordRecommender` now recommends `until` after any unbraced `do` body (checking whether the
  target token is the last token of a `DoStatement` whose `WhileKeyword` is still missing), not just
  after a `{ }` block — the previous block-only check was too narrow, matching how the parser actually
  accepts `until` in either shape.
- `SyntaxTreeExtensions.IsCatchOrFinallyContext` now offers `catch`/`finally` after an unbraced
  trailing `else` (`else Work();`), not just a braced one.
- `CSharpAddBracesDiagnosticAnalyzer.RequiresBracesToMatchContext` now matches braces across sibling
  if/catch arms (added `AnyArmOfIfCatchChainUsesBraces`), not just the classic if/else-if/else case.
- `CSharpProximityExpressionsService` (the source behind the debugger's Autos window): added a
  `VisitMutateStatement` collector override (previously the mutated local was silently omitted from
  Autos for a breakpoint on the following statement), and `Worker.AddLastStatementOfConstruct`'s
  `IfCatchStatement` case now visits the chain's arms/else *and* catches together when catches exist
  (previously only catches) — mirrors the adjacent `TryStatement` case, which already visits both its
  normal body and every catch.
- Razor's `CSharpCodeParser.ParseElseClause` now parses a trailing `catch`/`finally` after a plain
  (non-`else if`) trailing `else`, via a `ParseTrailingCatchOrFinally` helper shared with
  `ParseAfterIfClause` — previously only the arm-chain-without-trailing-else path checked for a
  following catch/finally, so `if { ... } { ... } else { ... } catch { ... }` misparsed in
  `.razor`/`.cshtml`. Covered by `CSharpBlockTest.SupportsCatchClauseAfterBracedTrailingElse`/
  `SupportsCatchClauseAfterUnbracedTrailingElse` and the finally-clause equivalents
  (`src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/test/Legacy/CSharpBlockTest.cs`), using
  the existing `ParseDocumentTest`/baseline-tree infrastructure that file already has for `try`/`catch`/
  `finally` and `if`/`else` — no new test infrastructure was needed. **Still an open gap:** that's the
  only Razor parser coverage this fork's if/catch/mutate/until statements have; `do`/`until`, `mutate`,
  and the rest of the if/catch chain shape (block-condition arms, multiple arms, `ifout`) remain
  untested in Razor specifically (each has real compiler-level test coverage elsewhere in this file,
  just not through Razor's embedded-C#-in-markup parsing path).
- Also fixed while in the area (not from the Codex review, noticed alongside the mutate collector
  gap): `CSharpProximityExpressionsService.RelevantExpressionsCollector` had no
  `VisitDoUntilStatement` override, mirroring the existing `VisitDoStatement` one.

**Separately noticed, not yet fixed:** `object? value = null; mutate value to C;` (mutating to a
user-defined class, or any reference type whose name is a bare `IdentifierNameSyntax`/
`QualifiedNameSyntax` rather than a keyword/array-type syntax) crashes
`MethodCompiler.BindMethodBody`'s debug-only `assertBindIdentifierTargets` consistency check.
`Binder_MutateStatement` binds the mutation's target `Type` via `BindType`, which doesn't register the
type-name identifier in `InMethodBinder.IdentifierMap` the way ordinary expression identifiers get
registered via `BindExpression` — but the assert's identifier-prediction pre-pass apparently still
predicts that identifier needs binding through the tracked path, so it never sees the "actually bound"
flag get set. Every existing `mutate` test only targets a `PredefinedTypeSyntax` (`int`, `string`,
etc.) or (found while adding `MutateStatementTests.Regression_NullableWalker_ReferenceToReferenceMutationPreservesSourceNullability`)
an `ArrayTypeSyntax` (`string[]`), neither of which trips this — so mutating to a *named* custom or
BCL reference type (`C`, `System.Exception`, ...) is apparently untested and currently broken in debug
builds (release builds skip the assert but the underlying identifier-map inconsistency would presumably
still exist, just unobserved). Whoever next touches `mutate` should investigate whether `BindType`
needs to route target-type identifier binding through whatever records into `IdentifierMap`, or
whether the identifier-prediction pre-pass needs a `MutateStatementSyntax`-aware case to stop
predicting its `Type` position needs tracked binding at all.

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
