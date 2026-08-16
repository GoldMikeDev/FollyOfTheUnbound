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
    if ($result.ExitCode -eq 0 -and $result.Output -match "Cleansed 0 B from artefacts\." -and -not (Test-Path -LiteralPath (Join-Path $dir "artifacts"))) {
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
    if ($result.ExitCode -eq 0 -and $result.Output.Contains("Cleansed $expectedSize from artefacts.") -and -not (Test-Path -LiteralPath $artifactsDir)) {
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
        $acl = Get-Acl -LiteralPath $lockedSub
        $acl.AddAccessRule($denyRule)
        Set-Acl -LiteralPath $lockedSub -AclObject $acl

        try {
            $result = Invoke-Cleanse -Dir $dir
        }
        finally {
            # Remove the deny rule so the final workRoot cleanup below can
            # actually delete this tree.
            $acl2 = Get-Acl -LiteralPath $lockedSub
            $acl2.RemoveAccessRule($denyRule) | Out-Null
            Set-Acl -LiteralPath $lockedSub -AclObject $acl2
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

    Write-Host ""
    Write-Host "$($script:passCount) passed, $($script:failCount) failed"
    if ($script:failCount -gt 0) {
        exit 1
    }
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $workRoot -ErrorAction SilentlyContinue
}
