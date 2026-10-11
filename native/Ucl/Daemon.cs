using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UnityCompat.Cli;

internal static partial class Program
{
    static async Task<JsonObject> DaemonInvoke(Invocation a)
    {
        string project=Project(a),path=Path.Combine(project,"Library/UnityCompat/daemon.json");
        var descriptor=ReadFile(path);
        if(!String.Equals(Text(descriptor["project"]),project,StringComparison.OrdinalIgnoreCase))
            throw new Failure("PROJECT_MISMATCH","Daemon belongs to another project",4);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(Timeout(a)));
        using var pipe=new NamedPipeClientStream(".",Text(descriptor["pipe"]),PipeDirection.InOut,PipeOptions.Asynchronous);
        bool sent=false;
        try
        {
            await pipe.ConnectAsync(2000,stop.Token);
            var request=new JsonObject{["token"]=descriptor["token"]?.DeepClone(),["command"]=a.Command,
                ["params"]=a.Params.DeepClone(),["timeout"]=Timeout(a)};
            if(Encoding.UTF8.GetByteCount(request.ToJsonString(JsonOptions))>4*1024*1024) throw new Failure("REQUEST_TOO_LARGE","Request exceeds 4 MiB",2);
            sent=true; await WriteFrame(pipe,request,stop.Token); var response=await ReadFrame(pipe,stop.Token);
            if(response["daemonError"]?.GetValue<bool>()==true)
                throw new Failure(Text(response["error"]?["code"]),Text(response["error"]?["message"]),response["exitCode"]?.GetValue<int>()??6,response["data"]?.DeepClone());
            return response;
        }
        catch(Exception e) when(e is IOException or TimeoutException or OperationCanceledException)
        {
            throw new Failure(sent?"TIMEOUT_OR_DISCONNECTED":"DAEMON_UNAVAILABLE",sent?
                "Daemon connection failed after transmission. Inspect recent jobs before retrying modifications.":
                "Daemon is not responding. Run ucl daemon stop, then start, or use --direct.",sent?7:4);
        }
    }
    static async Task<int> Daemon(Invocation a)
    {
        Arity(a,2,2); Allow(a); string project=Project(a),root=Path.Combine(project,"Library/UnityCompat");
        Directory.CreateDirectory(root); string path=Path.Combine(root,"daemon.json"); string action=a.Words[1];
        if(action=="serve") return await ServeDaemon(project,path);
        if(File.Exists(path))
        {
            var descriptor=ReadFile(path);
            if(!String.Equals(Text(descriptor["project"]),project,StringComparison.OrdinalIgnoreCase)) throw new Failure("PROJECT_MISMATCH","Daemon belongs to another project",4);
            bool dead=false;
            try
            {
                using var worker=Process.GetProcessById(descriptor["pid"]!.GetValue<int>());
                dead=worker.HasExited || Math.Abs(new DateTimeOffset(worker.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()/1000.0-
                    descriptor["started"]!.GetValue<double>())>5;
            }
            catch(ArgumentException) {dead=true;}
            if(dead) File.Delete(path); // Remove only a dead/reused process descriptor, never kill by PID.
        }
        if(action=="start")
        {
            if(File.Exists(path))
            {
                var existing=ReadFile(path); a.Command="@status";
                var healthy=await DaemonInvoke(a); Render(a,healthy); return 0;
            }
            int child=StartDetached(project);
            double until=Now+5;
            while(Now<until && !File.Exists(path)) await Task.Delay(20);
            if(!File.Exists(path)) throw new Failure("DAEMON_START_FAILED","Daemon did not become ready",4);
            a.Command="@status"; Render(a,await DaemonInvoke(a)); return 0;
        }
        if(action is "status" or "stop")
        {
            if(!File.Exists(path)) {Render(a,new JsonObject{["running"]=false});return 0;}
            a.Command=action=="status"?"@status":"@stop";
            Render(a,await DaemonInvoke(a));return 0;
        }
        throw new Failure("BAD_ARGUMENTS","daemon: start, status, stop (serve is the foreground worker)",2);
    }
    static async Task<int> ServeDaemon(string project,string path)
    {
        // An OS named mutex prevents two workers from winning simultaneous startup.
        string hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project.ToUpperInvariant())))[..24];
        using var ownership=new Mutex(true,"Local\\UnityCompatDaemon-"+hash,out bool owned);
        if(!owned) return 4;
        string name="ucl-daemon-"+hash+"-"+Guid.NewGuid().ToString("N"),token=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
        string temp=path+".tmp"; File.WriteAllText(temp,new JsonObject{["protocol"]=1,["project"]=project,
            ["pipe"]=name,["token"]=token,["pid"]=Environment.ProcessId,["started"]=Now}.ToJsonString(JsonOptions)); File.Move(temp,path,true);
        using var shutdown=new CancellationTokenSource(); var clients=new List<Task>();
        try
        {
            while(!shutdown.IsCancellationRequested)
            {
                var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,32,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                try {await pipe.WaitForConnectionAsync(shutdown.Token);}
                catch {pipe.Dispose();break;}
                clients.RemoveAll(t=>t.IsCompleted);
                clients.Add(Task.Run(async()=>
                {
                    using(pipe)
                    {
                        try
                        {
                            using var idle=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            var request=await ReadFrame(pipe,idle.Token);
                            if(Text(request["token"])!=token) return;
                            string command=Text(request["command"]); JsonObject result;
                            if(command is "@status" or "@stop") result=new JsonObject{["running"]=command!="@stop",["pid"]=Environment.ProcessId,["project"]=project,["transport"]="persistent-editor-pipe"};
                            else
                            {
                                double timeout=request["timeout"]?.GetValue<double>()??60;
                                if(!double.IsFinite(timeout) || timeout<=0 || timeout>86400) throw new Failure("INVALID_TIMEOUT","Invalid timeout",2);
                                result=await Sessions.GetOrAdd(project,p=>new EditorPool(p)).Invoke(command,request["params"] as JsonObject??new(),timeout);
                            }
                            await WriteFrame(pipe,result,CancellationToken.None,64*1024*1024);
                            if(command=="@stop") shutdown.Cancel();
                        }
                        catch(Exception ex)
                        {
                            try {var result=Error(ex as Failure??new Failure("DAEMON_ERROR",ex.Message)); result["daemonError"]=true;
                                await WriteFrame(pipe,result,CancellationToken.None,64*1024*1024);} catch { }
                        }
                    }
                }));
            }
            await Task.WhenAny(Task.WhenAll(clients),Task.Delay(2000)); return 0;
        }
        finally
        {
            foreach(var s in Sessions.Values) s.Dispose();
            if(File.Exists(path) && ReadFile(path)["pid"]?.GetValue<int>()==Environment.ProcessId) File.Delete(path);
            // Mutex ownership is thread-affine; disposing releases process ownership on exit.
        }
    }
}
