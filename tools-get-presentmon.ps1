# Copyright (c) 2026 Andrey Mantilla Leon. Licencia MIT.
# Descarga la versión de consola de Intel PresentMon (MIT) para medir FPS de cualquier API.
param([string]$Destination = "dist\PresentMon.exe")
$ErrorActionPreference = "Stop"
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ "User-Agent" = "DynamicIsland" }
    $releases = Invoke-RestMethod "https://api.github.com/repos/GameTechDev/PresentMon/releases?per_page=10" -Headers $headers
    foreach ($r in $releases) {
        if ($r.prerelease) { continue }
        $asset = $r.assets | Where-Object { $_.name -like "PresentMon-*-x64.exe" } | Select-Object -First 1
        if ($asset) {
            Invoke-WebRequest $asset.browser_download_url -OutFile $Destination -UseBasicParsing -Headers $headers
            Write-Host "  PresentMon $($r.tag_name) listo."
            exit 0
        }
    }
    Write-Host "  No se encontro PresentMon. Se usara el contador DirectX."
} catch {
    Write-Host "  No se pudo descargar PresentMon. Se usara el contador DirectX."
}
exit 0
