using System;
using UnityCompatVendor.Editor.Constants;
using UnityCompatVendor.Editor.Services.Transport.Transports;
using UnityEditor;

namespace UnityCompatVendor.Editor
{
    public static class McpCiBoot
    {
        public static void StartStdioForCi()
        {
            try 
            { 
                EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, false); 
            }
            catch { /* ignore */ }

            StdioBridgeHost.StartAutoConnect();
        }
    }
}
