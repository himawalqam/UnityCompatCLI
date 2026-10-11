using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityCompat.Compiler.Microsoft.CodeAnalysis;
using UnityCompat.Compiler.Microsoft.CodeAnalysis.CSharp;
using UnityEditor;
using UnityEngine;

namespace UnityCompat.Editor
{
    [CliCommand("eval", "Trusted local C# with in-memory Roslyn, cached code and no project/domain reload", Group = "scripting")]
    public static class FastEval
    {
        // Loaded dynamic assemblies cannot be unloaded individually in Unity Mono. Bound
        // unique compilations per domain, not just cache entries, to bound accumulation.
        const int MaxCompilations = 256;
        static readonly Dictionary<string, MethodInfo> cache = new Dictionary<string, MethodInfo>();
        static MetadataReference[] references;
        internal static MetadataReference[] References()
        {
            if (references != null) return references;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                try { if (!assembly.IsDynamic && !String.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location)) paths.Add(assembly.Location); } catch { }
            // Unity engine modules may not have been loaded by the user's project yet.
            string managed = Path.Combine(EditorApplication.applicationContentsPath, "Managed");
            foreach (string folder in new[] { Path.Combine(managed, "UnityEngine"), Path.Combine(managed, "UnityEditor") })
                if (Directory.Exists(folder)) foreach (var file in Directory.GetFiles(folder, "*.dll")) paths.Add(file);
            references = paths.Where(p => !Path.GetFileName(p).StartsWith("UnityCompat.Microsoft.", StringComparison.Ordinal))
                .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
            return references;
        }
        public static object HandleCommand(JObject p)
        {
            string code = p["code"]?.ToString();
            if (String.IsNullOrWhiteSpace(code) || code.Length > 50000) throw new CliFault("INVALID_ARGUMENT", "Supply 1..50000 characters of C# method-body code");
            if (p["args"] != null && !(p["args"] is JObject)) throw new CliFault("INVALID_ARGUMENT", "args must be an object");
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(code))).Replace("-", "");
            var watch = System.Diagnostics.Stopwatch.StartNew(); bool hit = cache.TryGetValue(key, out var method);
            if (!hit)
            {
                if (cache.Count >= MaxCompilations) throw new CliFault("EVAL_CACHE_LIMIT", "256 unique snippets are already loaded. Reuse code with args, or explicitly reload the Editor domain.");
                string source = "using System; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor;\n" +
                    "public static class UclEval { public static object Run(Newtonsoft.Json.Linq.JObject args) {\n#line 1 \"ucl-eval.cs\"\n" +
                    code + "\n#line default\nreturn null; } }";
                var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp9));
                var compilation = CSharpCompilation.Create("UnityCompat.Eval." + key, new[] { tree }, References(),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: false));
                using (var buffer = new MemoryStream())
                {
                    var emitted = compilation.Emit(buffer);
                    if (!emitted.Success)
                    {
                        var diagnostics = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(30).Select(d => {
                            var span = d.Location.GetMappedLineSpan(); return new { code = d.Id, message = d.GetMessage(), line = span.StartLinePosition.Line + 1, column = span.StartLinePosition.Character + 1 };
                        }).ToArray();
                        return new JObject { ["success"] = false, ["error"] = new JObject { ["code"] = "COMPILATION_FAILED", ["message"] = "C# compilation failed", ["diagnostics"] = JArray.FromObject(diagnostics) } };
                    }
                    method = Assembly.Load(buffer.ToArray()).GetType("UclEval").GetMethod("Run"); cache.Add(key, method);
                }
            }
            double compiledMs = watch.Elapsed.TotalMilliseconds;
            var logs = new List<string>(); int logBytes = 0;
            Application.LogCallback capture = (message, stack, type) => { if (logs.Count < 64 && logBytes < 8192) {logs.Add(message);logBytes += message.Length;} };
            Application.logMessageReceived += capture;
            try
            {
                MailboxBridge.CheckActive(); object result;
                try { result = method.Invoke(null, new object[] { p["args"] as JObject ?? new JObject() }); }
                catch (TargetInvocationException ex) { throw new CliFault("EVAL_RUNTIME_ERROR", ex.InnerException?.Message ?? ex.Message); }
                return new JObject { ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result, UnityCompatVendor.Editor.Helpers.UnityJsonSerializer.Instance),
                    ["compiler"] = "roslyn-3.8-relocated", ["cacheHit"] = hit, ["compileMs"] = compiledMs,
                    ["elapsedMs"] = watch.Elapsed.TotalMilliseconds, ["logs"] = new JArray(logs), ["session"] = MailboxBridge.Session,
                    ["trust"] = "local-editor-full-trust", ["uniqueSnippets"] = cache.Count };
            }
            finally { Application.logMessageReceived -= capture; }
        }
    }
}
