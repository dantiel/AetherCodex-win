# ============================================================================
#  make_release.ps1 - AetherCodex (Windows) release builder
# ============================================================================
#  One entry point for cutting a release, now and in the future.
#
#  WORKFLOW
#    1. Bump <Version> in src/AetherCodex/AetherCodex.csproj (single source of truth).
#    2. Run:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\make_release.ps1
#    3. Artifacts land in dist/:
#         AetherCodex-<Version>-win-x64.zip
#         AetherCodex-<Version>-win-x64.zip.sha256
#    4. The script also creates a local git tag v<Version> (skip with -NoTag).
#
#  WHAT IT DOES
#    * dotnet publish (self-contained win-x64 by default) - bundles the .NET
#      runtime so end users do not need it installed.
#    * Ensures the Ruby daemon's vendored gems are present (bundle install if
#      missing) - the C# shell launches Ruby/standalone_daemon.rb as its "soul".
#    * Packages the published tree + README/LICENSE into a versioned zip + sha256.
#    * Tags the release commit and (optionally) pushes / opens a GitHub release.
#
#  REQUIREMENTS
#    * .NET 8 SDK      (dotnet on PATH)
#    * Ruby 3.3.x x64  (ruby + bundle on PATH) - the bundled gems are compiled
#      for the x64-mingw-ucrt ABI and live under Ruby/.vendor_bundle/ruby/3.3.0.
#      End users need Ruby 3.3.x installed (or a future portable-Ruby bundle).
#    * Optional: gh CLI for -GitHub, git remote for -Push.
# ============================================================================

