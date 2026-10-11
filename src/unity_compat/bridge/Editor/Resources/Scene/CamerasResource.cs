using System;
using System.Collections.Generic;
using System.Linq;
using UnityCompatVendor.Editor.Helpers;
using UnityCompatVendor.Editor.Tools.Cameras;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCompatVendor.Editor.Resources.Scene
{
    [McpForUnityResource("get_cameras")]
    public static class CamerasResource
    {
        public static object HandleCommand(JObject @params)
        {
            try
            {
                return CameraControl.ListCameras(@params ?? new JObject());
            }
            catch (Exception e)
            {
                McpLog.Error($"[CamerasResource] Error listing cameras: {e}");
                return new ErrorResponse($"Error listing cameras: {e.Message}");
            }
        }
    }
}
