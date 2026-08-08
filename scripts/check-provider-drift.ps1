#Requires -Version 7.0

[CmdletBinding()]
param(
    [string] $UpstreamPath = "",

    [string] $Revision = "",

    [switch] $AllowNetwork,

    [string] $LockPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $PSScriptRoot "..\provider-upstream-lock.json"
}

$ExpectedPurpose = "codexbar-upstream-compatibility"
$ExpectedRepository = "https://github.com/steipete/CodexBar"
$ExpectedProviderListPath = "Sources/CodexBarCore/Providers/Providers.swift"
$ExpectedImplementationRegistryPath = "Sources/CodexBar/Providers/Shared/ProviderImplementationRegistry.swift"
$ExpectedAppRegistryPath = "Sources/CodexBar/ProviderRegistry.swift"
$ExpectedQuotaLensCatalogPath = "winui/Core/Catalog.cs"
$FullRevisionPattern = "^[0-9a-f]{40}$"
$Sha256Pattern = "^[0-9a-f]{64}$"
$ProviderIdPattern = "^[a-z0-9][a-z0-9-]*$"
$temporaryClonePath = $null
$exitCode = 2

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory)] [object] $InputObject,
        [Parameter(Mandatory)] [string] $Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "The compatibility lock is missing '$Name'."
    }

    return $property.Value
}

function Get-RequiredString {
    param(
        [Parameter(Mandatory)] [object] $InputObject,
        [Parameter(Mandatory)] [string] $Name
    )

    $value = Get-RequiredProperty -InputObject $InputObject -Name $Name
    if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) {
        throw "The compatibility lock property '$Name' must be a non-empty string."
    }

    return $value
}

function Get-RequiredInteger {
    param(
        [Parameter(Mandatory)] [object] $InputObject,
        [Parameter(Mandatory)] [string] $Name
    )

    $value = Get-RequiredProperty -InputObject $InputObject -Name $Name
    if ($value -isnot [int] -and $value -isnot [long]) {
        throw "The compatibility lock property '$Name' must be an integer."
    }

    return [long] $value
}

function Get-OrdinalSorted {
    param([Parameter(Mandatory)] [string[]] $Values)

    $sorted = [string[]] $Values.Clone()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return ,$sorted
}

function Assert-OrdinalSequence {
    param(
        [Parameter(Mandatory)] [string[]] $Expected,
        [Parameter(Mandatory)] [string[]] $Actual,
        [Parameter(Mandatory)] [string] $Description
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "$Description has $($Actual.Count) entries; expected $($Expected.Count)."
    }

    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if (-not [string]::Equals($Expected[$index], $Actual[$index], [StringComparison]::Ordinal)) {
            throw "$Description differs at index $index ('$($Actual[$index])' instead of '$($Expected[$index])')."
        }
    }
}

function Get-ValidatedIdArray {
    param(
        [Parameter(Mandatory)] [object] $InputObject,
        [Parameter(Mandatory)] [string] $Name,
        [switch] $AllowEmpty
    )

    $rawValue = Get-RequiredProperty -InputObject $InputObject -Name $Name
    $values = @($rawValue)
    if (-not $AllowEmpty -and $values.Count -eq 0) {
        throw "The compatibility lock property '$Name' cannot be empty."
    }

    $ids = [Collections.Generic.List[string]]::new()
    foreach ($value in $values) {
        if ($value -isnot [string] -or $value -cnotmatch $ProviderIdPattern) {
            throw "The compatibility lock property '$Name' contains an invalid provider ID."
        }

        $ids.Add($value)
    }

    $duplicates = @($ids | Group-Object -CaseSensitive | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "The compatibility lock property '$Name' contains duplicate IDs: $($duplicates.Name -join ', ')."
    }

    $actual = $ids.ToArray()
    $sorted = Get-OrdinalSorted -Values $actual
    Assert-OrdinalSequence -Expected $sorted -Actual $actual -Description "The compatibility lock property '$Name' (which must use ordinal sort order)"
    return ,$actual
}

