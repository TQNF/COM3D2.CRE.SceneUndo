$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
Set-Location $root
$cre = Join-Path $root "CR EditSystem"
$managed = Join-Path $cre "CR EditSystem_Data\Managed"

$src = [System.IO.File]::ReadAllText((Join-Path $here "CRE.SceneUndo.cs"))
$refs = @(
  (Join-Path $cre "BepInEx\core\BepInEx.dll"),
  (Join-Path $managed "UnityEngine.dll"),
  (Join-Path $managed "UnityEngine.CoreModule.dll"),
  (Join-Path $managed "UnityEngine.InputLegacyModule.dll"),
  (Join-Path $managed "Assembly-CSharp.dll"),
  (Join-Path $managed "Assembly-CSharp-firstpass.dll"),
  (Join-Path $managed "netstandard.dll"),
  (Join-Path $managed "MessagePack.Annotations.dll"),
  (Join-Path $managed "MessagePack.dll"),
  "System.dll",
  "System.Core.dll"
)
$outDll = Join-Path $here "CRE.SceneUndo.dll"
if (Test-Path $outDll) { Remove-Item $outDll -Force }
Add-Type -TypeDefinition $src -ReferencedAssemblies $refs -OutputAssembly $outDll -OutputType Library
Write-Output ("COMPILED: " + $outDll + " (" + (Get-Item $outDll).Length + " bytes)")
