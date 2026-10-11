using System;
using Newtonsoft.Json;

namespace UnityCompatVendor.Editor.Models
{
    [Serializable]
    public class McpConfig
    {
        [JsonProperty("mcpServers")]
        public McpConfigServers mcpServers;
    }
}