function Assert-SafeGitPath {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Name
    )

    if ([IO.Path]::IsPathRooted($Path) -or $Path.Contains("\") -or $Path -notmatch "^[A-Za-z0-9._/-]+$") {
        throw "The compatibility lock property '$Name' is not a safe repository-relative path."
    }

    $segments = $Path.Split("/")
    $invalidSegments = @($segments | Where-Object { $_ -eq "" -or $_ -eq "." -or $_ -eq ".." })
    if ($segments.Count -eq 0 -or $invalidSegments.Count -gt 0) {
        throw "The compatibility lock property '$Name' is not a safe repository-relative path."
    }
}

function Assert-Matches {
    param(
        [Parameter(Mandatory)] [string] $Value,
        [Parameter(Mandatory)] [string] $Pattern,
        [Parameter(Mandatory)] [string] $Description
    )

    if ($Value -cnotmatch $Pattern) {
        throw "$Description is malformed: '$Value'."
    }
}

function Normalize-RepositoryUrl {
    param([Parameter(Mandatory)] [string] $RepositoryUrl)

    $normalized = $RepositoryUrl.Trim().Replace("\", "/").TrimEnd("/")
    if ($normalized -match "^git@github\.com:(?<path>.+)$") {
        $normalized = "https://github.com/$($Matches.path)"
    }
    elseif ($normalized -match "^ssh://git@github\.com/(?<path>.+)$") {
        $normalized = "https://github.com/$($Matches.path)"
    }

    if ($normalized.EndsWith(".git", [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(0, $normalized.Length - 4)
    }

    return $normalized
}

function Invoke-GitProcess {
    param(
        [Parameter(Mandatory)] [string] $GitPath,
        [Parameter(Mandatory)] [string] $WorkingDirectory,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [switch] $AsBytes
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $GitPath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Unable to start git."
    }

    try {
        $errorTask = $process.StandardError.ReadToEndAsync()
        if ($AsBytes) {
            $memory = [IO.MemoryStream]::new()
            try {
                $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($memory)
                [void] $copyTask.GetAwaiter().GetResult()
                $stderr = $errorTask.GetAwaiter().GetResult()
                $process.WaitForExit()
                if ($process.ExitCode -ne 0) {
                    throw "git $($Arguments -join ' ') failed: $($stderr.Trim())"
                }

                return ,$memory.ToArray()
            }
            finally {
                $memory.Dispose()
            }
        }

        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $stdout = $outputTask.GetAwaiter().GetResult()
        $stderr = $errorTask.GetAwaiter().GetResult()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git $($Arguments -join ' ') failed: $($stderr.Trim())"
        }

        return $stdout.Trim()
    }
    finally {
        $process.Dispose()
    }
}

function Get-GitBlobBytes {
    param(
        [Parameter(Mandatory)] [string] $GitPath,
        [Parameter(Mandatory)] [string] $RepositoryPath,
        [Parameter(Mandatory)] [string] $Commit,
        [Parameter(Mandatory)] [string] $Path
    )

    return ,(Invoke-GitProcess -GitPath $GitPath -WorkingDirectory $RepositoryPath -Arguments @(
        "cat-file",
        "blob",
        "${Commit}:$Path"
    ) -AsBytes)
}

function Get-Sha256 {
    param([Parameter(Mandatory)] [byte[]] $Bytes)

    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Convert-StrictUtf8 {
    param(
        [Parameter(Mandatory)] [byte[]] $Bytes,
        [Parameter(Mandatory)] [string] $Description
    )

    try {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        return $encoding.GetString($Bytes)
    }
    catch {
        throw "$Description is not valid UTF-8."
    }
}

function Get-ProviderIdsFromSource {
    param([Parameter(Mandatory)] [byte[]] $Bytes)

    $text = Convert-StrictUtf8 -Bytes $Bytes -Description "The upstream provider-list source"
    $enumMatches = [regex]::Matches(
        $text,
        "(?ms)^public enum UsageProvider:[^{]+\{(?<body>.*?)^\}")
    if ($enumMatches.Count -ne 1) {
        throw "Expected exactly one UsageProvider enum in the upstream provider-list source; found $($enumMatches.Count)."
    }

    $body = $enumMatches[0].Groups["body"].Value
    $caseLines = [regex]::Matches($body, "(?m)^\s*case\b[^\r\n]*$")
    $caseMatches = [regex]::Matches($body, "(?m)^\s*case\s+([a-z0-9][a-z0-9-]*)\s*$")
    if ($caseLines.Count -ne $caseMatches.Count -or $caseMatches.Count -eq 0) {
        throw "The UsageProvider enum format changed; refusing to infer an incomplete provider list."
    }

    $ids = [Collections.Generic.List[string]]::new()
    foreach ($match in $caseMatches) {
        $ids.Add($match.Groups[1].Value)
    }

    $duplicates = @($ids | Group-Object -CaseSensitive | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "The upstream UsageProvider enum contains duplicate IDs: $($duplicates.Name -join ', ')."
    }

    return ,(Get-OrdinalSorted -Values $ids.ToArray())
}

function Get-ImplementationRegistryIds {
    param([Parameter(Mandatory)] [byte[]] $Bytes)

    $text = Convert-StrictUtf8 -Bytes $Bytes -Description "The upstream implementation registry"
    $caseLines = [regex]::Matches($text, "(?m)^\s*case\s+\.[^\r\n]*$")
    $caseMatches = [regex]::Matches($text, "(?m)^\s*case\s+\.([a-z0-9][a-z0-9-]*)\s*:")
    if ($caseLines.Count -ne $caseMatches.Count -or $caseMatches.Count -eq 0) {
        throw "The implementation registry format changed; refusing to infer incomplete registry coverage."
    }

    $ids = [Collections.Generic.List[string]]::new()
    foreach ($match in $caseMatches) {
        $ids.Add($match.Groups[1].Value)
    }

    $duplicates = @($ids | Group-Object -CaseSensitive | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "The upstream implementation registry contains duplicate IDs: $($duplicates.Name -join ', ')."
    }

    return ,(Get-OrdinalSorted -Values $ids.ToArray())
}

function Get-QuotaLensCatalogIds {
    param([Parameter(Mandatory)] [string] $Path)

    $text = Get-Content -LiteralPath $Path -Raw
    $providerLines = [regex]::Matches($text, '(?m)^\s*new ProviderType\(')
    $providerMatches = [regex]::Matches(
        $text,
        '(?m)^\s*new ProviderType\("(?<id>[a-z0-9][a-z0-9-]*)",\s*"[^"\r\n]+"\),?\s*$')
    if ($providerLines.Count -ne $providerMatches.Count -or $providerMatches.Count -eq 0) {
        throw "The QuotaLens catalog format changed; refusing to infer an incomplete local provider list."
    }

    $ids = [Collections.Generic.List[string]]::new()
    foreach ($match in $providerMatches) {
        $ids.Add($match.Groups["id"].Value)
    }

    $duplicates = @($ids | Group-Object -CaseSensitive | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "The QuotaLens catalog contains duplicate provider IDs: $($duplicates.Name -join ', ')."
    }

    return ,(Get-OrdinalSorted -Values $ids.ToArray())
}

function Get-SetDifference {
    param(
        [Parameter(Mandatory)] [string[]] $Left,
        [Parameter(Mandatory)] [string[]] $Right
    )

    $rightSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Right) {
        [void] $rightSet.Add($value)
    }

    return ,@($Left | Where-Object { -not $rightSet.Contains($_) })
}

function Format-IdList {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Ids)

    if ($Ids.Count -eq 0) {
        return "(none)"
    }

    return $Ids -join ", "
}

try {
    $resolvedLockPath = (Resolve-Path -LiteralPath $LockPath).Path
    try {
        $lock = Get-Content -LiteralPath $resolvedLockPath -Raw | ConvertFrom-Json -Depth 32
    }
    catch {
        throw "The compatibility lock is not valid JSON: $($_.Exception.Message)"
    }

    $schemaVersion = Get-RequiredInteger -InputObject $lock -Name "schemaVersion"
    if ($schemaVersion -ne 1) {
        throw "Unsupported compatibility lock schema version '$schemaVersion'."
    }

    $purpose = Get-RequiredString -InputObject $lock -Name "purpose"
    if (-not [string]::Equals($purpose, $ExpectedPurpose, [StringComparison]::Ordinal)) {
        throw "The compatibility lock purpose must be '$ExpectedPurpose'."
    }

    $scopeNote = Get-RequiredString -InputObject $lock -Name "scopeNote"
    if ($scopeNote -notmatch "(?i)not official provider evidence") {
        throw "The compatibility lock must state that it is not official provider evidence."
    }

    $upstream = Get-RequiredProperty -InputObject $lock -Name "upstream"
    $repository = Get-RequiredString -InputObject $upstream -Name "repository"
    if (-not [string]::Equals($repository, $ExpectedRepository, [StringComparison]::Ordinal)) {
        throw "The compatibility lock repository must be '$ExpectedRepository'."
    }

    $baselineRevision = Get-RequiredString -InputObject $upstream -Name "baselineRevision"
    Assert-Matches -Value $baselineRevision -Pattern $FullRevisionPattern -Description "The locked baseline revision"

    $providerListPath = Get-RequiredString -InputObject $upstream -Name "providerListPath"
    $implementationRegistryPath = Get-RequiredString -InputObject $upstream -Name "implementationRegistryPath"
    $appRegistryPath = Get-RequiredString -InputObject $upstream -Name "appRegistryPath"
    if ($providerListPath -cne $ExpectedProviderListPath) {
        throw "The compatibility lock provider-list path must be '$ExpectedProviderListPath'."
    }
    if ($implementationRegistryPath -cne $ExpectedImplementationRegistryPath) {
        throw "The compatibility lock implementation-registry path must be '$ExpectedImplementationRegistryPath'."
    }
    if ($appRegistryPath -cne $ExpectedAppRegistryPath) {
        throw "The compatibility lock app-registry path must be '$ExpectedAppRegistryPath'."
    }

    Assert-SafeGitPath -Path $providerListPath -Name "providerListPath"
    Assert-SafeGitPath -Path $implementationRegistryPath -Name "implementationRegistryPath"
    Assert-SafeGitPath -Path $appRegistryPath -Name "appRegistryPath"

    $providerListSha256 = Get-RequiredString -InputObject $upstream -Name "providerListSha256"
    $implementationRegistrySha256 = Get-RequiredString -InputObject $upstream -Name "implementationRegistrySha256"
    $appRegistrySha256 = Get-RequiredString -InputObject $upstream -Name "appRegistrySha256"
    Assert-Matches -Value $providerListSha256 -Pattern $Sha256Pattern -Description "The provider-list SHA-256"
    Assert-Matches -Value $implementationRegistrySha256 -Pattern $Sha256Pattern -Description "The implementation-registry SHA-256"
    Assert-Matches -Value $appRegistrySha256 -Pattern $Sha256Pattern -Description "The app-registry SHA-256"

    $providerIds = Get-ValidatedIdArray -InputObject $lock -Name "providerIds"
    $providerCount = Get-RequiredInteger -InputObject $lock -Name "providerCount"
    if ($providerCount -ne $providerIds.Count) {
        throw "The compatibility lock providerCount is $providerCount, but providerIds contains $($providerIds.Count) entries."
    }

    $relationship = Get-RequiredProperty -InputObject $lock -Name "quotaLensRelationship"
    $catalogPath = Get-RequiredString -InputObject $relationship -Name "catalogPath"
    if ($catalogPath -cne $ExpectedQuotaLensCatalogPath) {
        throw "The compatibility lock QuotaLens catalog path must be '$ExpectedQuotaLensCatalogPath'."
    }
    Assert-SafeGitPath -Path $catalogPath -Name "catalogPath"
    $catalogCount = Get-RequiredInteger -InputObject $relationship -Name "catalogCount"
    $sharedCount = Get-RequiredInteger -InputObject $relationship -Name "sharedCount"
    $quotaLensOnlyIds = Get-ValidatedIdArray -InputObject $relationship -Name "quotaLensOnlyIds" -AllowEmpty
    $upstreamOnlyIds = Get-ValidatedIdArray -InputObject $relationship -Name "upstreamOnlyIds" -AllowEmpty
    $relationshipOverlap = @($quotaLensOnlyIds | Where-Object { $upstreamOnlyIds -contains $_ })
    if ($relationshipOverlap.Count -gt 0) {
        throw "The compatibility relationship lists IDs on both sides: $($relationshipOverlap -join ', ')."
    }

    $lockRoot = Split-Path -Parent $resolvedLockPath
    $catalogFullPath = [IO.Path]::GetFullPath((Join-Path $lockRoot $catalogPath))
    $lockRootPrefix = [IO.Path]::GetFullPath($lockRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) `
        + [IO.Path]::DirectorySeparatorChar
    if (-not $catalogFullPath.StartsWith($lockRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The compatibility lock catalog path resolves outside the QuotaLens repository."
    }
    if (-not (Test-Path -LiteralPath $catalogFullPath -PathType Leaf)) {
        throw "The QuotaLens catalog was not found at '$catalogFullPath'."
    }

    $catalogIds = Get-QuotaLensCatalogIds -Path $catalogFullPath
    $actualQuotaLensOnlyIds = Get-SetDifference -Left $catalogIds -Right $providerIds
    $actualUpstreamOnlyIds = Get-SetDifference -Left $providerIds -Right $catalogIds
    $actualSharedCount = $catalogIds.Count - $actualQuotaLensOnlyIds.Count
    if ($catalogCount -ne $catalogIds.Count) {
        throw "The compatibility lock catalogCount is $catalogCount, but the QuotaLens catalog contains $($catalogIds.Count) providers."
    }
    if ($sharedCount -ne $actualSharedCount) {
        throw "The compatibility lock sharedCount is $sharedCount, but the current overlap is $actualSharedCount."
    }
    Assert-OrdinalSequence -Expected $quotaLensOnlyIds -Actual $actualQuotaLensOnlyIds -Description "The locked QuotaLens-only provider IDs"
    Assert-OrdinalSequence -Expected $upstreamOnlyIds -Actual $actualUpstreamOnlyIds -Description "The locked upstream-only provider IDs"

    $gitCommands = @(Get-Command git -CommandType Application -ErrorAction SilentlyContinue)
    if ($gitCommands.Count -eq 0) {
        throw "git is required for the upstream compatibility check but was not found."
    }
    $gitPath = $gitCommands[0].Source

    if ([string]::IsNullOrWhiteSpace($UpstreamPath)) {
        if (-not $AllowNetwork) {
            throw "Provide -UpstreamPath for an existing CodexBar checkout, or explicitly use -AllowNetwork to create a temporary public checkout."
        }

        $temporaryClonePath = Join-Path ([IO.Path]::GetTempPath()) "quotalens-codexbar-$([Guid]::NewGuid().ToString('N'))"
        Write-Output "Creating a temporary public CodexBar checkout because -AllowNetwork was explicitly supplied."
        [void] (Invoke-GitProcess -GitPath $gitPath -WorkingDirectory ([IO.Path]::GetTempPath()) -Arguments @(
            "clone",
            "--filter=blob:none",
            "--no-checkout",
            "--no-tags",
            $repository,
            $temporaryClonePath
        ))
        $repositoryPath = $temporaryClonePath
    }
    else {
        $repositoryPath = (Resolve-Path -LiteralPath $UpstreamPath).Path
    }

    $insideWorkTree = Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
        "rev-parse",
        "--is-inside-work-tree"
    )
    if ($insideWorkTree -cne "true") {
        throw "The upstream path is not a git work tree: '$repositoryPath'."
    }

    $remoteUrl = Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
        "remote",
        "get-url",
        "origin"
    )
    $normalizedRemote = Normalize-RepositoryUrl -RepositoryUrl $remoteUrl
    if (-not [string]::Equals($normalizedRemote, $repository, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The upstream checkout origin is '$remoteUrl', not the locked repository '$repository'."
    }

    if ($AllowNetwork -and $null -eq $temporaryClonePath) {
        Write-Output "Fetching public CodexBar metadata because -AllowNetwork was explicitly supplied."
        [void] (Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
            "fetch",
            "--quiet",
            "--no-tags",
            "origin"
        ))
    }

    try {
        $resolvedBaselineRevision = Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
            "rev-parse",
            "--verify",
            "$baselineRevision^{commit}"
        )
    }
    catch {
        if (-not $AllowNetwork) {
            throw "The locked baseline revision is missing from the local checkout. Fetch it explicitly or rerun with -AllowNetwork. $($_.Exception.Message)"
        }

        [void] (Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
            "fetch",
            "--quiet",
            "--no-tags",
            "origin",
            $baselineRevision
        ))
        $resolvedBaselineRevision = Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
            "rev-parse",
            "--verify",
            "$baselineRevision^{commit}"
        )
    }
    if ($resolvedBaselineRevision -cne $baselineRevision) {
        throw "The locked baseline revision did not resolve to itself."
    }

    if (-not [string]::IsNullOrWhiteSpace($Revision)) {
        $Revision = $Revision.ToLowerInvariant()
        Assert-Matches -Value $Revision -Pattern $FullRevisionPattern -Description "The requested target revision"
        $targetRevision = Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
            "rev-parse",
            "--verify",
            "$Revision^{commit}"
        )
        if ($targetRevision -cne $Revision) {
            throw "The requested target revision did not resolve to itself."
        }
    }
    elseif ($AllowNetwork) {
        $targetRevision = Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
            "rev-parse",
            "--verify",
            "refs/remotes/origin/HEAD^{commit}"
        )
    }
    else {
        $targetRevision = Invoke-GitProcess -GitPath $gitPath -WorkingDirectory $repositoryPath -Arguments @(
            "rev-parse",
            "--verify",
            "HEAD^{commit}"
        )
    }
    Assert-Matches -Value $targetRevision -Pattern $FullRevisionPattern -Description "The resolved target revision"

    $baselineProviderBytes = Get-GitBlobBytes -GitPath $gitPath -RepositoryPath $repositoryPath -Commit $baselineRevision -Path $providerListPath
    $baselineImplementationBytes = Get-GitBlobBytes -GitPath $gitPath -RepositoryPath $repositoryPath -Commit $baselineRevision -Path $implementationRegistryPath
    $baselineAppRegistryBytes = Get-GitBlobBytes -GitPath $gitPath -RepositoryPath $repositoryPath -Commit $baselineRevision -Path $appRegistryPath

    $baselineHashes = @{
        $providerListPath = Get-Sha256 -Bytes $baselineProviderBytes
        $implementationRegistryPath = Get-Sha256 -Bytes $baselineImplementationBytes
        $appRegistryPath = Get-Sha256 -Bytes $baselineAppRegistryBytes
    }
    $lockedHashes = @{
        $providerListPath = $providerListSha256
        $implementationRegistryPath = $implementationRegistrySha256
        $appRegistryPath = $appRegistrySha256
    }
    foreach ($path in $lockedHashes.Keys) {
        if ($baselineHashes[$path] -cne $lockedHashes[$path]) {
            throw "The locked SHA-256 for '$path' does not match the locked baseline revision."
        }
    }

    $baselineProviderIds = Get-ProviderIdsFromSource -Bytes $baselineProviderBytes
    Assert-OrdinalSequence -Expected $providerIds -Actual $baselineProviderIds -Description "The provider IDs parsed from the locked baseline"
    $baselineImplementationIds = Get-ImplementationRegistryIds -Bytes $baselineImplementationBytes
    Assert-OrdinalSequence -Expected $baselineProviderIds -Actual $baselineImplementationIds -Description "The implementation registry coverage at the locked baseline"

    $targetProviderBytes = Get-GitBlobBytes -GitPath $gitPath -RepositoryPath $repositoryPath -Commit $targetRevision -Path $providerListPath
    $targetImplementationBytes = Get-GitBlobBytes -GitPath $gitPath -RepositoryPath $repositoryPath -Commit $targetRevision -Path $implementationRegistryPath
    $targetAppRegistryBytes = Get-GitBlobBytes -GitPath $gitPath -RepositoryPath $repositoryPath -Commit $targetRevision -Path $appRegistryPath
    $targetProviderIds = Get-ProviderIdsFromSource -Bytes $targetProviderBytes
    $targetImplementationIds = Get-ImplementationRegistryIds -Bytes $targetImplementationBytes

    $addedIds = Get-SetDifference -Left $targetProviderIds -Right $providerIds
    $removedIds = Get-SetDifference -Left $providerIds -Right $targetProviderIds
    $missingImplementations = Get-SetDifference -Left $targetProviderIds -Right $targetImplementationIds
    $extraImplementations = Get-SetDifference -Left $targetImplementationIds -Right $targetProviderIds

    $targetHashes = @{
        $providerListPath = Get-Sha256 -Bytes $targetProviderBytes
        $implementationRegistryPath = Get-Sha256 -Bytes $targetImplementationBytes
        $appRegistryPath = Get-Sha256 -Bytes $targetAppRegistryBytes
    }
    $changedPaths = [Collections.Generic.List[string]]::new()
    foreach ($path in $lockedHashes.Keys | Sort-Object) {
        if ($targetHashes[$path] -cne $lockedHashes[$path]) {
            $changedPaths.Add($path)
        }
    }

    Write-Output "CodexBar upstream compatibility check"
    Write-Output "Scope: $scopeNote"
    Write-Output "Repository: $repository"
    Write-Output "Locked baseline: $baselineRevision"
    Write-Output "Target revision: $targetRevision"
    Write-Output "Locked provider IDs: $($providerIds.Count)"
    Write-Output "Target provider IDs: $($targetProviderIds.Count)"
    Write-Output "Added provider IDs: $(Format-IdList -Ids $addedIds)"
    Write-Output "Removed provider IDs: $(Format-IdList -Ids $removedIds)"
    Write-Output "Missing implementation cases: $(Format-IdList -Ids $missingImplementations)"
    Write-Output "Extra implementation cases: $(Format-IdList -Ids $extraImplementations)"
    Write-Output "Changed tracked source files: $(Format-IdList -Ids $changedPaths.ToArray())"

    $hasDrift = $addedIds.Count -gt 0 `
        -or $removedIds.Count -gt 0 `
        -or $missingImplementations.Count -gt 0 `
        -or $extraImplementations.Count -gt 0 `
        -or $changedPaths.Count -gt 0

    $hasProviderSetDrift = $addedIds.Count -gt 0 `
        -or $removedIds.Count -gt 0 `
        -or $missingImplementations.Count -gt 0 `
        -or $extraImplementations.Count -gt 0

    if ($hasDrift) {
        if ($hasProviderSetDrift) {
            Write-Warning "Provider-set compatibility drift detected. Review the reported IDs; this script never adds or modifies providers."
        }
        else {
            Write-Warning "Tracked upstream compatibility source changed while provider IDs stayed stable. Human review is required; provider drift is not inferred from bytes alone."
        }
        $exitCode = 1
    }
    else {
        Write-Output "No upstream compatibility drift detected."
        $exitCode = 0
    }
}
catch {
    Write-Error "Upstream compatibility check failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 2
}
finally {
    if ($null -ne $temporaryClonePath -and (Test-Path -LiteralPath $temporaryClonePath)) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $temporaryCloneFullPath = [IO.Path]::GetFullPath($temporaryClonePath)
        $expectedPrefix = $temporaryRoot + [IO.Path]::DirectorySeparatorChar
        if ($temporaryCloneFullPath.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $temporaryCloneFullPath -Recurse -Force
        }
        else {
            Write-Warning "Refusing to remove unexpected temporary checkout path '$temporaryCloneFullPath'."
        }
    }
}

exit $exitCode
