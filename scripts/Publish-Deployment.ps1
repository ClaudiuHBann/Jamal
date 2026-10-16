#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidatePattern('^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,126}$')]
    [string]$ImageTag = '1.1.2'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $projectRoot '.artifacts/deployment'
$bundleName = 'discord-music-bot-linux-x64'
$bundleDirectory = Join-Path $outputRoot $bundleName
$imagePath = Join-Path $bundleDirectory 'discord-music-bot-image.tar.gz'
$bundleArchivePath = Join-Path $outputRoot "$bundleName.tar.gz"
$projectPath = Join-Path $projectRoot 'src/DiscordMusicBot/DiscordMusicBot.csproj'
$portableSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'

if (Test-Path -LiteralPath $portableSdk) {
    $dotnetExecutable = $portableSdk
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.artifacts/cli'
} else {
    $dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$workspacePackages = Join-Path $projectRoot '.artifacts/nuget'
if (Test-Path -LiteralPath $workspacePackages) { $env:NUGET_PACKAGES = $workspacePackages }

New-Item -ItemType Directory -Force -Path (Join-Path $bundleDirectory 'lavalink') | Out-Null

Push-Location $projectRoot
try {
    & $dotnetExecutable publish $projectPath -c Release --os linux --arch x64 --self-contained false `
        /t:PublishContainer /p:RestoreLockedMode=true /p:UseAppHost=false /p:UseSharedCompilation=false `
        /p:ContainerRepository=discord-music-bot "/p:ContainerImageTag=$ImageTag" `
        /p:ContainerBaseImage=mcr.microsoft.com/dotnet/runtime:10.0 /p:ContainerImageFormat=Docker `
        /p:ContainerUser=1654 /p:ContainerWorkingDirectory=/app/ "/p:ContainerArchiveOutputPath=$imagePath"
    if ($LASTEXITCODE -ne 0) { throw "Container publish failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'deploy/compose.yml') -Destination (Join-Path $bundleDirectory 'compose.yml') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'deploy/compose.youtube-oauth.yml') -Destination (Join-Path $bundleDirectory 'compose.youtube-oauth.yml') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'deploy/README.md') -Destination (Join-Path $bundleDirectory 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'deploy/YOUTUBE.md') -Destination (Join-Path $bundleDirectory 'YOUTUBE.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'deploy/diagnose-youtube.py') -Destination (Join-Path $bundleDirectory 'diagnose-youtube.py') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'lavalink/application.yml') -Destination (Join-Path $bundleDirectory 'lavalink/application.yml') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot '.env.example') -Destination (Join-Path $bundleDirectory '.env.example') -Force

$bundleFiles = @('discord-music-bot-image.tar.gz', 'compose.yml', 'compose.youtube-oauth.yml', 'README.md', 'YOUTUBE.md', 'diagnose-youtube.py', '.env.example', 'lavalink/application.yml')
$productionEnvironmentPath = Join-Path $projectRoot '.env'
if (Test-Path -LiteralPath $productionEnvironmentPath) {
    # Copy credentials only to runtime configuration, never to the image or build output.
    $environmentText = [System.IO.File]::ReadAllText($productionEnvironmentPath)
    $environmentText = [regex]::Replace($environmentText, '(?m)^BOT_IMAGE_TAG=.*\r?\n?', '')
    $environmentText = $environmentText.TrimEnd() + "`nBOT_IMAGE_TAG=$ImageTag`n"
    $youtubeTokenMatch = [regex]::Match($environmentText, '(?m)^YOUTUBE_REFRESH_TOKEN=(.*)$')
    $youtubeTokenValue = $youtubeTokenMatch.Groups[1].Value.Trim().Trim([char]39, [char]34)
    if ($youtubeTokenMatch.Success -and -not [string]::IsNullOrWhiteSpace($youtubeTokenValue)) {
        # The extracted bundle keeps its optional override beside compose.yml.
        $environmentText = [regex]::Replace($environmentText, '(?m)^COMPOSE_FILE=.*\r?\n?', '')
        $environmentText = $environmentText.TrimEnd() + "`nCOMPOSE_FILE=compose.yml:compose.youtube-oauth.yml`n"
    }
    [System.IO.File]::WriteAllText((Join-Path $bundleDirectory '.env'), $environmentText, [System.Text.UTF8Encoding]::new($false))
    $bundleFiles += '.env'
} elseif ($ImageTag -ne '1.1.2') {
    Add-Content -LiteralPath (Join-Path $bundleDirectory '.env.example') -Value "BOT_IMAGE_TAG=$ImageTag"
}

$checksumLines = foreach ($relativePath in $bundleFiles) {
    $checksum = (Get-FileHash -LiteralPath (Join-Path $bundleDirectory $relativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum  $relativePath"
}
[System.IO.File]::WriteAllText((Join-Path $bundleDirectory 'SHA256SUMS'), ($checksumLines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

$archiveStream = $null
$gzipStream = $null
$tarWriter = $null
try {
    $archiveStream = [System.IO.File]::Create($bundleArchivePath)
    $gzipStream = [System.IO.Compression.GZipStream]::new($archiveStream, [System.IO.Compression.CompressionLevel]::Optimal)
    $tarWriter = [System.Formats.Tar.TarWriter]::new($gzipStream, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
    foreach ($relativePath in @($bundleFiles + 'SHA256SUMS')) {
        $filePath = Join-Path $bundleDirectory $relativePath
        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, "$bundleName/$relativePath")
        $entry.Mode = if ($relativePath -eq '.env') { [System.IO.UnixFileMode]0x180 } else { [System.IO.UnixFileMode]0x1A4 }
        $entry.ModificationTime = [System.IO.File]::GetLastWriteTimeUtc($filePath)
        $fileStream = [System.IO.File]::OpenRead($filePath)
        try {
            $entry.DataStream = $fileStream
            $tarWriter.WriteEntry($entry)
        } finally {
            $fileStream.Dispose()
        }
    }
} finally {
    if ($null -ne $tarWriter) { $tarWriter.Dispose() }
    if ($null -ne $gzipStream) { $gzipStream.Dispose() }
    if ($null -ne $archiveStream) { $archiveStream.Dispose() }
}

Write-Host "Deployment bundle: $bundleArchivePath"
Write-Host "Bot image: discord-music-bot:$ImageTag (linux/amd64)"
Write-Host 'The deployment bundle contains private runtime credentials if .env was present.'
