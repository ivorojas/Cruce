# Builds dist\Cruce.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK or downloads needed; the result runs on any Windows 10/11 PC as-is.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
New-Item -ItemType Directory -Force build, dist | Out-Null

$refs = @(
  "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Security.dll", "System.Xaml.dll",
  "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll", "$wpf\WindowsBase.dll",
  "Microsoft.CSharp.dll", "$wpf\UIAutomationClient.dll", "$wpf\UIAutomationTypes.dll", "System.Web.Extensions.dll"
) | ForEach-Object { "-r:$_" }
$src = Get-ChildItem src\*.cs | ForEach-Object { $_.FullName }
$common = @('-nologo', '-target:winexe', '-unsafe', '-optimize+', '-platform:anycpu', '-codepage:65001', '-nowarn:1685',
            '-win32manifest:src\app.manifest', '-resource:src\ui.xaml,Cruce.ui.xaml') + $refs

function Compile($out, $extra) {
  $o = & "$fw\csc.exe" @common @extra "-out:$out" @src 2>&1 | Where-Object { $_ -notmatch 'C# 5|language versions' }
  if ($LASTEXITCODE -ne 0) { $o | Write-Host; throw "Compilation failed" }
  $o | Where-Object { $_ -match 'warning' } | Write-Host
}

Compile 'build\stage.exe' @()
& .\build\stage.exe --write-icon build\app.ico | Out-Null
Start-Sleep -Milliseconds 300
Compile 'dist\Cruce.exe' @('-win32icon:build\app.ico')
Get-Item dist\Cruce.exe | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
