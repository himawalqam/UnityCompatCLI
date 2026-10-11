using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace UnityCompat.Cli;

internal static partial class Program
{
    static readonly HashSet<string> FrontCommands=new(){"debug_request_context","manage_tools","set_active_instance","unity_docs"};
    static readonly HttpClient DocsClient=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(15)};
    static readonly ConcurrentDictionary<string,(double time,JsonObject data)> DocsCache=new();
    static JsonArray FrontCatalog()=>JsonNode.Parse(File.ReadAllText(Path.Combine(PackageRoot(),"frontend-catalog.json"))) as JsonArray??new();
    static JsonArray MergeFront(JsonArray commands)=>new(commands.Where(x=>!FrontCommands.Contains(Text(x?["name"]))).Concat(FrontCatalog()).Select(x=>x!.DeepClone()).ToArray());
    static async Task<JsonNode> Frontend(Invocation a,string command,JsonObject p,McpState? state=null)
    {
        string project=Project(a);
        if(command=="debug_request_context") return new JsonObject{["project"]=project,["transport"]=state==null?"native-cli":"mcp-stdio",
            ["version"]=Version,["profile"]=a.Option("profile")??"compact"};
        if(command=="set_active_instance")
        {
            string instance=Text(p["instance"]);
            if(instance==Path.GetFileName(project)) instance=project;
            if(!Path.IsPathFullyQualified(instance)) throw new Failure("INVALID_INSTANCE","Use an absolute Unity project path; MCP server ports and legacy hashes are not project selectors here",2);
            if(state?.Jobs.Count>0) throw new Failure("EDITOR_BUSY","Wait for outstanding Unity jobs before changing projects",4);
            var candidate=new Invocation(); candidate.Options["project"]=instance; string selected=Project(candidate);
            a.Options["project"]=selected;
            if(state!=null) {state.Catalog=null;state.CatalogSession=null;}
            return new JsonObject{["project"]=selected,["scope"]=state==null?"this-invocation":"this-mcp-session"};
        }
        if(command=="manage_tools")
        {
            if(state==null && Text(p["action"]) is not ("list_groups" or "sync"))
                throw new Failure("INVALID_STATE","Visibility belongs to an MCP session; use --profile compact|full or manage_tools inside that session",6);
            var catalog=state!=null?await state.Commands():MergeFront(JsonNode.Parse(File.ReadAllText(Path.Combine(PackageRoot(),"catalog.json"))) as JsonArray??new());
            var groups=catalog.Select(x=>Text(x?["group"])).Distinct().Order().ToArray();
            string action=Text(p["action"]),group=Text(p["group"]);
            if(action is "activate" or "deactivate")
            {
                if(!groups.Contains(group)) throw new Failure("UNKNOWN_GROUP","Unknown group: "+group,2);
                state!.GroupOverrides[group]=action=="activate";
            }
            else if(action=="reset") state!.GroupOverrides.Clear();
            else if(action is not ("list_groups" or "sync")) throw new Failure("INVALID_ARGUMENT","manage_tools: list_groups, activate, deactivate, sync, reset",2);
            return new JsonObject{["groups"]=new JsonArray(groups.Select(g=>(JsonNode)new JsonObject{["name"]=g,
                ["active"]=state?.GroupOverrides.GetValueOrDefault(g,a.Option("profile")=="full" || g=="agent")??true}).ToArray()),
                ["visibilitySource"]="native-session-profile",["refresh"]="Call tools/list after changing visibility; Editor MCP GUI toggles are independent"};
        }
        if(command=="unity_docs") return await Documentation(project,p);
        throw new Failure("UNKNOWN_COMMAND",command,2);
    }
    static string DocPart(string value,bool slash=false)
    {
        if(String.IsNullOrWhiteSpace(value) || value=="null" || value.Length>160 || !Regex.IsMatch(value,slash?"^[a-zA-Z0-9_./-]+$":"^[a-zA-Z0-9_.-]+$") || value.Split('/').Any(x=>x is ".." or "."))
            throw new Failure("INVALID_ARGUMENT","Invalid documentation identifier",2);
        return value;
    }
    static async Task<JsonObject> DocPage(string url)
    {
        if(DocsCache.TryGetValue(url,out var cached) && Now-cached.time<900) return (JsonObject)cached.data.DeepClone();
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        using var response=await DocsClient.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        if(!response.IsSuccessStatusCode) return new JsonObject{["url"]=url,["found"]=false,["httpStatus"]=(int)response.StatusCode};
        using var stream=await response.Content.ReadAsStreamAsync(); using var memory=new MemoryStream();byte[] buffer=new byte[8192];
        while(true) {int n=await stream.ReadAsync(buffer);if(n==0) break;if(memory.Length+n>2*1024*1024) throw new Failure("DOC_TOO_LARGE","Documentation exceeds 2 MiB",6);memory.Write(buffer,0,n);}
        string html=System.Text.Encoding.UTF8.GetString(memory.ToArray());
        var body=Regex.Match(html,"<(?:article|div)[^>]*(?:id=[\"']content[\"']|class=[\"'][^\"']*content[^\"']*[\"'])[^>]*>([\\s\\S]*)",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
        if(body.Success) html=body.Groups[1].Value;
        html=Regex.Replace(html,"<(script|style|nav|footer)[^>]*>[\\s\\S]*?</\\1>","",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
        html=Regex.Replace(html,"</(?:p|div|h[1-6]|li|tr|pre|section)>|<br[^>]*>","\n",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
        string text=WebUtility.HtmlDecode(Regex.Replace(html,"<[^>]+>"," ",RegexOptions.None,TimeSpan.FromSeconds(1)));
        text=String.Join("\n",text.Split('\n').Select(x=>Regex.Replace(x,"[ \\t\\r]+"," ").Trim()).Where(x=>x.Length>0));
        bool truncated=text.Length>12000;if(truncated) text=text[..12000];
        var data=new JsonObject{["url"]=url,["found"]=true,["text"]=text,["truncated"]=truncated};
        if(DocsCache.Count>128) DocsCache.Clear();DocsCache[url]=(Now,(JsonObject)data.DeepClone());return data;
    }
    static async Task<JsonNode> Documentation(string project,JsonObject p)
    {
        string supplied=Text(p["version"]);
        if(supplied=="null") supplied=File.ReadAllText(Path.Combine(project,"ProjectSettings/ProjectVersion.txt"));
        var version=Regex.Match(supplied,@"\b(\d{4})\.(\d+)\b");
        if(!version.Success) throw new Failure("INVALID_ARGUMENT","Supply Unity major.minor version",2);
        string baseUrl="https://docs.unity3d.com/"+version.Groups[1].Value+"."+version.Groups[2].Value+"/Documentation/";
        string action=Text(p["action"]);var urls=new List<string>();
        void Script(string name,string? member=null)
        {
            name=DocPart(name.Replace("UnityEngine.","").Replace("UnityEditor.",""));
            if(member!=null && member!="null") {member=DocPart(member);urls.Add(baseUrl+"ScriptReference/"+name+"."+member+".html");urls.Add(baseUrl+"ScriptReference/"+name+"-"+member+".html");}
            else urls.Add(baseUrl+"ScriptReference/"+name+".html");
        }
        void Package(string page)=>urls.Add("https://docs.unity3d.com/Packages/"+DocPart(Text(p["package"]))+"@"+DocPart(Text(p["pkg_version"]))+"/manual/"+DocPart(page,true)+".html");
        if(action=="get_doc") Script(Text(p["class_name"]),Text(p["member_name"]));
        else if(action=="get_manual") urls.Add(baseUrl+"Manual/"+DocPart(Text(p["slug"]),true)+".html");
        else if(action=="get_package_doc") Package(Text(p["page"]));
        else if(action=="lookup")
        {
            string query=Text(p["queries"]??p["query"]);var queries=query.Split(',').Select(x=>x.Trim()).ToArray();
            if(queries.Length>4 || query=="null") throw new Failure("INVALID_ARGUMENT","lookup requires query/queries, maximum 4 topics",2);
            foreach(string q in queries) {Script(q);urls.Add(baseUrl+"Manual/"+DocPart(q,true)+".html");if(p["package"]!=null) Package(q);}
        }
        else throw new Failure("INVALID_ARGUMENT","unity_docs: get_doc, get_manual, get_package_doc, lookup",2);
        // Fetch independent primary-source pages concurrently; a miss stays explicit.
        var pages=await Task.WhenAll(urls.Distinct().Select(async url=>{try{return await DocPage(url);}catch(Exception ex){return new JsonObject{["url"]=url,["found"]=false,["error"]=ex.Message};}}));
        return new JsonObject{["pages"]=new JsonArray(pages.Select(x=>(JsonNode)x).ToArray()),["search"]="exact documentation candidates; no indexed semantic search"};
    }
}
