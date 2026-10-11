using System.Text.Json.Nodes;

namespace UnityCompat.Cli;

internal static partial class Program
{
    static async Task<JsonObject> RunTestWorkflow(Invocation a)
    {
        double deadline=Now+Timeout(a);
        Invocation Call(string name,JsonObject p)
        {
            var call=new Invocation{Command=name,Params=p,JobCreated=a.JobCreated};
            foreach(var option in a.Options) call.Options[option.Key]=option.Value;
            call.Options["timeout"]=Math.Max(.01,deadline-Now).ToString(System.Globalization.CultureInfo.InvariantCulture);return call;
        }
        var start=await Invoke(Call("cli_test_start",a.Params["params"] as JsonObject??new()));
        if(!Successful(start)) return start;
        string id=Text(Payload(start)?["job_id"]);
        if(id=="null") throw new Failure("INVALID_RESPONSE","Test startup did not return a job ID",6);
        // Start exactly once. Only the read-only status query is retried across reloads.
        while(Now<deadline)
        {
            await Task.Delay(150);
            try
            {
                var raw=await Invoke(Call("get_test_job",new JsonObject{["job_id"]=id,["includeDetails"]=true,["includeFailedTests"]=true}));
                if(!Successful(raw))
                {
                    var fault=ResultFailure(raw);
                    if(fault.Code=="INTERRUPTED" || fault.Exit==4) continue;
                    return raw;
                }
                var result=Payload(raw);string status=Text(result?["status"]);
                if(status is "running" or "queued" or "pending") continue;
                int failed=result?["result"]?["summary"]?["failed"]?.GetValue<int>()??0;
                if(status=="failed" || failed>0) return new JsonObject{["success"]=false,["error"]=new JsonObject{
                    ["code"]="TESTS_FAILED",["message"]="Unity test job failed"},["data"]=result?.DeepClone()};
                return new JsonObject{["success"]=true,["data"]=result?.DeepClone()};
            }
            catch(Failure ex) when(ex.Exit is 4 or 7) { /* A read can safely resume in the new Editor domain. */ }
        }
        throw new Failure("TIMEOUT","Test job may still be running; poll get_test_job before starting another run",7,
            new JsonObject{["testJobId"]=id,["mayStillBeRunning"]=true});
    }
}
