$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sdk = Join-Path $PSScriptRoot 'release-staging'
$sdkBin = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object { Test-Path (Join-Path $_ 'makeappx.exe') } | Select-Object -First 1
$packager = if ($env:MAKEAPPX_EXE) { $env:MAKEAPPX_EXE } elseif ($sdkBin) { Join-Path $sdkBin 'makeappx.exe' } else { throw 'Install Windows SDK or set MAKEAPPX_EXE.' }
$signer = if ($env:SIGNTOOL_EXE) { $env:SIGNTOOL_EXE } elseif ($sdkBin) { Join-Path $sdkBin 'signtool.exe' } else { throw 'Install Windows SDK or set SIGNTOOL_EXE.' }
$package = Join-Path $PSScriptRoot 'Yizuka.CloudFiles.msix'
$certificate = $env:YIZUKA_SIGNING_THUMBPRINT
if (!$certificate) { throw 'Set YIZUKA_SIGNING_THUMBPRINT to a code-signing certificate in your user store.' }
$output = Join-Path $workspace 'outputs\小云盘\YizukaCloudSetup.exe'

& $dotnet publish (Join-Path $workspace 'work\yizuka-cfapi\yizuka-cfapi.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' -o (Join-Path $workspace 'work\yizuka-cfapi\release-publish') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Cloud Files client publish failed.' }
Copy-Item -LiteralPath (Join-Path $workspace 'work\yizuka-cfapi\release-publish\yizuka-cfapi.exe') -Destination (Join-Path $sdk 'Yizuka.CloudFiles.exe') -Force

$thumbnailSource = Join-Path $workspace 'work\yizuka-thumbnail-handler\ThumbnailHandler.cs'
$thumbnailOutput = Join-Path $sdk 'YizukaCloud.Thumbnail.exe'
& $csc /nologo /target:exe "/out:$thumbnailOutput" /define:YIZUKA_RELEASE /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $thumbnailSource
if ($LASTEXITCODE -ne 0) { throw 'Thumbnail provider build failed.' }

& $packager pack /d $sdk /p $package /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MSIX packaging failed.' }
& $signer sign /sha1 $certificate /fd SHA256 $package | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }

& $dotnet publish (Join-Path $workspace 'work\yizuka-cloudfiles-setup\YizukaCloudFilesSetup.csproj') -c Release -r win-x64 --self-contained true '-p:ReleaseChannel=Production' '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' -o (Join-Path $workspace 'work\yizuka-cloudfiles-setup\release-publish') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
Copy-Item -LiteralPath (Join-Path $workspace 'work\yizuka-cloudfiles-setup\release-publish\YizukaCloudSetup.exe') -Destination $output -Force
& $signer sign /sha1 $certificate /fd SHA256 $output | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Installer signing failed.' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
"$hash *YizukaCloudSetup.exe" | Set-Content -LiteralPath (Join-Path $workspace 'outputs\小云盘\YizukaCloudSetup.sha256.txt') -Encoding ascii
Write-Output "Installer: $output"
Write-Output "SHA256: $hash"
