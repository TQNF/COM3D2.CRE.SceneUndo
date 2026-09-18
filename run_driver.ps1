$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
Set-Location $root
$managed = Join-Path (Join-Path $root "CR EditSystem") "CR EditSystem_Data\Managed"

# resolve game assemblies for the driver assembly's compile-time references
# (scriptblock delegates lose $args - use script-scoped vars + reentrancy guard)
$script:mgd = $managed
$script:bepcore = Join-Path (Split-Path (Split-Path $managed -Parent) -Parent) "BepInEx\core"
$script:busy = New-Object 'System.Collections.Generic.HashSet[string]'
$handler = [ResolveEventHandler]{
  param($s, $e)
  $n = (New-Object System.Reflection.AssemblyName($e.Name)).Name
  if (-not $script:busy.Add($n)) { return $null }
  try {
    foreach ($d in @($script:mgd, $script:bepcore)) {
      $f = Join-Path $d ($n + ".dll")
      if (Test-Path -LiteralPath $f) { return [System.Reflection.Assembly]::LoadFrom($f) }
    }
    return $null
  } finally { [void]$script:busy.Remove($n) }
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($handler)
try {
  Add-Type -TypeDefinition ([System.IO.File]::ReadAllText((Join-Path $here "Driver.cs"))) -ReferencedAssemblies @("mscorlib.dll", "System.dll", "System.Core.dll", (Join-Path $managed "Assembly-CSharp.dll"), (Join-Path $managed "UnityEngine.dll"), (Join-Path $managed "MessagePack.Annotations.dll"), (Join-Path $managed "netstandard.dll"))
} finally {
  [System.AppDomain]::CurrentDomain.remove_AssemblyResolve($handler)
}
try {
  $results = [Driver]::Run((Join-Path $here "CRE.SceneUndo.dll"), $managed)
} catch {
  Write-Output ("RUN EXCEPTION: " + $_.Exception.GetType().FullName)
  Write-Output $_.Exception.Message
  $inner = $_.Exception.InnerException
  while ($inner -ne $null) {
    Write-Output ("INNER: " + $inner.GetType().FullName + " @ " + $inner.Source)
    Write-Output $inner.StackTrace
    $inner = $inner.InnerException
  }
  exit 1
}
foreach ($r in $results) { Write-Output ("  " + $r) }
$fails = @($results | Where-Object { $_ -like "FAIL*" })
Write-Output ("TOTAL: " + $results.Count + "  FAILED: " + $fails.Count)
if ($fails.Count -gt 0) { exit 1 }
