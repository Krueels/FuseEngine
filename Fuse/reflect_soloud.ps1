$asm = [System.Reflection.Assembly]::LoadFile('C:\Users\niko\.nuget\packages\soloudsharp\0.2.0\lib\net8.0\SoLoudSharp.dll')
$t = $asm.GetType('SoLoudSharp.Soloud')
$t.GetMethods() | Select-Object -ExpandProperty Name | Sort-Object -Unique
