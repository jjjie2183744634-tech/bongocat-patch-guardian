param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifactRoot = Join-Path $repoRoot "artifacts"
$buildRoot = Join-Path $artifactRoot "build"
$componentRoot = Join-Path $buildRoot "components"
$packageRoot = Join-Path $artifactRoot ("BongoCatPatchPack-v" + $Version)
$vendorRoot = Join-Path $repoRoot "vendor"

foreach ($target in @($buildRoot, $packageRoot)) {
    if (Test-Path -LiteralPath $target) {
        $resolved = (Resolve-Path -LiteralPath $target).Path
        if (-not $resolved.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unexpected path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $componentRoot, (Join-Path $packageRoot "components"), $vendorRoot | Out-Null

$cecil = Join-Path $vendorRoot "Mono.Cecil.dll"
if (-not (Test-Path -LiteralPath $cecil)) {
    $nupkg = Join-Path $vendorRoot "Mono.Cecil.0.11.6.nupkg"
    $extract = Join-Path $vendorRoot "Mono.Cecil.0.11.6"
    if (-not (Test-Path -LiteralPath $nupkg)) {
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Mono.Cecil/0.11.6" -OutFile $nupkg
    }
    if (-not (Test-Path -LiteralPath $extract)) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $extract)
    }
    Copy-Item -LiteralPath (Join-Path $extract "lib\net40\Mono.Cecil.dll") -Destination $cecil
}

$compiler = (Get-Command csc.exe -ErrorAction SilentlyContinue).Source
if (-not $compiler) {
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "Could not find the .NET Framework C# compiler."
}

& $compiler /nologo /target:exe "/out:$componentRoot\BongoCatAdaptivePatcher.exe" "/r:$cecil" (Join-Path $repoRoot "src\BongoCatAdaptivePatcher.cs")
if ($LASTEXITCODE -ne 0) { throw "Patcher compilation failed." }
& $compiler /nologo /target:winexe "/out:$componentRoot\BongoCatPatchGuardian.exe" (Join-Path $repoRoot "src\BongoCatPatchGuardian.cs")
if ($LASTEXITCODE -ne 0) { throw "Guardian compilation failed." }
& $compiler /nologo /target:library "/out:$componentRoot\BongoCatChatLogger.dll" (Join-Path $repoRoot "src\BongoCatChatLogger.cs")
if ($LASTEXITCODE -ne 0) { throw "Chat logger compilation failed." }
& $compiler /nologo /target:winexe "/out:$componentRoot\BongoCatTray.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $repoRoot "src\BongoCatTray.cs")
if ($LASTEXITCODE -ne 0) { throw "Tray helper compilation failed." }
Copy-Item -LiteralPath $cecil -Destination (Join-Path $componentRoot "Mono.Cecil.dll")

$componentHashes = @{}
Get-ChildItem -LiteralPath $componentRoot -File | ForEach-Object {
    $componentHashes[$_.Name] = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
}
$installerSource = Get-Content -LiteralPath (Join-Path $repoRoot "src\BongoCatPatchInstaller.cs") -Raw
foreach ($name in $componentHashes.Keys) {
    $pattern = '(?m)(\{\s*"' + [Regex]::Escape($name) + '",\s*")[A-F0-9]{64}("\s*\})'
    $replacement = '${1}' + $componentHashes[$name] + '${2}'
    $installerSource = [Regex]::Replace($installerSource, $pattern, $replacement)
}
$generatedInstaller = Join-Path $buildRoot "BongoCatPatchInstaller.generated.cs"
[IO.File]::WriteAllText($generatedInstaller, $installerSource, (New-Object Text.UTF8Encoding($false)))
& $compiler /nologo /target:winexe "/out:$buildRoot\安装补丁.exe" /r:System.Windows.Forms.dll $generatedInstaller
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
Copy-Item -LiteralPath (Join-Path $buildRoot "安装补丁.exe") -Destination (Join-Path $buildRoot "恢复原版.exe")

Copy-Item -LiteralPath (Join-Path $buildRoot "安装补丁.exe") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $buildRoot "恢复原版.exe") -Destination $packageRoot
Copy-Item -Path (Join-Path $componentRoot "*") -Destination (Join-Path $packageRoot "components")
Copy-Item -LiteralPath (Join-Path $repoRoot "package-files\使用说明.txt") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $packageRoot "LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE-Mono.Cecil.txt") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md") -Destination $packageRoot

$hashLines = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
    ((Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash + "  " + $relative)
}
[IO.File]::WriteAllLines((Join-Path $packageRoot "SHA256SUMS.txt"), $hashLines, (New-Object Text.UTF8Encoding($false)))

$zip = Join-Path $artifactRoot ("BongoCatPatchPack-v" + $Version + ".zip")
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Built: $zip"
Write-Host "SHA256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash)"
