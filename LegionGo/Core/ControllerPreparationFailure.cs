// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    public static class NativeReadFailure
    {
        public static Win32Exception Create(string operation, int error) => new Win32Exception(error,
            "Windows no confirmó " + operation + (error == 0 ? "; la API no proporcionó un código de error." : " (código " + error + ")."));
    }
    // Separate, protected error evidence. Exception messages, argument values,
    // source filenames and account/device paths are deliberately excluded.
    [DataContract]
    public sealed class ControllerPreparationFailure
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public string RunId { get; set; }
        [DataMember] public int ProcessId { get; set; }
        [DataMember] public string CapturedAtUtc { get; set; }
        [DataMember] public string ExceptionType { get; set; }
        [DataMember] public int HResult { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? NativeCode { get; set; }
        [DataMember] public string[] Methods { get; set; }
        public static ControllerPreparationFailure Capture(Exception error, string runId, int processId)
        {
            Guid id;
            if (error == null || !Guid.TryParseExact(runId,"N",out id) || id==Guid.Empty || processId<=0) throw new ArgumentException();
            Type type=error.GetType();
            var known=new[] {typeof(NullReferenceException),typeof(InvalidOperationException),typeof(TimeoutException),typeof(Win32Exception),
                typeof(System.IO.IOException),typeof(System.IO.InvalidDataException),typeof(ArgumentException),typeof(ObjectDisposedException),typeof(OperationCanceledException)};
            var frames=new StackTrace(error,false).GetFrames() ?? new StackFrame[0];
            var methods=frames.Select(f=>f.GetMethod()).Where(m=>m?.DeclaringType?.Namespace != null
                && (m.DeclaringType.Namespace.StartsWith("LegionTweaks",StringComparison.Ordinal)
                    || m.DeclaringType.Namespace.StartsWith("Nefarius.ViGEm",StringComparison.Ordinal)))
                .Select(m=>m.DeclaringType.FullName+"."+m.Name).Where(n=>n.Length<=240).Take(16).ToArray();
            return new ControllerPreparationFailure { RunId=id.ToString("N"),ProcessId=processId,CapturedAtUtc=DateTime.UtcNow.ToString("O"),
                ExceptionType=known.Contains(type)?type.Name:"Exception",HResult=error.HResult,NativeCode=(error as Win32Exception)?.NativeErrorCode,Methods=methods };
        }
    }
}
