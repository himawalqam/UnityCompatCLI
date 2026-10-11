using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityCompatVendor.Editor.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityCompat.Editor
{
    internal static class Args
    {
        internal static string Required(JObject p, string key)
        {
            string value = p[key]?.ToString();
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException("Required parameter: " + key);
            return value;
        }
        internal static GameObject Object(JObject p)
        {
            var token = p["target"];
            GameObject result = null;
            if (token?.Type == JTokenType.Integer) result = EditorUtility.InstanceIDToObject(token.Value<int>()) as GameObject;
            else if (token != null)
            {
                var target = token.ToString();
                var found = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => !EditorUtility.IsPersistent(g) &&
                    g.scene.IsValid() && (PathOf(g.transform) == target || g.name == target)).ToArray();
                if (found.Length > 1) throw new CliFault("AMBIGUOUS_TARGET", "Ambiguous target; use an instance ID or full hierarchy path.");
                result = found.FirstOrDefault();
            }
            if (result == null) throw new CliFault("TARGET_NOT_FOUND", "GameObject target not found: " + token);
            return result;
        }
        internal static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;
        internal static object Summary(GameObject go) => new { id = go.GetInstanceID(), path = PathOf(go.transform),
            scene = go.scene.path, active = go.activeSelf, tag = go.tag, layer = go.layer,
            position = new[] { go.transform.position.x, go.transform.position.y, go.transform.position.z } };
        internal static Vector3 Vector(JToken value)
        {
            var a = value as JArray;
            if (a == null || a.Count != 3) throw new ArgumentException("A vector requires [x,y,z].");
            return new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>());
        }
    }

    [CliCommand("project_settings", "Get/set public PlayerSettings, QualitySettings, EditorSettings, Physics, Time and RenderSettings properties", Group = "project")]
    public static class ProjectSettingsCommand
    {
        static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase) {
            {"player", typeof(PlayerSettings)}, {"quality", typeof(QualitySettings)}, {"editor", typeof(EditorSettings)},
            {"physics", typeof(Physics)}, {"physics2d", typeof(Physics2D)}, {"time", typeof(Time)}, {"render", typeof(RenderSettings)} };
        public static object HandleCommand(JObject p)
        {
            if (!Types.TryGetValue(p["section"]?.ToString() ?? "player", out var type)) throw new ArgumentException("Unknown settings section.");
            string action = p["action"]?.ToString() ?? "list";
            var props = type.GetProperties(BindingFlags.Static | BindingFlags.Public).Where(x => x.GetIndexParameters().Length == 0);
            if (action == "list") return new SuccessResponse("Settings properties", props.Select(x => new {
                name = x.Name, type = x.PropertyType.FullName, writable = x.SetMethod?.IsPublic == true }).ToArray());
            var prop = props.FirstOrDefault(x => x.Name == Args.Required(p, "property"));
            if (prop == null) throw new ArgumentException("Property not found.");
            if (action == "get")
            {
                var value = prop.GetValue(null);
                return new SuccessResponse("Setting value", value is Enum ? value.ToString() : value);
            }
            if (action != "set" || prop.SetMethod?.IsPublic != true) throw new ArgumentException("Property is read-only or action is invalid.");
            if (p["value"] == null) throw new ArgumentException("Required parameter: value");
            object next = prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, p["value"].ToString(), true) :
                p["value"].ToObject(prop.PropertyType, UnityJsonSerializer.Instance);
            prop.SetValue(null, next);
            AssetDatabase.SaveAssets();
            if (type == typeof(RenderSettings)) EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            return new SuccessResponse("Setting updated", new { property = prop.Name, value = p["value"] });
        }
    }

    [CliCommand("asset_references", "Inspect forward dependencies or find assets referring to an asset/GUID", Group = "project")]
    public static class AssetReferencesCommand
    {
        public static object HandleCommand(JObject p)
        {
            string path = Args.Required(p, "path");
            if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/")) path = AssetDatabase.GUIDToAssetPath(path);
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) throw new ArgumentException("Asset not found.");
            string action = p["action"]?.ToString() ?? "dependencies";
            if (action == "dependencies") return new SuccessResponse("Asset dependencies", AssetDatabase.GetDependencies(path, p["recursive"]?.Value<bool>() ?? true));
            if (action != "referencers") throw new ArgumentException("Use dependencies or referencers.");
            int limit = Math.Max(1, Math.Min(p["limit"]?.Value<int>() ?? 1000, 10000));
            var all = AssetDatabase.GetAllAssetPaths().Where(x => x.StartsWith("Assets/") && !AssetDatabase.IsValidFolder(x)).ToArray();
            var refs = all.Take(limit).Where(x => x != path && AssetDatabase.GetDependencies(x, false).Contains(path)).ToArray();
            return new SuccessResponse("Reverse references", new { path, references = refs, scanned = Math.Min(limit, all.Length), truncated = all.Length > limit });
        }
    }

    [CliCommand("project_audit", "Audit loaded scenes for missing scripts/references; inspect duplicate GUIDs and missing .meta files", Group = "project")]
    public static class ProjectAuditCommand
    {
        public static object HandleCommand(JObject p)
        {
            string action = p["action"]?.ToString() ?? "scene";
            if (action == "assets")
            {
                int limit = Math.Max(1, Math.Min(p["limit"]?.Value<int>() ?? 5000, 100000));
                var paths = Directory.EnumerateFiles(Application.dataPath, "*", SearchOption.AllDirectories).Take(limit + 1).ToArray();
                var missing = new List<string>();
                var guids = new Dictionary<string, List<string>>();
                foreach (var file in paths.Take(limit))
                {
                    MailboxBridge.CheckActive();
                    if (file.EndsWith(".meta"))
                    {
                        var line = File.ReadLines(file).FirstOrDefault(x => x.StartsWith("guid: "));
                        if (line == null) continue;
                        var guid = line.Substring(6).Trim();
                        if (!guids.TryGetValue(guid, out var list)) guids[guid] = list = new List<string>();
                        list.Add(file.Substring(MailboxBridge.Project.Length + 1).Replace('\\', '/'));
                    }
                    else if (!Path.GetFileName(file).StartsWith(".") && !File.Exists(file + ".meta"))
                        missing.Add(file.Substring(MailboxBridge.Project.Length + 1).Replace('\\', '/'));
                }
                return new SuccessResponse("Asset audit", new { missingMeta = missing, duplicateGuids = guids.Where(x => x.Value.Count > 1)
                    .Select(x => new { guid = x.Key, paths = x.Value }).ToArray(), scanned = Math.Min(paths.Length, limit), truncated = paths.Length > limit });
            }
            if (action != "scene") throw new ArgumentException("Use scene or assets.");
            var issues = new JArray();
            int objects = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    objects++;
                    int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                    if (missing > 0) issues.Add(new JObject { ["kind"] = "missing_script", ["path"] = Args.PathOf(t), ["count"] = missing, ["scene"] = scene.path });
                    foreach (var component in t.GetComponents<Component>().Where(c => c != null))
                    {
                        using (var serialized = new SerializedObject(component))
                        {
                            var prop = serialized.GetIterator();
                            while (prop.NextVisible(true))
                                if (prop.propertyType == SerializedPropertyType.ObjectReference && prop.objectReferenceValue == null && prop.objectReferenceInstanceIDValue != 0)
                                    issues.Add(new JObject { ["kind"] = "broken_reference", ["path"] = Args.PathOf(t), ["component"] = component.GetType().FullName, ["property"] = prop.propertyPath, ["scene"] = scene.path });
                        }
                    }
                }
            }
            return new SuccessResponse("Loaded scene audit", new { objects, issues, scenes = SceneManager.sceneCount });
        }
    }

    [CliCommand("serialized_inspect", "Read serialized properties of a GameObject component or asset without lossy YAML editing", Group = "inspection")]
    public static class SerializedInspectCommand
    {
        internal static object Value(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.doubleValue;
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum: return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference: return p.objectReferenceValue == null ? null : new { id = p.objectReferenceValue.GetInstanceID(), name = p.objectReferenceValue.name, asset = AssetDatabase.GetAssetPath(p.objectReferenceValue) };
                case SerializedPropertyType.Vector3: return new[] { p.vector3Value.x, p.vector3Value.y, p.vector3Value.z };
                case SerializedPropertyType.Vector2: return new[] { p.vector2Value.x, p.vector2Value.y };
                case SerializedPropertyType.Color: return new[] { p.colorValue.r, p.colorValue.g, p.colorValue.b, p.colorValue.a };
                default: return null;
            }
        }
        public static object HandleCommand(JObject p)
        {
            Object target;
            if (p["path"] != null) target = AssetDatabase.LoadMainAssetAtPath(p["path"].ToString());
            else
            {
                var go = Args.Object(p);
                target = p["component"] == null ? (Object)go : go.GetComponent(p["component"].ToString());
            }
            if (target == null) throw new ArgumentException("Serialized target not found.");
            var rows = new JArray();
            int limit = Math.Max(1, Math.Min(p["limit"]?.Value<int>() ?? 300, 2000));
            using (var serialized = new SerializedObject(target))
            {
                var prop = serialized.GetIterator();
                bool more = prop.NextVisible(true);
                while (more && rows.Count < limit)
                {
                    if (p["prefix"] == null || prop.propertyPath.StartsWith(p["prefix"].ToString()))
                        rows.Add(JObject.FromObject(new { path = prop.propertyPath, type = prop.propertyType.ToString(), value = Value(prop), editable = prop.editable }));
                    more = prop.NextVisible(true);
                }
                return new SuccessResponse("Serialized properties", new { target = target.GetInstanceID(), properties = rows, truncated = more });
            }
        }
    }

    [CliCommand("asset_importer", "Inspect/set serialized asset importer properties, then safely reimport", Group = "project")]
    public static class AssetImporterCommand
    {
        public static object HandleCommand(JObject p)
        {
            var importer = AssetImporter.GetAtPath(Args.Required(p, "path"));
            if (importer == null) throw new ArgumentException("Asset importer not found.");
            string action = p["action"]?.ToString() ?? "get";
            if (action == "reimport") { importer.SaveAndReimport(); return new SuccessResponse("Reimported"); }
            using (var serialized = new SerializedObject(importer))
            {
                if (action == "get")
                {
                    var rows = new JArray();
                    var prop = serialized.GetIterator();
                    while (prop.NextVisible(true)) rows.Add(JObject.FromObject(new { path = prop.propertyPath, type = prop.propertyType.ToString(), value = SerializedInspectCommand.Value(prop) }));
                    return new SuccessResponse("Importer properties", rows);
                }
                if (action != "set") throw new ArgumentException("Use get, set or reimport.");
                var target = serialized.FindProperty(Args.Required(p, "property"));
                if (target == null || !target.editable) throw new ArgumentException("Editable serialized property not found.");
                var value = p["value"] ?? throw new ArgumentException("Required parameter: value");
                Undo.RecordObject(importer, "CLI importer change");
                switch (target.propertyType)
                {
                    case SerializedPropertyType.Integer: target.intValue = value.Value<int>(); break;
                    case SerializedPropertyType.Boolean: target.boolValue = value.Value<bool>(); break;
                    case SerializedPropertyType.Float: target.floatValue = value.Value<float>(); break;
                    case SerializedPropertyType.String: target.stringValue = value.ToString(); break;
                    case SerializedPropertyType.Enum:
                        int index = value.Type == JTokenType.Integer ? value.Value<int>() : Array.IndexOf(target.enumNames, value.ToString());
                        if (index < 0 || index >= target.enumNames.Length) throw new ArgumentException("Invalid enum value.");
                        target.enumValueIndex = index; break;
                    default: throw new ArgumentException("This property type is read-only through this command.");
                }
                serialized.ApplyModifiedProperties();
                importer.SaveAndReimport();
            }
            return new SuccessResponse("Importer setting updated");
        }
    }

    [CliCommand("undo_history", "Perform editor undo/redo or create and collapse named undo groups", Group = "editing")]
    public static class UndoCommand
    {
        public static object HandleCommand(JObject p)
        {
            switch (p["action"]?.ToString() ?? "status")
            {
                case "undo": Undo.PerformUndo(); break;
                case "redo": Undo.PerformRedo(); break;
                case "begin": Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName(p["name"]?.ToString() ?? "CLI changes"); break;
                case "collapse": Undo.CollapseUndoOperations(p["group"]?.Value<int>() ?? Undo.GetCurrentGroup()); break;
                case "status": break;
                default: throw new ArgumentException("Use status, undo, redo, begin or collapse.");
            }
            return new SuccessResponse("Undo operation completed", new { group = Undo.GetCurrentGroup(), name = Undo.GetCurrentGroupName() });
        }
    }

    [CliCommand("manage_selection", "Read, set or frame editor selection, including inactive scene objects", Group = "editing")]
    public static class SelectionCommand
    {
        public static object HandleCommand(JObject p)
        {
            string action = p["action"]?.ToString() ?? "get";
            if (action == "set" || action == "frame") Selection.activeGameObject = Args.Object(p);
            else if (action == "clear") Selection.objects = new Object[0];
            else if (action != "get") throw new ArgumentException("Use get, set, frame or clear.");
            if (action == "frame" && SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
            return new SuccessResponse("Editor selection", Selection.gameObjects.Select(Args.Summary).ToArray());
        }
    }

    [CliCommand("scene_snapshot", "Capture scene object transforms and components for comparison or automation", Group = "inspection")]
    public static class SceneSnapshotCommand
    {
        public static object HandleCommand(JObject p)
        {
            int limit = Math.Max(1, Math.Min(p["limit"]?.Value<int>() ?? 1000, 10000));
            var all = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => !EditorUtility.IsPersistent(g) && g.scene.IsValid()).ToArray();
            var objects = all.OrderBy(g => g.scene.path).ThenBy(g => Args.PathOf(g.transform)).Take(limit).Select(g => new {
                item = Args.Summary(g), rotation = new[] { g.transform.eulerAngles.x, g.transform.eulerAngles.y, g.transform.eulerAngles.z },
                scale = new[] { g.transform.localScale.x, g.transform.localScale.y, g.transform.localScale.z },
                components = g.GetComponents<Component>().Select(c => c == null ? "<missing>" : c.GetType().FullName).ToArray() }).ToArray();
            return new SuccessResponse("Scene snapshot", new { objects, total = all.Length, truncated = all.Length > limit });
        }
    }

    [CliCommand("type_query", "Discover loaded types, component types and public members for reflection-driven tooling", Group = "inspection")]
    public static class TypeQueryCommand
    {
        public static object HandleCommand(JObject p)
        {
            string query = p["query"]?.ToString() ?? "";
            int limit = Math.Max(1, Math.Min(p["limit"]?.Value<int>() ?? 100, 1000));
            var types = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).SelectMany(a => {
                try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); } catch { return new Type[0]; } });
            if (p["components_only"]?.Value<bool>() == true) types = types.Where(t => typeof(Component).IsAssignableFrom(t));
            var matches = types.Where(t => t.FullName != null && t.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Take(limit + 1).ToArray();
            return new SuccessResponse("Types", new { types = matches.Take(limit).Select(t => new {
                name = t.FullName, assembly = t.Assembly.GetName().Name,
                members = p["members"]?.Value<bool>() == true ? t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Select(m => m.Name).Distinct().ToArray() : null }).ToArray(), truncated = matches.Length > limit });
        }
    }
}
