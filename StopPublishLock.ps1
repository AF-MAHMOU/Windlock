param(
    [Parameter(Mandatory = $true)][string] $PublishDir,
    [Parameter(Mandatory = $true)][string] $AssemblyName
)

$ErrorActionPreference = 'SilentlyContinue'
if ([string]::IsNullOrWhiteSpace($PublishDir) -or [string]::IsNullOrWhiteSpace($AssemblyName)) {
    exit 0
}

$publishRoot = [System.IO.Path]::GetFullPath($PublishDir.TrimEnd('\', '/'))
if (-not [System.IO.Directory]::Exists($publishRoot)) {
    exit 0
}

# Hub + randomly named helpers living in the publish folder (and legacy wlguard).
Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
    $path = $_.Path
    if (-not $path) { return }
    try {
        $full = [System.IO.Path]::GetFullPath($path)
    } catch {
        return
    }
    if ($full.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "Windlock: stopping PID $($_.Id) locking publish output: $full"
        Stop-Process -InputObject $_ -Force
    }
}

Start-Sleep -Milliseconds 300
exit 0
