// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;

namespace LegionTweaks
{
    // WMI property lookup is case-insensitive. Keep that behavior when copying
    // ManagementBaseObject.Properties: some Lenovo providers expose "value".
    // This parser has no Windows dependency so the real adapter logic is testable.
    internal sealed class WmiOutput
    {
        private readonly Dictionary<string, object> values =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, object value) { values[name] = value; }

        public void EnsureSuccess(string method)
        {
            object code;
            if (values.TryGetValue("ReturnValue", out code) && code != null && Convert.ToUInt32(code) != 0)
                throw new InvalidOperationException(method + " devolvió " + code);
        }

        public int ReadInt32(string name)
        {
            object value;
            if (!values.TryGetValue(name, out value) || value == null)
                throw new InvalidOperationException("WMI no devolvió " + name
                    + ". Campos recibidos: " + (values.Count == 0 ? "ninguno" : string.Join(", ", values.Keys)) + ".");
            return Convert.ToInt32(value);
        }
    }
}
