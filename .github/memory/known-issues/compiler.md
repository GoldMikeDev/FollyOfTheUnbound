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

## `if` statements require a block consequence everywhere, including in old/upstream test fixtures

**Affected area:** any test source (this repo's own, or newly merged from upstream `dotnet/roslyn`)
that uses a classic brace-less `if (cond) statement; else statement;` form.
**Description:** `LanguageParser.ParseIfStatementOrIfCatchStatement` (the parser entry point for
*every* `if` statement, not just the new `if`/`catch`/`finally` chain construct — see
`.github/memory/experimental-language-features.md`) unconditionally calls `ParseBlock` for each arm's
consequence: `// Always require an actual block for the consequence -- this is a simplification over
the classic if statement (which allows any embedded statement).` A brace-less `if` body is a parse
error in this fork, full stop, regardless of whether `else`/`catch`/`finally` follow. This is a
sweeping, deliberate change to core C# syntax, and it silently breaks any test source containing the
classic form — most visibly when merging upstream commits that add new test fixtures using it (e.g.
`TestSources.Index`'s constructor, arrived via an upstream merge, used `if (fromEnd) _value = ~value;
else _value = value;` and failed to parse until braced). A repo-wide sweep of `src/Compilers/**/Test/**`
for this pattern has never been done: a broad `dotnet test --filter "FullyQualifiedName~Index|FullyQualifiedName~Range"`
sweep found 67 pre-existing failures (as of the upstream-main merge landing `c9f12709e`/`dc1db3e7d`),
of which only the ones actually touching newly-merged fixture code were fixed; the rest are
long-standing, out-of-scope debt from this feature never getting a full test-suite adaptation pass.
**Workaround:** When a merge or new test brings in code with brace-less `if`/`else` bodies, add braces
(same line-count trick works to avoid shifting other tests' `.WithLocation(line, col)` assertions
elsewhere in the same shared fixture: `if (cond)\n{ stmt; }\nelse\n{ stmt; }` instead of reformatting
across more lines). Don't assume an existing test failure in this area is caused by whatever you were
actually working on -- check with `git stash`/a baseline run first, the same way the upstream-main
merge sessions did.
