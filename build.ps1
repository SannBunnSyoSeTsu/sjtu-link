$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:SJTU-Link.exe /win32manifest:app.manifest /resource:backend.ps1,backend.ps1 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll Client.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { Pop-Location }
