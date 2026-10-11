using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityCompatVendor.Editor.Tools;
using UnityCompatVendor.Editor.Resources;

namespace UnityCompat.Editor
{
    internal sealed class CommandEntry
    {
        public string Name, Description, Group, Kind;
        public bool Polling;
        public bool Typed, MainThreadRequired = true;
        public MethodInfo Method;
        public object Describe() => new { name = Name, description = Description, group = Group,
            kind = Kind, asynchronous = typeof(Task).IsAssignableFrom(Method.ReturnType),
            requiresPolling = Polling, mainThreadRequired = MainThreadRequired,
            inputSchema = Schema(), declaringType = Method.DeclaringType.FullName };
        internal JObject Schema()
        {
            if (!Typed && QuerySchemas.Value.TryGetValue(Name, out var query)) return (JObject)query.DeepClone();
            var properties = new JObject(); var required = new JArray();
            if (Typed) foreach (var p in Method.GetParameters())
            {
                var a = p.GetCustomAttribute<CliArgAttribute>(); string name = a?.Name ?? p.Name;
                var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                var s = new JObject { ["type"] = type == typeof(bool) ? "boolean" : type == typeof(string) || type.IsEnum ? "string" :
                    type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" :
                    type.IsPrimitive ? "integer" : type.IsArray || type == typeof(JArray) ? "array" : "object" };
                if (type == typeof(JToken)) s.Remove("type");
                if (type.IsEnum) s["enum"] = new JArray(Enum.GetNames(type));
                if (type.IsArray || type == typeof(JArray)) s["items"] = new JObject();
                if (Nullable.GetUnderlyingType(p.ParameterType) != null || (p.HasDefaultValue && p.DefaultValue == null && type != typeof(JToken)))
                    s["type"] = new JArray(s["type"], "null");
                if (!String.IsNullOrEmpty(a?.Description)) s["description"] = a.Description;
                if (p.HasDefaultValue && p.DefaultValue != null) s["default"] = JToken.FromObject(p.DefaultValue);
                properties[name] = s;
                if (a?.Required == true || (!p.HasDefaultValue && a?.DefaultValue == null)) required.Add(name);
            }
            return new JObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = required, ["additionalProperties"] = !Typed };
        }
        static readonly Lazy<JObject> QuerySchemas = new Lazy<JObject>(() => JObject.Parse(System.IO.File.ReadAllText(
            System.IO.Path.Combine(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CommandEntry).Assembly).resolvedPath, "agent-schemas.json"))));
    }

    internal static class CliRegistry
    {
        internal static readonly Dictionary<string, CommandEntry> Commands = new Dictionary<string, CommandEntry>();
        static CliRegistry()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var type in types)
                {
                    try
                    {
                        foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        {
                            var attr = candidate.GetCustomAttribute<CliCommandAttribute>();
                            if (attr == null || attr.RuntimeOnly) continue;
                            if (candidate.ContainsGenericParameters || candidate.GetParameters().Any(p => p.ParameterType.IsByRef))
                                throw new ArgumentException("Generic/ref/out CLI methods are unsupported: " + candidate.Name);
                            Register(new CommandEntry { Name = attr.Name, Description = attr.Description, Group = attr.Group,
                                Kind = "extension", Method = candidate, Typed = true, MainThreadRequired = attr.MainThreadRequired });
                        }
                        var local = type.GetCustomAttribute<CliCommandAttribute>();
                        var tool = type.GetCustomAttribute<McpForUnityToolAttribute>();
                        var resource = type.GetCustomAttribute<McpForUnityResourceAttribute>();
                        if (local == null && tool == null && resource == null) continue;
                        var method = type.GetMethod("HandleCommand", BindingFlags.Public | BindingFlags.Static,
                            null, new[] { typeof(JObject) }, null);
                        if (method == null) continue;
                        var name = local?.Name ?? tool?.Name ?? resource?.ResourceName;
                        if (String.IsNullOrWhiteSpace(name)) continue;
                        Register(new CommandEntry { Name = name, Method = method,
                            Description = local?.Description ?? tool?.Description ?? resource?.Description ?? name,
                            Group = local?.Group ?? tool?.Group ?? "resources",
                            Kind = local != null ? "extension" : (tool != null ? "tool" : "resource"),
                            Polling = tool?.RequiresPolling ?? false });
                    }
                    catch (Exception e) { UnityEngine.Debug.LogWarning("[UnityCompat] Command discovery: " + e.Message); }
                }
            }
        }

        static void Register(CommandEntry entry)
        {
            if (String.IsNullOrWhiteSpace(entry.Name) || Commands.ContainsKey(entry.Name))
                throw new InvalidOperationException("Empty or duplicate CLI command: " + entry.Name);
            Commands.Add(entry.Name, entry);
        }

        static object[] Bind(CommandEntry entry, JObject parameters)
        {
            if (!entry.Typed) return new object[] { parameters };
            var ps = entry.Method.GetParameters(); var values = new object[ps.Length];
            var names = ps.Select(p => p.GetCustomAttribute<CliArgAttribute>()?.Name ?? p.Name).ToArray();
            foreach (var key in parameters.Properties()) if (!names.Contains(key.Name))
                throw new CliFault("INVALID_ARGUMENT", "Unknown argument: " + key.Name);
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i]; var attr = p.GetCustomAttribute<CliArgAttribute>();
                if (!parameters.TryGetValue(names[i], out var token))
                {
                    if (attr?.Required == true || (!p.HasDefaultValue && attr?.DefaultValue == null))
                        throw new CliFault("MISSING_ARGUMENT", "Required argument: " + names[i]);
                    values[i] = attr?.DefaultValue ?? p.DefaultValue; continue;
                }
                try { values[i] = token.ToObject(p.ParameterType, UnityCompatVendor.Editor.Helpers.UnityJsonSerializer.Instance); }
                catch { throw new CliFault("INVALID_ARGUMENT", "Invalid value for " + names[i] + "; expected " + p.ParameterType.Name); }
            }
            return values;
        }

        internal static async Task<JToken> Invoke(string name, JObject parameters)
        {
            if (!Commands.TryGetValue(name, out var command))
                throw new CliFault("UNKNOWN_COMMAND", "Unknown command: " + name + ". Run ucl list.");
            object value;
            var args = Bind(command, parameters ?? new JObject());
            try { value = command.MainThreadRequired ? command.Method.Invoke(null, args) :
                await Task.Run(() => command.Method.Invoke(null, args)); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            if (value is Task task)
            {
                await task;
                MailboxBridge.CheckActive();
                var prop = task.GetType().GetProperty("Result");
                value = prop?.GetValue(task);
            }
            return value == null ? JValue.CreateNull() : (value is JToken token ? token :
                JToken.FromObject(value, UnityCompatVendor.Editor.Helpers.UnityJsonSerializer.Instance));
        }

        internal static bool Succeeded(JToken result)
        {
            if (!(result is JObject obj)) return true;
            return obj["success"]?.Value<bool?>() != false && obj["status"]?.ToString() != "error";
        }
    }
}
