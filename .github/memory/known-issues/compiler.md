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
