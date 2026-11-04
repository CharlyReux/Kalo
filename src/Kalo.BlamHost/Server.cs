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
            var scenarios = new ScenarioService(tags);
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
                        object result = Dispatch(request, workspace, tagRoot, startMs, tags, scenarios);
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

        private static object Dispatch(RpcRequest r, string workspace, string tagRoot, long startMs, TagService tags, ScenarioService scenarios)
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
                    var open = Params<TagPathParams>(r);
                    return tags.Open(open.Path, open.MaxElements ?? 64);
                case Methods.SetField:
                    var set = Params<SetFieldParams>(r);
                    return tags.SetField(set.Path, set.FieldPath, set.Value, set.Force);
                case Methods.SaveTag:
                {
                    string savePath = Params<TagPathParams>(r).Path;
                    // Kalo writes only the .scenario: the game must not keep loading older copies from split resource tags.
                    if (savePath.EndsWith(".scenario", StringComparison.OrdinalIgnoreCase)) scenarios.BeforeSave(savePath);
                    return tags.Save(savePath);
                }
                case Methods.ScenarioNames:
                    return scenarios.Names(Params<TagPathParams>(r).Path);
                case Methods.Squad:
                    var sq = Params<SquadParams>(r);
                    return scenarios.Squad(sq.Path, sq.Index);
                case Methods.Categories:
                    return scenarios.Categories(Params<CategoriesParams>(r));
                case Methods.LevelPieces:
                    return scenarios.LevelPieces(Params<LevelPiecesParams>(r));
                case Methods.HideLevelPieces:
                    return scenarios.HideLevelPieces(Params<HideLevelPiecesParams>(r));
                case Methods.SquadOp:
                    return scenarios.SquadOp(Params<SquadOpParams>(r));
                case Methods.BspBounds:
                    return scenarios.BspBounds(Params<TagPathParams>(r).Path);
                case Methods.ScenarioType:
                    return scenarios.TypeOf(Params<TagPathParams>(r).Path);
                case Methods.ReadMissionScript:
                    return scenarios.ReadMissionScript(Params<MissionScriptParams>(r));
                case Methods.WriteMissionScript:
                    return scenarios.WriteMissionScript(Params<MissionScriptParams>(r));
                case Methods.SaveTagAs:
                    var saveAs = Params<SaveAsParams>(r);
                    return tags.SaveAs(saveAs.Path, saveAs.NewPath);
                case Methods.CloseTag:
                    tags.Close(Params<TagPathParams>(r).Path);
                    return true;
                case Methods.BlockOp:
                    var op = Params<BlockOpParams>(r);
                    return tags.BlockOp(op.Path, op.FieldPath, op.Op, op.Index, op.Force);
                case Methods.MegaloCatalog:
                    return MegaloService.Catalog(Params<MegaloCatalogParams>(r).ObjectLists);
                case Methods.MegaloCompile:
                    return MegaloService.Compile(Params<MegaloCompileParams>(r));
                case Methods.GetPlacements:
                    return scenarios.Get(Params<TagPathParams>(r).Path);
                case Methods.SetPlacement:
                    return scenarios.Set(Params<SetPlacementParams>(r));
                case Methods.PlacementOp:
                    return scenarios.Op(Params<PlacementOpParams>(r));
                case Methods.ObjectInfo:
                    return scenarios.ObjectInfo(Params<TagListParams>(r));
                case Methods.ShaderInfo:
                    return LevelMeshService.ShaderInfo(Params<TagListParams>(r));
                case Methods.MakeVariant:
                    return scenarios.MakeVariant(Params<MakeVariantParams>(r));
                case Methods.Snapshot:
                    return scenarios.Snapshot(Params<SnapshotParams>(r));
                case Methods.Restore:
                    return scenarios.Restore(Params<SnapshotParams>(r));
                case Methods.PlacementStatus:
                    return scenarios.Status(Params<PlacementStatusParams>(r));
                case Methods.ReadElement:
                    var el = Params<ReadElementParams>(r);
                    return tags.ReadElement(el.Path, el.Block, el.Index);
                case Methods.ExportLevelMesh:
                    var mesh = Params<ExportLevelMeshParams>(r);
                    return LevelMeshService.Export(tags, mesh.Scenario, mesh.OutputFile, mesh.Bsps);
                case Methods.ExportModelMeshes:
                    var models = Params<ExportModelMeshesParams>(r);
                    return LevelMeshService.ExportModels(models.RenderModels, models.OutputFolder);
                case Methods.DiffTag:
                    var diff = Params<DiffTagParams>(r);
                    return tags.Diff(diff.Path, diff.VanillaFile);
                case Methods.CreateMap:
                    return scenarios.CreateMap(Params<CreateMapParams>(r));
                case Methods.ScriptObjects:
                    return new ScriptObjectService(tags).Run(Params<ScriptObjectsParams>(r));
                case Methods.ForgeNames:
                    return ForgeNameService.Run(tags);
                case Methods.ExportTextures:
                    var tex = Params<ExportTexturesParams>(r);
                    return TextureService.Export(tex.Bitmaps, tex.OutputFolder, tex.MaxSize <= 0 ? 256 : tex.MaxSize);
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
