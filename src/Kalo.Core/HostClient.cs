using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Kalo.Protocol;

namespace Kalo.Core;

public sealed class HostException(string message, bool engineHalted) : Exception(message)
{
    public bool EngineHalted { get; } = engineHalted;
}

/// <summary>
/// Starts kalo-blamhost.exe and talks to it over a named pipe (one JSON object per line).
/// Calls are serialized: the host handles one request at a time on its ManagedBlam thread.
/// </summary>
public sealed class HostClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NamedPipeServerStream _pipe;
    private StreamWriter? _writer; // created once the host has connected
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<RpcResponse>> _pending = new();
    private int _nextId;

    /// <summary>Host log lines (its stderr and stdout).</summary>
    public event Action<string>? Log;
    public event Action? Exited;
    public bool IsRunning => !_process.HasExited;
    public HelloResult? Info { get; private set; }

    private HostClient(Process process, NamedPipeServerStream pipe)
    {
        _process = process;
        _pipe = pipe;
    }

    public static async Task<HostClient> StartAsync(KaloPaths paths, Action<string>? log = null, CancellationToken ct = default)
    {
        string pipeName = $"kalo-blamhost-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var psi = new ProcessStartInfo(paths.FindBlamHost())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = paths.Workspace,
        };
        psi.ArgumentList.Add("--workspace"); psi.ArgumentList.Add(paths.Workspace);
        psi.ArgumentList.Add("--pipe"); psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--hrek"); psi.ArgumentList.Add(paths.Hrek);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var client = new HostClient(process, pipe);
        if (log != null) client.Log += log;
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) client.Log?.Invoke(e.Data); };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) client.Log?.Invoke(e.Data); };
        process.Exited += (_, _) => client.OnExited();
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await pipe.WaitForConnectionAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                throw new HostException("The Blam Host did not connect within 30 s" + (process.HasExited ? $" (exit code {process.ExitCode})" : ""), false);
            }
        }
        client._writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(client.ReadLoop);
        client.Info = await client.CallAsync<HelloResult>(Methods.Hello, new { }, TimeSpan.FromSeconds(30));
        return client;
    }

    public async Task<T> CallAsync<T>(string method, object parameters, TimeSpan? timeout = null)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var request = new RpcRequest { Id = id, Method = method, Params = JsonSerializer.SerializeToElement(parameters, Wire.Options) };

        await _send.WaitAsync();
        try { await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Options)); }
        finally { _send.Release(); }

        RpcResponse response = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(2));
        if (response.Error != null) throw new HostException(response.Error, response.EngineHalted);
        if (response.Result is not JsonElement result) return default!;
        return result.Deserialize<T>(Wire.Options)!;
    }

    public Task<TagDocument> OpenTagAsync(string path, int? maxElements = null) => CallAsync<TagDocument>(Methods.OpenTag, new TagPathParams { Path = path, MaxElements = maxElements });
    public Task<SetFieldResult> SetFieldAsync(string path, string fieldPath, string value, bool force = false) =>
        CallAsync<SetFieldResult>(Methods.SetField, new SetFieldParams { Path = path, FieldPath = fieldPath, Value = value, Force = force });
    public Task<SaveResult> SaveTagAsync(string path) => CallAsync<SaveResult>(Methods.SaveTag, new TagPathParams { Path = path });
    public Task<SaveResult> SaveTagAsAsync(string path, string newPath) => CallAsync<SaveResult>(Methods.SaveTagAs, new SaveAsParams { Path = path, NewPath = newPath });
    public Task<bool> CloseTagAsync(string path) => CallAsync<bool>(Methods.CloseTag, new TagPathParams { Path = path });
    public Task<BlockOpResult> BlockOpAsync(string path, string blockPath, string op, int index = 0, bool force = false) =>
        CallAsync<BlockOpResult>(Methods.BlockOp, new BlockOpParams { Path = path, FieldPath = blockPath, Op = op, Index = index, Force = force });
    public Task<ScenarioPlacements> GetPlacementsAsync(string scenario) =>
        CallAsync<ScenarioPlacements>(Methods.GetPlacements, new TagPathParams { Path = scenario }, TimeSpan.FromMinutes(3));
    public Task<Placement> SetPlacementAsync(SetPlacementParams p) => CallAsync<Placement>(Methods.SetPlacement, p);
    public Task<PlacementOpResult> PlacementOpAsync(PlacementOpParams p) => CallAsync<PlacementOpResult>(Methods.PlacementOp, p);
    public Task<PlacementStatusResult> PlacementStatusAsync(string path, string? baselineFile) =>
        CallAsync<PlacementStatusResult>(Methods.PlacementStatus, new PlacementStatusParams { Path = path, BaselineFile = baselineFile }, TimeSpan.FromMinutes(2));
    public Task<TagDocument> ReadElementAsync(string path, string block, int index) =>
        CallAsync<TagDocument>(Methods.ReadElement, new ReadElementParams { Path = path, Block = block, Index = index });
    public Task<ObjectInfoResult> ObjectInfoAsync(IEnumerable<string> tags) =>
        CallAsync<ObjectInfoResult>(Methods.ObjectInfo, new TagListParams { Tags = tags.ToList() }, TimeSpan.FromMinutes(2));
    public Task<ShaderInfoResult> ShaderInfoAsync(IEnumerable<string> shaders) =>
        CallAsync<ShaderInfoResult>(Methods.ShaderInfo, new TagListParams { Tags = shaders.ToList() }, TimeSpan.FromMinutes(5));
    public Task<SnapshotResult> SnapshotAsync(string path) => CallAsync<SnapshotResult>(Methods.Snapshot, new SnapshotParams { Path = path });
    public Task<SnapshotResult> RestoreAsync(string path, int id) => CallAsync<SnapshotResult>(Methods.Restore, new SnapshotParams { Path = path, Id = id });
    public Task<MakeVariantResult> MakeVariantAsync(MakeVariantParams p) => CallAsync<MakeVariantResult>(Methods.MakeVariant, p, TimeSpan.FromMinutes(2));
    public Task<ExportLevelMeshResult> ExportLevelMeshAsync(string scenario, string outputFile, IReadOnlyList<int>? bsps = null) =>
        CallAsync<ExportLevelMeshResult>(Methods.ExportLevelMesh, new ExportLevelMeshParams { Scenario = scenario, OutputFile = outputFile, Bsps = bsps?.ToList() }, TimeSpan.FromMinutes(10));
    public Task<DiffTagResult> DiffTagAsync(string path, string vanillaFile) =>
        CallAsync<DiffTagResult>(Methods.DiffTag, new DiffTagParams { Path = path, VanillaFile = vanillaFile }, TimeSpan.FromMinutes(3));
    public Task<CreateMapResult> CreateMapAsync(CreateMapParams p) => CallAsync<CreateMapResult>(Methods.CreateMap, p, TimeSpan.FromMinutes(3));
    public Task<ScriptObjectsResult> ScriptObjectsAsync(ScriptObjectsParams p) => CallAsync<ScriptObjectsResult>(Methods.ScriptObjects, p);
    public Task<ScenarioNamesResult> ScenarioNamesAsync(string path) => CallAsync<ScenarioNamesResult>(Methods.ScenarioNames, new TagPathParams { Path = path }, TimeSpan.FromMinutes(2));
    public Task<SquadDetail> SquadAsync(string path, int index) => CallAsync<SquadDetail>(Methods.Squad, new SquadParams { Path = path, Index = index });
    public Task<SquadOpResult> SquadOpAsync(SquadOpParams p) => CallAsync<SquadOpResult>(Methods.SquadOp, p);
    public Task<BspBoundsResult> BspBoundsAsync(string path) => CallAsync<BspBoundsResult>(Methods.BspBounds, new TagPathParams { Path = path }, TimeSpan.FromMinutes(5));
    public Task<string> ScenarioTypeAsync(string path) => CallAsync<string>(Methods.ScenarioType, new TagPathParams { Path = path }, TimeSpan.FromMinutes(2));
    public Task<MissionScriptResult> ReadMissionScriptAsync(string path) =>
        CallAsync<MissionScriptResult>(Methods.ReadMissionScript, new MissionScriptParams { Path = path });
    public Task<MissionScriptResult> WriteMissionScriptAsync(string path, string text) =>
        CallAsync<MissionScriptResult>(Methods.WriteMissionScript, new MissionScriptParams { Path = path, Text = text });
    public Task<ForgeNamesResult> ForgeNamesAsync() => CallAsync<ForgeNamesResult>(Methods.ForgeNames, new { }, TimeSpan.FromMinutes(5));
    public Task<MegaloCatalog> MegaloCatalogAsync(string objectLists) =>
        CallAsync<MegaloCatalog>(Methods.MegaloCatalog, new MegaloCatalogParams { ObjectLists = objectLists }, TimeSpan.FromMinutes(2));
    public Task<MegaloCompileResult> MegaloCompileAsync(MegaloCompileParams p) =>
        CallAsync<MegaloCompileResult>(Methods.MegaloCompile, p, TimeSpan.FromMinutes(2));

    private async Task ReadLoop()
    {
        var reader = new StreamReader(_pipe, new UTF8Encoding(false));
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                RpcResponse? response;
                try { response = JsonSerializer.Deserialize<RpcResponse>(line, Wire.Options); }
                catch (Exception ex) { Log?.Invoke("Bad response from host: " + ex.Message); continue; }
                if (response != null && _pending.TryRemove(response.Id, out var tcs)) tcs.TrySetResult(response);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Host pipe closed: " + ex.Message);
        }
        FailPending("The Blam Host stopped.");
    }

    private void OnExited()
    {
        FailPending("The Blam Host exited" + (_process.HasExited ? $" (code {_process.ExitCode})." : "."));
        Exited?.Invoke();
    }

    private void FailPending(string message)
    {
        foreach (var key in _pending.Keys.ToList())
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(new HostException(message, false));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) await CallAsync<bool>(Methods.Shutdown, new { }, TimeSpan.FromSeconds(5));
        }
        catch { }
        try { if (!_process.WaitForExit(5000)) _process.Kill(true); } catch { }
        _pipe.Dispose();
        _process.Dispose();
    }
}
