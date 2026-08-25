# Manual test harness for folly.ps1 cleanse's background bulk-delete path
# (Start-Job + Remove-Item -Recurse -Force, the byte/count scan, the
# locked-file retry, and the final accounting). Not wired into CI -- run by
# hand after touching folly.ps1's cleanse action:
#   pwsh -File ./scripts/test-folly-cleanse.ps1
#
# Covers: empty artifacts/, a populated tree with an exact byte total, a
# locked file surviving the bulk delete and its retry (accurate count,
# "could not be removed" reported), an unreadable subtree (an NTFS deny ACE)
# reporting an honest uncertain remainder rather than a false "0 files could
# not be removed", and a file vanishing mid-scan under a concurrent writer.
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $PSScriptRoot
$follyPs1 = Join-Path $scriptRoot "folly.ps1"
$pwshExe = (Get-Process -Id $PID).Path

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("folly-cleanse-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

$script:syntheticPids = @()  # PIDs of any detached background process this harness spawns (e.g. the synthetic build-server case below) -- appended to as each is launched, so the finally block can reap them even if the harness is interrupted or throws before its own explicit cleanup runs

$script:passCount = 0
$script:failCount = 0

function Test-Pass([string]$Message) {
    Write-Host "PASS: $Message" -ForegroundColor Green
    $script:passCount++
}

function Test-Fail([string]$Message) {
    Write-Host "FAIL: $Message" -ForegroundColor Red
    $script:failCount++
}

function New-TestCase([string]$Name) {
    $dir = Join-Path $workRoot $Name
    Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item -LiteralPath $follyPs1 -Destination (Join-Path $dir "folly.ps1")
    return $dir
}

function Invoke-Cleanse([string]$Dir) {
    $output = & $pwshExe -NoProfile -File (Join-Path $Dir "folly.ps1") cleanse 2>&1 | Out-String
    return [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
}

try {
    # --- empty artifacts/ -------------------------------------------------
    $dir = New-TestCase "empty"
    New-Item -ItemType Directory -Force -Path (Join-Path $dir "artifacts") | Out-Null
    $result = Invoke-Cleanse -Dir $dir
    if ($result.ExitCode -eq 0 -and $result.Output -match "Cleansed 0 B of artefacts\." -and -not (Test-Path -LiteralPath (Join-Path $dir "artifacts"))) {
        Test-Pass "empty artifacts/ directory removed cleanly"
    }
    else {
        Test-Fail "empty artifacts/ directory (exit=$($result.ExitCode), output='$($result.Output)')"
    }

    # --- populated tree -----------------------------------------------------
    $dir = New-TestCase "populated"
    $artifactsDir = Join-Path $dir "artifacts"
    New-Item -ItemType Directory -Force -Path (Join-Path $artifactsDir "sub") | Out-Null
    for ($i = 1; $i -le 20; $i++) {
        Set-Content -LiteralPath (Join-Path $artifactsDir "f_$i.bin") -Value ("x" * 100) -NoNewline
    }
    Set-Content -LiteralPath (Join-Path $artifactsDir "sub\nested.bin") -Value ("x" * 50) -NoNewline
    $result = Invoke-Cleanse -Dir $dir
    # Built with the same {0:N2} format Format-ByteSize itself uses, rather
    # than a hard-coded "2.00 KiB" -- on a host whose current culture uses a
    # comma decimal separator, Format-ByteSize would emit "2,00 KiB" and a
    # literal-period regex would report a false failure on correct output.
    # Plain string Contains (not -match) sidesteps the separator being a
    # regex metachar in either case.
    $expectedSize = "{0:N2} KiB" -f (2050 / 1KB)
    if ($result.ExitCode -eq 0 -and $result.Output.Contains("Cleansed $expectedSize of artefacts.") -and -not (Test-Path -LiteralPath $artifactsDir)) {
        Test-Pass "populated tree removed with correct byte total"
    }
    else {
        Test-Fail "populated tree (exit=$($result.ExitCode), output='$($result.Output)')"
    }

    # --- locked file survives the bulk delete and its retry -----------------
    # Simulates the exact scenario the build-server-shutdown workaround
    # targets: a file another process still has open (e.g. a BuildHost DLL)
    # can't be deleted by Remove-Item -Recurse -Force, nor by the retry --
    # cleanse must still finish, report an accurate survivor count, and not
    # throw.
    #
    # Windows-only: an open FileStream with FileShare.None only blocks
    # deletion on Windows' mandatory file locking. Unix lets a file be
    # unlinked out from under an open handle regardless -- cleanse would
    # correctly remove locked.bin there, and this assertion would always
    # (and wrongly) report a failure.
    if (-not $IsWindows) {
        Write-Host "SKIP: locked-file case (FileShare.None doesn't block deletion on Unix)"
    }
    else {
        $dir = New-TestCase "locked"
        $artifactsDir = Join-Path $dir "artifacts"
        New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
        $removableFile = Join-Path $artifactsDir "removable.bin"
        $lockedFile = Join-Path $artifactsDir "locked.bin"
        Set-Content -LiteralPath $removableFile -Value ("x" * 100) -NoNewline
        Set-Content -LiteralPath $lockedFile -Value ("x" * 10) -NoNewline
        $stream = [System.IO.File]::Open($lockedFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        try {
            $result = Invoke-Cleanse -Dir $dir
        }
        finally {
            $stream.Close()
        }
        if ($result.ExitCode -eq 1 -and $result.Output -match "1 file\(s\) could not be removed\." -and -not (Test-Path -LiteralPath $removableFile) -and (Test-Path -LiteralPath $lockedFile)) {
            Test-Pass "locked file survives the bulk delete and its retry, reported accurately"
        }
        else {
            Test-Fail "locked file (exit=$($result.ExitCode), output='$($result.Output)')"
        }
    }

    # --- unreadable subtree during the scan: uncertain (not false-zero) remainder
    # Windows-only: NTFS honors an explicit Deny ACE even for the current
    # user/owner (unlike Unix, where root bypasses permission checks
    # entirely), so this doesn't need the root-skip the bash harness needs.
    if (-not $IsWindows) {
        Write-Host "SKIP: unreadable-subtree case (Windows-only; needs an NTFS deny ACE)"
    }
    else {
        $dir = New-TestCase "unreadable"
        $artifactsDir = Join-Path $dir "artifacts"
        $lockedSub = Join-Path $artifactsDir "locked"
        New-Item -ItemType Directory -Force -Path $lockedSub | Out-Null
        Set-Content -LiteralPath (Join-Path $lockedSub "hidden.bin") -Value ("x" * 100) -NoNewline
        Set-Content -LiteralPath (Join-Path $artifactsDir "visible.bin") -Value ("x" * 100) -NoNewline

        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $denyRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $identity,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.AccessControlType]::Deny
        )
        # Captured separately from (not aliased to) the ACL object that gets
        # the deny rule added below, and restored via this saved copy in
        # finally rather than a fresh Get-Acl -- a FullControl deny includes
        # READ_CONTROL, so re-reading the ACL after it's in effect could
        # itself raise Access Denied and abort cleanup before the deny rule
        # or the temporary tree is ever removed.
        $originalAcl = Get-Acl -LiteralPath $lockedSub
        $acl = Get-Acl -LiteralPath $lockedSub
        $acl.AddAccessRule($denyRule)
        Set-Acl -LiteralPath $lockedSub -AclObject $acl

        try {
            $result = Invoke-Cleanse -Dir $dir
        }
        finally {
            Set-Acl -LiteralPath $lockedSub -AclObject $originalAcl
        }
        if ($result.ExitCode -eq 1 -and $result.Output -match "at least" -and $result.Output -match "unreadable and not counted") {
            Test-Pass "unreadable subtree reports an uncertain (not false-zero) remainder"
        }
        else {
            Test-Fail "unreadable subtree (exit=$($result.ExitCode), output='$($result.Output)')"
        }
    }

    # --- file vanishing mid-scan (concurrent writer) -------------------------
    $dir = New-TestCase "concurrent"
    $artifactsDir = Join-Path $dir "artifacts"
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
    for ($i = 1; $i -le 50; $i++) {
        Set-Content -LiteralPath (Join-Path $artifactsDir "f_$i.bin") -Value ("x" * 100) -NoNewline
    }
    # Race a background deletion against cleanse's own scan/delete passes. A
    # vanished file must not abort cleanse -- it should still finish
    # successfully (exit 0), regardless of who actually removed each file.
    $racer = Start-Job -ScriptBlock {
        param($dir)
        for ($i = 1; $i -le 50; $i++) {
            Remove-Item -Force -LiteralPath (Join-Path $dir "f_$i.bin") -ErrorAction SilentlyContinue
        }
    } -ArgumentList $artifactsDir
    $result = Invoke-Cleanse -Dir $dir
    Wait-Job -Job $racer -Timeout 30 | Out-Null
    Remove-Job -Job $racer -Force -ErrorAction SilentlyContinue
    if ($result.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $artifactsDir)) {
        Test-Pass "concurrent file removal during cleanse does not abort (output='$($result.Output.Trim())')"
    }
    else {
        Test-Fail "concurrent file removal (exit=$($result.ExitCode), output='$($result.Output)')"
    }

    # --- build-server force-kill: scoped survivor gets killed, foreign one left alone
    # Exercises the process-killing fallback itself (scoping the force-kill to this
    # checkout's own .dotnet SDK root, and the TERM-then-KILL escalation with
    # confirmed-exit counting), not just file deletion.
    #
    # Unix-only: reliably making a process ignore a graceful stop needs a POSIX
    # signal trap (bash `trap '' TERM`), which Windows has no equivalent for --
    # Stop-Process there doesn't distinguish a "graceful" signal a target process
    # could choose to ignore, so this scenario can't be faithfully reproduced there.
    if ($IsWindows) {
        Write-Host "SKIP: build-server force-kill case (Unix-only; needs a POSIX signal trap to simulate a process ignoring a graceful stop)"
    }
    else {
        $dir = New-TestCase "buildserver"
        $dotnetSdkDir = Join-Path $dir ".dotnet/sdk"
        New-Item -ItemType Directory -Force -Path $dotnetSdkDir | Out-Null
        Set-Content -LiteralPath (Join-Path $dir ".dotnet/dotnet") -Value "#!/bin/bash`nexit 0`n" -NoNewline
        & chmod +x (Join-Path $dir ".dotnet/dotnet")

        $trappedScript = Join-Path $workRoot "trapped_vbcs.sh"
        $trappedVbcsPath = Join-Path $dotnetSdkDir "VBCSCompiler.dll"
        Set-Content -LiteralPath $trappedScript -Value @"
#!/bin/bash
exec -a "dotnet exec $trappedVbcsPath -pipename:pstrapped" bash -c 'trap "" TERM; while true; do sleep 1; done'
"@ -NoNewline
        & chmod +x $trappedScript

        $foreignScript = Join-Path $workRoot "foreign_vbcs.sh"
        Set-Content -LiteralPath $foreignScript -Value @'
#!/bin/bash
exec -a "dotnet exec /some/other/checkout/.dotnet/sdk/VBCSCompiler.dll -pipename:psforeign" sleep 300
'@ -NoNewline
        & chmod +x $foreignScript

        $trappedProc = Start-Process -FilePath $trappedScript -PassThru  # -PassThru's own .Id is already the final PID -- the script it launches `exec -a`s into bash without forking, so no ps lookup/race is needed to discover it
        $trappedPid = $trappedProc.Id
        $script:syntheticPids += $trappedPid  # registered with the finally block the instant the PID is known -- before the second launch or the sleep/verify below can throw/exit and leave it orphaned
        $foreignProc = Start-Process -FilePath $foreignScript -PassThru
        $foreignPid = $foreignProc.Id
        $script:syntheticPids += $foreignPid
        Start-Sleep -Milliseconds 500

        # Verify each PID is actually the process we think it is (matches its pipename marker), not just that Start-Process succeeded.
        if (-not (& bash -c "ps -eo pid,command | grep '^[[:space:]]*$trappedPid[[:space:]]' | grep 'pipename:pstrapped'")) { $trappedPid = $null }
        if (-not (& bash -c "ps -eo pid,command | grep '^[[:space:]]*$foreignPid[[:space:]]' | grep 'pipename:psforeign'")) { $foreignPid = $null }

        if ($trappedPid -and $foreignPid) {
            $result = Invoke-Cleanse -Dir $dir
            Start-Sleep -Milliseconds 300
            $trappedAlive = ((& bash -c "kill -0 $trappedPid 2>/dev/null && echo alive") -eq "alive")
            $foreignAlive = ((& bash -c "kill -0 $foreignPid 2>/dev/null && echo alive") -eq "alive")
            & bash -c "kill -9 $trappedPid 2>/dev/null; kill -9 $foreignPid 2>/dev/null"
            if ($result.ExitCode -eq 0 -and $result.Output -match "Force-killed 1 build server" -and -not $trappedAlive -and $foreignAlive) {
                Test-Pass "build-server force-kill escalates a same-checkout trapped survivor and leaves a foreign-checkout one alone"
            }
            else {
                Test-Fail "build-server force-kill scoping/escalation (exit=$($result.ExitCode), output='$($result.Output)', trappedAlive=$trappedAlive, foreignAlive=$foreignAlive)"
            }
        }
        else {
            Write-Host "SKIP: build-server force-kill case (couldn't spawn synthetic processes in this environment)"
            if ($trappedPid) { & bash -c "kill -9 $trappedPid 2>/dev/null" }
            if ($foreignPid) { & bash -c "kill -9 $foreignPid 2>/dev/null" }
        }
    }

    # --- no artifacts/ at all -------------------------------------------------
    $dir = New-TestCase "nothing"
    $result = Invoke-Cleanse -Dir $dir
    if ($result.ExitCode -eq 0 -and $result.Output -match "No artefacts to cleanse\.") {
        Test-Pass "missing artifacts/ reports nothing to cleanse"
    }
    else {
        Test-Fail "missing artifacts/ (exit=$($result.ExitCode), output='$($result.Output)')"
    }

    Write-Host ""
    Write-Host "$($script:passCount) passed, $($script:failCount) failed"
    if ($script:failCount -gt 0) {
        exit 1
    }
    exit 0
}
finally {
    foreach ($p in $script:syntheticPids) {
        & bash -c "kill -9 $p 2>/dev/null"
    }
    Remove-Item -Recurse -Force -LiteralPath $workRoot -ErrorAction SilentlyContinue
}
