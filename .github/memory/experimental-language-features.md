---
coverage: This fork's experimental C# language features (do/until, mutate, inline expression declaration, if/catch/finally chains) — syntax shape, compiler status, and IDE support status
---

# Experimental Language Features ("Folly of the Unbound")

This fork prototypes four new C#-like statement/expression constructs. The compiler side (parser,
binder, lowering, flow analysis) is real and verified end-to-end with real builds and executed test
programs. IDE support is being added incrementally; this file tracks what's done.

## The four constructs

| Construct | `SyntaxKind` | Node type(s) | Keyword(s) | Notes |
|---|---|---|---|---|
| `do { } until (cond);` | `DoUntilStatement` | `DoUntilStatementSyntax` | `until` — real contextual `SyntaxKind.UntilKeyword` | Structurally identical to `DoStatementSyntax` with `until` instead of `while`. |
| `mutate x to Type;` | `MutateStatement` | `MutateStatementSyntax` | `mutate` — **not** a real `SyntaxKind`, matched by identifier text in the parser (`LanguageParser.ParseMutateStatement`); `to` — real contextual `SyntaxKind.ToKeyword` | No braces, single-line, semicolon-terminated — behaves like an ordinary statement for anything block/brace-related. |
| `if { cond-block } ifout = expr; { consequence } (catch/finally)*` | `IfCatchStatement`/`IfCatchArm` | `IfCatchStatementSyntax` (`Arms`, `Else`, `Catches`, `Finally`), `IfCatchArmSyntax` (`ElseKeyword`, `IfKeyword`, optional `Condition`/parens, optional `ConditionBlock`, `Consequence`) | `if`/`else`/`catch`/`finally` — all real, reused `SyntaxKind`s | Fuses if/else-if/else with try/catch/finally: `Catches`/`Finally` reuse the exact same `CatchClauseSyntax`/`FinallyClauseSyntax` nodes as ordinary `try`. `ifout` is not a keyword — it's a synthesized `bool?` local (`IfOutLocalSymbol`) referenced as a plain identifier. |
| inline expression declaration | `InlineExpressionDeclaration` | `InlineExpressionDeclarationSyntax` (`Expression`, `Identifier`) | none | Expression-level, no braces. |

No `LanguageVersion` gating exists for any of these — they're unconditionally enabled regardless of
declared `LangVersion`.

## IDE support status (see "Adding IDE Support for a New Statement/Expression SyntaxKind" in `.github/instructions/IDE.instructions.md`)

As of this writing, all four constructs have: classification, keyword completion, formatting rules,
outlining/brace-matching, keyword highlighting, and breakpoint spans.

**Deliberately not yet done (forward-looking, flagged as premature by the user):** code fixes /
refactorings that suggest replacing existing constructs with these new ones (e.g. suggesting
`do/until` in place of `do/while(!cond)`, or the if/catch chain in place of separate `if`+`try/catch`).
This would be a `CodeRefactoringProvider`/`CodeFixProvider`, not yet started.

**Not audited:** whether other IDE analyzers with exhaustive `SyntaxKind` switches (IDE0xxx style/
simplification analyzers) silently skip these new node kinds. Not yet checked.
