using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityCompatVendor.Editor.Helpers;

namespace UnityCompat.Editor
{
    [CliCommand("cli_test_start", "Start a saved-scene test run and return its durable Test Runner job ID", Group = "testing")]
    public static class StartTestCommand
    {
        public static Task<object> HandleCommand(JObject p)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                    throw new CliFault("INVALID_STATE", "Save changed scenes explicitly before unattended tests");
            return UnityCompatVendor.Editor.Tools.RunTests.HandleCommand(p);
        }
    }
    [CliCommand("cli_list", "List all available tools, resources and project extension commands", Group = "cli")]
    public static class ListCommands
    {
        public static object HandleCommand(JObject p)
        {
            string group = p["group"]?.ToString();
            return new SuccessResponse("Commands discovered", CliRegistry.Commands.Values
                .Where(c => String.IsNullOrEmpty(group) || c.Group == group).OrderBy(c => c.Name)
                .Select(c => c.Describe()).ToArray());
        }
    }

    [CliCommand("cli_describe", "Describe one command and its execution metadata", Group = "cli")]
    public static class DescribeCommand
    {
        public static object HandleCommand(JObject p)
        {
            var name = p["name"]?.ToString() ?? "";
            return CliRegistry.Commands.TryGetValue(name, out var c)
                ? (object)new SuccessResponse("Command metadata", c.Describe()) : new ErrorResponse("Unknown command: " + name);
        }
    }

    [CliCommand("cli_batch", "Execute sequential commands with per-operation results and stop-on-error", Group = "cli")]
    public static class BatchCommands
    {
        public static async Task<object> HandleCommand(JObject p)
        {
            if (!(p["operations"] is JArray operations) || operations.Count == 0 || operations.Count > 256)
                throw new ArgumentException("operations must contain 1 to 256 commands.");
            // Validate the entire batch before applying the first mutation.
            foreach (var op in operations)
            {
                string name = op["command"]?.ToString();
                if (name == null || !CliRegistry.Commands.ContainsKey(name) || name == "cli_batch")
                    throw new ArgumentException("Unknown or nested batch command: " + name);
                if (op["params"] != null && !(op["params"] is JObject)) throw new ArgumentException("Each params must be an object.");
            }
            var results = new JArray();
            bool success = true;
            foreach (var op in operations)
            {
                MailboxBridge.CheckActive();
                var name = op["command"].ToString();
                JToken result;
                try { result = await CliRegistry.Invoke(name, op["params"] as JObject); }
                catch (Exception e) { result = new JObject { ["success"] = false, ["error"] = e.Message }; }
                bool ok = CliRegistry.Succeeded(result);
                results.Add(new JObject { ["command"] = name, ["success"] = ok, ["result"] = result });
                success &= ok;
                if (!ok && p["continue_on_error"]?.Value<bool>() != true) break;
            }
            return new JObject { ["success"] = success, ["results"] = results, ["completed"] = results.Count,
                ["total"] = operations.Count, ["rollback"] = false };
        }
    }

    [CliCommand("cli_poll", "Start and await an upstream build, package, asset-generation or test job", Group = "cli")]
    public static class PollCommand
    {
        public static async Task<object> HandleCommand(JObject p)
        {
            string name = p["command"]?.ToString();
            if (name == null || name.StartsWith("cli_")) throw new ArgumentException("Specify an upstream command.");
            var parameters = p["params"] as JObject ?? new JObject();
            if (name == "run_tests")
            {
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                    if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                        throw new InvalidOperationException("Save modified scenes explicitly before running tests; unattended test execution cannot show a save dialog.");
            }
            var result = await CliRegistry.Invoke(name, parameters);
            string jobId = result["data"]?["job_id"]?.ToString();
            bool tests = name == "run_tests";
            while (CliRegistry.Succeeded(result) && ((tests && jobId != null &&
                new[] { "running", "queued", "pending" }.Contains(result["data"]?["status"]?.ToString()?.ToLowerInvariant())) ||
                result["_mcp_status"]?.ToString() == "pending"))
            {
                MailboxBridge.CheckActive();
                await Task.Delay(250);
                MailboxBridge.CheckActive();
                var next = (JObject)parameters.DeepClone();
                next["action"] = "status";
                if (jobId != null) next["job_id"] = jobId;
                if (tests) { next["includeDetails"] = true; next["includeFailedTests"] = true; }
                result = await CliRegistry.Invoke(tests ? "get_test_job" : name, next);
                jobId = result["data"]?["job_id"]?.ToString() ?? jobId;
            }
            // An executed test suite with failing tests must fail the command as well.
            var summary = (result as JObject)?["data"] as JObject;
            var testResult = summary?["result"] as JObject;
            var counts = testResult?["summary"] as JObject;
            if (tests && ((counts?["failed"]?.Value<int?>() ?? 0) > 0 ||
                          result["data"]?["status"]?.ToString() == "failed"))
                return new JObject { ["success"] = false, ["error"] = new JObject { ["code"] = "TESTS_FAILED", ["message"] = "Test suite failed" }, ["data"] = result };
            return result;
        }
    }
}
