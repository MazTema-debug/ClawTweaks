# SPDX-License-Identifier: AGPL-3.0-only
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReadOnlyHost)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop'){throw 'Windows PowerShell 5.1 is required.'}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
public static class LegionTweaksCompiledGuard {
    sealed class Instruction { public int Offset, Next, Token, Target; public string Name; }
    public static void Check(MethodInfo method) {
        var map = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)) {
            if (field.FieldType == typeof(OpCode)) { var op = (OpCode)field.GetValue(null); map[op.Value] = op; }
        }
        var bytes = method.GetMethodBody().GetILAsByteArray(); var code = new List<Instruction>();
        for (int p = 0; p < bytes.Length;) {
            int start = p; short key = bytes[p++]; if (key == 0xfe) key = (short)(0xfe00 | bytes[p++]);
            var op = map[key]; var item = new Instruction { Offset = start, Name = op.Name };
            switch (op.OperandType) {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: item.Target = p + 1 + (sbyte)bytes[p]; p++; break;
                case OperandType.InlineBrTarget: item.Target = p + 4 + BitConverter.ToInt32(bytes, p); p += 4; break;
                case OperandType.InlineMethod: case OperandType.InlineString: case OperandType.InlineField:
                case OperandType.InlineType: case OperandType.InlineTok: case OperandType.InlineSig:
                case OperandType.InlineI: item.Token = BitConverter.ToInt32(bytes, p); p += 4; break;
                case OperandType.ShortInlineI: case OperandType.ShortInlineVar: p++; break;
                case OperandType.InlineVar: p += 2; break;
                case OperandType.ShortInlineR: p += 4; break;
                case OperandType.InlineI8: case OperandType.InlineR: p += 8; break;
                case OperandType.InlineSwitch: int count = BitConverter.ToInt32(bytes, p); p += 4 + count * 4; break;
                default: throw new Exception("Unsupported IL operand.");
            }
            item.Next = p; if (op != OpCodes.Nop) code.Add(item);
        }
        string[] expected = { "ldarg.0", "ldlen", "conv.i4", "ldc.i4.3", "bne.un", "ldarg.0", "ldc.i4.0", "ldelem.ref", "ldstr", "call", "brfalse", "ldstr", "newobj", "throw" };
        if (code.Count < expected.Length) throw new Exception("Compiled guard is absent.");
        for (int n = 0; n < expected.Length; n++) {
            string name = code[n].Name; if (name.EndsWith(".s")) name = name.Substring(0, name.Length - 2);
            if (name != expected[n]) throw new Exception("Unexpected guard IL at " + n + ": " + code[n].Name);
        }
        var module = method.Module;
        var comparison = module.ResolveMethod(code[9].Token); var ctor = module.ResolveMethod(code[12].Token);
        if (code[4].Target != code[11].Offset || code[10].Target != code[13].Next ||
            module.ResolveString(code[8].Token) != "--qam-readonly" ||
            module.ResolveString(code[11].Token) != "Este ejecutable solo admite el lector temporal. Usa Iniciar-Prueba-QAM." ||
            comparison.DeclaringType != typeof(string) || comparison.Name != "op_Inequality" || ctor.DeclaringType != typeof(ArgumentException))
            throw new Exception("The guard can reach product code before rejecting an invalid request.");
    }
}
'@
$assembly=[Reflection.Assembly]::LoadFrom($ReadOnlyHost)
if($assembly.GetName().Name -cne 'LegionTweaks.QamReadOnly' -or $assembly.GetName().ProcessorArchitecture -ne 'Amd64'){throw 'Unexpected readonly host.'}
$main=$assembly.GetType('LegionTweaks.Program',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
# Prove the rejecting prefix before invoking it, including on elevated CI runners.
# No valid readonly request or normal helper entry point is executed.
[LegionTweaksCompiledGuard]::Check($main)
$cases=@(@{Args=@('--fps-y3-inspect')},@{Args=@('--fps-y3-inspect','extra')},@{Args=@()},@{Args=@('--diagnose')},@{Args=@('--fps-y3-watch','fixture','fixture')},@{Args=@('--qam-readonly','fixture')})
foreach($case in $cases){
    $oldOut=[Console]::Out;$oldErr=[Console]::Error;$output=[IO.StringWriter]::new();$errorOutput=[IO.StringWriter]::new()
    try{[Console]::SetOut($output);[Console]::SetError($errorOutput);$arguments=[object[]]::new(1);$arguments[0]=[string[]]$case.Args;$exit=$main.Invoke($null,$arguments)}finally{[Console]::SetOut($oldOut);[Console]::SetError($oldErr)}
    if($exit -ne 1 -or $output.ToString().Trim() -ne '' -or $errorOutput.ToString().Trim() -cne 'Este ejecutable solo admite el lector temporal. Usa Iniciar-Prueba-QAM.'){throw 'Readonly guard did not reject the fixture.'}
}
Write-Output 'PASS SDK readonly guard: IL boundary and six invalid entries; no journal, mutex or hardware opened'
