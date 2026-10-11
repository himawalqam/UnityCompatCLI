using System;
using Newtonsoft.Json.Linq;

namespace UnityCompat.Editor
{
    public sealed class CliFault : Exception
    {
        public string Code { get; }
        public bool Retryable { get; }
        public CliFault(string code, string message, bool retryable = false) : base(message)
        { Code = code; Retryable = retryable; }
        internal static JObject Error(Exception e) => new JObject {
            ["code"] = e is CliFault f ? f.Code : e is OperationCanceledException ? "CANCELLED_OR_EXPIRED" :
                e is ArgumentException ? "INVALID_ARGUMENT" : "COMMAND_FAILED",
            ["message"] = e.Message, ["retryable"] = e is CliFault c && c.Retryable };
    }
}
