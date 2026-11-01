using System;
using System.Collections.Generic;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>
    /// The objects gametype scripts can create: multiplayer\globals.multiplayer_object_type_list. HREK marks its block
    /// read-only because scripts refer to entries by position; Kalo only appends, renames, or removes the last entry,
    /// which keeps every other position where it was.
    /// </summary>
    internal sealed class ScriptObjectService
    {
        public const string ListTag = @"multiplayer\globals.multiplayer_object_type_list";
        private const string BlockPath = "Block:object types";
        private readonly TagService _tags;

        public ScriptObjectService(TagService tags) { _tags = tags; }

        public ScriptObjectsResult Run(ScriptObjectsParams p)
        {
            TagFile tag = _tags.GetOrOpen(ListTag);
            var block = tag.SelectField(BlockPath) as TagFieldBlock;
            if (block == null) throw new InvalidOperationException("The object type list has no object types block.");
            int count = block.Elements.Count;
            switch (p.Op ?? ScriptObjectOps.List)
            {
                case ScriptObjectOps.List:
                    break;
                case ScriptObjectOps.Add:
                {
                    if (string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.ObjectTag)) throw new ArgumentException("A name and an object tag are needed.");
                    if (count >= block.MaximumElementCount) throw new InvalidOperationException("The object type list is full (" + block.MaximumElementCount + " entries at most).");
                    foreach (var e in Read(tag, count))
                        if (string.Equals(e.Name, p.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The name " + p.Name + " is already used.");
                    block.AddElement();
                    _tags.SetField(ListTag, BlockPath + "[" + count + "]/StringId:name", p.Name, true);
                    _tags.SetField(ListTag, BlockPath + "[" + count + "]/Reference:object", p.ObjectTag, true);
                    tag.Save();
                    break;
                }
                case ScriptObjectOps.Rename:
                    if (p.Index < 0 || p.Index >= count) throw new ArgumentOutOfRangeException("index");
                    _tags.SetField(ListTag, BlockPath + "[" + p.Index + "]/StringId:name", p.Name, true);
                    tag.Save();
                    break;
                case ScriptObjectOps.RemoveLast:
                    if (p.Index != count - 1) throw new InvalidOperationException("Only the last entry can be removed (scripts refer to the others by position).");
                    block.RemoveElement(count - 1);
                    tag.Save();
                    break;
                default:
                    throw new ArgumentException("Unknown script object operation: " + p.Op);
            }
            block = (TagFieldBlock)tag.SelectField(BlockPath);
            return new ScriptObjectsResult { Entries = Read(tag, block.Elements.Count), MaximumCount = block.MaximumElementCount };
        }

        private static List<ScriptObjectEntry> Read(TagFile tag, int count)
        {
            var list = new List<ScriptObjectEntry>(count);
            for (int i = 0; i < count; i++)
                list.Add(new ScriptObjectEntry
                {
                    Index = i,
                    Name = TagService.ValueOf(tag.SelectField(BlockPath + "[" + i + "]/StringId:name")) ?? "",
                    ObjectTag = TagService.ValueOf(tag.SelectField(BlockPath + "[" + i + "]/Reference:object")) ?? "",
                });
            return list;
        }
    }
}
