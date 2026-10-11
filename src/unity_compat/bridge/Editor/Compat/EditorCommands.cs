using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCompat.Editor
{
    public static class EditorCommands
    {
        static object State(string requested = null) => new { state = requested == null ? "completed" : "transition_requested",
            requested, playing = EditorApplication.isPlaying, paused = EditorApplication.isPaused,
            changingPlayMode = EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying,
            compiling = EditorApplication.isCompiling, importing = EditorApplication.isUpdating, session = MailboxBridge.Session };
        static void Later(Action action)
        {
            double at = EditorApplication.timeSinceStartup + 0.25;
            EditorApplication.CallbackFunction tick = null;
            tick = () => { if (EditorApplication.timeSinceStartup < at) return; EditorApplication.update -= tick; action(); };
            EditorApplication.update += tick;
        }
        [CliCommand("editor_status", "Current play/pause/compile/import state", Group = "editor")]
        public static object Status() => State();
        [CliCommand("editor_play", "Request Play Mode; acknowledge before potential domain reload", Group = "editor")]
        public static object Play()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) throw new CliFault("EDITOR_BUSY", "Editor is compiling/importing", true);
            if (EditorApplication.isPlaying) return State();
            Later(() => EditorApplication.isPlaying = true); return State("play");
        }
        [CliCommand("editor_stop", "Request leaving Play Mode; acknowledge before potential domain reload", Group = "editor")]
        public static object Stop() { if (!EditorApplication.isPlaying) return State(); Later(() => EditorApplication.isPlaying = false); return State("stop"); }
        [CliCommand("editor_pause", "Pause Play Mode", Group = "editor")]
        public static object Pause() { if (!EditorApplication.isPlaying) throw new CliFault("INVALID_STATE", "Pause requires Play Mode"); EditorApplication.isPaused = true; return State(); }
        [CliCommand("editor_step", "Advance one paused Play Mode frame", Group = "editor")]
        public static object Step() { if (!EditorApplication.isPlaying || !EditorApplication.isPaused) throw new CliFault("INVALID_STATE", "Step requires paused Play Mode"); EditorApplication.Step(); return State("step"); }
        [CliCommand("editor_recompile", "Request script compilation; observe session/readiness afterward", Group = "editor")]
        public static object Recompile() { Later(() => UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation()); return State("recompile"); }
        [CliCommand("editor_refresh", "Request AssetDatabase refresh after acknowledging the command", Group = "editor")]
        public static object Refresh() { Later(() => AssetDatabase.Refresh()); return State("refresh"); }
        [CliCommand("build_targets", "List defined build targets with installed support and current selection", Group = "build")]
        public static object BuildTargets([CliArg("installed", "Only show installed targets")] bool installed = false)
        {
            return Enum.GetValues(typeof(BuildTarget)).Cast<BuildTarget>().Distinct().Where(t => (int)t > 0).Select(t => {
                var group = BuildPipeline.GetBuildTargetGroup(t);
                bool supported = group != BuildTargetGroup.Unknown && BuildPipeline.IsBuildTargetSupported(group, t);
                return new { name = t.ToString(), group = group.ToString(), installed = supported, active = t == EditorUserBuildSettings.activeBuildTarget };
            }).Where(x => !installed || x.installed).ToArray();
        }
        [CliCommand("editor_screenshot", "Render a scene camera to a PNG; requires a graphics device", Group = "editor")]
        public static object Screenshot([CliArg("output")] string output = null, [CliArg("width")] int width = 1280,
            [CliArg("height")] int height = 720, [CliArg("camera")] string camera = null)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) throw new CliFault("UNSUPPORTED", "Screenshot requires a graphics device; restart the fixture without -nographics.");
            if (width < 1 || height < 1 || width > 4096 || height > 4096) throw new CliFault("INVALID_ARGUMENT", "Dimensions must be 1..4096");
            Camera selected;
            if (camera == null) selected = Camera.main ?? Resources.FindObjectsOfTypeAll<Camera>().FirstOrDefault(c => c.gameObject.scene.IsValid() && c.enabled);
            else { int id; var arg = new JObject { ["target"] = int.TryParse(camera, out id) ? (JToken)new JValue(id) : new JValue(camera) }; selected = Args.Object(arg).GetComponent<Camera>(); }
            if (selected == null) throw new CliFault("COMPONENT_NOT_FOUND", "No scene Camera found");
            string path = Path.GetFullPath(Path.Combine(MailboxBridge.Project, output ?? "Library/UnityCompat/Captures/" + Guid.NewGuid().ToString("N") + ".png"));
            if (!path.StartsWith(MailboxBridge.Project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path) != ".png")
                throw new CliFault("INVALID_PATH", "Output must be a PNG within this project");
            for (var d = new DirectoryInfo(Path.GetDirectoryName(path)); d != null && d.FullName.Length >= MailboxBridge.Project.Length; d = d.Parent)
                if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFault("INVALID_PATH", "Junction/symlink output is unsupported");
            if (File.Exists(path)) throw new CliFault("PRECONDITION_FAILED", "Output already exists");
            var oldTarget = selected.targetTexture; var oldActive = RenderTexture.active;
            var render = RenderTexture.GetTemporary(width, height, 24); Texture2D image = null;
            try
            {
                selected.targetTexture = render; selected.Render(); RenderTexture.active = render;
                image = new Texture2D(width, height, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, width, height), 0, 0); image.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(path)); byte[] bytes = image.EncodeToPNG(); File.WriteAllBytes(path, bytes);
                return new { path, width, height, bytes = bytes.Length, camera = selected.GetInstanceID() };
            }
            finally { selected.targetTexture = oldTarget; RenderTexture.active = oldActive; if (image != null) UnityEngine.Object.DestroyImmediate(image); RenderTexture.ReleaseTemporary(render); }
        }
    }
}
