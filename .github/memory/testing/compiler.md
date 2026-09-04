---
coverage: Compiler-layer (src/{Compilers,Dependencies,ExpressionEvaluator,Tools}) test base classes & authoring conventions
---

# Compiler — Testing

Layer-specific test guidance for compiler tests under `src/Compilers/*/Test/`.

## Test structure

Inherit from language-specific base classes: `CSharpTestBase` for C#,
`VisualBasicTestBase` for VB.

```cs
public class MyTests : CSharpTestBase
{
    [Fact]
    public void TestMethod()
    {
        var comp = CreateCompilation(sourceCode);
        // Test compilation, symbols, diagnostics
    }
}
```

## Conventions

- **Unit tests** target individual compiler phases (lexing, parsing); **compilation
  tests** create `Compilation` objects and verify symbols/diagnostics.
- **Cross-language patterns**: many test patterns work for both C# and VB with
  minor syntax changes.
- **Verification baselines**: when helpers like `VerifyDiagnostics`,
  `VerifyEmitDiagnostics`, `VerifyIL`, and similar compiler test APIs fail with an
  `Actual:` block containing the expected content, copy that block directly into
  the verification call.
- **Use `comp.VerifyEmitDiagnostics()`** (rather than only `VerifyDiagnostics`) so
  reviewers can see whether the code under test is legal.
- **Keep tests focused**: do the minimal work to reach the core assertions; use
  `Single()` instead of checking counts then indexing.
- **Prefer raw string literals** (`"""..."""`) over verbatim strings (`@"..."`)
  for test source code.

## Large/deep-recursion canary tests need `NoIOperationValidation` under `--testIOperation`

`CreateCompilation`/`CreateEmptyCompilation` (`CSharpTestBase.ValidateCompilation` /
`CompilationUtils.ValidateCompilation` in VB) auto-run a full-tree
`CompilationExtensions.ValidateIOperations` pass whenever `--testIOperation`
(`ROSLYN_TEST_IOPERATION`) is set — this walks the semantic model + IOperation tree
for every syntax node in the compilation and Assert.False's a hard-coded 15-second
watchdog (`CompilationExtensions.cs`, `checkTimeout()`). It's collateral, unrelated
to whatever the test itself asserts. Any test that deliberately generates a very
large or very deeply nested program (thousands of enum members/interceptors/binary
patterns, tens of thousands of nested locals/blocks) will blow that 15s budget, or
— worse, for genuinely deep-recursion canaries like `EndToEndTests.OverflowOnFluentCall`
— can recurse deep enough in `CSharpOperationFactory.Create` to hit a real,
**uncatchable** `StackOverflowException` that crashes the whole test host (found by
trying to fix it via a bigger thread stack size instead — that just pushes the
crash past `StackGuard`'s calibrated threshold into fatal territory, which is worse
than the clean `InsufficientExecutionStackException` the original 1MB-ish default
stack produces). The correct fix, and the pattern already used by other tests in
`EndToEndTests.cs` (e.g. `NestedIfElse`) and `PDBTests.{cs,vb}`, is to opt the whole
test out of the auto-triggered validation via
`[ConditionalFact(typeof(NoIOperationValidation))]` /
`[ConditionalTheory(typeof(NoIOperationValidation))]` (C#) or
`<ConditionalFact(GetType(NoIOperationValidation))>` (VB) — combine with other
`ConditionalFact` types via `params Type[]` when the test already has one (e.g.
`typeof(WindowsOnly), typeof(NoIOperationValidation)`). This only skips the
redundant whole-tree auto-validation; any `model.GetOperation(...)` calls the test
makes explicitly in its own body (several of these tests do, to check a specific
node's `IOperation`/`ControlFlowGraph`) still run and are still verified — so this
doesn't lose real coverage, it just stops re-verifying the entire huge synthetic
program's IOperation tree redundantly. Confirmed via `.\folly scry --testIOperation`
runs: `EndToEndTests.{OverflowOnFluentCall, Interceptors,
ForAttributeWithMetadataName_DeepRecursion, ManyBinaryPatterns_01/02/03,
ManyUnreferencedSuppressMessageAttributes}`, `PDBTests.NativeWriterLimit`
(C# `_EndToEnd` and VB), and VB `EnumTests.LongDependencyChain` all needed this.
