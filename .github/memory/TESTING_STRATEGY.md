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
Covers: empty `artifacts/`, a populated tree, redirected (non-TTY) output staying free of escape codes, a permission failure reporting an accurate count with a nonzero exit, a file vanishing mid-scan under a concurrent writer, an unreadable subtree during the background scan reporting an honest uncertain remainder rather than a false "0 files could not be removed" (skips under root, since root bypasses the permission check needed to trigger it), `artifacts/` existing as a non-directory, and `cleanse`'s build-server process-killing fallback itself (not just file deletion): a synthetic same-checkout build server that traps `SIGTERM` still ends up force-killed and confirmed dead (proving the TERM-then-KILL escalation) while a synthetic foreign-checkout one matching the same name pattern survives untouched (proving the `.dotnet`-path scoping), and a synthetic wrapper process whose own command line matches the build-server pattern survives `cleanse` running as its child (proving the ancestor-process exclusion). These process-killing cases register their synthetic PIDs with the harness's `EXIT` trap immediately upon spawning, so an interrupted run can't leak them.

Similarly, `folly.ps1 scry`'s argument parsing (`[config]`, `--core`/`--framework`, `--timeout <minutes>` -- including that the value is actually forwarded to `eng/build.ps1` for both legs, and that a missing value, an invalid value, and use on a non-`scry` action are all rejected -- and the pre-existing named `-action`/`-config` form) and its unified pass/fail/timeout summary have a manual harness at `scripts/test-folly-scry-args.ps1`, run against a mocked `eng/build.ps1` so no real build/test happens. Run it by hand after touching `folly.ps1`'s argument parsing or `scry` action:
```powershell
pwsh -File ./scripts/test-folly-scry-args.ps1
```
Covers: the default (both legs), `--core`-only, `--framework`-only, positional `[config]`, named `-config` (backward compatibility), and a rejected unknown argument.

`folly.sh scry`'s argument parsing has the bash counterpart `scripts/test-folly-scry-args.sh`, run against a mocked `eng/build.sh` (records the args it was invoked with) the same way -- see the `folly.sh`/`folly.ps1` parity rule in `CONVENTIONS.md` for why this pair needs to stay in lockstep with the PowerShell harness rather than that one being the only coverage. Run it by hand after touching `folly.sh`'s argument parsing or `scry` action:
```bash
bash ./scripts/test-folly-scry-args.sh
```
Covers: no `--testTimeout` forwarded by default, `--timeout <minutes>` actually forwarded as `--testTimeout <minutes>`, positional `[config]` alongside `--timeout`, a leading-zero value (`08`) normalized to decimal instead of misparsed as octal, a missing value, a non-numeric value, a value overflowing bash's 64-bit arithmetic, a value exceeding `Task.Delay`'s supported millisecond range (RunTests' actual downstream limit), use on a non-`scry` action, `grimoire` still ignoring a trailing config, and a rejected unknown argument.

`folly.ps1 cleanse`'s own background bulk-delete path (`Start-Job` + `Remove-Item -Recurse -Force`, the byte/count scan, the locked-file retry) has a manual harness at `scripts/test-folly-cleanse.ps1`, mirroring `test-folly-cleanse.sh`'s coverage for the PowerShell implementation. Run it by hand after touching `folly.ps1`'s `cleanse` action:
```powershell
pwsh -File ./scripts/test-folly-cleanse.ps1
```
Covers: empty `artifacts/`, a populated tree with an exact byte total, a file locked by an open handle (simulating a BuildHost DLL still in use) surviving both the bulk delete and its retry with an accurate reported count *and exit code 1*, an unreadable subtree (an NTFS deny ACE, which unlike Unix `chmod` also blocks the current user/owner) reporting an honest uncertain remainder rather than a false "0 files could not be removed" (also exit code 1), and a file vanishing mid-scan under a concurrent writer. `cleanse`'s exit code is a real contract (0 only if `artifacts/` is gone afterward, 1 if anything survives) — see `API_MAP.md`. Also covers the same build-server process-killing fallback as `test-folly-cleanse.sh` (scoping, TERM-then-KILL escalation, ancestor exclusion) — gated Unix-only, since faithfully reproducing "ignores a graceful stop" needs a POSIX signal trap (via `bash`) that Windows has no direct equivalent for.
