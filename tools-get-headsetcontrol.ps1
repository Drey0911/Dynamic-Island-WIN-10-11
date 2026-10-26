# Copyright (c) 2026 Andrey Mantilla Leon. Licencia MIT.
# Descarga HeadsetControl (GPL-3.0, github.com/Sapd/HeadsetControl): lee la bateria de
# audifonos inalambricos USB de SteelSeries, HyperX, Corsair, Logitech, Sony y otras marcas.
# Se usa como programa externo; no se incluye su codigo dentro de Dynamic Island.
param([string]$Destination = "dist\headsetcontrol.exe")
$ErrorActionPreference = "Stop"
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ "User-Agent" = "DynamicIsland" }
    $releases = Invoke-RestMethod "https://api.github.com/repos/Sapd/HeadsetControl/releases?per_page=15" -Headers $headers
    # Primero versiones estables; si ninguna trae ejecutable de Windows, la compilacion continua.
    $ordered = @($releases | Where-Object { -not $_.prerelease }) + @($releases | Where-Object { $_.prerelease })
    foreach ($r in $ordered) {
        $exe = $r.assets | Where-Object { $_.name -like "headsetcontrol-windows*.exe" -and $_.name -notlike "*setup*" } | Select-Object -First 1
        if ($exe) {
            Invoke-WebRequest $exe.browser_download_url -OutFile $Destination -UseBasicParsing -Headers $headers
            Write-Host "  HeadsetControl $($r.tag_name) listo."
            exit 0
        }
        $zip = $r.assets | Where-Object { $_.name -like "*windows*.zip" } | Select-Object -First 1
        if ($zip) {
            $tmp = Join-Path $env:TEMP "headsetcontrol-dl"
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
            New-Item $tmp -ItemType Directory | Out-Null
            Invoke-WebRequest $zip.browser_download_url -OutFile "$tmp\hc.zip" -UseBasicParsing -Headers $headers
            Expand-Archive "$tmp\hc.zip" -DestinationPath $tmp -Force
            $found = Get-ChildItem $tmp -Recurse -Filter "headsetcontrol.exe" | Select-Object -First 1
            if ($found) {
                Copy-Item $found.FullName $Destination -Force
                Write-Host "  HeadsetControl $($r.tag_name) listo."
                exit 0
            }
        }
    }
    Write-Host "  No se encontro HeadsetControl. Los audifonos USB no mostraran bateria."
} catch {
    Write-Host "  No se pudo descargar HeadsetControl. Los audifonos USB no mostraran bateria."
}
exit 0
