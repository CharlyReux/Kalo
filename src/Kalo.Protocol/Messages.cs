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
}
