$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    foreach ($target in @('Smoke','UiSmoke')) {
        & $compiler /nologo /codepage:65001 /target:exe "/main:$target" /platform:x64 "/out:$target.exe" /win32manifest:app.manifest /resource:backend.ps1,backend.ps1 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll Client.cs "$target.cs"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $target" }
        & ".\$target.exe"
        if ($LASTEXITCODE -ne 0) { throw "Test failed: $target" }
    }
} finally { Pop-Location }
