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
        public const string DiffTag = "diffTag";
        public const string CreateMap = "createMap";
        public const string ExportTextures = "exportTextures";
        public const string ReadElement = "readElement";
        public const string PlacementStatus = "placementStatus";
        public const string ObjectInfo = "objectInfo";
        public const string ShaderInfo = "shaderInfo";
        public const string MakeVariant = "makeVariant";
        public const string Snapshot = "snapshot";
        public const string Restore = "restore";
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
        /// <summary>Edit a field HREK marks read-only (only for Kalo's own tools, e.g. registering a script object).</summary>
        public bool Force { get; set; }
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
        /// <summary>Append to a block HREK marks read-only (only "add": existing elements keep their positions).</summary>
        public bool Force { get; set; }
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
        /// <summary>When set and the script compiles, the compiler's serialized variant (SerializedEngineBin) is written here.</summary>
        public string BinFile { get; set; }
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
        /// <summary>The object id's unique id (stable across edits; used to compare with vanilla).</summary>
        public int UniqueId { get; set; }
        /// <summary>Editor folder index (Sapien's folders), -1 for none.</summary>
        public int Folder { get; set; } = -1;
        /// <summary>Multiplayer boundary (objective zones): 0 none, 1 sphere, 2 cylinder, 3 box.</summary>
        public int Shape { get; set; }
        /// <summary>Boundary sizes in world units: width or radius, box length, height above, depth below.</summary>
        public float[] ShapeSize { get; set; }
    }

    /// <summary>A Sapien editor folder of the scenario (objects point at one).</summary>
    public sealed class EditorFolder
    {
        public int Index { get; set; }
        public int Parent { get; set; } = -1;
        public string Name { get; set; }
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
        public List<EditorFolder> Folders { get; set; }
        public long Ms { get; set; }
    }

    public sealed class SetPlacementParams
    {
        public string Path { get; set; }
        public string Category { get; set; }
        public int Index { get; set; }
        public float[] Position { get; set; }
        public float[] Rotation { get; set; }
        public float? Scale { get; set; }
    }

    public static class PlacementOps
    {
        /// <summary>New placement of palette entry Type at Position.</summary>
        public const string Add = "add";
        public const string Duplicate = "duplicate";
        public const string Remove = "remove";
        /// <summary>Adds Tag to the category's palette (if missing) and returns its index in SelectIndex.</summary>
        public const string AddPalette = "addPalette";
        /// <summary>Gives objects with a blank multiplayer block (from older Kalo versions) spawnable settings; SelectIndex = how many.</summary>
        public const string RepairMultiplayer = "repairMultiplayer";
        /// <summary>Makes placement Index use palette entry Type (another object, e.g. a repainted block).</summary>
        public const string SetType = "setType";
        /// <summary>Puts placement Index in editor folder Folder (-1: none).</summary>
        public const string SetFolder = "setFolder";
        /// <summary>New editor folder named Tag under Folder (-1: top level); SelectIndex is the new folder.</summary>
        public const string AddFolder = "addFolder";
        public const string RenameFolder = "renameFolder";
        /// <summary>Removes folder Folder; its objects and subfolders move to its parent.</summary>
        public const string DeleteFolder = "deleteFolder";
        /// <summary>Gives placement Index the object name Tag (empty: no name). Names are unique in a map.</summary>
        public const string Rename = "rename";
        /// <summary>
        /// Play from here: copies the saved scenario Path to Tag (a throwaway path) with Index player starting locations at
        /// Position (x, y, z, facing in degrees), so the game starts there. The map itself is not changed.
        /// </summary>
        public const string MakePlayCopy = "makePlayCopy";
        /// <summary>Copy: Data = placement Index serialized (every field), Tag = its object tag. Nothing changes.</summary>
        public const string CopyElement = "copyElement";
        /// <summary>
        /// Paste: a new placement from Data (a CopyElement result) for object Tag at Position, with a fresh object id and
        /// no name. Folder -2 keeps the copied editor folder and parent (same map); otherwise they are cleared.
        /// </summary>
        public const string PasteElement = "pasteElement";
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
        public int Folder { get; set; } = -1;
        /// <summary>pasteElement: a serialized placement (base64).</summary>
        public string Data { get; set; }
    }

    public sealed class PlacementOpResult
    {
        /// <summary>repairMultiplayer: object resource tags of the template map the scenario stopped using (see DetachTemplateResources).</summary>
        public int Detached { get; set; }
        /// <summary>copyElement: the serialized placement (base64) and its object tag in Tag.</summary>
        public string Data { get; set; }
        public string Tag { get; set; }
        public PlacementCategory Category { get; set; }
        public int SelectIndex { get; set; }
        /// <summary>Set by folder operations: every folder after the change.</summary>
        public List<EditorFolder> Folders { get; set; }
        /// <summary>Set by folder operations that move objects: every category after the change.</summary>
        public List<PlacementCategory> Categories { get; set; }
    }

    // ---- object library and materials -------------------------------------------------------------

    public sealed class TagListParams
    {
        public List<string> Tags { get; set; }
    }

    public sealed class ObjectInfoResult
    {
        public List<PaletteEntry> Objects { get; set; }
    }

    public sealed class ShaderInfo
    {
        public string Shader { get; set; }
        /// <summary>The color map shown as the material's preview (empty if none).</summary>
        public string BaseMap { get; set; }
    }

    public sealed class ShaderInfoResult
    {
        public List<ShaderInfo> Shaders { get; set; }
    }

    /// <summary>
    /// A copy of an object (crate, scenery...) with every material of its render model replaced by Shader:
    /// object, model and render model are saved next to each other under NewObject's folder.
    /// </summary>
    public sealed class MakeVariantParams
    {
        public string Object { get; set; }
        public string Shader { get; set; }
        /// <summary>Path of the new object tag, with the same extension as Object.</summary>
        public string NewObject { get; set; }
    }

    public sealed class MakeVariantResult
    {
        public string Object { get; set; }
        public string RenderModel { get; set; }
        public int Materials { get; set; }
    }

    // ---- undo: snapshots of a scenario's placement blocks ---------------------------------------------

    public sealed class SnapshotParams
    {
        public string Path { get; set; }
        /// <summary>For restore: the snapshot to put back.</summary>
        public int Id { get; set; }
    }

    public sealed class SnapshotResult
    {
        public int Id { get; set; }
        public int Bytes { get; set; }
        public long Ms { get; set; }
    }

    // ---- one block element (object properties) ------------------------------------------------

    public sealed class ReadElementParams
    {
        public string Path { get; set; }
        /// <summary>Block name, e.g. scenery.</summary>
        public string Block { get; set; }
        public int Index { get; set; }
    }

    // ---- placements compared with the map they came from -------------------------------------

    public sealed class PlacementStatusParams
    {
        public string Path { get; set; }
        /// <summary>The scenario file to compare with (vanilla, or the template a new map was copied from).</summary>
        public string BaselineFile { get; set; }
    }

    public static class PlacementStates
    {
        public const int Same = 0, Changed = 1, New = 2;
    }

    public sealed class PlacementStatusResult
    {
        /// <summary>Per category, one state per placement index (see PlacementStates).</summary>
        public Dictionary<string, List<int>> States { get; set; }
        /// <summary>Baseline objects no longer in the map.</summary>
        public int Removed { get; set; }
        public long Ms { get; set; }
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

    // ---- compare with vanilla -------------------------------------------------------------------

    public sealed class DiffTagParams
    {
        /// <summary>Tag path relative to tags\, with extension.</summary>
        public string Path { get; set; }
        /// <summary>The vanilla file (HREK's copy). The host copies it into the workspace for the comparison and deletes it after.</summary>
        public string VanillaFile { get; set; }
    }

    public static class DiffKinds
    {
        public const string Changed = "changed";
        public const string Added = "added";
        public const string Removed = "removed";
    }

    public sealed class DiffEntry
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Vanilla { get; set; }
        public string Current { get; set; }
        public string Kind { get; set; }
    }

    public sealed class DiffTagResult
    {
        public List<DiffEntry> Entries { get; set; }
        public int FieldsCompared { get; set; }
        public bool Truncated { get; set; }
        public long Ms { get; set; }
    }

    // ---- new content from templates -------------------------------------------------------------

    public sealed class CreateMapParams
    {
        /// <summary>Scenario to start from (tag path with extension).</summary>
        public string Template { get; set; }
        /// <summary>New scenario path (tag path with extension).</summary>
        public string NewPath { get; set; }
        /// <summary>Remove every object except respawn points (an empty map on the template's level).</summary>
        public bool Empty { get; set; }
        public int MapId { get; set; }
        /// <summary>
        /// Barebone: the template is a small test level (not a multiplayer map); make it a multiplayer scenario and
        /// add one platform with spawn points on it.
        /// </summary>
        public bool Barebone { get; set; }
    }

    public static class StartLocations
    {
        /// <summary>Pseudo placement category for the scenario's player starting locations (where players first spawn).</summary>
        public const string Category = "start locations";
        public const string Block = "player starting locations";
    }

    public static class BareboneMap
    {
        /// <summary>A 58 x 58 unit floor under the sky, shipped with HREK as a test level.</summary>
        public const string Template = @"levels\test\box\box.scenario";
        public const string Platform = @"objects\levels\forge\ff_plat_4x4_flat\ff_plat_4x4_flat.crate";
        public const string RespawnPoint = @"objects\multi\spawning\respawn_point.scenery";
    }

    public sealed class CreateMapResult
    {
        public string Path { get; set; }
        public int Kept { get; set; }
        public int Removed { get; set; }
        public long Ms { get; set; }
    }

    // ---- textures for the map view --------------------------------------------------------------------

    public sealed class ExportTexturesParams
    {
        public List<string> Bitmaps { get; set; }
        public string OutputFolder { get; set; }
        /// <summary>Largest side kept (a smaller mip level is used for big bitmaps).</summary>
        public int MaxSize { get; set; }
    }

    public sealed class TextureInfo
    {
        public string Bitmap { get; set; }
        public string File { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Format { get; set; }
        public string Error { get; set; }
    }

    public sealed class ExportTexturesResult
    {
        public List<TextureInfo> Textures { get; set; }
        public long Ms { get; set; }
    }

    public static class TextureNames
    {
        public static string FileName(string bitmap) => bitmap.Replace('\\', '_').Replace('/', '_') + ".ktex";
    }
}
