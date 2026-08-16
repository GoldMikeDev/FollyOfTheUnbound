---
coverage: Repo-wide test layout, run commands, and shared authoring conventions; per-layer test base classes live in testing/{compiler,ide,razor}.md
---

# Testing Strategy

Repo-wide test layout, run commands, and shared authoring conventions. **Per-layer
test base classes and conventions** live in dedicated per-layer files (load only
the one for your area):
- Compiler (`CSharpTestBase`, `VerifyEmitDiagnostics`) → `.github/memory/testing/compiler.md`
- IDE (`AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor`, `[UseExportProvider]`, `TestInRegularAndScriptAsync`) → `.github/memory/testing/ide.md`
- Razor (`TestCode` span markers) → `.github/memory/testing/razor.md`

## Test Layout

| Type | Location convention |
|------|---------------------|
| Unit tests | Sibling `*Test` / `*.UnitTests` project next to the product project (e.g., `Workspaces/Core` ↔ `Workspaces/CoreTest`). |
| Compiler tests | `src/Compilers/*/Test/`. |
| IDE/analyzer tests | `*Test` projects under `src/Features`, `src/Analyzers`, `src/EditorFeatures`. |
| Integration tests | VS integration tests (`azure-pipelines-integration*.yml`); runnable locally on **Windows** hosts with a VS install, also run in CI. |

Frameworks: xUnit with Roslyn test utilities.

## Repo-wide Authoring Conventions

- Prefer raw string literals (`"""..."""`) over verbatim strings for test source code.
- Keep tests focused: use `.Single()` rather than asserting a count then indexing.
- For issue-linked changes, add a `WorkItem` attribute next to the test
  attribute, e.g. `[Fact, WorkItem("https://github.com/dotnet/roslyn/issues/1234")]`
  or `[Theory, WorkItem("https://github.com/dotnet/roslyn/issues/1234")]`.
  Use the originating GitHub issue/PR or Azure DevOps work item URL.

## Running Tests

### During development (preferred — targeted)
```bash
dotnet test <path/to/Specific.UnitTests.csproj>
dotnet test <proj> --filter "FullyQualifiedName~MyTestClass"
```
Targeted runs are strongly preferred — the full suite is large and slow. Tests can take a while to build/run; wait for completion unless you're confident a run is hung.

### Full suite (final validation only)
```bash
./test.sh        # or Test.cmd on Windows
```

### Test types to be aware of
- VS integration tests (`azure-pipelines-integration*.yml`) require a VS install, so they run only on **Windows** hosts (not CI-only — they can be run locally on Windows). Prefer unit tests for the inner development loop; reach for integration tests when validating end-to-end VS behavior.
- A handful of tests fail only for environmental reasons — see `KNOWN_ISSUES.md`.

## CI

PR validation runs via `azure-pipelines-pr-validation.yml` (Azure DevOps + Helix). For investigating failures, use the `ci-analysis` and `integration-test-analysis` skills.

## Standalone shell script tests

Not every test lives in a `dotnet test` project — `folly.sh cleanse`'s file-enumeration/deletion logic has its own manual harness at `scripts/test-folly-cleanse.sh` (not wired into CI; there's no existing shell-test CI job to hook into). Run it by hand after touching `folly.sh`'s `cleanse` action:
```bash
./scripts/test-folly-cleanse.sh
```
Covers: empty `artifacts/`, a populated tree, redirected (non-TTY) output staying free of escape codes, a permission failure reporting an accurate count with a nonzero exit, a file vanishing mid-scan under a concurrent writer, an unreadable subtree during the background scan reporting an honest uncertain remainder rather than a false "0 files could not be removed" (skips under root, since root bypasses the permission check needed to trigger it), and `artifacts/` existing as a non-directory.

Similarly, `folly.ps1 scry`'s argument parsing (`[config]`, `--core`/`--desktop`, and the pre-existing named `-action`/`-config` form) and its unified pass/fail/timeout summary have a manual harness at `scripts/test-folly-scry-args.ps1`, run against a mocked `eng/build.ps1` so no real build/test happens. Run it by hand after touching `folly.ps1`'s argument parsing or `scry` action:
```powershell
pwsh -File ./scripts/test-folly-scry-args.ps1
```
Covers: the default (both legs), `--core`-only, `--desktop`-only, positional `[config]`, named `-config` (backward compatibility), and a rejected unknown argument.
