using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnityCompat.Cli;

internal static partial class Program
{
    sealed class McpState(Invocation options)
    {
        internal readonly Invocation Options=options;
        internal bool Initialized,Ready;
        internal readonly SemaphoreSlim Output=new(1,1);
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string,Task> Requests=new();
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> Jobs=new();
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string,bool> Cancelled=new();
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string,bool> GroupOverrides=new();
        internal JsonArray? Catalog;
        internal string? CatalogSession;
        internal async Task Send(JsonObject message)
        {
            await Output.WaitAsync();
            try {await Console.Out.WriteLineAsync(message.ToJsonString(JsonOptions)); await Console.Out.FlushAsync();}
            finally {Output.Release();}
        }
        internal Invocation Call(string command,JsonObject parameters)
        {
            var call=new Invocation{Command=command,Params=parameters};
            foreach(var option in Options.Options) call.Options[option.Key]=option.Value;
            call.Options["direct"]="true"; return call;
        }
        internal async Task<JsonArray> Commands()
        {
            try
            {
                string project=Project(Options); var endpoint=ReadFile(Path.Combine(project,"Library/UnityCompat/endpoint.json"));
                string session=Text(endpoint["session"]);
                if(Catalog!=null && session==CatalogSession) return Catalog;
                var result=await Invoke(Call("cli_list",new()));
                Catalog=MergeFront(Payload(result) as JsonArray??throw new Failure("INVALID_RESPONSE","Cannot discover Editor commands"));
                CatalogSession=session;
            }
            catch(Exception ex) when(ex is Failure or IOException or UnauthorizedAccessException)
            {
                Catalog=JsonNode.Parse(File.ReadAllText(Path.Combine(PackageRoot(),"catalog.json"))) as JsonArray??new();
                CatalogSession=null;
            }
            return Catalog;
        }
        internal async Task<JsonObject> Tools()
        {
            bool full=Options.Option("profile")=="full";
            var tools=new JsonArray();
            tools.Add((JsonNode)new JsonObject { ["name"]="ucl_command",["description"]="Call any discovered Unity command. Use ucl_discover to look up its schema.",
                ["inputSchema"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{
                    ["command"]=new JsonObject{["type"]="string"},["params"]=new JsonObject{["type"]="object"}},
                    ["required"]=new JsonArray("command"),["additionalProperties"]=false} });
            tools.Add((JsonNode)new JsonObject { ["name"]="ucl_discover",["description"]="Discover commands, optionally by group or name, without loading all schemas.",
                ["inputSchema"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{
                    ["name"]=new JsonObject{["type"]="string"},["group"]=new JsonObject{["type"]="string"}},["additionalProperties"]=false} });
            tools.Add((JsonNode)new JsonObject{["name"]="ucl_status",["description"]="Read Editor readiness, heartbeat and session.",
                ["inputSchema"]=new JsonObject{["type"]="object",["properties"]=new JsonObject(),["additionalProperties"]=false} });
            foreach(var item in await Commands())
            {
                string name=Text(item?["name"]);
                bool visible=full || name is "agent_hierarchy" or "agent_find" or "agent_inspect" or "agent_set" or "agent_snapshot" or "eval";
                if(GroupOverrides.TryGetValue(Text(item?["group"]),out bool groupVisible)) visible=groupVisible;
                if(!visible) continue;
                tools.Add((JsonNode)new JsonObject{["name"]=name,["description"]=item?["description"]?.DeepClone(),
                    ["inputSchema"]=item?["inputSchema"]?.DeepClone()??new JsonObject{["type"]="object",["properties"]=new JsonObject(),["additionalProperties"]=true}});
            }
            return new JsonObject{["tools"]=tools};
        }
        internal void Cancel(string key)
        {
            if(!Requests.ContainsKey(key)) return;
            Cancelled[key]=true;
            if(!Jobs.TryGetValue(key,out var id)) return;
            string folder=Path.Combine(Project(Options),"Library/UnityCompat/cancellations"); Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,id+".json"),temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temp,"{}"); File.Move(temp,path,true);
        }
        internal async Task<JsonObject> ToolCall(JsonObject p,string key)
        {
            string name=Text(p["name"]); var args=p["arguments"] as JsonObject??new(); JsonNode? data;
            try
            {
                if(name=="ucl_status")
                {
                    data=ReadFile(Path.Combine(Project(Options),"Library/UnityCompat/status.json"));
                    if(Now-(data["heartbeat"]?.GetValue<double>()??0)>12) throw new Failure("EDITOR_UNAVAILABLE","Editor heartbeat is stale",4);
                }
                else if(name=="ucl_discover")
                {
                    var commands=await Commands();
                    data=new JsonArray(commands.Where(x=>
                        (args["name"]==null || Text(x?["name"])==Text(args["name"])) &&
                        (args["group"]==null || Text(x?["group"])==Text(args["group"]))).Select(x=>x!.DeepClone()).ToArray());
                }
                else
                {
                    string command=name=="ucl_command"?Text(args["command"]):name;
                    var parameters=name=="ucl_command"?args["params"] as JsonObject??new():args;
                    if(!(await Commands()).Any(x=>Text(x?["name"])==command)) throw new Failure("UNKNOWN_COMMAND","Unknown Editor command: "+command,2);
                    if(FrontCommands.Contains(command)) data=await Frontend(Options,command,parameters,this);
                    else
                    {
                    var call=Call(command,parameters); call.JobCreated=id=>{Jobs[key]=id;if(Cancelled.ContainsKey(key)) Cancel(key);};
                    var raw=await Invoke(call);
                    if(!Successful(raw)) throw ResultFailure(raw); data=Payload(raw);
                    if(command=="eval" && parameters["metrics"]?.GetValue<bool>()!=true) data=data?["result"];
                    }
                }
                string text=data?.ToJsonString(JsonOptions)??"null";
                return new JsonObject { ["content"]=new JsonArray(new JsonObject{["type"]="text",["text"]=text}),
                    ["structuredContent"]=data is JsonObject?data.DeepClone():new JsonObject{["data"]=data?.DeepClone()},["isError"]=false };
            }
            catch(Exception ex)
            {
                var error=Error(ex as Failure??new Failure("COMMAND_FAILED",ex.Message));
                return new JsonObject{["content"]=new JsonArray(new JsonObject{["type"]="text",["text"]=error.ToJsonString(JsonOptions)}),
                    ["structuredContent"]=error,["isError"]=true};
            }
            finally {Jobs.TryRemove(key,out _);Cancelled.TryRemove(key,out _);Requests.TryRemove(key,out _);}
        }
        internal async Task<JsonObject> ResourceList(bool templates)
        {
            var rows=new JsonArray();
            foreach(var item in (await Commands()).Where(x=>Text(x?["kind"])=="resource"))
            {
                string name=Text(item?["name"]); var row=new JsonObject{["name"]=name,["description"]=item?["description"]?.DeepClone(),["mimeType"]="application/json"};
                row[templates?"uriTemplate":"uri"]="unitycompat://resource/"+name+(templates?"?params={params}":""); rows.Add((JsonNode)row);
            }
            return new JsonObject{[templates?"resourceTemplates":"resources"]=rows};
        }
        internal async Task<JsonObject> ResourceRead(JsonObject p)
        {
            string original=Text(p["uri"]); var uri=new Uri(original);
            if(uri.Scheme!="unitycompat" || uri.Host!="resource") throw new Failure("RESOURCE_NOT_FOUND","Unknown resource URI",2);
            string command=uri.AbsolutePath.TrimStart('/');
            if(!(await Commands()).Any(x=>Text(x?["name"])==command && Text(x?["kind"])=="resource")) throw new Failure("RESOURCE_NOT_FOUND","Unknown resource",2);
            string query=uri.Query.TrimStart('?'); JsonObject args=new();
            if(query.StartsWith("params=",StringComparison.Ordinal)) args=JsonNode.Parse(Uri.UnescapeDataString(query[7..])) as JsonObject??throw new Failure("INVALID_ARGUMENT","Resource params must be JSON",2);
            var raw=await Invoke(Call(command,args)); if(!Successful(raw)) throw ResultFailure(raw);
            return new JsonObject{["contents"]=new JsonArray(new JsonObject{["uri"]=original,["mimeType"]="application/json",["text"]=Payload(raw)?.ToJsonString(JsonOptions)??"null"})};
        }
        internal async Task Handle(JsonObject request)
        {
            var id=request["id"]?.DeepClone(); string method=Text(request["method"]);
            if(id==null)
            {
                if(method=="notifications/initialized" && Initialized) Ready=true;
                if(method=="notifications/cancelled" && request["params"]?["requestId"] is JsonNode target)
                {try {Cancel(target.ToJsonString());} catch(Exception ex) {await Console.Error.WriteLineAsync(ex.Message);} }
                return;
            }
            try
            {
                JsonObject result;
                if(request["jsonrpc"]?.GetValue<string>()!="2.0") throw new RpcFailure(-32600,"Expected JSON-RPC 2.0");
                var p=request["params"] as JsonObject??new();
                if(method=="initialize")
                {
                    if(Initialized) throw new RpcFailure(-32600,"Already initialized");
                    string version=Text(p["protocolVersion"]);
                    if(version is not ("2024-11-05" or "2025-03-26" or "2025-06-18")) version="2025-06-18";
                    Initialized=true;
                    result=new JsonObject{["protocolVersion"]=version,["serverInfo"]=new JsonObject{["name"]="unity-compat-cli",["version"]=Version},
                        ["capabilities"]=new JsonObject{["tools"]=new JsonObject{["listChanged"]=false},["resources"]=new JsonObject()},
                        ["instructions"]="Use compact Agent queries and ucl_discover. Eval is trusted local C#, not an OS sandbox. Timed-out changes must not be replayed."};
                }
                else if(method=="ping") result=new();
                else
                {
                    if(!Ready) throw new RpcFailure(-32002,"Initialize and send notifications/initialized first");
                    result=method switch { "tools/list"=>await Tools(),"tools/call"=>await ToolCall(p,id.ToJsonString()),
                        "resources/list"=>await ResourceList(false),"resources/templates/list"=>await ResourceList(true),
                        "resources/read"=>await ResourceRead(p),_=>throw new RpcFailure(-32601,"Method not found: "+method) };
                }
                await Send(new JsonObject{["jsonrpc"]="2.0",["id"]=id,["result"]=result});
            }
            catch(Exception ex)
            {
                await Send(new JsonObject{["jsonrpc"]="2.0",["id"]=id,["error"]=new JsonObject{
                    ["code"]=ex is RpcFailure f?f.Code:-32602,["message"]=ex.Message}});
            }
        }
    }
    sealed class RpcFailure(int code,string message):Exception(message) {internal readonly int Code=code;}
    static async Task<int> Mcp(Invocation a)
    {
        Arity(a,1,1); Allow(a,"profile");
        if(a.Option("profile") is string profile && profile is not ("compact" or "full")) throw new Failure("BAD_ARGUMENTS","mcp --profile compact|full",2);
        var state=new McpState(a); var calls=new List<Task>();
        // StreamReader.ReadLine's unbounded allocation is avoided for untrusted stdio input.
        using var input=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false,true),false,4096);
        var line=new StringBuilder(); char[] chars=new char[4096];
        while(true)
        {
            int n=await input.ReadAsync(chars); if(n==0) break;
            for(int i=0;i<n;i++)
            {
                char c=chars[i];
                if(c!='\n') {if(c!='\r') line.Append(c); if(line.Length>4*1024*1024) throw new Failure("REQUEST_TOO_LARGE","MCP message exceeds 4 MiB",2);continue;}
                if(line.Length==0) continue;
                try
                {
                    var request=JsonNode.Parse(line.ToString()) as JsonObject??throw new JsonException();
                    // Lifecycle notifications stay ordered; tool calls may overlap at the input
                    // layer so keepalive/ping does not wait for a long Unity operation.
                    if(Text(request["method"])=="tools/call" || Text(request["method"])=="resources/read")
                    {calls.RemoveAll(t=>t.IsCompleted); if(calls.Count>=64) await Task.WhenAny(calls);
                        if(Text(request["method"])=="tools/call" && request["id"]!=null) state.Requests[request["id"]!.ToJsonString()]=Task.CompletedTask;
                        calls.Add(state.Handle(request));}
                    else await state.Handle(request);
                }
                catch(JsonException) {await state.Send(new JsonObject{["jsonrpc"]="2.0",["id"]=null,["error"]=new JsonObject{["code"]=-32700,["message"]="Parse error"}});}
                line.Clear();
            }
        }
        if(line.Length>0) await state.Send(new JsonObject{["jsonrpc"]="2.0",["id"]=null,["error"]=new JsonObject{["code"]=-32700,["message"]="Incomplete message: newline required"}});
        await Task.WhenAll(calls); foreach(var session in Sessions.Values) session.Dispose(); return 0;
    }
}
