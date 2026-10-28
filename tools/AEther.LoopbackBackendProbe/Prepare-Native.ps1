param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$native = Join-Path $root 'artifacts/capture-backends/native'
New-Item -ItemType Directory -Force -Path $native | Out-Null
$archives = @(
    @{Name='bass24'; Hash='3A03EC9A33D0F4F9D167660DA51C8BB1432E8977496995455AB137277D69636E'},
    @{Name='basswasapi24'; Hash='74B754A925FEDED1DDD6B8B7B1A37BDC9B185E86ABEBDD46B1A7E0FF67815779'}
)
foreach ($archive in $archives) {
    $zip = Join-Path $native ($archive.Name + '.zip')
    if (-not (Test-Path -LiteralPath $zip)) {
        Invoke-WebRequest -Uri ('https://www.un4seen.com/files/' + $archive.Name + '.zip') -OutFile $zip
    }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $archive.Hash) {
        throw "The $($archive.Name) download differs from the tested version. Review the upstream update before changing the recorded checksum."
    }
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $native $archive.Name) -Force
}
