---
coverage: Compiler-layer (src/{Compilers,Dependencies,ExpressionEvaluator,Tools}) known issues, quirks & workarounds
---

# Compiler — Known Issues

Layer-specific quirks for the compiler. Load when working under
`src/{Compilers,Dependencies,ExpressionEvaluator,Tools}`. Cross-cutting issues
(generated code, CI marker gating, environmental test failures) live in
`.github/memory/KNOWN_ISSUES.md`.

## Namespace-declaration `Name` is decomposed independently in two places

**Affected area:** `src/Compilers/CSharp/Portable/Declarations/DeclarationTreeBuilder.cs`,
`src/Compilers/CSharp/Portable/Binder/BinderFactory.BinderFactoryVisitor.cs`
**Description:** `BaseNamespaceDeclarationSyntax.Name` (a dotted `NameSyntax` chain) is walked
right-to-left in two entirely separate places that don't share code: `DeclarationTreeBuilder.VisitBaseNamespaceDeclaration`
(builds the `SingleNamespaceDeclaration` tree used for symbol lookup) and
`BinderFactory.BinderFactoryVisitor.MakeNamespaceBinder` (resolves the container symbol for member
binding). A change to how a namespace declaration's name resolves (e.g. adding the `*.`
root-namespace placeholder — see `.github/memory/experimental-language-features.md`) has to be made
in **both** or the second one fails silently/asserts the first time a member inside the namespace is
bound. There's also a pre-existing same-category comment in `DeclarationTreeBuilder.cs` about staying
in sync with `NamespaceSymbol.GetNestedNamespace` for alias-qualified names — same root cause, third
call site.
**Workaround:** When touching namespace-declaration name resolution, grep for
`GetNestedNamespace`/`MakeNamespaceBinder`/`GetUnqualifiedName` and check all call sites, not just
`DeclarationTreeBuilder`.

## `if` block-consequence requirement is now scoped to block-condition arms only — old/upstream test fixtures are fine

**Affected area:** `src/Compilers/CSharp/Portable/Parser/LanguageParser.cs`'s
`ParseIfStatementOrIfCatchStatement` (the parser entry point for *every* `if` statement, not just the
`if`/`catch`/`finally` chain construct — see `.github/memory/experimental-language-features.md`).
**Fixed:** An earlier version of this method unconditionally called `ParseBlock` for every arm's
consequence, making a classic brace-less `if (cond) stmt; else stmt;` a parse error everywhere in this
fork — this broke old/upstream test fixtures (e.g. `TestSources.Index`'s constructor) and would have
required an enormous, error-prone sweep of `src/Compilers/**/Test/**` to re-brace every brace-less
`if`/`else` in every test-source string. That's no longer how it works: the method now tracks
`armHasBlockCondition` per arm (true only for the new block-condition form, `if { ... }`, not classic
`if (cond)`), and only forces `ParseBlock` for the consequence when that arm actually used a block
condition:
```csharp
StatementSyntax consequence = armHasBlockCondition
    ? this.ParseBlock(default)
    : this.ParseEmbeddedStatement();
```
A classic `if (cond)` arm — and the trailing classic `else` — still calls `ParseEmbeddedStatement()`,
i.e. accepts any embedded statement, brace-less included, exactly like upstream `dotnet/roslyn`. So
`TestSources.Index`'s `if (fromEnd) _value = ~value; else _value = value;` and any other classic-form
test fixture parses without modification; nothing needs re-bracing. The block-consequence requirement
now applies **only** to the new block-condition arm shape (`if { ... }`) and to
`if`/`catch`/`finally` chains that mix arm styles — see `experimental-language-features.md` for that
construct's actual shape.
**Guidance:** Don't assume brace-less `if`/`else` in test source is broken in this fork — it isn't,
for the classic parenthesized-condition form. If you hit a genuine parse failure on an `if`, check
first whether a block-condition arm (`if { ... }`) is actually involved before reaching for the
re-bracing workaround.
