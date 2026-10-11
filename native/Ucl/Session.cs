using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace UnityCompat.Cli;

internal enum ExitCode { Success=0, Usage=2, Unavailable=4, CommandFailed=6, Timeout=7, TestsFailed=8, Cancelled=9, Conflict=10, Unsupported=11, Authentication=12 }

internal static partial class Program
{
    static readonly ConcurrentDictionary<string,EditorPool> Sessions=new(StringComparer.OrdinalIgnoreCase);
    sealed class EditorPool(string project) : IDisposable
    {
        readonly EditorSession?[] connections=new EditorSession?[8];
        readonly int[] loads=new int[8];
        readonly object gate=new();
        internal async Task<JsonObject> Invoke(string command,JsonObject parameters,double timeout,Action<string>? created=null)
        {
            int slot=0; EditorSession connection;
            lock(gate)
            {
                for(int i=1;i<loads.Length;i++) if(loads[i]<loads[slot]) slot=i;
                loads[slot]++; connection=connections[slot]??=new EditorSession(project);
            }
            try {return await connection.Invoke(command,parameters,timeout,created);}
            finally {lock(gate) loads[slot]--;}
        }
        public void Dispose() {foreach(var connection in connections) connection?.Dispose();}
    }
    static JsonObject Error(Failure f) => new() { ["success"]=false, ["state"]=f.Code is "TIMEOUT_OR_DISCONNECTED" or "TIMEOUT"?"unknown":"failed",
        ["exitCode"]=f.Exit, ["error"]=new JsonObject { ["code"]=f.Code,["message"]=f.Message }, ["data"]=f.Detail?.DeepClone() };
    static Failure ResultFailure(JsonNode? raw)
    {
        var e=raw?["error"] ?? raw?["data"]?["error"];
        string message=e is JsonObject ? Text(e["message"]) : e!=null?Text(e):Text(raw?["data"]?["message"]);
        string code=e is JsonObject && e["code"]!=null ? Text(e["code"]) : "COMMAND_FAILED";
        if(code=="COMMAND_FAILED") code=Text(raw?["data"]?["code"]??raw?["code"]) switch {
            "stale_file" or "precondition_required" or "precondition_failed"=>"PRECONDITION_FAILED",
            "tests_running"=>"EDITOR_BUSY",_=>code };
        var status=code switch { "PRECONDITION_FAILED"=>ExitCode.Conflict,"CANCELLED_OR_EXPIRED"=>ExitCode.Cancelled,
            "UNSUPPORTED"=>ExitCode.Unsupported,"TESTS_FAILED"=>ExitCode.TestsFailed,"AUTHENTICATION_FAILED"=>ExitCode.Authentication,
            "SESSION_CHANGED" or "EDITOR_BUSY"=>ExitCode.Unavailable,_=>ExitCode.CommandFailed };
        return new Failure(code,message,(int)status,raw?.DeepClone());
    }
    static async Task WriteFrame(Stream stream,JsonObject data,CancellationToken cancel,int maxBytes=4*1024*1024)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(data.ToJsonString(JsonOptions));
        if(bytes.Length>maxBytes) throw new Failure("FRAME_TOO_LARGE","Message exceeds frame limit",2);
        byte[] frame=new byte[bytes.Length+4]; BinaryPrimitives.WriteInt32LittleEndian(frame,bytes.Length); bytes.CopyTo(frame,4);
        await stream.WriteAsync(frame,cancel); await stream.FlushAsync(cancel);
    }
    static async Task<JsonObject> ReadFrame(Stream stream,CancellationToken cancel)
    {
        byte[] header=new byte[4]; await stream.ReadExactlyAsync(header,cancel);
        int length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length<2 || length>64*1024*1024) throw new Failure("INVALID_RESPONSE","Response exceeds frame limit");
        byte[] payload=new byte[length]; await stream.ReadExactlyAsync(payload,cancel);
        return JsonNode.Parse(payload) as JsonObject??throw new Failure("INVALID_RESPONSE","Response must be an object");
    }
    sealed class EditorSession : IDisposable
    {
        readonly string project;
        readonly SemaphoreSlim gate=new(1,1);
        readonly Timer keepAlive;
        NamedPipeClientStream? pipe;
        JsonObject? endpoint;
        bool disposed;
        internal EditorSession(string project)
        {
            this.project=project;
            keepAlive=new Timer(_=>{_ = Ping();},null,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5));
        }
        internal async Task<JsonObject> Invoke(string command,JsonObject parameters,double timeout,Action<string>? created=null)
        {
            string id=Guid.NewGuid().ToString("N");
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(timeout)); bool acquired=false,sent=false;
            try
            {
                await gate.WaitAsync(stop.Token); acquired=true;
                var path=Path.Combine(project,"Library/UnityCompat/endpoint.json");
                if(!File.Exists(path)) throw new Failure("FAST_BRIDGE_NOT_RUNNING","Install the bridge and wait for Unity compilation",4);
                var current=ReadFile(path);
                if(!Path.GetFullPath(Text(current["project"])).Equals(project,StringComparison.OrdinalIgnoreCase))
                    throw new Failure("PROJECT_MISMATCH","Endpoint belongs to another project",4);
                if(endpoint==null || Text(endpoint["session"])!=Text(current["session"]) || pipe?.IsConnected!=true)
                {
                    pipe?.Dispose(); endpoint=current;
                    pipe=new NamedPipeClientStream(".",Text(endpoint["pipe"]),PipeDirection.InOut,PipeOptions.Asynchronous);
                    await pipe.ConnectAsync((int)Math.Max(1,Math.Min(timeout*1000,3000)),stop.Token);
                }
                var request=new JsonObject { ["protocol"]=1,["id"]=id,["session"]=endpoint["session"]?.DeepClone(),
                    ["token"]=endpoint["token"]?.DeepClone(),["command"]=command,["params"]=parameters.DeepClone(),["created"]=Now,["deadline"]=Now+timeout };
                // Validate before marking transmission uncertain. Never replay after any write.
                if(Encoding.UTF8.GetByteCount(request.ToJsonString(JsonOptions))>4*1024*1024)
                    throw new Failure("REQUEST_TOO_LARGE","Request exceeds 4 MiB",2);
                created?.Invoke(id); sent=true; await WriteFrame(pipe!,request,stop.Token);
                var response=await ReadFrame(pipe!,stop.Token);
                if(Text(response["id"])!=id || response["protocol"]?.GetValue<int>()!=1)
                { pipe!.Dispose(); throw new Failure("INVALID_RESPONSE","Response ID/protocol mismatch"); }
                return response;
            }
            catch(Exception e) when(e is IOException or TimeoutException or OperationCanceledException)
            {
                if(acquired) {pipe?.Dispose();pipe=null;}
                throw new Failure(sent?"TIMEOUT_OR_DISCONNECTED":"EDITOR_UNAVAILABLE",sent?
                    "Command may have run. Inspect ucl job status "+id+" before retrying.":"Editor is unavailable or the local connection queue timed out.",
                    sent?(int)ExitCode.Timeout:(int)ExitCode.Unavailable,new JsonObject{["jobId"]=id,["mayHaveExecuted"]=sent});
            }
            finally {if(acquired) gate.Release();}
        }
        async Task Ping()
        {
            if(disposed || !await gate.WaitAsync(0)) return;
            try
            {
                if(pipe?.IsConnected!=true || endpoint==null) return;
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await WriteFrame(pipe,new JsonObject{["protocol"]=1,["id"]="ping",["kind"]="ping",
                    ["session"]=endpoint["session"]?.DeepClone(),["token"]=endpoint["token"]?.DeepClone()},stop.Token);
                var response=await ReadFrame(pipe,stop.Token);
                if(!Successful(response)) {pipe.Dispose();pipe=null;}
            }
            catch {pipe?.Dispose();pipe=null;}
            finally {gate.Release();}
        }
        public void Dispose() {disposed=true;keepAlive.Dispose();pipe?.Dispose();}
    }
}
