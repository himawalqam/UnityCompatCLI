using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityCompat.Compiler.Microsoft.CodeAnalysis;
using UnityCompat.Compiler.Microsoft.CodeAnalysis.CSharp;
using UnityCompatVendor.Editor.Tools;

namespace UnityCompat.Editor
{
    // Replace local Python-server helpers through the same authenticated Editor endpoint.
    public static class ServerCommands
    {
        internal static string FilePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new CliFault("INVALID_PATH", "Supply an Assets path or script resource URI");
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile) value = uri.LocalPath;
                else if (uri.Scheme == "mcpforunity" && uri.Host == "path") value = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                else throw new CliFault("INVALID_PATH", "Unsupported URI scheme/host");
            }
            string path = Path.GetFullPath(Path.Combine(MailboxBridge.Project, value));
            string assets = Path.Combine(MailboxBridge.Project, "Assets") + Path.DirectorySeparatorChar;
            if (!path.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) throw new CliFault("INVALID_PATH", "File must be under this project's Assets");
            for (var d = new DirectoryInfo(Path.GetDirectoryName(path)); d != null && d.FullName.Length >= assets.Length - 1; d = d.Parent)
                if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFault("INVALID_PATH", "Junction/symlink file paths are unsupported");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CliFault("INVALID_PATH", "Symlink files are unsupported");
            return path;
        }
        static JObject ScriptParams(JObject p, string action)
        {
            string full = FilePath(p["uri"]?.ToString() ?? p["path"]?.ToString());
            if (!full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) throw new CliFault("INVALID_PATH", "Script operations require a .cs file");
            var result = (JObject)p.DeepClone(); result["action"] = action;
            result["name"] = Path.GetFileNameWithoutExtension(full);
            result["path"] = Path.GetDirectoryName(full).Substring(MailboxBridge.Project.Length + 1).Replace('\\', '/');
            if (result["script_type"] != null) result["scriptType"] = result["script_type"];
            return result;
        }
        [CliCommand("execute_custom_tool", "Call a discovered project command by name", Group = "cli")]
        public static async Task<object> Custom([CliArg("tool_name", Required = true)] string toolName, [CliArg("parameters")] JObject parameters = null)
        {
            if (toolName == "execute_custom_tool") throw new CliFault("INVALID_ARGUMENT", "Nested execute_custom_tool is unsupported");
            return await CliRegistry.Invoke(toolName, parameters ?? new JObject());
        }
        [CliCommand("get_sha", "Read a script SHA with the same text convention as apply_text_edits", Group = "scripting")]
        public static object Sha([CliArg("uri", Required = true)] string uri) => ManageScript.HandleCommand(ScriptParams(new JObject { ["uri"] = uri }, "get_sha"));
        [CliCommand("create_script", "Create a C# script from plain text; preserve existing files", Group = "scripting")]
        public static object Create([CliArg("path", Required = true)] string path, [CliArg("contents", Required = true)] string contents,
            [CliArg("script_type")] string scriptType = null, [CliArg("namespace")] string namespaceName = null)
        {
            if (File.Exists(FilePath(path))) throw new CliFault("PRECONDITION_FAILED", "Script already exists; use an explicit edit");
            return ManageScript.HandleCommand(ScriptParams(new JObject { ["path"] = path, ["contents"] = contents, ["script_type"] = scriptType, ["namespace"] = namespaceName }, "create"));
        }
        [CliCommand("delete_script", "Delete a script through the Editor asset workflow", Group = "scripting")]
        public static object Delete([CliArg("uri", Required = true)] string uri) => ManageScript.HandleCommand(ScriptParams(new JObject { ["uri"] = uri }, "delete"));
        [CliCommand("apply_text_edits", "Apply 1-indexed script text edits with optional SHA precondition", Group = "scripting")]
        public static object TextEdits([CliArg("uri", Required = true)] string uri, [CliArg("edits", Required = true)] JArray edits,
            [CliArg("precondition_sha256")] string sha = null, [CliArg("strict")] bool? strict = null, [CliArg("options")] JObject options = null)
        {
            if (strict == true && String.IsNullOrWhiteSpace(sha)) throw new CliFault("PRECONDITION_FAILED", "strict=true requires precondition_sha256");
            return ManageScript.HandleCommand(ScriptParams(new JObject { ["uri"] = uri, ["edits"] = edits, ["precondition_sha256"] = sha, ["options"] = options }, "apply_text_edits"));
        }
        [CliCommand("script_apply_edits", "Apply structured class/method/anchor script edits", Group = "scripting")]
        public static object Structured([CliArg("name", Required = true)] string name, [CliArg("path", Required = true)] string path,
            [CliArg("edits", Required = true)] JToken edits, [CliArg("options")] JObject options = null,
            [CliArg("script_type")] string scriptType = "MonoBehaviour", [CliArg("namespace")] string namespaceName = null)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) path = path.TrimEnd('/', '\\') + "/" + name + ".cs";
            var array = edits is JArray a ? a : JArray.Parse(edits.ToString());
            return ManageScript.HandleCommand(ScriptParams(new JObject { ["path"] = path, ["edits"] = array, ["options"] = options, ["script_type"] = scriptType, ["namespace"] = namespaceName }, "edit"));
        }
        [CliCommand("manage_script_capabilities", "Describe script editing and Roslyn validation support", Group = "scripting")]
        public static object Capabilities() => new { compiler = "roslyn-3.8-relocated", language = "CSharp9", textEdits = "1-indexed startLine/startCol/endLine/endCol/newText",
            structuredEdits = new[] { "replace_class", "delete_class", "replace_method", "delete_method", "insert_method", "anchor_insert", "anchor_delete", "anchor_replace" },
            preconditions = "Use get_sha for apply_text_edits; file sha uses raw bytes and is a separate contract" };
        [CliCommand("validate_script", "Roslyn syntax/semantic diagnostics without changing or loading the script", Group = "scripting")]
        public static object Validate([CliArg("uri", Required = true)] string uri, [CliArg("level")] string level = "standard", [CliArg("include_diagnostics")] bool includeDiagnostics = true)
        {
            if (level != "basic" && level != "standard" && level != "strict") throw new CliFault("INVALID_ARGUMENT", "level: basic, standard, strict");
            string full = FilePath(uri); if (!File.Exists(full)) throw new CliFault("TARGET_NOT_FOUND", "Script does not exist");
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(full, new UTF8Encoding(false, true)), new CSharpParseOptions(LanguageVersion.CSharp9));
            var diagnostics = level == "basic" ? tree.GetDiagnostics() : CSharpCompilation.Create("Ucl.ScriptValidation", new[] { tree }, FastEval.References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetDiagnostics();
            bool valid = !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
            var rows = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning).Take(100).Select(d => {
                var span = d.Location.GetMappedLineSpan(); return new { code = d.Id, severity = d.Severity.ToString().ToLowerInvariant(), message = d.GetMessage(), line = span.StartLinePosition.Line + 1, col = span.StartLinePosition.Character + 1 };
            }).ToArray();
            return new { valid, compiler = "roslyn", diagnostics = includeDiagnostics ? rows : null };
        }
        [CliCommand("find_in_file", "Bounded regular-expression search in project Assets", Group = "scripting")]
        public static object Find([CliArg("uri", Required = true)] string uri, [CliArg("pattern", Required = true)] string pattern,
            [CliArg("project_root")] string projectRoot = null, [CliArg("max_results")] int maxResults = 200, [CliArg("ignore_case")] bool ignoreCase = true)
        {
            if (projectRoot != null && Path.GetFullPath(projectRoot) != MailboxBridge.Project) throw new CliFault("PROJECT_MISMATCH", "project_root must match this Editor");
            string full = FilePath(uri); if (!File.Exists(full)) throw new CliFault("TARGET_NOT_FOUND", "File does not exist");
            if (new FileInfo(full).Length > 16 * 1024 * 1024) throw new CliFault("INVALID_ARGUMENT", "Regex search is limited to 16 MiB files");
            maxResults = Math.Max(1, Math.Min(10000, maxResults)); string text = File.ReadAllText(full, new UTF8Encoding(false, true));
            var regex = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(250));
            var matches = new JArray(); bool truncated = false; var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
            foreach (Match match in regex.Matches(text))
            {
                if (matches.Count == maxResults) {truncated = true; break;}
                int row = starts.BinarySearch(match.Index); if (row < 0) row = ~row - 1;
                matches.Add(new JObject { ["line"] = row + 1, ["column"] = match.Index - starts[row] + 1, ["length"] = match.Length,
                    ["match"] = match.Value.Length > 200 ? match.Value.Substring(0, 200) : match.Value });
            }
            return new { uri, matches, truncated };
        }
    }
}
