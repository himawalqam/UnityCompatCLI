using System;
using Newtonsoft.Json;

namespace UnityCompatVendor.Editor.Models
{
    [Serializable]
    public class McpConfigServers
    {
        [JsonProperty("unityMCP")]
        public McpConfigServer unityMCP;
    }
}
