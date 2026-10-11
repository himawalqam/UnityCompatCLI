using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCompat.Editor
{
    [InitializeOnLoad]
    internal static class MailboxBridge
    {
        const int Protocol = 1;
        const long MaxRequestBytes = 4 * 1024 * 1024;
        internal static readonly string Project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        internal static readonly string Root = Path.Combine(Project, "Library", "UnityCompat");
        internal static readonly string Session = Guid.NewGuid().ToString("N");
        static double nextHeartbeat, nextPoll;
        static bool busy, stopping;
        static string activeId;
        static double activeDeadline;
        static SynchronizationContext context;
        static int wakePosted;
        static double activeUntil;
        internal static double Now => (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

        static MailboxBridge()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            context = SynchronizationContext.Current;
            Directory.CreateDirectory(Root);
            foreach (var dir in new[] { "requests", "running", "responses", "cancellations" })
                Directory.CreateDirectory(Path.Combine(Root, dir));
            // A previous domain must never cause execution to be retried.
            foreach (var file in Directory.GetFiles(Path.Combine(Root, "running"), "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                WriteFailure(id, "INTERRUPTED", "Editor restarted or assemblies reloaded. Side effects may already exist; request was not replayed.");
                File.Delete(file);
            }
            EditorApplication.update += Update;
            EditorApplication.quitting += () => Stop("closed");
            AssemblyReloadEvents.beforeAssemblyReload += () => Stop("reloading");
            PipeBridge.Start();
            EditorApplication.delayCall += () => Heartbeat("ready");
        }

        internal static void Wake()
        {
            if (stopping || context == null || Interlocked.Exchange(ref wakePosted, 1) != 0) return;
            context.Post(_ => { Interlocked.Exchange(ref wakePosted, 0); if (!stopping) Update(); }, null);
        }

        internal static void Write(string path, object value)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(value), new UTF8Encoding(false));
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        break;
                    }
                    catch (IOException) when (attempt < 8) { Thread.Sleep(5); }
                    catch (UnauthorizedAccessException) when (attempt < 8) { Thread.Sleep(5); }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static void Heartbeat(string state)
        {
            Write(Path.Combine(Root, "status.json"), new {
                protocol = Protocol, session = Session, project = Project, unity = Application.unityVersion,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id, heartbeat = Now, state,
                playing = EditorApplication.isPlaying, activeJob = activeId,
                commandCount = CliRegistry.Commands.Count, transport = "named-pipe+atomic-file-mailbox", pipeError = PipeBridge.Error, externalServer = false });
        }

        static void Update()
        {
            if (stopping) return;
            try
            {
                if (EditorApplication.timeSinceStartup >= nextHeartbeat)
                {
                    Heartbeat(EditorApplication.isCompiling ? "compiling" : EditorApplication.isUpdating ? "importing" : busy ? "busy" : "ready");
                    nextHeartbeat = EditorApplication.timeSinceStartup + 1;
                }
                if (EditorApplication.timeSinceStartup < activeUntil) EditorApplication.QueuePlayerLoopUpdate();
                if (busy || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                // Drain a bounded burst rather than one read per idle Editor tick. Keep UI time bounded.
                var slice = System.Diagnostics.Stopwatch.StartNew(); int dispatched = 0;
                while (!busy && dispatched < 32 && slice.ElapsedMilliseconds < 12 && PipeBridge.Take(out var fast))
                { ExecuteFast(fast); dispatched++; }
                // Keep the loop responsive for a short interactive burst, then return to idle.
                // This API is invoked exclusively from the main thread.
                if (dispatched > 0) {activeUntil = EditorApplication.timeSinceStartup + 0.25; EditorApplication.QueuePlayerLoopUpdate();}
                if (busy) return;
                if (EditorApplication.timeSinceStartup < nextPoll) return;
                nextPoll = EditorApplication.timeSinceStartup + 0.05;
                var file = Directory.GetFiles(Path.Combine(Root, "requests"), "*.json")
                    .OrderBy(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (file != null) Execute(file);
            }
            catch (Exception e) { Debug.LogError("[UnityCompat] Mailbox: " + e.Message); }
        }

        static bool ValidId(string id) => id.Length == 32 && id.All(c => "0123456789abcdef".Contains(c));

        static async void ExecuteFast(PipeBridge.Work work)
        {
            var req = work.Request;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string id = req["id"]?.ToString() ?? "";
            if (!ValidId(id)) { work.Completion.TrySetException(new ArgumentException("Invalid job ID.")); return; }
            string running = Path.Combine(Root, "running", id + ".json");
            busy = true;
            activeId = id;
            JObject response;
            bool persist = true, claimed = false;
            try
            {
                if (req["protocol"]?.Value<int>() != Protocol || req["session"]?.ToString() != Session)
                    throw new CliFault("SESSION_CHANGED", "Protocol/session changed; request was not executed.", true);
                activeDeadline = req["deadline"]?.Value<double>() ?? 0;
                CheckActive();
                if (File.Exists(running) || File.Exists(Path.Combine(Root, "responses", id + ".json")))
                { persist = false; throw new CliFault("DUPLICATE_REQUEST", "Duplicate job ID; request was not replayed."); }
                if (!(req["params"] is JObject parameters)) throw new ArgumentException("params must be an object.");
                // Preserve recovery and cancellation semantics, without polling for the response.
                Write(running, req);
                claimed = true;
                var result = await CliRegistry.Invoke(req["command"]?.ToString(), parameters);
                response = new JObject { ["protocol"] = Protocol, ["id"] = id,
                    ["success"] = CliRegistry.Succeeded(result), ["data"] = result, ["session"] = Session, ["completed"] = Now,
                    ["state"] = CliRegistry.Succeeded(result) ? "completed" : "failed", ["timing"] = new JObject {
                        ["queueMs"] = (System.Diagnostics.Stopwatch.GetTimestamp() - work.Received) * 1000.0 / System.Diagnostics.Stopwatch.Frequency - watch.Elapsed.TotalMilliseconds,
                        ["handlerMs"] = watch.Elapsed.TotalMilliseconds } };
            }
            catch (Exception e)
            {
                response = new JObject { ["protocol"] = Protocol, ["id"] = id, ["success"] = false,
                    ["state"] = "failed", ["error"] = CliFault.Error(e) };
            }
            try
            {
                if (!stopping && persist) Write(Path.Combine(Root, "responses", id + ".json"), response);
                work.Completion.TrySetResult(response);
                if (claimed && File.Exists(running)) File.Delete(running);
                string cancellation = Path.Combine(Root, "cancellations", id + ".json");
                if (File.Exists(cancellation)) File.Delete(cancellation);
            }
            finally { activeId = null; busy = false; }
        }

        static async void Execute(string file)
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (!ValidId(id)) { File.Move(file, file + ".invalid"); return; }
            string running = Path.Combine(Root, "running", id + ".json");
            busy = true;
            activeId = id;
            try
            {
                File.Move(file, running); // Claim exactly once before examining or executing it.
                if (new FileInfo(running).Length > MaxRequestBytes) throw new ArgumentException("Request exceeds 4 MiB.");
                var req = JObject.Parse(File.ReadAllText(running));
                if (req["protocol"]?.Value<int>() != Protocol || req["id"]?.ToString() != id)
                    throw new ArgumentException("Request protocol or ID does not match.");
                if (req["session"]?.ToString() != Session)
                { WriteFailure(id, "SESSION_CHANGED", "Editor reloaded before this request started. It was not executed."); return; }
                activeDeadline = req["deadline"]?.Value<double>() ?? 0;
                CheckActive();
                if (!(req["params"] is JObject parameters)) throw new ArgumentException("params must be a JSON object.");
                string command = req["command"]?.ToString();
                var result = await CliRegistry.Invoke(command, parameters);
                if (!stopping)
                    Write(Path.Combine(Root, "responses", id + ".json"), new {
                        protocol = Protocol, id, success = CliRegistry.Succeeded(result), data = result,
                        completed = Now, session = Session });
            }
            catch (OperationCanceledException e) { if (!stopping) WriteFailure(id, "CANCELLED_OR_EXPIRED", e.Message); }
            catch (Exception e) { if (!stopping) WriteFailure(id, e is CliFault f ? f.Code : e is ArgumentException ? "INVALID_ARGUMENT" : "COMMAND_FAILED", e.Message); }
            finally
            {
                if (File.Exists(running)) File.Delete(running);
                string cancellation = Path.Combine(Root, "cancellations", id + ".json");
                if (File.Exists(cancellation)) File.Delete(cancellation);
                activeId = null;
                busy = false;
            }
        }

        internal static void CheckActive()
        {
            if (stopping || (activeId != null && File.Exists(Path.Combine(Root, "cancellations", activeId + ".json"))))
                throw new OperationCanceledException("Cancellation requested. Already-applied changes are not rolled back.");
            if (activeId != null && Now > activeDeadline)
                throw new OperationCanceledException("Request deadline expired. Already-applied changes are not rolled back.");
        }

        static void WriteFailure(string id, string code, string message)
        {
            Write(Path.Combine(Root, "responses", id + ".json"), new {
                protocol = Protocol, id, success = false, error = new { code, message }, completed = Now, session = Session });
        }

        static void Stop(string state)
        {
            stopping = true;
            if (activeId != null) WriteFailure(activeId, "INTERRUPTED", "Editor stopped/reloaded during command; changes may already exist.");
            PipeBridge.Stop();
            Heartbeat(state);
        }
    }
}
