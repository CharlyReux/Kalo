using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Bungie;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>Request loop. Everything runs on the main thread, as ManagedBlam requires.</summary>
    internal static class Server
    {
        private static bool _halted;

        public static int Run(string workspace, StreamReader reader, StreamWriter writer)
        {
            var sw = Stopwatch.StartNew();
            var parameters = new ManagedBlamStartupParameters();
            parameters.InitializationLevel = InitializationType.TagsOnly;
            ManagedBlamSystem.Start(workspace, new ManagedBlamCrashCallback(OnCrash), parameters);
            long startMs = sw.ElapsedMilliseconds;

            string tagRoot = ManagedBlamSystem.TagRootPath;
            if (!Program.IsUnder(tagRoot, workspace))
            {
                Log("ManagedBlam's tag root " + tagRoot + " is outside the workspace; refusing to serve.");
                ManagedBlamSystem.Stop();
                return 4;
            }
            Log("ManagedBlam started in " + startMs + " ms, tag root " + tagRoot);

            var tags = new TagService(tagRoot);
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    RpcRequest request;
                    try { request = JsonSerializer.Deserialize<RpcRequest>(line, Wire.Options); }
                    catch (Exception ex)
                    {
                        Log("Bad request: " + ex.Message);
                        continue;
                    }

                    var response = new RpcResponse { Id = request.Id };
                    _halted = false;
                    try
                    {
                        object result = Dispatch(request, workspace, tagRoot, startMs, tags);
                        response.Result = JsonSerializer.SerializeToElement(result, Wire.Options);
                    }
                    catch (Exception ex)
                    {
                        response.Error = ex.GetType().Name == "ManagedBlamCrashException" ? "The engine stopped: " + ex.Message : ex.Message;
                        response.EngineHalted = _halted || ex.GetType().Name == "ManagedBlamCrashException";
                        Log(request.Method + " failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    writer.WriteLine(JsonSerializer.Serialize(response, Wire.Options));

                    if (request.Method == Methods.Shutdown) break;
                }
            }
            finally
            {
                tags.CloseAll();
                try { ManagedBlamSystem.Stop(); } catch (Exception) { }
                Log("Stopped.");
            }
            return 0;
        }

        private static object Dispatch(RpcRequest r, string workspace, string tagRoot, long startMs, TagService tags)
        {
            switch (r.Method)
            {
                case Methods.Hello:
                    return new HelloResult
                    {
                        HostVersion = "0.1",
                        Workspace = workspace,
                        TagRoot = tagRoot,
                        EngineBuild = Safe(() => ManagedBlamSystem.BuildString),
                        TagGroups = TagGroupType.GetTagGroups().Length,
                        StartMs = startMs,
                        ProcessId = Process.GetCurrentProcess().Id
                    };
                case Methods.OpenTag:
                    return tags.Open(Params<TagPathParams>(r).Path);
                case Methods.SetField:
                    var set = Params<SetFieldParams>(r);
                    return tags.SetField(set.Path, set.FieldPath, set.Value);
                case Methods.SaveTag:
                    return tags.Save(Params<TagPathParams>(r).Path);
                case Methods.SaveTagAs:
                    var saveAs = Params<SaveAsParams>(r);
                    return tags.SaveAs(saveAs.Path, saveAs.NewPath);
                case Methods.CloseTag:
                    tags.Close(Params<TagPathParams>(r).Path);
                    return true;
                case Methods.Shutdown:
                    return true;
                default:
                    throw new InvalidOperationException("Unknown method: " + r.Method);
            }
        }

        private static T Params<T>(RpcRequest r)
        {
            T value = r.Params.ValueKind == JsonValueKind.Undefined ? default(T) : r.Params.Deserialize<T>(Wire.Options);
            if (value == null) throw new ArgumentException("Missing parameters for " + r.Method);
            return value;
        }

        private static void OnCrash(ManagedBlamCrashInfo info)
        {
            _halted = true;
            Log("ENGINE " + info.Type + ": " + info.Message + " (" + info.File + ":" + info.Line + ")");
        }

        internal static void Log(string message)
        {
            Console.Error.WriteLine("[blamhost " + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
        }

        private static string Safe(Func<string> get)
        {
            try { return get(); } catch (Exception) { return null; }
        }
    }
}