param(
    [string]$Version,                 # Defaults to <Version> in the csproj.
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,      # Opt out of self-contained publish.
    [switch]$SkipBuild,
    [switch]$SkipBundle,
    [switch]$SkipZip,
    [switch]$NoTag,
    [switch]$Push,                    # Push the tag to origin.
    [switch]$GitHub,                  # Open a GitHub release (requires gh).
    [switch]$Prerelease               # Mark the GitHub release as pre-release.
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot   # tools/ -> repo root
$Csproj   = Join-Path $RepoRoot "src\AetherCodex\AetherCodex.csproj"
# The soul is pinned as the AetherOracle submodule; the daemon + its gems live
# in the submodule's ruby/ subdirectory.
$RubyDir  = Join-Path $RepoRoot "Ruby\ruby"
$DistDir  = Join-Path $RepoRoot "dist"

function Die([string]$msg) { Write-Host "`n[make_release] ERROR: $msg" -ForegroundColor Red; exit 1 }
function Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# ---- Resolve version -------------------------------------------------------
if (-not $Version) {
    if (-not (Test-Path $Csproj)) { Die "csproj not found: $Csproj" }
    $raw = Get-Content -Raw $Csproj
    $m = [regex]::Match($raw, '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $m.Success) { Die "No <Version> element in csproj - add one first." }
    $Version = $m.Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+') { Die "Version '$Version' is not SemVer-ish (x.y.z)." }

$SelfContained = (-not $FrameworkDependent)
$PublishDir = Join-Path $DistDir "AetherCodex-$Version-$Runtime"
$ZipPath    = Join-Path $DistDir "AetherCodex-$Version-$Runtime.zip"
$HashPath   = "$ZipPath.sha256"

Step "Release $Version  (config=$Configuration, runtime=$Runtime, self-contained=$SelfContained)"
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

# ---- Soul submodule (AetherOracle is pinned, never copied) -----------------
Step "Initializing the AetherOracle submodule"
Push-Location $RepoRoot
try {
    & git submodule update --init --recursive | Out-Host
    if ($LASTEXITCODE -ne 0) { Die "git submodule update failed." }
}
finally { Pop-Location }

# ---- Ruby daemon gems (the soul must ship its deps) ------------------------
$VendorBundle = Join-Path $RubyDir ".vendor_bundle"
if (-not $SkipBundle) {
    if (-not (Test-Path (Join-Path $VendorBundle "ruby"))) {
        Step "Vendored Ruby gems missing - running 'bundle install' in $RubyDir"
        Push-Location $RubyDir
        try   { & bundle install | Out-Host }
        catch { Die "bundle install failed. Install Ruby 3.3.x + bundler and retry." }
        finally { Pop-Location }
    } else {
        Step "Vendored Ruby gems present (Ruby/.vendor_bundle)"
    }
}

# ---- Publish the .NET shell -------------------------------------------------
if (-not $SkipBuild) {
    Step "dotnet publish (self-contained=$SelfContained)"
    $dotnetArgs = @("publish", $Csproj, "-c", $Configuration, "-r", $Runtime, "-o", $PublishDir)
    if ($SelfContained) { $dotnetArgs += @("--self-contained", "true") } else { $dotnetArgs += @("--self-contained", "false") }
    $dotnetArgs += @("-p:Version=$Version")
    & dotnet @dotnetArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { Die "dotnet publish failed (exit $LASTEXITCODE)." }

    # Ship the human-facing docs alongside the binary.
    foreach ($doc in @("README.md", "LICENSE", "hermetic.manifest.md")) {
        $src = Join-Path $RepoRoot $doc
        if (Test-Path $src) { Copy-Item $src $PublishDir -Force }
    }
    if (-not (Test-Path (Join-Path $PublishDir "AetherCodex.exe"))) {
        Die "Publish output missing AetherCodex.exe - inspect publish errors above."
    }
}

# ---- Package ----------------------------------------------------------------
if (-not $SkipZip) {
    Step "Packaging $ZipPath"
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path $PublishDir -DestinationPath $ZipPath -Force
    $zipBytes = (Get-Item $ZipPath).Length
    $hash = (Get-FileHash -Algorithm SHA256 $ZipPath).Hash.ToLowerInvariant()
    Set-Content -Path $HashPath -Value "$hash  AetherCodex-$Version-$Runtime.zip" -Encoding ascii
    Write-Host ("    zip    : {0:N1} MB" -f ($zipBytes / 1MB)) -ForegroundColor Green
    Write-Host ("    sha256 : {0}" -f $hash) -ForegroundColor Green
}

# ---- Tag ---------------------------------------------------------------------
$Tag = "v$Version"
if (-not $NoTag) {
    Step "Tagging $Tag"
    $existing = @(git tag --list $Tag)
    if ($existing.Count -gt 0) {
        Write-Host "    tag $Tag already exists - skipping."
    } else {
        & git tag -a $Tag -m "AetherCodex Windows $Tag (alpha)" | Out-Host
        if ($LASTEXITCODE -ne 0) { Die "git tag failed." }
        Write-Host "    created local tag $Tag" -ForegroundColor Green
    }
}

# ---- Push / GitHub release (optional, guarded) -------------------------------
if ($Push) {
    Step "Pushing tag to origin"
    & git push origin $Tag | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "    push failed - check credentials/remote." -ForegroundColor Yellow }
}
if ($GitHub) {
    Step "Opening GitHub release"
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Host "    gh CLI not found - install it (https://cli.github.com) or create the release manually." -ForegroundColor Yellow
    } else {
        $notes = Join-Path $DistDir "RELEASE_NOTES_$Version.md"
        $ghArgs = @("release", "create", $Tag, $ZipPath, "--title", "AetherCodex Windows $Tag")
        if ($Prerelease) { $ghArgs += "--prerelease" }
        if (Test-Path $notes) { $ghArgs += @("--notes-file", $notes) } else { $ghArgs += @("--generate-notes") }
        & gh @ghArgs | Out-Host
    }
}

Step "Done."
Write-Host "    tag      : $Tag (local)"
Write-Host "    artifact : $ZipPath"
Write-Host "    checksum : $HashPath"
if (-not $Push) { Write-Host "`n    To publish:  git push origin $Tag   (and optionally: gh release create $Tag $ZipPath --generate-notes)" -ForegroundColor DarkGray }