# Registers Chop for the current user (HKCU only - no admin, nothing machine-wide) so it shows up in
# Settings > Apps > Default apps and in right-click > Open with. Windows requires you to confirm the
# actual default yourself; this script opens that Settings page at the end.
#   powershell -ExecutionPolicy Bypass -File register.ps1            register
#   powershell -ExecutionPolicy Bypass -File register.ps1 -Uninstall remove everything it added
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'app\Chop.exe'
$exts = '.mp4', '.mkv', '.mov', '.m4v', '.webm', '.avi', '.ts', '.m2ts', '.mts', '.flv', '.wmv', '.mpg', '.mpeg', '.3gp'
$classes = 'HKCU:\Software\Classes'
$progId = 'Chop.Video'

function Set-Default([string]$path, [string]$value) {
    if (-not (Test-Path -LiteralPath $path)) { New-Item -Path $path -Force | Out-Null }
    Set-Item -LiteralPath $path -Value $value
}

if ($Uninstall) {
    foreach ($p in "$classes\$progId", "$classes\Applications\Chop.exe", 'HKCU:\Software\Chop') {
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
    foreach ($e in $exts) {
        $k = "$classes\$e\OpenWithProgids"
        if (Test-Path -LiteralPath $k) { Remove-ItemProperty -LiteralPath $k -Name $progId -ErrorAction SilentlyContinue }
    }
    Remove-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name 'Chop' -ErrorAction SilentlyContinue
    Write-Host 'Chop unregistered.'
}
else {
    if (-not (Test-Path $exe)) { throw "Chop.exe not found at $exe - run build.ps1 first." }
    $cmd = "`"$exe`" `"%1`""
    # ProgID the file types point at
    Set-Default "$classes\$progId" 'Video (Chop)'
    Set-Default "$classes\$progId\DefaultIcon" "`"$exe`",0"
    Set-Default "$classes\$progId\shell\open\command" $cmd
    Set-ItemProperty -LiteralPath "$classes\$progId" -Name 'FriendlyTypeName' -Value 'Video'
    # Application entry (Open with list)
    Set-Default "$classes\Applications\Chop.exe\shell\open\command" $cmd
    Set-ItemProperty -LiteralPath "$classes\Applications\Chop.exe" -Name 'FriendlyAppName' -Value 'Chop'
    if (-not (Test-Path "$classes\Applications\Chop.exe\SupportedTypes")) { New-Item "$classes\Applications\Chop.exe\SupportedTypes" -Force | Out-Null }
    # Capabilities, so Chop gets its own page under Settings > Apps > Default apps
    $cap = 'HKCU:\Software\Chop\Capabilities'
    if (-not (Test-Path "$cap\FileAssociations")) { New-Item "$cap\FileAssociations" -Force | Out-Null }
    Set-ItemProperty -LiteralPath $cap -Name 'ApplicationName' -Value 'Chop'
    Set-ItemProperty -LiteralPath $cap -Name 'ApplicationDescription' -Value 'Lightweight video player and lossless trimmer'
    Set-ItemProperty -LiteralPath $cap -Name 'ApplicationIcon' -Value "`"$exe`",0"
    foreach ($e in $exts) {
        Set-ItemProperty -LiteralPath "$cap\FileAssociations" -Name $e -Value $progId
        Set-ItemProperty -LiteralPath "$classes\Applications\Chop.exe\SupportedTypes" -Name $e -Value ''
        $k = "$classes\$e\OpenWithProgids"
        if (-not (Test-Path -LiteralPath $k)) { New-Item -Path $k -Force | Out-Null }
        Set-ItemProperty -LiteralPath $k -Name $progId -Value ([byte[]]@()) -Type Binary
    }
    if (-not (Test-Path 'HKCU:\Software\RegisteredApplications')) { New-Item 'HKCU:\Software\RegisteredApplications' -Force | Out-Null }
    Set-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name 'Chop' -Value 'Software\Chop\Capabilities'
    Write-Host 'Chop registered for:' ($exts -join ' ')
}

# tell Explorer file associations changed
Add-Type -Namespace Chop -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);'
[Chop.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

if (-not $Uninstall) { Start-Process 'ms-settings:defaultapps?registeredAppUser=Chop' }
