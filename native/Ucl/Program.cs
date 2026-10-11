using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace UnityCompat.Cli;

internal sealed class Failure(string code, string message, int exit = 6, JsonNode? detail = null) : Exception(message)
{
    internal readonly string Code = code;
    internal readonly int Exit = exit;
    internal readonly JsonNode? Detail = detail;
}

internal sealed class Invocation
{
    internal string Command = "";
    internal JsonObject Params = new();
    internal readonly List<string> Words = new();
    internal readonly Dictionary<string, string> Options = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Arguments = new();
    internal Action<string>? JobCreated;
    internal bool Flag(string key) => Options.TryGetValue(key, out var value) && value == "true";
    internal string? Option(string key) => Options.GetValueOrDefault(key);
}

internal static partial class Program
{
    const string Version = "0.3.0";
    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) {
        "json", "envelope", "components", "all", "types", "offline", "continue-on-error", "wait-job", "detach", "headless", "help", "version", "dry-run", "direct", "verbose", "installed", "metrics" };
    static readonly Dictionary<string, string> Aliases = new() {
        ["scene"]="manage_scene", ["object"]="manage_gameobject", ["component"]="manage_components", ["prefab"]="manage_prefabs",
        ["asset"]="manage_asset", ["material"]="manage_material", ["texture"]="manage_texture", ["shader"]="manage_shader",
        ["camera"]="manage_camera", ["animation"]="manage_animation", ["vfx"]="manage_vfx", ["ui"]="manage_ui",
        ["physics"]="manage_physics", ["profiler"]="manage_profiler", ["build"]="manage_build", ["package"]="manage_packages",
        ["script"]="manage_script", ["editor"]="manage_editor", ["settings"]="project_settings", ["references"]="asset_references",
        ["audit"]="project_audit", ["undo"]="undo_history", ["terrain"]="manage_terrain", ["audio"]="manage_audio",
        ["navigation"]="manage_navigation", ["lighting"]="manage_lighting", ["selection"]="manage_selection" };
    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase) {
        "project", "project-path", "format", "json", "envelope", "timeout", "params", "arg", "wait-job", "detach", "help", "version", "direct", "verbose" };

    static async Task<int> Main(string[] values)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        bool machine = values.Contains("--json") || values.Contains("json") || values.Contains("ndjson");
        try
        {
            var args = Parse(values);
            if (args.Flag("version")) { Console.WriteLine(Version + " Windows x64 NativeAOT"); return 0; }
            if (args.Flag("help") || args.Words.Count == 0 || args.Words[0] == "help") { Help(args); return 0; }
            if (args.Words[0] == "shell") return await Shell(args);
            if (args.Words[0] == "mcp") return await Mcp(args);
            if (args.Words[0] == "daemon") return await Daemon(args);
            return await Run(args);
        }
        catch (Exception ex)
        {
            var failure = ex as Failure ?? new Failure("IO_OR_COMMAND_ERROR", ex.Message);
            Console.Error.WriteLine(Error(failure).ToJsonString(JsonOptions));
            return failure.Exit;
        }
    }

    internal static Invocation Parse(IEnumerable<string> values)
    {
        var args = new Invocation();
        var tokens = values.ToArray();
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token == "-h") token = "--help";
            if (token == "-p") token = "--project";
            if (!token.StartsWith("--", StringComparison.Ordinal)) { args.Words.Add(token); continue; }
            var pair = token[2..].Split('=', 2);
            string key = pair[0];
            string value = pair.Length == 2 ? pair[1] : Flags.Contains(key) ? "true" :
                ++i < tokens.Length && !tokens[i].StartsWith("--", StringComparison.Ordinal) ? tokens[i] : throw new Failure("BAD_ARGUMENTS", "Missing value for --" + key, 2);
            if (key == "arg") args.Arguments.Add(value);
            else if (!args.Options.TryAdd(key, value)) throw new Failure("BAD_ARGUMENTS", "Repeated option --" + key, 2);
        }
        return args;
    }

    static JsonNode? Typed(string value)
    {
        try { return JsonNode.Parse(value); }
        catch (JsonException) { return JsonValue.Create(value); }
    }
    static JsonObject ReadObject(string value)
    {
        string text = value.StartsWith('@') ? File.ReadAllText(value[1..]) : value;
        return JsonNode.Parse(text) as JsonObject ?? throw new Failure("INVALID_PARAMS", "Parameters must be a JSON object.", 2);
    }
    static void GenericParams(Invocation a)
    {
        if (a.Option("params") is string json) a.Params = ReadObject(json);
        foreach (var entry in a.Arguments)
        {
            var pair = entry.Split('=', 2);
            if (pair.Length != 2 || pair[0].Length == 0) throw new Failure("BAD_ARGUMENTS", "--arg requires key=value", 2);
            a.Params[pair[0]] = Typed(pair[1]);
        }
    }
    static void Allow(Invocation a, params string[] keys)
    {
        foreach (var key in a.Options.Keys)
            if (!Common.Contains(key) && !keys.Contains(key)) throw new Failure("BAD_ARGUMENTS", "Unknown option --" + key, 2);
    }
    static string Word(Invocation a, int index, string name) => a.Words.Count > index ? a.Words[index] : throw new Failure("BAD_ARGUMENTS", "Missing " + name + "; use ucl help " + a.Words[0], 2);
    static void Arity(Invocation a, int min, int max)
    {
        if (a.Words.Count < min || a.Words.Count > max) throw new Failure("BAD_ARGUMENTS", "Unexpected positional arguments; use ucl help " + a.Words[0], 2);
    }
    static void Params(Invocation a, params string[] keys)
    {
        foreach (string key in keys) if (a.Option(key) is string value) a.Params[key.Replace('-', '_')] = Typed(value);
    }
    static JsonArray Vector(string text)
    {
        if (text.StartsWith('[')) return JsonNode.Parse(text) as JsonArray ?? throw new Failure("BAD_ARGUMENTS", "Invalid vector", 2);
        var result = new JsonArray();
        foreach (var item in text.Split(',')) result.Add((JsonNode)JsonValue.Create(double.Parse(item, CultureInfo.InvariantCulture)));
        if (result.Count is not (2 or 3)) throw new Failure("BAD_ARGUMENTS", "Use x,y,z or [x,y,z]", 2);
        return result;
    }

    static void Prepare(Invocation a)
    {
        GenericParams(a);
        string cmd = a.Words[0];
        switch (cmd)
        {
            case "hierarchy": case "snapshot":
                Arity(a, 1, 1); Allow(a, "root", "depth", "limit", "components", "fields"); Params(a, "root", "depth", "limit", "components", "fields");
                a.Command = cmd == "snapshot" ? "agent_snapshot" : "agent_hierarchy"; break;
            case "inspect":
                Arity(a, 2, 2); Allow(a, "component", "fields", "limit", "all", "types", "max-depth");
                string target = Word(a, 1, "target");
                a.Params[target.StartsWith("Assets/") || target.StartsWith("Packages/") ? "path" : "target"] = Typed(target);
                Params(a, "component", "fields", "limit", "all", "types", "max-depth"); a.Command = "agent_inspect"; break;
            case "find":
                Arity(a, 1, 2); Allow(a, "type", "limit"); a.Params["query"] = a.Words.Count == 2 ? a.Words[1] : "";
                Params(a, "type", "limit"); a.Command = "agent_find"; break;
            case "set":
                Arity(a, 5, 5); Allow(a, "expect"); string setTarget = Word(a, 1, "target");
                a.Params[setTarget.StartsWith("Assets/") || setTarget.StartsWith("Packages/") ? "path" : "target"] = Typed(setTarget);
                if (a.Words[2] != "-") a.Params["component"] = a.Words[2];
                a.Params["property"] = a.Words[3];
                a.Params["value"] = a.Words[4].Contains(',') && !a.Words[4].StartsWith('[') ? Vector(a.Words[4]) : Typed(a.Words[4]);
                Params(a, "expect"); a.Command = "agent_set"; break;
            case "create":
                Arity(a, 1, 2); Allow(a, "name", "position", "parent");
                a.Params["action"] = "create"; a.Params["name"] = a.Option("name") ?? throw new Failure("BAD_ARGUMENTS", "create requires --name", 2);
                if (a.Words.Count == 2) a.Params["primitiveType"] = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(a.Words[1].ToLowerInvariant());
                if (a.Option("position") is string pos) a.Params["position"] = Vector(pos);
                Params(a, "parent"); a.Command = "manage_gameobject"; break;
            case "add":
                Arity(a, 3, 3); Allow(a); a.Params["action"]="add"; a.Params["target"]=Typed(a.Words[1]); a.Params["componentType"]=a.Words[2]; a.Command="manage_components"; break;
            case "delete":
                Arity(a, 2, 2); Allow(a); a.Params["action"]="delete"; a.Params["target"]=Typed(a.Words[1]); a.Command="manage_gameobject"; break;
            case "windows": case "project-info":
                Arity(a, 1, 1); Allow(a); a.Command=cmd == "windows" ? "get_windows" : "get_project_info"; break;
            case "console":
                Arity(a, 1, 2); Allow(a, "count", "type"); a.Command="read_console"; a.Params["action"]=a.Words.Count==2 ? a.Words[1] : "get";
                a.Params["count"] = Typed(a.Option("count") ?? "20"); a.Params["format"]="json";
                if (a.Option("type") is string severity) a.Params["types"] = new JsonArray(severity.Split(',').Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()); break;
            case "eval":
                Arity(a, 1, 2); Allow(a, "file", "metrics"); Params(a,"metrics"); a.Command="eval";
                if ((a.Words.Count==2) == (a.Option("file")!=null)) throw new Failure("BAD_ARGUMENTS", "Supply code or --file, exclusively.", 2);
                a.Params["code"] = a.Option("file") is string file ? File.ReadAllText(file) : a.Words[1]; break;
            case "test":
                Arity(a, 1, 1); Allow(a, "mode"); a.Command="cli_poll"; a.Params=new JsonObject { ["command"]="run_tests", ["params"]=new JsonObject { ["mode"]=a.Option("mode")??"EditMode" } }; break;
            case "list": case "describe":
                Arity(a, cmd=="list" ? 1 : 2, cmd=="list" ? 1 : 2); Allow(a, "offline", "group");
                a.Command=cmd=="list" ? "cli_list" : "cli_describe";
                if (cmd=="describe") a.Params["name"]=a.Words[1]; else Params(a, "group"); break;
            case "command":
                Arity(a, 2, 2); a.Command=a.Words[1];
                foreach (var pair in a.Options.Where(x=>!Common.Contains(x.Key))) a.Params[pair.Key.Replace('-','_')] = Typed(pair.Value);
                break;
            case "play": case "pause": case "stop": case "step": case "recompile": case "refresh":
                Arity(a, 1, 1); Allow(a); a.Command="editor_"+cmd; break;
            case "build-targets":
                Arity(a,1,1); Allow(a,"installed"); Params(a,"installed"); a.Command="build_targets"; break;
            case "screenshot":
                Arity(a,1,1); Allow(a,"output","width","height","camera"); Params(a,"output","width","height","camera"); a.Command="editor_screenshot"; break;
            default:
                if (!Aliases.TryGetValue(cmd, out var alias)) throw new Failure("UNKNOWN_COMMAND", "Unknown command " + cmd + "; use ucl help", 2);
                Arity(a, 2, 2); a.Command=alias; a.Params["action"]=a.Words[1];
                foreach (var pair in a.Options.Where(x=>!Common.Contains(x.Key))) a.Params[pair.Key.Replace('-','_')] = Typed(pair.Value);
                break;
        }
        if (a.Flag("wait-job")) a.Params = new JsonObject { ["command"]=a.Command, ["params"]=a.Params.DeepClone() };
        if (a.Flag("wait-job")) a.Command = "cli_poll";
    }

    static string Project(Invocation a)
    {
        string? explicitPath = a.Option("project") ?? a.Option("project-path") ?? Environment.GetEnvironmentVariable("UCL_PROJECT");
        var dir = new DirectoryInfo(Path.GetFullPath(explicitPath ?? Environment.CurrentDirectory));
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName,"Assets")) && File.Exists(Path.Combine(dir.FullName,"ProjectSettings","ProjectVersion.txt"))) return dir.FullName;
            if (explicitPath != null) break;
            dir = dir.Parent;
        }
        throw new Failure("NOT_UNITY_PROJECT", "Specify a Unity project with --project.", 4);
    }
    static JsonObject ReadFile(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new Failure("INVALID_JSON", path); }
            catch (IOException) when (attempt < 8) { Thread.Sleep(5); }
            catch (UnauthorizedAccessException) when (attempt < 8) { Thread.Sleep(5); }
        }
    }
    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0;
    static double Timeout(Invocation a)
    {
        if (!double.TryParse(a.Option("timeout") ?? "60", NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value<=0 || value>86400)
            throw new Failure("INVALID_TIMEOUT", "Timeout must be finite and between 0 and 86400 seconds.", 2);
        return value;
    }
    static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString(JsonOptions) ?? "null";
    static bool Successful(JsonNode? node) => node is not JsonObject o || (o["success"]?.GetValue<bool>() != false && Text(o["status"]) != "error");
    static JsonNode? Payload(JsonNode? node)
    {
        // Unwrap protocol/SuccessResponse only. Arbitrary domain objects with a data member stay intact.
        for (int i=0; i<5 && node is JsonObject o && o.ContainsKey("success"); i++)
        {
            if (!Successful(o) || !o.ContainsKey("data")) break;
            if (!o.ContainsKey("protocol") && !o.ContainsKey("message") && o.Count>3) break;
            node=o["data"];
        }
        return node;
    }

    static async Task<JsonObject> Invoke(Invocation a)
    {
        if(a.Command=="cli_poll" && Text(a.Params["command"])=="run_tests") return await RunTestWorkflow(a);
        string project=Project(a);
        if (!a.Flag("direct") && File.Exists(Path.Combine(project,"Library/UnityCompat/daemon.json"))) return await DaemonInvoke(a);
        return await Sessions.GetOrAdd(project,p=>new EditorPool(p)).Invoke(a.Command,a.Params,Timeout(a),a.JobCreated);
    }

    static void Render(Invocation a, JsonNode? raw)
    {
        string format=a.Flag("json") ? "json" : a.Option("format") ?? "human";
        if (format is not ("human" or "json" or "ndjson" or "tsv")) throw new Failure("BAD_ARGUMENTS", "format: human, json, ndjson, tsv", 2);
        JsonNode? node=a.Flag("envelope") ? raw : Payload(raw);
        if (!Successful(raw)) throw ResultFailure(raw);
        if(a.Command=="eval" && !a.Flag("envelope") && !a.Flag("verbose") && a.Params["metrics"]?.GetValue<bool>()!=true)
            node=node?["result"];
        if (a.Flag("envelope") || format is "json" or "ndjson") { Console.WriteLine(node?.ToJsonString(JsonOptions) ?? "null"); return; }
        if (node is JsonObject tree && tree["nodes"] is JsonArray nodes)
        {
            if(format=="tsv")
            {
                string[] columns=tree["columns"] is JsonArray c?c.Select(Text).ToArray():new[]{"id","parent","depth","name","children","active"};
                static string Cell(string value)=>value.Replace("\\","\\\\").Replace("\t","\\t").Replace("\r","\\r").Replace("\n","\\n");
                Console.WriteLine(String.Join("\t",columns));
                foreach(var row in nodes) Console.WriteLine(String.Join("\t",columns.Select(k=>Cell(Text(row?[k])))));
                return;
            }
            foreach (var n in nodes)
                Console.WriteLine(format=="tsv" ? $"{Text(n?["id"])}\t{Text(n?["parent"])}\t{Text(n?["depth"])}\t{Text(n?["name"])}\t{Text(n?["children"])}\t{Text(n?["active"])}" :
                    $"{new string(' ',(n?["depth"]?.GetValue<int>()??0)*2)}{Text(n?["id"])} {Text(n?["name"])} [{Text(n?["children"])} children{(n?["active"]?.GetValue<bool>()==false?", inactive":"")}]" + (n?["components"]!=null ? " " + n["components"]!.ToJsonString(JsonOptions) : ""));
            Console.WriteLine($"depth={Text(tree["depth"])} truncated={Text(tree["truncated"])} depthLimited={Text(tree["depthLimited"])}");
        }
        else if (node is JsonObject inspect && inspect["fields"] is JsonObject fields)
        {
            Console.WriteLine(Text(inspect["id"])+" "+Text(inspect["type"]));
            foreach (var field in fields) Console.WriteLine(field.Key+(format=="tsv"?"\t":"=")+Text(field.Value));
            Console.WriteLine("truncated="+Text(inspect["truncated"]));
        }
        else if (node is JsonArray rows && rows.All(x=>x is JsonObject o && o["name"]!=null && o["description"]!=null))
            foreach (var row in rows) Console.WriteLine(Text(row?["name"])+"\t"+Text(row?["description"]));
        else Console.WriteLine(node?.ToJsonString(JsonOptions) ?? "null");
    }

    static async Task<int> Run(Invocation a)
    {
        Timeout(a);
        if(a.Option("format") is string explicitFormat && explicitFormat is not ("human" or "json" or "ndjson" or "tsv"))
            throw new Failure("BAD_ARGUMENTS","format: human, json, ndjson, tsv",2);
        string format=a.Flag("json")?"json":a.Option("format")??"human";
        if(format is not ("human" or "json" or "ndjson" or "tsv")) throw new Failure("BAD_ARGUMENTS","format: human, json, ndjson, tsv",2);
        string cmd=a.Words[0];
        if (cmd=="status")
        {
            Arity(a,1,1); Allow(a); var raw=ReadFile(Path.Combine(Project(a),"Library","UnityCompat","status.json"));
            if (Now-(raw["heartbeat"]?.GetValue<double>()??0)>12 || Text(raw["state"]) is "closed" or "reloading") throw new Failure("EDITOR_UNAVAILABLE","Editor is closed/reloading or not ticking",4);
            Render(a,raw); return 0;
        }
        if (cmd=="install") { Install(a); return 0; }
        if (cmd is "open" or "uninstall" or "doctor" or "editors") return PythonFallback(a);
        if (cmd=="job") return Job(a);
        if (cmd=="batch") return await Batch(a);
        if (cmd=="file") return FileCommand(a);
        Prepare(a);
        if(FrontCommands.Contains(a.Command)) {Render(a,await Frontend(a,a.Command,a.Params));return 0;}
        if (a.Flag("offline") && cmd is ("list" or "describe"))
        {
            var entries=JsonNode.Parse(File.ReadAllText(Path.Combine(PackageRoot(),"catalog.json"))) as JsonArray ?? new JsonArray();
            JsonNode? output=entries;
            if (cmd=="describe") output=entries.FirstOrDefault(x=>Text(x?["name"])==a.Words[1]) ?? throw new Failure("UNKNOWN_COMMAND",a.Words[1],2);
            else if (a.Option("group") is string group) output=new JsonArray(entries.Where(x=>Text(x?["group"])==group).Select(x=>x!.DeepClone()).ToArray());
            Render(a,output); return 0;
        }
        if(cmd=="describe" && FrontCommands.Contains(a.Words[1])) {Render(a,FrontCatalog().First(x=>Text(x?["name"])==a.Words[1]));return 0;}
        if (a.Flag("detach")) return Detach(a);
        var result=await Invoke(a);
        if(cmd=="list" && Payload(result) is JsonArray catalog)
        {
            var merged=MergeFront(catalog);
            if(a.Option("group") is string group) merged=new JsonArray(merged.Where(x=>Text(x?["group"])==group).Select(x=>x!.DeepClone()).ToArray());
            Render(a,merged);return 0;
        }
        Render(a,result); return Successful(result) ? 0 : 6;
    }

    static string PackageRoot()
    {
        string? configured=Environment.GetEnvironmentVariable("UCL_HOME");
        if (configured!=null && Directory.Exists(Path.Combine(configured,"bridge"))) return Path.GetFullPath(configured);
        string paths=Path.Combine(AppContext.BaseDirectory,"ucl.paths.json");
        if (File.Exists(paths)) return Text(ReadFile(paths)["package"]);
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        for (int i=0; dir!=null && i<5; i++,dir=dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName,"bridge"))) return dir.FullName;
        throw new Failure("PACKAGE_NOT_FOUND","Set UCL_HOME to the directory containing bridge/ and catalog.json",4);
    }
    static void Atomic(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temp,value.ToJsonString(JsonOptions),new UTF8Encoding(false));
            for (int i=0;;i++)
            {
                try { File.Move(temp,path,true); break; }
                catch (IOException) when(i<8) { Thread.Sleep(5); }
                catch (UnauthorizedAccessException) when(i<8) { Thread.Sleep(5); }
            }
        }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    static void Install(Invocation a)
    {
        Arity(a,1,1); Allow(a);
        string project=Project(a), package=Path.Combine(project,"Packages","com.unitycompat.cli");
        var version=Regex.Match(File.ReadAllText(Path.Combine(project,"ProjectSettings","ProjectVersion.txt")),@"m_EditorVersion:\s*(\d+)\.(\d+)");
        if (!version.Success || int.Parse(version.Groups[1].Value)<2021 || (int.Parse(version.Groups[1].Value)==2021 && int.Parse(version.Groups[2].Value)<3))
            throw new Failure("UNSUPPORTED_UNITY","Unity 2021.3 or newer required",4);
        string source=Path.Combine(PackageRoot(),"bridge");
        var bundled=ReadFile(Path.Combine(source,"package.json"));
        if(Directory.Exists(package) && (!File.Exists(Path.Combine(package,"package.json")) || Text(ReadFile(Path.Combine(package,"package.json"))["name"])!="com.unitycompat.cli"))
            throw new Failure("PACKAGE_CONFLICT","Refusing to replace an unrelated package directory",4);
        var data=ReadFile(Path.Combine(project,"Packages","manifest.json"));
        var deps=data["dependencies"] as JsonObject ?? throw new Failure("INVALID_MANIFEST","dependencies must be an object",4);
        foreach(var dependency in (JsonObject)bundled["dependencies"]!) if(!deps.ContainsKey(dependency.Key)) deps[dependency.Key]=dependency.Value?.DeepClone();
        deps["com.unitycompat.cli"]=Version;
        string backup=Path.Combine(project,".unity-compat-backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        string manifest=Path.Combine(project,"Packages","manifest.json");
        File.Copy(manifest,Path.Combine(backup,"manifest.json"));
        string staging=Path.Combine(project,"Packages",".ucl-stage-"+Guid.NewGuid().ToString("N"));
        foreach (string file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {
            string dest=Path.Combine(staging,Path.GetRelativePath(source,file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file,dest);
        }
        bool moved=false, installed=false;
        try
        {
            if (Directory.Exists(package)) { Directory.Move(package,Path.Combine(backup,"com.unitycompat.cli")); moved=true; }
            Directory.Move(staging,package); installed=true;
            Atomic(manifest,data);
        }
        catch
        {
            // Archive the failed install instead of deleting user files during recovery.
            if(installed) Directory.Move(package,Path.Combine(backup,"failed-install"));
            if(moved) Directory.Move(Path.Combine(backup,"com.unitycompat.cli"),package);
            File.Copy(Path.Combine(backup,"manifest.json"),manifest,true);
            throw;
        }
        Render(a,new JsonObject { ["installed"]=Version,["project"]=project,["backup"]=backup });
    }
    static int PythonFallback(Invocation a)
    {
        string? python=Environment.GetEnvironmentVariable("UCL_PYTHON");
        string config=Path.Combine(AppContext.BaseDirectory,"ucl.paths.json");
        if(python==null && File.Exists(config)) python=Text(ReadFile(config)["python"]);
        var start=new ProcessStartInfo(python??"python") { UseShellExecute=false };
        start.ArgumentList.Add("-m"); start.ArgumentList.Add("unity_compat");
        foreach(var word in a.Words) start.ArgumentList.Add(word);
        foreach(var pair in a.Options) { start.ArgumentList.Add("--"+pair.Key); if(!Flags.Contains(pair.Key)) start.ArgumentList.Add(pair.Value); }
        start.Environment["UCL_USE_PYTHON"]="1";
        using var process=Process.Start(start) ?? throw new Failure("PYTHON_NOT_FOUND","This administrative command requires the pip package.",4);
        process.WaitForExit(); return process.ExitCode;
    }
    static int Detach(Invocation a)
    {
        string root=Path.Combine(Project(a),"Library","UnityCompat"); var status=ReadFile(Path.Combine(root,"status.json"));
        if(Now-(status["heartbeat"]?.GetValue<double>()??0)>12) throw new Failure("EDITOR_UNAVAILABLE","Editor heartbeat expired",4);
        string id=Guid.NewGuid().ToString("N");
        Atomic(Path.Combine(root,"requests",id+".json"),new JsonObject { ["protocol"]=1,["id"]=id,["session"]=status["session"]!.DeepClone(),["command"]=a.Command,["params"]=a.Params.DeepClone(),["created"]=Now,["deadline"]=Now+Timeout(a) });
        Render(a,new JsonObject { ["id"]=id,["state"]="queued" }); return 0;
    }
    static int Job(Invocation a)
    {
        Arity(a,3,3); Allow(a); string action=a.Words[1], id=a.Words[2];
        if(id.Length!=32 || id.Any(x=>!"0123456789abcdef".Contains(x))) throw new Failure("INVALID_JOB_ID","Job ID requires 32 lowercase hexadecimal characters",2);
        string root=Path.Combine(Project(a),"Library","UnityCompat"), response=Path.Combine(root,"responses",id+".json");
        if(action=="cancel") { Atomic(Path.Combine(root,"cancellations",id+".json"),new JsonObject{["id"]=id}); Render(a,new JsonObject{["id"]=id,["state"]="cancel_requested"}); return 0; }
        if(action is not ("status" or "wait")) throw new Failure("BAD_ARGUMENTS","job: status, wait, cancel",2);
        double until=Now+(action=="wait"?Timeout(a):0);
        do
        {
            if(File.Exists(response)) { var raw=ReadFile(response); Render(a,raw); return Successful(raw)?0:6; }
            if(action=="status")
            {
                string state=File.Exists(Path.Combine(root,"running",id+".json"))?"running":File.Exists(Path.Combine(root,"requests",id+".json"))?"queued":"unknown";
                Render(a,new JsonObject{["id"]=id,["state"]=state}); return state=="unknown"?4:0;
            }
            Thread.Sleep(10);
        } while(Now<until);
        throw new Failure("TIMEOUT","Job may still be running; inspect it before retrying.",7);
    }

    static async Task<int> Batch(Invocation a)
    {
        Arity(a,2,2); Allow(a,"continue-on-error");
        string file=a.Words[1]; string content=file=="-"?Console.In.ReadToEnd():File.ReadAllText(file);
        JsonArray operations;
        if(content.TrimStart().StartsWith('[')) operations=JsonNode.Parse(content) as JsonArray ?? throw new Failure("INVALID_BATCH","Expected array",2);
        else
        {
            operations=new JsonArray();
            foreach(string line in content.Split('\n').Select(x=>x.Trim()).Where(x=>x.Length>0 && !x.StartsWith('#')))
            {
                var child=Parse(Split(line)); if(child.Words.Count==0) continue;
                Prepare(child); operations.Add((JsonNode)new JsonObject{["command"]=child.Command,["params"]=child.Params.DeepClone()});
            }
        }
        if(operations.Count==0 || operations.Count>256) throw new Failure("INVALID_BATCH","Batch requires 1..256 commands",2);
        a.Command="cli_batch"; a.Params=new JsonObject{["operations"]=operations,["continue_on_error"]=a.Flag("continue-on-error")};
        if(a.Flag("detach")) return Detach(a);
        var result=await Invoke(a);
        if(!a.Flag("envelope") && Successful(result) && result["data"] is JsonObject batch && batch["results"] is JsonArray rows)
            Render(a,new JsonObject{["results"]=new JsonArray(rows.Select(row=>new JsonObject{["command"]=row?["command"]?.DeepClone(),["result"]=Payload(row?["result"])?.DeepClone()}).Cast<JsonNode?>().ToArray()),["completed"]=batch["completed"]?.DeepClone(),["total"]=batch["total"]?.DeepClone()});
        else Render(a,result);
        return Successful(result)?0:6;
    }
    internal static List<string> Split(string line)
    {
        var words=new List<string>(); var b=new StringBuilder(); char quote='\0'; bool started=false;
        foreach(char c in line)
        {
            if(quote!='\0') { if(c==quote) quote='\0'; else b.Append(c); started=true; }
            else if(c=='\'' || c=='"') { quote=c; started=true; }
            else if(char.IsWhiteSpace(c)) { if(started) {words.Add(b.ToString()); b.Clear(); started=false;} }
            else {b.Append(c); started=true;}
        }
        if(quote!='\0') throw new Failure("BAD_ARGUMENTS","Unclosed quote in command file",2);
        if(started) words.Add(b.ToString()); return words;
    }
    static async Task<int> Shell(Invocation parent)
    {
        Arity(parent,1,1); Allow(parent);
        int exit=0; bool interactive=!Console.IsInputRedirected;
        while(true)
        {
            if(interactive) Console.Write("ucl> ");
            string? line=Console.ReadLine(); if(line==null || line.Trim() is "exit" or "quit") break;
            if(string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var a=Parse(Split(line)); foreach(var option in parent.Options) a.Options.TryAdd(option.Key,option.Value);
                if(a.Words.Count==0) continue; if(a.Flag("help")) {Help(a);continue;}
                exit=await Run(a);
            }
            catch(Exception e) {var f=e as Failure??new Failure("COMMAND_FAILED",e.Message); exit=f.Exit; Console.Error.WriteLine(Error(f).ToJsonString(JsonOptions));}
        }
        return exit;
    }

    static string AssetFile(string project,string path)
    {
        string full=Path.GetFullPath(Path.Combine(project,path));
        string assets=Path.GetFullPath(Path.Combine(project,"Assets"))+Path.DirectorySeparatorChar;
        if(!full.StartsWith(assets,StringComparison.OrdinalIgnoreCase)) throw new Failure("INVALID_PATH","file commands are restricted to Assets/",2);
        // Do not cross directory junctions/symlinks when validating a project-relative edit.
        var parent=new DirectoryInfo(Path.GetDirectoryName(full)!);
        while(parent!=null && parent.FullName.Length>=assets.Length-1)
        {if(parent.Exists && (parent.Attributes&FileAttributes.ReparsePoint)!=0) throw new Failure("INVALID_PATH","Symlink/junction paths are not supported",2);parent=parent.Parent;}
        if(File.Exists(full) && (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0) throw new Failure("INVALID_PATH","Symlink files are not supported",2);
        return full;
    }
    static int FileCommand(Invocation a)
    {
        Arity(a,3,3); Allow(a,"search","from","to","sha","dry-run");
        string path=AssetFile(Project(a),a.Words[2]); byte[] bytes=File.ReadAllBytes(path);
        string sha=Convert.ToHexStringLower(SHA256.HashData(bytes));
        switch(a.Words[1])
        {
            case "sha": Render(a,new JsonObject{["path"]=a.Words[2],["sha256"]=sha,["bytes"]=bytes.Length}); break;
            case "find":
                string search=a.Option("search")??throw new Failure("BAD_ARGUMENTS","Requires --search",2);
                var matches=new JsonArray(); int line=0;
                foreach(string text in Encoding.UTF8.GetString(bytes).Split('\n')) {line++;if(text.Contains(search,StringComparison.Ordinal)) matches.Add((JsonNode)new JsonObject{["line"]=line,["text"]=text.TrimEnd('\r')});}
                Render(a,new JsonObject{["path"]=a.Words[2],["sha256"]=sha,["matches"]=matches}); break;
            case "replace":
                if(!String.Equals(a.Option("sha"),sha,StringComparison.OrdinalIgnoreCase)) throw new Failure("PRECONDITION_FAILED","Supply --sha from file sha/find; content changed or SHA missing",2);
                string from=a.Option("from")??throw new Failure("BAD_ARGUMENTS","Requires --from",2), to=a.Option("to")??throw new Failure("BAD_ARGUMENTS","Requires --to",2);
                if(from.Length==0) throw new Failure("BAD_ARGUMENTS","--from must be nonempty",2);
                var utf8=new UTF8Encoding(false,true); bool bom=bytes.AsSpan().StartsWith(new byte[]{239,187,191});
                string original=utf8.GetString(bytes.AsSpan(bom?3:0)); int count=(original.Length-original.Replace(from,"",StringComparison.Ordinal).Length)/from.Length;
                if(count!=1) throw new Failure("AMBIGUOUS_EDIT","--from must match exactly once; found "+count,2);
                string next=original.Replace(from,to,StringComparison.Ordinal);
                if(!a.Flag("dry-run"))
                {
                    string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try {File.WriteAllText(temp,next,new UTF8Encoding(bom));
                        // Compare again immediately before atomic replacement. No silent overwrite.
                        if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!=sha) throw new Failure("PRECONDITION_FAILED","File changed during edit");
                        File.Move(temp,path,true);
                    } finally {if(File.Exists(temp)) File.Delete(temp);}
                }
                Render(a,new JsonObject{["path"]=a.Words[2],["matches"]=count,["dryRun"]=a.Flag("dry-run"),["before"]=from,["after"]=to}); break;
            default: throw new Failure("BAD_ARGUMENTS","file: sha, find, replace",2);
        }
        return 0;
    }

    static void Help(Invocation a)
    {
        string topic=a.Words.FirstOrDefault(x=>x!="help")??"";
        var help=new Dictionary<string,string> {
            ["hierarchy"]="ucl hierarchy [--root ID|path] [--depth 2] [--limit 80] [--components] [--fields id,name]",
            ["inspect"]="ucl inspect ID|path|Assets/file [--component Type] [--fields a,b,nested.field] [--all] [--types] [--limit 80]",
            ["find"]="ucl find [name/path] [--type ComponentType] [--limit 30]",
            ["set"]="ucl set TARGET COMPONENT|- PROPERTY VALUE [--expect OLD_VALUE]\nExample: ucl set Cube AudioSource m_Volume 0.25 --expect 1\nVectors: ucl set Cube Transform m_LocalPosition 1,2,3",
            ["create"]="ucl create [cube|sphere|capsule|plane|quad] --name NAME [--position 1,2,3] [--parent ID|path]",
            ["add"]="ucl add TARGET COMPONENT", ["delete"]="ucl delete TARGET",
            ["batch"]="ucl batch commands.ucl|- [--continue-on-error]\nOne normal CLI command per line. Quote spaces; # starts a whole-line comment. JSON operation arrays also accepted. No rollback; first failure stops by default.",
            ["shell"]="ucl shell [--project PATH] [--json]\nWarm process reads commands until exit or EOF. Output one result per line. Batch is faster for independent commands.",
            ["file"]="ucl file sha Assets/file\nucl file find Assets/file --search TEXT\nucl file replace Assets/file --from OLD --to NEW --sha HASH [--dry-run]\nUTF-8 only; preserves BOM/newlines; replacement requires exactly one match.",
            ["command"]="ucl command NAME [--arg key=value] [--params @file.json] [--detach] [--wait-job]\nAll existing bridge commands and project [CliCommand] extensions are available.",
            ["eval"]="ucl eval 'return UnityEngine.Application.unityVersion;' | ucl eval --file code.cs [--metrics]\nTrusted local C#; in-memory Roslyn, no project/domain reload. Not an OS sandbox. Reuse code with --arg args={...} for cache hits.",
            ["mcp"]="ucl mcp [--project PATH] [--profile compact|full]\nNative MCP stdio: the Agent starts this executable directly. stdout is JSON-RPC only. compact exposes focused tools plus command discovery; full lists every Editor command.",
            ["daemon"]="ucl daemon start|status|stop [--project PATH]\nOptional hidden native worker with a persistent Editor connection. CLI uses it while running; --direct bypasses it. Shell startup still belongs to the calling terminal.",
            ["play"]="ucl play | ucl pause | ucl step | ucl stop\nPlay/stop acknowledge a requested transition; poll editor_status after the potential domain reload.",
            ["recompile"]="ucl recompile | ucl refresh\nAcknowledge before scheduling compilation/import; observe readiness and the new session afterward.",
            ["build-targets"]="ucl build-targets [--installed]",
            ["screenshot"]="ucl screenshot [--output Library/capture.png] [--camera ID|path] [--width 1280] [--height 720]\nRender a scene camera to PNG; needs graphics support.",
            ["console"]="ucl console [get|clear] [--count 20] [--type error,warning]",
            ["snapshot"]="ucl snapshot [--depth 2] [--limit 80] [--components]\nReads hierarchy, windows, selection and state together.",
            ["list"]="ucl list [--group agent] [--offline] | ucl describe NAME [--offline]",
            ["install"]="ucl install --project PATH\nInstall/update the bundled bridge with backups. Wait for Editor compilation afterward.",
            ["job"]="ucl job status|wait|cancel JOB_ID [--timeout 60]"
        };
        if(help.TryGetValue(topic,out var detail)) Console.WriteLine(detail);
        else Console.WriteLine("ucl 0.3 — Unity 2021.3+ local automation, Windows NativeAOT\nRead: status, hierarchy, inspect, find, windows, snapshot, project-info, console\nEdit: create, add, set, delete, scene, asset, prefab, material, ...\nWork: batch, shell, eval, test, command, job; play/pause/step/stop; recompile/refresh; screenshot/build-targets\nAgent: mcp (native stdio); optional daemon start|status|stop; --direct bypass\nSetup: install, list, describe; pip administration: open, doctor, editors, uninstall\nUse: ucl help COMMAND. Editor must have the bridge loaded.");
        Console.WriteLine("Global: --project PATH | UCL_PROJECT; --json returns compact payload, --envelope keeps protocol; --format human|json|ndjson|tsv; --timeout 60");
    }
}
