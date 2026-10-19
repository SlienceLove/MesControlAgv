param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path -LiteralPath $Repository).Path
function Read-Git([string[]]$Arguments) {
    $value = @(& git -C $repoPath @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "git failed: $($Arguments -join ' ')" }
    return $value
}
$worktrees = @(Read-Git @('worktree', 'list', '--porcelain'))
$states = @()
foreach ($line in $worktrees) {
    if (-not $line.StartsWith('worktree ')) { continue }
    $worktreePath = $line.Substring(9)
    if (-not (Test-Path -LiteralPath $worktreePath -PathType Container)) { continue }
    $state = @(& git -C $worktreePath -c core.quotepath=false status --short)
    if ($LASTEXITCODE -ne 0) { throw "Cannot read status: $worktreePath" }
    $states += [ordered]@{ path = $worktreePath; status = $state }
}
$record = [ordered]@{
    createdAt = (Get-Date).ToString('o')
    repository = $repoPath
    refs = @(Read-Git @('for-each-ref', '--format=%(refname)|%(objectname)|%(upstream)|%(subject)'))
    worktrees = $worktrees
    states = $states
    stashes = @(Read-Git @('stash', 'list', '--format=%gd|%H|%s'))
}
$outputPath = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $outputPath) { throw "Refusing to overwrite inventory: $outputPath" }
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force
[IO.File]::WriteAllText($outputPath, ($record | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
Write-Output $outputPath
