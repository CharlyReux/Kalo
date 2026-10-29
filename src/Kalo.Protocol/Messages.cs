using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kalo.Protocol
{
    /// <summary>
    /// Wire format between the Kalo app and the Blam Host: one JSON object per line over a named pipe.
    /// The app sends requests, the host answers each one with a response carrying the same id.
    /// </summary>
    public sealed class RpcRequest
    {
        public int Id { get; set; }
        public string Method { get; set; }
        public JsonElement Params { get; set; }
    }

    public sealed class RpcResponse
    {
        public int Id { get; set; }
        public JsonElement? Result { get; set; }
        public string Error { get; set; }
        /// <summary>True when ManagedBlam itself halted; the host should be restarted.</summary>
        public bool EngineHalted { get; set; }
    }

    public static class Methods
    {
        public const string Hello = "hello";
        public const string OpenTag = "openTag";
        public const string SetField = "setField";
        public const string SaveTag = "saveTag";
        public const string SaveTagAs = "saveTagAs";
        public const string CloseTag = "closeTag";
        public const string BlockOp = "blockOp";
        public const string MegaloCatalog = "megaloCatalog";
        public const string GetPlacements = "getPlacements";
        public const string SetPlacement = "setPlacement";
        public const string PlacementOp = "placementOp";
        public const string ExportLevelMesh = "exportLevelMesh";
        public const string ExportModelMeshes = "exportModelMeshes";
        public const string MegaloCompile = "megaloCompile";
        public const string Shutdown = "shutdown";
    }

    public static class Wire
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }

    // ---- hello --------------------------------------------------------------------------------

    public sealed class HelloResult
    {
        public string HostVersion { get; set; }
        public string Workspace { get; set; }
        public string TagRoot { get; set; }
        public string EngineBuild { get; set; }
        public int TagGroups { get; set; }
        public long StartMs { get; set; }
        public int ProcessId { get; set; }
    }

    // ---- tags ---------------------------------------------------------------------------------

    public sealed class TagPathParams
    {
        /// <summary>Path relative to the tags folder, with extension: objects\weapons\rifle\assault_rifle\assault_rifle.weapon</summary>
        public string Path { get; set; }
        /// <summary>openTag only: elements listed per block (default 64). Small values let big tags show every field.</summary>
        public int? MaxElements { get; set; }
    }

    public sealed class SaveAsParams
    {
        public string Path { get; set; }
        public string NewPath { get; set; }
    }

    public static class ValueKinds
    {
        public const string Integer = "integer";
        public const string Real = "real";
        public const string Text = "text";
        public const string Enum = "enum";
        public const string Flags = "flags";
        public const string Reference = "reference";
        public const string Bounds = "bounds";
        public const string BlockIndex = "blockIndex";
        public const string Block = "block";
        public const string Struct = "struct";
        public const string Array = "array";
        public const string Element = "element";
        public const string Explanation = "explanation";
        public const string Other = "other";
    }

    public sealed class FieldNode
    {
        /// <summary>ManagedBlam field path, e.g. Block:magazines[0]/ShortInteger:rounds loaded maximum.</summary>
        public string Path { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        /// <summary>ManagedBlam TagFieldType name (ShortInteger, Real, Block...).</summary>
        public string Type { get; set; }
        /// <summary>One of <see cref="ValueKinds"/>.</summary>
        public string Kind { get; set; }
        public string Units { get; set; }
        public string Description { get; set; }
        /// <summary>Current value, always formatted with the invariant culture ("0.3", never "0,3").</summary>
        public string Value { get; set; }
        public bool Editable { get; set; }
        public bool Visible { get; set; }
        /// <summary>Enum item names or flag names, in index order.</summary>
        public List<string> Options { get; set; }
        public List<FieldNode> Children { get; set; }
        /// <summary>For blocks and arrays: the real element count (Children may be capped).</summary>
        public int ElementCount { get; set; }
        public bool Truncated { get; set; }
        /// <summary>For references: the tag extensions the field accepts (empty or null = any).</summary>
        public List<string> Allowed { get; set; }
    }

    public sealed class TagDocument
    {
        public string Path { get; set; }
        public string Group { get; set; }
        public List<FieldNode> Fields { get; set; }
        public int FieldCount { get; set; }
        public long LoadMs { get; set; }
    }

    public sealed class SetFieldParams
    {
        public string Path { get; set; }
        public string FieldPath { get; set; }
        public string Value { get; set; }
    }

    public sealed class SetFieldResult
    {
        /// <summary>The value as the engine stored it (it may clamp or round).</summary>
        public string Value { get; set; }
    }

    public sealed class SaveResult
    {
        public string File { get; set; }
        public long Ms { get; set; }
    }

    // ---- blocks -------------------------------------------------------------------------------

    public static class BlockOps
    {
        public const string Add = "add";
        public const string Insert = "insert";
        public const string Duplicate = "duplicate";
        public const string Remove = "remove";
        public const string Clear = "clear";
    }

    public sealed class BlockOpParams
    {
        public string Path { get; set; }
        /// <summary>Path of the block field, e.g. Block:magazines.</summary>
        public string FieldPath { get; set; }
        public string Op { get; set; }
        public int Index { get; set; }
    }

    public sealed class BlockOpResult
    {
        /// <summary>The block rebuilt after the change.</summary>
        public FieldNode Block { get; set; }
        /// <summary>The element to select afterwards, or -1.</summary>
        public int SelectIndex { get; set; }
    }

    // ---- megalo (gametype scripts) ------------------------------------------------------------

    public sealed class MegaloEntry
    {
        public string Name { get; set; }
        public string Description { get; set; }
    }

    public sealed class MegaloCatalogParams
    {
        /// <summary>data\multiplayer\megalo\object_lists in the workspace.</summary>
        public string ObjectLists { get; set; }
    }

    public sealed class MegaloCatalog
    {
        public List<MegaloEntry> Actions { get; set; }
        public List<MegaloEntry> Conditions { get; set; }
        /// <summary>Named value lists: weapons, vehicles, objectTypes, sounds, incidents, gameOptions...</summary>
        public Dictionary<string, List<string>> Lists { get; set; }
        public string DeployDirectory { get; set; }
        public long EncodingVersion { get; set; }
        public string EngineBuild { get; set; }
    }

    public sealed class MegaloCompileParams
    {
        public string ObjectLists { get; set; }
        /// <summary>Script text to compile (the editor's current text).</summary>
        public string Text { get; set; }
        /// <summary>Folder that "include" lines are relative to (data\multiplayer\megalo).</summary>
        public string IncludeFolder { get; set; }
        /// <summary>Where the compiled variant goes. Null = do not write it.</summary>
        public string OutputFolder { get; set; }
        /// <summary>When set and the script compiles, the engine (as MegaloEdit deploys it, .mglo) is written here.</summary>
        public string MgloFile { get; set; }
    }

    public sealed class MegaloMessage
    {
        public int Line { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }
        public string Token { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
    }

    public sealed class MegaloCompileResult
    {
        public bool Succeeded { get; set; }
        public List<MegaloMessage> Messages { get; set; }
        public Dictionary<string, long> Stats { get; set; }
        public Dictionary<string, List<string>> Variables { get; set; }
        public List<string> OutputFiles { get; set; }
        public long Ms { get; set; }
    }

    // ---- scenario placements (map editor) -----------------------------------------------------

    public sealed class PaletteEntry
    {
        /// <summary>Object tag, e.g. objects\multi\spawning\respawn_point.scenery.</summary>
        public string Tag { get; set; }
        /// <summary>The object's render model (through its .model tag), or null.</summary>
        public string RenderModel { get; set; }
        public float BoundingRadius { get; set; }
        public float[] BoundingOffset { get; set; }
    }

    public sealed class Placement
    {
        public int Index { get; set; }
        /// <summary>Palette index, -1 if none.</summary>
        public int Type { get; set; }
        public string Name { get; set; }
        /// <summary>World units: x, y, z (z up).</summary>
        public float[] Position { get; set; }
        /// <summary>Degrees: yaw, pitch, roll.</summary>
        public float[] Rotation { get; set; }
        public float Scale { get; set; }
        public string Label { get; set; }
    }

    public sealed class PlacementCategory
    {
        /// <summary>Block name in the scenario: scenery, vehicles, weapons, crates...</summary>
        public string Name { get; set; }
        public List<PaletteEntry> Palette { get; set; }
        public List<Placement> Items { get; set; }
    }

    public sealed class ScenarioPlacements
    {
        public string Path { get; set; }
        public List<string> Bsps { get; set; }
        public List<PlacementCategory> Categories { get; set; }
        public List<Placement> SpawnPoints { get; set; }
        public long Ms { get; set; }
    }

    public sealed class SetPlacementParams
    {
        public string Path { get; set; }
        public string Category { get; set; }
        public int Index { get; set; }
        public float[] Position { get; set; }
        public float[] Rotation { get; set; }
    }

    public static class PlacementOps
    {
        /// <summary>New placement of palette entry Type at Position.</summary>
        public const string Add = "add";
        public const string Duplicate = "duplicate";
        public const string Remove = "remove";
        /// <summary>Adds Tag to the category's palette (if missing) and returns its index in SelectIndex.</summary>
        public const string AddPalette = "addPalette";
    }

    public sealed class PlacementOpParams
    {
        public string Path { get; set; }
        public string Category { get; set; }
        public string Op { get; set; }
        public int Index { get; set; }
        public int Type { get; set; }
        public string Tag { get; set; }
        public float[] Position { get; set; }
    }

    public sealed class PlacementOpResult
    {
        public PlacementCategory Category { get; set; }
        public int SelectIndex { get; set; }
    }

    // ---- level geometry ---------------------------------------------------------------------

    public sealed class ExportLevelMeshParams
    {
        public string Scenario { get; set; }
        /// <summary>Where to write the mesh (Kalo's .kmesh cache format).</summary>
        public string OutputFile { get; set; }
    }

    public sealed class ExportLevelMeshResult
    {
        public string File { get; set; }
        public int Vertices { get; set; }
        public int Triangles { get; set; }
        public int Clusters { get; set; }
        public int Instances { get; set; }
        public long Ms { get; set; }
        public string Notes { get; set; }
    }

    public sealed class ExportModelMeshesParams
    {
        /// <summary>render_model tag paths.</summary>
        public List<string> RenderModels { get; set; }
        /// <summary>Folder for the .kmesh files (one per model, named by <see cref="ModelMeshNames.FileName"/>).</summary>
        public string OutputFolder { get; set; }
    }

    public sealed class ModelMeshInfo
    {
        public string RenderModel { get; set; }
        public string File { get; set; }
        public int Triangles { get; set; }
        public string Error { get; set; }
    }

    public sealed class ExportModelMeshesResult
    {
        public List<ModelMeshInfo> Models { get; set; }
        public long Ms { get; set; }
    }

    public static class ModelMeshNames
    {
        public static string FileName(string renderModel) => renderModel.Replace('\\', '_').Replace('/', '_') + ".kmesh";
    }
}
