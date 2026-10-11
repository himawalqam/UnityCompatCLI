using System;
using UnityCompatVendor.Editor.Helpers;
using UnityCompatVendor.Editor.Tools.Graphics;
using Newtonsoft.Json.Linq;

namespace UnityCompatVendor.Editor.Resources.Scene
{
    [McpForUnityResource("get_renderer_features")]
    public static class RendererFeaturesResource
    {
        public static object HandleCommand(JObject @params)
        {
            try
            {
                return RendererFeatureOps.ListFeatures(@params ?? new JObject());
            }
            catch (Exception e)
            {
                McpLog.Error($"[RendererFeaturesResource] Error: {e}");
                return new ErrorResponse($"Error listing renderer features: {e.Message}");
            }
        }
    }
}
