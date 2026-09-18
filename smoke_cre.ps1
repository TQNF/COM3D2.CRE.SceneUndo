$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
Set-Location $root
$cre = Join-Path $root "CR EditSystem"
$managed = Join-Path $cre "CR EditSystem_Data\Managed"

Add-Type -Path (Join-Path $root "BepInEx\core\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $here "CRE.SceneUndo.dll"))
Write-Output "=== assembly references ==="
foreach ($r in $asm.MainModule.AssemblyReferences) { Write-Output ("  " + $r.Name + " " + $r.Version) }
Write-Output "=== types ==="
foreach ($t in $asm.MainModule.GetTypes()) {
  Write-Output ("  " + $t.FullName + " base=" + $t.BaseType)
  foreach ($f in $t.Fields) { Write-Output ("    F: " + $f.FieldType.Name + " " + $f.Name) }
  foreach ($m in $t.Methods) { Write-Output ("    M: " + $m.Name) }
}

# ---------- reflection logic test ----------
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Reflection;
public static class CRELoader {
    public static Dictionary<string, Assembly> Map = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    public static Assembly OnResolve(object sender, ResolveEventArgs e) {
        string name = new AssemblyName(e.Name).Name;
        Assembly a;
        if (Map.TryGetValue(name, out a)) return a;
        return null;
    }
}
"@
$handler = [System.Delegate]::CreateDelegate([System.ResolveEventHandler], [CRELoader].GetMethod("OnResolve"))
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($handler)
$loadOrder = @(
  (Join-Path $managed "netstandard.dll"),
  (Join-Path $managed "MessagePack.Annotations.dll"),
  (Join-Path $managed "MessagePack.dll"),
  (Join-Path $managed "UnityEngine.dll"),
  (Join-Path $managed "UnityEngine.CoreModule.dll"),
  (Join-Path $managed "UnityEngine.InputLegacyModule.dll"),
  (Join-Path $cre "BepInEx\core\BepInEx.dll"),
  (Join-Path $managed "Assembly-CSharp-firstpass.dll"),
  (Join-Path $managed "Assembly-CSharp.dll")
)
foreach ($f in $loadOrder) {
  try {
    $a = [System.Reflection.Assembly]::LoadFrom($f)
    [CRELoader]::Map[$a.GetName().Name] = $a
    Write-Output ("loaded: " + $a.GetName().Name)
  } catch { Write-Output ("SKIP (load fail): " + (Split-Path -Leaf $f) + " : " + $_.Exception.Message) }
}
$pluginAsm = [System.Reflection.Assembly]::LoadFrom((Join-Path $here "CRE.SceneUndo.dll"))
[CRELoader]::Map[$pluginAsm.GetName().Name] = $pluginAsm

$bf = [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance
$ptype = $pluginAsm.GetType("CRESceneUndo.SceneUndoPlugin")
if ($null -eq $ptype) { throw "SceneUndoPlugin type not found" }
Write-Output ("plugin type loaded: " + $ptype.FullName)

$histT = $ptype.GetNestedType("History", $bf)
$snapT = $ptype.GetNestedType("Snap", $bf)
$propT = $ptype.GetNestedType("PropSnap", $bf)
if ($null -eq $histT -or $null -eq $snapT -or $null -eq $propT) { throw "nested types not found" }
$listF = $null; $maxF = $null
foreach ($f in $histT.GetFields($bf)) { if ($f.Name -eq "list") { $listF = $f }; if ($f.Name -eq "MaxEntries") { $maxF = $f } }

function NewHist([int]$max) {
  $h = [System.Activator]::CreateInstance($histT)
  $maxF.SetValue($h, $max)
  return $h
}
$dictT = [System.Collections.Generic.Dictionary``2].MakeGenericType([int], $propT)
function NewSnap {
  $s = [System.Activator]::CreateInstance($snapT)
  foreach ($f in $snapT.GetFields($bf)) {
    if ($f.Name -eq "Props") { $f.SetValue($s, ([System.Activator]::CreateInstance($script:dictT))) }
    if ($f.Name -eq "Colors") { $f.SetValue($s, $null) }
  }
  return $s
}
function GetFld($t, $obj, $name) { foreach ($f in $t.GetFields($bf)) { if ($f.Name -eq $name) { return $f.GetValue($obj) } }; return $null }
function SetFld($t, $obj, $name, $val) { foreach ($f in $t.GetFields($bf)) { if ($f.Name -eq $name) { $f.SetValue($obj, $val) } } }
function CallHist($h, $name, $arg) {
  foreach ($m in $histT.GetMethods($bf)) {
    if ($m.Name -eq $name) {
      if ($null -eq $arg) { return $m.Invoke($h, @()) }
      return $m.Invoke($h, @($arg))
    }
  }
  throw ("method not found: " + $name)
}
function CallHistOut($h, $name) {
  foreach ($m in $histT.GetMethods($bf)) {
    if ($m.Name -eq $name) {
      $arr = New-Object 'object[]' 1
      $arr[0] = $null
      $r = $m.Invoke($h, $arr)
      return @{ ret = $r; out = $arr[0] }
    }
  }
  throw ("method not found: " + $name)
}

$results = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail) {
  $script:results.Add(("{0} | {1} | {2}" -f ($(if ($ok) {"PASS"} else {"FAIL"})), $name, $detail))
}

# --- H1/H2: empty history ---
$h = NewHist 100
$u = CallHistOut $h "Undo"
Check "H1 empty Undo=false" ($u.ret -eq $false) ""
$u = CallHistOut $h "Redo"
Check "H2 empty Redo=false" ($u.ret -eq $false) ""

# --- H3..H9: record A,B,C then undo/redo walk ---
$h = NewHist 100
$sA = NewSnap; $sB = NewSnap; $sC = NewSnap; $sD = NewSnap; $sE = NewSnap
[void](CallHist $h "Record" $sA)
[void](CallHist $h "Record" $sB)
[void](CallHist $h "Record" $sC)
Check "H3 record x3 Count=3" ($listF.GetValue($h).Count -eq 3) ("count=" + $listF.GetValue($h).Count)
$u = CallHistOut $h "Undo"
Check "H4 undo1 -> B" ($u.ret -eq $true -and [object]::ReferenceEquals($u.out, $sB)) ("ret=$($u.ret)")
$u = CallHistOut $h "Undo"
Check "H5 undo2 -> A" ($u.ret -eq $true -and [object]::ReferenceEquals($u.out, $sA)) ""
$u = CallHistOut $h "Undo"
Check "H6 undo3 -> false (oldest)" ($u.ret -eq $false) ""
$u = CallHistOut $h "Redo"
Check "H7 redo -> B" ($u.ret -eq $true -and [object]::ReferenceEquals($u.out, $sB)) ""
$u = CallHistOut $h "Redo"
Check "H8 redo -> C (tip)" ($u.ret -eq $true -and [object]::ReferenceEquals($u.out, $sC)) ""
$u = CallHistOut $h "Redo"
Check "H9 redo at tip -> false" ($u.ret -eq $false) ""

# --- H10..H12: record after undo-to-B keeps the viewed entry ---
$h = NewHist 100
[void](CallHist $h "Record" $sA); [void](CallHist $h "Record" $sB); [void](CallHist $h "Record" $sC)
[void](CallHistOut $h "Undo")
[void](CallHist $h "Record" $sD)
Check "H10 record-after-undo -> [A,B,D]" ($listF.GetValue($h).Count -eq 3) ("count=" + $listF.GetValue($h).Count)
$u = CallHistOut $h "Redo"
Check "H11 redo after new record -> false" ($u.ret -eq $false) ""
$u = CallHistOut $h "Undo"
Check "H12 undo -> B (kept)" ($u.ret -eq $true -and [object]::ReferenceEquals($u.out, $sB)) ""

# --- H13..H16: Clear mid-undo + Record (the EditSceneUndo crash case) ---
$h = NewHist 100
[void](CallHist $h "Record" $sA); [void](CallHist $h "Record" $sB); [void](CallHist $h "Record" $sC)
[void](CallHistOut $h "Undo")
[void](CallHist $h "Clear")
Check "H13 Clear empties list" ($listF.GetValue($h).Count -eq 0) ("count=" + $listF.GetValue($h).Count)
$threw = $null
try { [void](CallHist $h "Record" $sD) } catch { $threw = $_.Exception.InnerException }
Check "H14 Record after Clear does not throw" ($null -eq $threw) ("$threw")
$u = CallHistOut $h "Undo"
Check "H15 single-entry undo=false" ($u.ret -eq $false) ""

# --- H17: trim to MaxEntries ---
$h = NewHist 3
foreach ($s in @($sA, $sB, $sC, $sD, $sE)) { [void](CallHist $h "Record" $s) }
Check "H17 trim to 3" ($listF.GetValue($h).Count -eq 3) ("count=" + $listF.GetValue($h).Count)

# --- SnapEquals tests ---
$inst = [System.Runtime.Serialization.FormatterServices]::GetUninitializedObject($ptype)
$eqM = $null
foreach ($m in $ptype.GetMethods($bf)) { if ($m.Name -eq "SnapEquals") { $eqM = $m } }
if ($null -eq $eqM) { throw "SnapEquals not found" }
function MkSnap([string]$file, [int]$val, [byte[]]$colors) {
  $s = NewSnap
  $props = GetFld $snapT $s "Props"
  $p = [System.Activator]::CreateInstance($propT)
  SetFld $propT $p "FileName" $file
  SetFld $propT $p "Value" $val
  SetFld $propT $p "Rid" ([System.UInt64]42)
  SetFld $propT $p "NoScale" $false
  SetFld $propT $p "Defines" 0
  $props[1] = $p
  SetFld $snapT $s "Colors" $colors
  return $s
}
$a = MkSnap "abc.menu" 5 ([byte[]](1,2,3))
$b = MkSnap "abc.menu" 5 ([byte[]](1,2,3))
$c = MkSnap "abc.menu" 6 ([byte[]](1,2,3))
$d = MkSnap "xyz.menu" 5 ([byte[]](1,2,3))
$e = MkSnap "abc.menu" 5 ([byte[]](1,2,4))
$n1 = MkSnap "abc.menu" 5 $null
$n2 = MkSnap "abc.menu" 5 $null
Check "E1 equal snaps -> true" ($eqM.Invoke($inst, @($a, $b)) -eq $true) ""
Check "E2 different value -> false" ($eqM.Invoke($inst, @($a, $c)) -eq $false) ""
Check "E3 different file -> false" ($eqM.Invoke($inst, @($a, $d)) -eq $false) ""
Check "E4 different colors -> false" ($eqM.Invoke($inst, @($a, $e)) -eq $false) ""
Check "E5 both null colors -> true" ($eqM.Invoke($inst, @($n1, $n2)) -eq $true) ""
Check "E6 null vs colors -> false" ($eqM.Invoke($inst, @($a, $n1)) -eq $false) ""

Write-Output "=== RESULTS ==="
foreach ($r in $results) { Write-Output ("  " + $r) }
