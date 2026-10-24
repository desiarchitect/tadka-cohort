# Installs the AI-assisted kit into a repository, WITHOUT overwriting anything that already exists.
# Works in Windows PowerShell 5.1 and PowerShell 7.
#
#   powershell -File ai-assisted/install.ps1 -Tool all -WhatIf     # preview
#   powershell -File ai-assisted/install.ps1 -Tool claude          # Claude Code only
#   powershell -File ai-assisted/install.ps1 -Tool all -RepoRoot C:\work\my-repo
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('all', 'claude', 'copilot', 'cursor')]
    [string]$Tool = 'all',

    # Where to install. Default: the root of the git repository that contains this script.
    [string]$RepoRoot = ''
)

$kit = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $RepoRoot) {
    $RepoRoot = (& git -C $kit rev-parse --show-toplevel 2>$null)
    if (-not $RepoRoot) { throw "Could not find the repository root. Pass -RepoRoot <folder>." }
}
$RepoRoot = (Resolve-Path $RepoRoot).Path

# Each entry: kit folder (relative to ai-assisted), then where it goes (relative to the repository root).
# A folder entry copies every file inside it. A file entry copies one file.
$plan = @{
    shared  = @( @{ From = 'shared/AGENTS.md'; To = 'AGENTS.md' } )
    claude  = @(
        @{ From = 'claude-code/CLAUDE.md'; To = 'CLAUDE.md' },
        @{ From = 'claude-code/.claude';   To = '.claude' }
    )
    copilot = @(
        @{ From = 'github-copilot/.github/copilot-instructions.md'; To = '.github/copilot-instructions.md' },
        @{ From = 'github-copilot/.github/instructions';            To = '.github/instructions' },
        @{ From = 'github-copilot/.github/prompts';                 To = '.github/prompts' },
        @{ From = 'github-copilot/.github/agents';                  To = '.github/agents' },
        @{ From = 'github-copilot/.vscode';                         To = '.vscode' }
    )
    cursor  = @( @{ From = 'cursor/.cursor'; To = '.cursor' } )
}

$groups = @('shared')
if ($Tool -eq 'all') { $groups += @('claude', 'copilot', 'cursor') } else { $groups += $Tool }

$script:created = 0
$script:skipped = 0

function Copy-One([string]$source, [string]$dest) {
    $rel = $dest.Substring($RepoRoot.Length).TrimStart('\', '/')
    if (Test-Path $dest) {
        Write-Host "  skip    $rel (already exists)" -ForegroundColor Yellow
        $script:skipped++
        return
    }
    if ($PSCmdlet.ShouldProcess($rel, 'create')) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $source -Destination $dest
        Write-Host "  create  $rel" -ForegroundColor Green
    }
    $script:created++
}

foreach ($g in $groups) {
    Write-Host "[$g]" -ForegroundColor Cyan
    foreach ($item in $plan[$g]) {
        $src = Join-Path $kit $item.From
        $dst = Join-Path $RepoRoot $item.To
        if (-not (Test-Path $src)) { throw "Missing in the kit: $($item.From)" }
        if (Test-Path $src -PathType Container) {
            $base = (Resolve-Path $src).Path
            Get-ChildItem -LiteralPath $src -Recurse -File | ForEach-Object {
                $relInside = $_.FullName.Substring($base.Length).TrimStart('\', '/')
                Copy-One $_.FullName (Join-Path $dst $relInside)
            }
        } else {
            Copy-One $src $dst
        }
    }
}

Write-Host ""
Write-Host "Done: $($script:created) created (with -WhatIf: would be created), $($script:skipped) skipped because they already exist." -ForegroundColor Cyan
if ($groups -contains 'claude') {
    Write-Host "Next (Claude Code): add the line '@AGENTS.md' near the top of your CLAUDE.md so it imports the shared rulebook." -ForegroundColor Cyan
}
