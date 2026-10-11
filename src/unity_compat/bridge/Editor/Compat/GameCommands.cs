using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityCompatVendor.Editor.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace UnityCompat.Editor
{
    [CliCommand("manage_terrain", "Inspect terrain, create TerrainData, and read/write bounded height maps", Group = "terrain")]
    public static class TerrainCommand
    {
        public static object HandleCommand(JObject p)
        {
            string action = p["action"]?.ToString() ?? "list";
            if (action == "list") return new SuccessResponse("Terrains", Terrain.activeTerrains.Select(t => new {
                id = t.gameObject.GetInstanceID(), path = Args.PathOf(t.transform), data = AssetDatabase.GetAssetPath(t.terrainData),
                resolution = t.terrainData.heightmapResolution, size = new[] { t.terrainData.size.x, t.terrainData.size.y, t.terrainData.size.z } }).ToArray());
            if (action == "create")
            {
                string path = Args.Required(p, "path");
                if (!path.StartsWith("Assets/") || !path.EndsWith(".asset") || path.Contains("..")) throw new ArgumentException("path must be an Assets/... .asset path.");
                if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(path).Replace('\\', '/')) || AssetDatabase.LoadMainAssetAtPath(path) != null)
                    throw new ArgumentException("Asset parent folder must exist and destination must be unused.");
                int resolution = p["resolution"]?.Value<int>() ?? 129;
                if (resolution < 33 || resolution > 513 || ((resolution - 1) & (resolution - 2)) != 0)
                    throw new ArgumentException("resolution must be 33, 65, 129, 257 or 513.");
                var data = new TerrainData { heightmapResolution = resolution, size = p["size"] == null ? new Vector3(100, 20, 100) : Args.Vector(p["size"]) };
                AssetDatabase.CreateAsset(data, path);
                var go = Terrain.CreateTerrainGameObject(data);
                go.name = p["name"]?.ToString() ?? "Terrain";
                Undo.RegisterCreatedObjectUndo(go, "Create terrain");
                AssetDatabase.SaveAssets();
                return new SuccessResponse("Terrain created", Args.Summary(go));
            }
            var terrain = Args.Object(p).GetComponent<Terrain>();
            if (terrain == null) throw new ArgumentException("Target has no Terrain component.");
            int x = p["x"]?.Value<int>() ?? 0, y = p["y"]?.Value<int>() ?? 0;
            int width = p["width"]?.Value<int>() ?? 16, height = p["height"]?.Value<int>() ?? 16;
            if (width <= 0 || height <= 0 || width > 513 || height > 513 || x < 0 || y < 0 || x + width > terrain.terrainData.heightmapResolution || y + height > terrain.terrainData.heightmapResolution)
                throw new ArgumentException("Height map bounds are invalid.");
            if (action == "get_heights")
            {
                var heights = terrain.terrainData.GetHeights(x, y, width, height);
                var rows = new JArray();
                for (int row = 0; row < height; row++) { var cells = new JArray(); for (int col = 0; col < width; col++) cells.Add(heights[row, col]); rows.Add(cells); }
                return new SuccessResponse("Terrain height map", rows);
            }
            if (action != "set_heights" || !(p["heights"] is JArray input) || input.Count != height) throw new ArgumentException("Use list, create, get_heights or set_heights; heights must match the rectangle.");
            var values = new float[height, width];
            for (int row = 0; row < height; row++)
            {
                if (!(input[row] is JArray cells) || cells.Count != width) throw new ArgumentException("Height map dimensions differ.");
                for (int col = 0; col < width; col++)
                {
                    float value = cells[col].Value<float>();
                    if (Single.IsNaN(value) || Single.IsInfinity(value) || value < 0 || value > 1) throw new ArgumentException("Heights must be finite values in [0,1].");
                    values[row, col] = value;
                }
            }
            Undo.RegisterCompleteObjectUndo(terrain.terrainData, "CLI terrain heights");
            terrain.terrainData.SetHeights(x, y, values);
            EditorUtility.SetDirty(terrain.terrainData);
            AssetDatabase.SaveAssets();
            return new SuccessResponse("Terrain heights updated");
        }
    }

    [CliCommand("manage_audio", "Inspect audio sources/clips, assign mixer groups, and play/stop preview sources", Group = "audio")]
    public static class AudioCommand
    {
        public static object HandleCommand(JObject p)
        {
            string action = p["action"]?.ToString() ?? "list";
            if (action == "clips") return new SuccessResponse("Audio clips", AssetDatabase.FindAssets("t:AudioClip").Select(AssetDatabase.GUIDToAssetPath).ToArray());
            if (action == "list") return new SuccessResponse("Audio sources", Resources.FindObjectsOfTypeAll<AudioSource>()
                .Where(s => !EditorUtility.IsPersistent(s) && s.gameObject.scene.IsValid()).Select(s => new {
                    id = s.gameObject.GetInstanceID(), path = Args.PathOf(s.transform), clip = s.clip == null ? null : AssetDatabase.GetAssetPath(s.clip),
                    volume = s.volume, loop = s.loop, playing = s.isPlaying, mixer = s.outputAudioMixerGroup?.name }).ToArray());
            var source = Args.Object(p).GetComponent<AudioSource>();
            if (source == null) throw new ArgumentException("Target has no AudioSource.");
            if (action == "play") source.Play();
            else if (action == "stop") source.Stop();
            else if (action == "set")
            {
                var clip = p["clip"] == null ? source.clip : AssetDatabase.LoadAssetAtPath<AudioClip>(p["clip"].ToString());
                if (p["clip"] != null && clip == null) throw new ArgumentException("AudioClip not found.");
                var group = source.outputAudioMixerGroup;
                if (p["mixer"] != null)
                {
                    var mixer = AssetDatabase.LoadAssetAtPath<UnityEngine.Audio.AudioMixer>(p["mixer"].ToString());
                    var groups = mixer == null ? new UnityEngine.Audio.AudioMixerGroup[0] : mixer.FindMatchingGroups(p["group"]?.ToString() ?? "Master");
                    if (groups.Length != 1) throw new ArgumentException("Mixer group missing or ambiguous; specify mixer and group.");
                    group = groups[0];
                }
                Undo.RecordObject(source, "CLI audio change");
                source.clip = clip;
                source.outputAudioMixerGroup = group;
                if (p["volume"] != null) source.volume = Mathf.Clamp01(p["volume"].Value<float>());
                if (p["loop"] != null) source.loop = p["loop"].Value<bool>();
                if (p["spatial_blend"] != null) source.spatialBlend = Mathf.Clamp01(p["spatial_blend"].Value<float>());
                EditorSceneManager.MarkSceneDirty(source.gameObject.scene);
            }
            else throw new ArgumentException("Use list, clips, set, play or stop.");
            return new SuccessResponse("Audio operation complete");
        }
    }

    [CliCommand("manage_navigation", "Inspect agents, sample NavMesh, calculate paths, and set agent destinations", Group = "navigation")]
    public static class NavigationCommand
    {
        public static object HandleCommand(JObject p)
        {
            string action = p["action"]?.ToString() ?? "status";
            if (action == "status")
            {
                var mesh = NavMesh.CalculateTriangulation();
                return new SuccessResponse("Navigation status", new { vertices = mesh.vertices.Length, triangles = mesh.indices.Length / 3,
                    agents = Resources.FindObjectsOfTypeAll<NavMeshAgent>().Where(a => !EditorUtility.IsPersistent(a) && a.gameObject.scene.IsValid())
                        .Select(a => new { id = a.gameObject.GetInstanceID(), path = Args.PathOf(a.transform), enabled = a.enabled, onNavMesh = a.isOnNavMesh, speed = a.speed }).ToArray() });
            }
            int mask = p["area_mask"]?.Value<int>() ?? NavMesh.AllAreas;
            if (action == "sample")
            {
                bool found = NavMesh.SamplePosition(Args.Vector(p["position"]), out var hit, p["distance"]?.Value<float>() ?? 5, mask);
                return new SuccessResponse("NavMesh sample", new { found, position = new[] { hit.position.x, hit.position.y, hit.position.z }, distance = hit.distance, areaMask = hit.mask });
            }
            if (action == "path")
            {
                var path = new NavMeshPath();
                bool found = NavMesh.CalculatePath(Args.Vector(p["from"]), Args.Vector(p["to"]), mask, path);
                return new SuccessResponse("Navigation path", new { found, status = path.status.ToString(), corners = path.corners.Select(v => new[] { v.x, v.y, v.z }).ToArray() });
            }
            if (action != "destination") throw new ArgumentException("Use status, sample, path or destination.");
            var agent = Args.Object(p).GetComponent<NavMeshAgent>();
            if (agent == null || !agent.isOnNavMesh) throw new ArgumentException("Target has no agent on a NavMesh.");
            return new SuccessResponse("Agent destination", new { accepted = agent.SetDestination(Args.Vector(p["position"])) });
        }
    }

    [CliCommand("manage_lighting", "Inspect lights/lightmap bake status, start or cancel asynchronous lightmap bakes", Group = "lighting")]
    public static class LightingCommand
    {
        public static object HandleCommand(JObject p)
        {
            string action = p["action"]?.ToString() ?? "status";
            if (action == "bake") return new SuccessResponse("Bake requested", new { accepted = Lightmapping.BakeAsync() });
            if (action == "cancel") Lightmapping.Cancel();
            else if (action != "status" && action != "list") throw new ArgumentException("Use status, list, bake or cancel.");
            return new SuccessResponse("Lighting", new { baking = Lightmapping.isRunning, progress = Lightmapping.buildProgress,
                lightmaps = LightmapSettings.lightmaps.Length, lights = Resources.FindObjectsOfTypeAll<Light>()
                    .Where(l => !EditorUtility.IsPersistent(l) && l.gameObject.scene.IsValid()).Select(l => new {
                        id = l.gameObject.GetInstanceID(), path = Args.PathOf(l.transform), type = l.type.ToString(), intensity = l.intensity,
                        color = new[] { l.color.r, l.color.g, l.color.b }, shadows = l.shadows.ToString() }).ToArray() });
        }
    }

    [CliCommand("animation_events", "Read and replace AnimationClip event timelines through Unity APIs", Group = "animation")]
    public static class AnimationEventsCommand
    {
        public static object HandleCommand(JObject p)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Args.Required(p, "path"));
            if (clip == null) throw new ArgumentException("AnimationClip not found; imported clips need their importer instead.");
            string action = p["action"]?.ToString() ?? "get";
            if (action == "set")
            {
                if (!(p["events"] is JArray rows)) throw new ArgumentException("events must be an array.");
                var events = rows.Select(row => new AnimationEvent {
                    time = row["time"]?.Value<float>() ?? 0, functionName = row["function"]?.ToString() ?? "",
                    stringParameter = row["string"]?.ToString() ?? "", intParameter = row["int"]?.Value<int>() ?? 0,
                    floatParameter = row["float"]?.Value<float>() ?? 0 }).ToArray();
                if (events.Any(e => e.time < 0 || e.time > clip.length || String.IsNullOrEmpty(e.functionName))) throw new ArgumentException("Event time/function is invalid.");
                Undo.RecordObject(clip, "CLI animation events");
                AnimationUtility.SetAnimationEvents(clip, events);
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssets();
            }
            else if (action != "get") throw new ArgumentException("Use get or set.");
            return new SuccessResponse("Animation events", AnimationUtility.GetAnimationEvents(clip).Select(e => new {
                time = e.time, function = e.functionName, text = e.stringParameter, integer = e.intParameter, number = e.floatParameter }).ToArray());
        }
    }

    [CliCommand("asset_bundles", "List and assign asset bundle names or build asset bundles", Group = "build")]
    public static class AssetBundlesCommand
    {
        public static object HandleCommand(JObject p)
        {
            string action = p["action"]?.ToString() ?? "list";
            if (action == "list") return new SuccessResponse("Asset bundles", AssetDatabase.GetAllAssetBundleNames().Select(name => new { name, assets = AssetDatabase.GetAssetPathsFromAssetBundle(name) }).ToArray());
            if (action == "set")
            {
                var importer = AssetImporter.GetAtPath(Args.Required(p, "path"));
                if (importer == null) throw new ArgumentException("Asset importer not found.");
                Undo.RecordObject(importer, "CLI asset bundle");
                importer.assetBundleName = p["name"]?.ToString() ?? "";
                importer.assetBundleVariant = p["variant"]?.ToString() ?? "";
                importer.SaveAndReimport();
                return new SuccessResponse("Asset bundle assignment updated");
            }
            if (action != "build") throw new ArgumentException("Use list, set or build.");
            string relative = p["output"]?.ToString() ?? "Builds/Bundles";
            string output = Path.GetFullPath(Path.Combine(MailboxBridge.Project, relative));
            if (!output.StartsWith(MailboxBridge.Project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be inside this project.");
            Directory.CreateDirectory(output);
            var target = p["target"] == null ? EditorUserBuildSettings.activeBuildTarget : (BuildTarget)Enum.Parse(typeof(BuildTarget), p["target"].ToString(), true);
            var manifest = BuildPipeline.BuildAssetBundles(output, BuildAssetBundleOptions.ChunkBasedCompression, target);
            if (manifest == null) throw new InvalidOperationException("AssetBundle build failed; inspect Console.");
            return new SuccessResponse("Asset bundles built", new { output, bundles = manifest.GetAllAssetBundles() });
        }
    }

    [CliCommand("editor_shutdown", "Close an editor explicitly; refuse unsaved scenes unless discard is true", Group = "cli")]
    public static class ShutdownCommand
    {
        public static object HandleCommand(JObject p)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty && p["discard"]?.Value<bool>() != true)
                    throw new InvalidOperationException("Unsaved scenes exist; save them or pass discard=true explicitly.");
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
            return new SuccessResponse("Editor shutdown scheduled");
        }
    }
}
