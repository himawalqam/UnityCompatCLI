using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityCompat.Editor
{
    [CliCommand("agent_hierarchy", "Bounded hierarchy with depth, subtree and component filters", Group = "agent")]
    public static class AgentHierarchy
    {
        public static object HandleCommand(JObject p)
        {
            int depth = Math.Max(0, Math.Min(64, p["depth"]?.Value<int>() ?? 2));
            int limit = Math.Max(1, Math.Min(10000, p["limit"]?.Value<int>() ?? 80));
            var queue = new Queue<Tuple<Transform, int>>();
            if (p["root"] != null) queue.Enqueue(Tuple.Create(Args.Object(new JObject { ["target"] = p["root"].DeepClone() }).transform, 0));
            else for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isLoaded)
                    foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects()) queue.Enqueue(Tuple.Create(root.transform, 0));
            var nodes = new JArray();
            bool limited = false;
            string[] columns = p["fields"] == null ? null : p["fields"] is JArray ca ? ca.Select(x => x.ToString()).ToArray() : p["fields"].ToString().Split(',').Select(x=>x.Trim()).ToArray();
            var valid = new[] { "id", "parent", "depth", "name", "active", "children", "components" };
            if (columns != null && (columns.Length == 0 || columns.Any(c => !valid.Contains(c))))
                throw new CliFault("INVALID_ARGUMENT", "Hierarchy fields: " + String.Join(",", valid));
            while (queue.Count > 0 && nodes.Count < limit)
            {
                var entry = queue.Dequeue();
                var t = entry.Item1;
                if (entry.Item2 == depth && t.childCount > 0) limited = true;
                var row = new JObject { ["id"] = t.gameObject.GetInstanceID(), ["parent"] = t.parent == null ? 0 : t.parent.gameObject.GetInstanceID(),
                    ["depth"] = entry.Item2, ["name"] = t.name, ["active"] = t.gameObject.activeSelf, ["children"] = t.childCount };
                if (p["components"]?.Value<bool>() == true || columns?.Contains("components") == true) row["components"] = new JArray(t.GetComponents<Component>().Select(c => c == null ? "<missing>" : c.GetType().FullName));
                nodes.Add(columns == null ? row : new JObject(columns.Select(c => new JProperty(c, row[c]))));
                if (entry.Item2 < depth) foreach (Transform child in t) queue.Enqueue(Tuple.Create(child, entry.Item2 + 1));
            }
            return new JObject { ["nodes"] = nodes, ["columns"] = columns == null ? null : new JArray(columns), ["depth"] = depth, ["truncated"] = queue.Count > 0,
                ["depthLimited"] = limited };
        }
    }

    [CliCommand("agent_find", "Find scene objects by name/path and component type, with bounded output", Group = "agent")]
    public static class AgentFind
    {
        public static object HandleCommand(JObject p)
        {
            string query = p["query"]?.ToString() ?? "";
            string type = p["type"]?.ToString();
            int limit = Math.Max(1, Math.Min(10000, p["limit"]?.Value<int>() ?? 30));
            var results = new JArray();
            int matches = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isLoaded)
                    foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                        foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        {
                            var path = Args.PathOf(t);
                            if (path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 ||
                                (!String.IsNullOrEmpty(type) && t.GetComponent(type) == null)) continue;
                            matches++;
                            if (results.Count < limit) results.Add(new JObject { ["id"] = t.gameObject.GetInstanceID(), ["path"] = path });
                        }
            return new JObject { ["matches"] = matches, ["objects"] = results, ["truncated"] = matches > limit };
        }
    }

    [CliCommand("agent_inspect", "Read only requested serialized fields; list components when no component is specified", Group = "agent")]
    public static class AgentInspect
    {
        internal static Object Target(JObject p)
        {
            if (p["path"] != null) return AssetDatabase.LoadMainAssetAtPath(p["path"].ToString());
            var go = Args.Object(p);
            return p["component"] == null ? go : (Object)go.GetComponent(p["component"].ToString());
        }
        internal static object CompactValue(SerializedProperty p)
        {
            if (p.isArray && p.propertyType != SerializedPropertyType.String) return new { count = p.arraySize };
            if (p.propertyType == SerializedPropertyType.Generic) return new { type = p.type };
            return SerializedInspectCommand.Value(p);
        }
        public static object HandleCommand(JObject p)
        {
            var target = Target(p);
            if (target == null) throw new CliFault(p["component"] == null ? "TARGET_NOT_FOUND" : "COMPONENT_NOT_FOUND", "Inspector target/component not found.");
            if (target is GameObject go && p["component"] == null && p["fields"] == null)
                return new JObject { ["id"] = go.GetInstanceID(), ["path"] = Args.PathOf(go.transform),
                    ["components"] = new JArray(go.GetComponents<Component>().Select(c => c == null ? "<missing>" : c.GetType().FullName)) };
            int limit = Math.Max(1, Math.Min(2000, p["limit"]?.Value<int>() ?? 80));
            var fields = new JObject();
            bool truncated = false;
            int maxDepth = Math.Max(0, Math.Min(64, p["max_depth"]?.Value<int>() ?? 64));
            using (var serialized = new SerializedObject(target))
            {
                if (p["fields"] != null)
                {
                    var requested = p["fields"] is JArray a ? a.Select(x => x.ToString()) : p["fields"].ToString().Split(',').Select(x => x.Trim());
                    foreach (var name in requested)
                    {
                        if (fields.Count >= limit) { truncated = true; break; }
                        if (name.EndsWith(".*", StringComparison.Ordinal))
                        {
                            string prefix = name.Substring(0, name.Length - 1);
                            var parent = serialized.FindProperty(name.Substring(0, name.Length - 2));
                            if (parent == null) throw new CliFault("PROPERTY_NOT_FOUND", "Serialized property not found: " + name);
                            var child = parent.Copy(); bool next = child.NextVisible(true);
                            while (next && child.propertyPath.StartsWith(prefix, StringComparison.Ordinal))
                            {
                                if (fields.Count >= limit) { truncated = true; break; }
                                if (child.depth - parent.depth <= maxDepth) fields[child.propertyPath] = Value(child, p);
                                next = child.NextVisible(child.depth - parent.depth < maxDepth);
                            }
                            continue;
                        }
                        var prop = serialized.FindProperty(name);
                        if (prop == null) throw new CliFault("PROPERTY_NOT_FOUND", "Serialized property not found: " + name);
                        fields[name] = Value(prop, p);
                    }
                }
                else
                {
                    var prop = serialized.GetIterator();
                    // Top-level fields by default. Explicit --fields supports arbitrary nested paths.
                    bool more = prop.NextVisible(true);
                    while (more)
                    {
                        if (fields.Count >= limit) { truncated = true; break; }
                        fields[prop.propertyPath] = Value(prop, p);
                        more = prop.NextVisible(p["all"]?.Value<bool>() == true && prop.depth < maxDepth);
                    }
                }
            }
            return new JObject { ["id"] = target.GetInstanceID(), ["type"] = target.GetType().FullName, ["fields"] = fields, ["truncated"] = truncated };
        }
        static JToken Value(SerializedProperty prop, JObject p)
        {
            var raw = CompactValue(prop);
            JToken value = raw == null ? JValue.CreateNull() : JToken.FromObject(raw);
            return p["types"]?.Value<bool>() == true ? new JObject { ["type"] = prop.propertyType.ToString(), ["value"] = value } : value;
        }
    }

    [CliCommand("agent_set", "Set a serialized component/asset field with Undo and precondition checking", Group = "agent")]
    public static class AgentSet
    {
        static bool Equivalent(JToken before, JToken expected)
        {
            if ((before.Type == JTokenType.Float || before.Type == JTokenType.Integer) &&
                (expected.Type == JTokenType.Float || expected.Type == JTokenType.Integer))
                return before.Value<double>() == expected.Value<double>();
            if (before is JArray a && expected is JArray b)
                return a.Count == b.Count && a.Zip(b, Equivalent).All(x => x);
            return JToken.DeepEquals(before, expected);
        }
        public static object HandleCommand(JObject p)
        {
            var target = AgentInspect.Target(p);
            if (target == null) throw new ArgumentException("Serialized target/component not found.");
            string name = Args.Required(p, "property");
            var value = p["value"] ?? throw new ArgumentException("Required parameter: value");
            using (var serialized = new SerializedObject(target))
            {
                var prop = serialized.FindProperty(name);
                if (prop == null || !prop.editable) throw new CliFault("PROPERTY_NOT_EDITABLE", "Editable serialized property not found: " + name);
                var before = AgentInspect.CompactValue(prop);
                var expected = p["expect"];
                if (expected != null && prop.propertyType == SerializedPropertyType.Float && prop.type != "double") expected = new JValue((double)expected.Value<float>());
                if (expected != null && !Equivalent(before == null ? JValue.CreateNull() : JToken.FromObject(before), expected))
                    throw new CliFault("PRECONDITION_FAILED", "Field value changed; no edit was applied.");
                // Validate/convert before recording Undo or applying any change.
                Action change;
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer: var integer = value.Value<long>(); change = () => prop.longValue = integer; break;
                    case SerializedPropertyType.Boolean: var boolean = value.Value<bool>(); change = () => prop.boolValue = boolean; break;
                    case SerializedPropertyType.Float: var number = value.Value<double>(); change = () => prop.doubleValue = number; break;
                    case SerializedPropertyType.String: var text = value.ToString(); change = () => prop.stringValue = text; break;
                    case SerializedPropertyType.Enum:
                        int index = value.Type == JTokenType.Integer ? value.Value<int>() : Array.IndexOf(prop.enumNames, value.ToString());
                        if (index < 0 || index >= prop.enumNames.Length) throw new ArgumentException("Invalid enum value.");
                        change = () => prop.enumValueIndex = index; break;
                    case SerializedPropertyType.Vector3: var vector = Args.Vector(value); change = () => prop.vector3Value = vector; break;
                    case SerializedPropertyType.Vector2:
                        if (!(value is JArray pair) || pair.Count != 2) throw new ArgumentException("Vector2 requires two numbers.");
                        var vector2 = new Vector2(pair[0].Value<float>(), pair[1].Value<float>()); change = () => prop.vector2Value = vector2; break;
                    case SerializedPropertyType.ObjectReference:
                        Object reference = null;
                        if (value.Type != JTokenType.Null)
                        {
                            if (value.Type == JTokenType.Integer) reference = EditorUtility.InstanceIDToObject(value.Value<int>());
                            else reference = AssetDatabase.LoadMainAssetAtPath(value.ToString());
                            if (reference == null) throw new ArgumentException("Reference target not found.");
                            // SerializedObject's assignment verifies the expected reference type.
                        }
                        change = () => prop.objectReferenceValue = reference; break;
                    default: throw new ArgumentException("Unsupported serialized type: " + prop.propertyType);
                }
                Undo.RecordObject(target, "CLI set " + name);
                change();
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(target);
                var component = target as Component;
                if (component != null)
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                    EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                }
                return new JObject { ["id"] = target.GetInstanceID(), ["property"] = name,
                    ["before"] = before == null ? JValue.CreateNull() : JToken.FromObject(before),
                    ["after"] = JToken.FromObject(AgentInspect.CompactValue(prop) ?? (object)JValue.CreateNull()) };
            }
        }
    }

    [CliCommand("agent_snapshot", "Read hierarchy, windows, selection and Editor state in a single round trip", Group = "agent")]
    public static class AgentSnapshot
    {
        static JToken Data(JToken result) => result is JObject o && o["success"] != null && o["data"] != null ? o["data"].DeepClone() : result;
        public static async System.Threading.Tasks.Task<object> HandleCommand(JObject p)
        {
            return new JObject { ["hierarchy"] = JToken.FromObject(AgentHierarchy.HandleCommand(p)),
                ["windows"] = Data(await CliRegistry.Invoke("get_windows", new JObject())),
                ["selection"] = Data(await CliRegistry.Invoke("get_selection", new JObject())),
                ["editor"] = new JObject { ["playing"] = EditorApplication.isPlaying, ["compiling"] = EditorApplication.isCompiling,
                    ["scene"] = SceneManager.GetActiveScene().path, ["dirty"] = SceneManager.GetActiveScene().isDirty } };
        }
    }
}
