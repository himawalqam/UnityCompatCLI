using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

namespace UnityCompat.Editor
{
    // Only framing/authentication runs on worker threads. Dispatch and every Unity API run
    // through MailboxBridge.Update on the Editor's main thread, sharing its execution gate.
    internal static class PipeBridge
    {
        internal sealed class Work
        {
            internal JObject Request;
            internal readonly long Received = System.Diagnostics.Stopwatch.GetTimestamp();
            internal readonly TaskCompletionSource<JObject> Completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        static readonly ConcurrentQueue<Work> pending = new ConcurrentQueue<Work>();
        static readonly ConcurrentDictionary<NamedPipeServerStream, byte> streams = new ConcurrentDictionary<NamedPipeServerStream, byte>();
        static readonly ConcurrentDictionary<Work, byte> work = new ConcurrentDictionary<Work, byte>();
        static volatile bool stopping;
        static string token, name;
        static PipeSecurity security;
        internal static string Error;

        internal static void Start()
        {
#if UNITY_EDITOR_WIN
            token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            // Unity 2021/2022 Mono does not implement WindowsIdentity.User/Owner, which
            // PipeOptions.CurrentUserOnly internally calls. Read the SID through Win32.
            security = new PipeSecurity();
            security.SetSecurityDescriptorSddlForm("D:P(A;;GA;;;" + CurrentSid() + ")");
            name = "ucl-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + MailboxBridge.Session;
            MailboxBridge.Write(Path.Combine(MailboxBridge.Root, "endpoint.json"), new {
                protocol = 1, pipe = name, token, project = MailboxBridge.Project, session = MailboxBridge.Session });
            Task.Run((Action)Listen);
#endif
        }

        static void Listen()
        {
            while (!stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    // Current user ACL: another local account cannot connect to this endpoint.
                    pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 128, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 4096, 4096, security);
                    streams.TryAdd(pipe, 0);
                    pipe.WaitForConnection();
                    if (stopping) break;
                    var connected = pipe;
                    Task.Run(() => Serve(connected));
                    pipe = null;
                }
                catch (Exception e) { Error = e.GetType().Name + ": " + e.Message; if (!stopping) Thread.Sleep(500); }
                finally { if (pipe != null) { streams.TryRemove(pipe, out _); pipe.Dispose(); } }
            }
        }

        static async Task Serve(NamedPipeServerStream pipe)
        {
            Work item = null;
            try
            {
                while (!stopping && pipe.IsConnected)
                {
                    // Warm clients ping during idle time; abandoned connections expire.
                    var read = ReadFrame(pipe);
                    if (await Task.WhenAny(read, Task.Delay(30000)) != read) return;
                    var request = JObject.Parse(await read);
                    if (request["token"]?.ToString() != token) return;
                    if (request["kind"]?.ToString() == "ping")
                    {
                        await Reply(pipe, new JObject { ["protocol"] = 1, ["id"] = request["id"],
                            ["success"] = request["session"]?.ToString() == MailboxBridge.Session,
                            ["session"] = MailboxBridge.Session });
                        continue;
                    }
                    request.Remove("token");
                    item = new Work { Request = request };
                    work.TryAdd(item, 0);
                    pending.Enqueue(item);
                    MailboxBridge.Wake();
                    double deadline = request["deadline"]?.Value<double>() ?? 0;
                    int wait = (int)Math.Min(86400000, Math.Max(0, (deadline - MailboxBridge.Now) * 1000) + 2000);
                    if (await Task.WhenAny(item.Completion.Task, Task.Delay(wait)) != item.Completion.Task) return;
                    await Reply(pipe, await item.Completion.Task);
                    work.TryRemove(item, out _); item = null;
                }
            }
            catch (Exception) { /* Failed clients cannot replay commands; durable jobs remain inspectable. */ }
            finally
            {
                if (item != null) work.TryRemove(item, out _);
                streams.TryRemove(pipe, out _);
                pipe.Dispose();
            }
        }

        static async Task Reply(Stream pipe, JObject response)
        {
            var bytes = Encoding.UTF8.GetBytes(response.ToString(Newtonsoft.Json.Formatting.None));
            if (bytes.Length > 64 * 1024 * 1024) bytes = Encoding.UTF8.GetBytes(new JObject {
                ["protocol"] = 1, ["id"] = response["id"], ["success"] = false,
                ["error"] = new JObject { ["code"] = "RESPONSE_TOO_LARGE", ["message"] = "Use field projection or a lower limit." } }.ToString(Newtonsoft.Json.Formatting.None));
            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), 0, 4);
            await pipe.WriteAsync(bytes, 0, bytes.Length);
            await pipe.FlushAsync();
        }

        static async Task<string> ReadFrame(Stream stream)
        {
            var header = new byte[4];
            await ReadExactly(stream, header);
            int length = BitConverter.ToInt32(header, 0);
            if (length < 2 || length > 4 * 1024 * 1024) throw new InvalidDataException("Invalid frame size.");
            var payload = new byte[length];
            await ReadExactly(stream, payload);
            return Encoding.UTF8.GetString(payload);
        }
        static async Task ReadExactly(Stream stream, byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
        }
        internal static bool Take(out Work item) => pending.TryDequeue(out item);

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr data, int size, out int needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
        static string CurrentSid()
        {
            if (!OpenProcessToken(GetCurrentProcess(), 8, out var handle)) throw new System.ComponentModel.Win32Exception();
            IntPtr data = IntPtr.Zero, text = IntPtr.Zero;
            try
            {
                GetTokenInformation(handle, 1, IntPtr.Zero, 0, out int needed);
                if (needed <= 0 || needed > 65536) throw new InvalidOperationException("Invalid token size.");
                data = Marshal.AllocHGlobal(needed);
                if (!GetTokenInformation(handle, 1, data, needed, out needed) || !ConvertSidToStringSid(Marshal.ReadIntPtr(data), out text))
                    throw new System.ComponentModel.Win32Exception();
                return Marshal.PtrToStringUni(text);
            }
            finally { if (text != IntPtr.Zero) LocalFree(text); if (data != IntPtr.Zero) Marshal.FreeHGlobal(data); CloseHandle(handle); }
        }
        internal static void Stop()
        {
            stopping = true;
            foreach (var item in work.Keys) item.Completion.TrySetResult(new JObject {
                ["protocol"] = 1, ["id"] = item.Request["id"]?.ToString(), ["success"] = false,
                ["error"] = new JObject { ["code"] = "INTERRUPTED", ["message"] = "Editor reloaded/stopped. Inspect the job before retrying changes." } });
            foreach (var pipe in streams.Keys) { try { pipe.Dispose(); } catch { } }
        }
    }
}
