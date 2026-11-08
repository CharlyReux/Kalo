using System;
using System.Collections.Generic;
using Bungie.Tags;
using Kalo.Protocol;

namespace Kalo.BlamHost
{
    /// <summary>
    /// What the player starts with: the scenario's starting profiles. A mission has several (one per insertion, one for
    /// co-op respawns), so writing sets every one of them and reading shows the first.
    /// </summary>
    internal sealed partial class ScenarioService
    {
        private const string Profiles = "Block:player starting profile";

        public StartingGear ReadStartingGear(string path)
        {
            TagFile tag = _tags.GetOrOpen(path);
            var block = tag.SelectField(Profiles) as TagFieldBlock;
            var gear = new StartingGear { Path = path, Profiles = block?.Elements.Count ?? 0 };
            if (gear.Profiles == 0) return gear;
            string e = Profiles + "[0]/";
            gear.Primary = TagService.ValueOf(tag.SelectField(e + "Reference:primary weapon")) ?? "";
            gear.Secondary = TagService.ValueOf(tag.SelectField(e + "Reference:secondary weapon")) ?? "";
            gear.Equipment = TagService.ValueOf(tag.SelectField(e + "Reference:starting equipment")) ?? "";
            gear.Frags = Num(tag, e + "CharInteger:starting fragmentation grenade count");
            gear.Plasmas = Num(tag, e + "CharInteger:starting plasma grenade count");
            return gear;
        }

        public StartingGear WriteStartingGear(StartingGear p)
        {
            TagFile tag = _tags.GetOrOpen(p.Path);
            EnsureStartingProfile(tag);
            var block = tag.SelectField(Profiles) as TagFieldBlock;
            if (block == null) throw new InvalidOperationException("This map has no starting profile block.");
            for (int i = 0; i < block.Elements.Count; i++)
            {
                string e = Profiles + "[" + i + "]/";
                Weapon(tag, e, "primary", p.Primary);
                Weapon(tag, e, "secondary", p.Secondary);
                SetRef(tag, e + "Reference:starting equipment", p.Equipment);
                ((TagFieldElementInteger)tag.SelectField(e + "CharInteger:starting fragmentation grenade count")).Data = p.Frags;
                ((TagFieldElementInteger)tag.SelectField(e + "CharInteger:starting plasma grenade count")).Data = p.Plasmas;
            }
            return ReadStartingGear(p.Path);
        }

        /// <summary>Sets a weapon; a weapon that changed starts with a full magazine and full reserve.</summary>
        private static void Weapon(TagFile tag, string e, string which, string weapon)
        {
            string field = e + "Reference:" + which + " weapon";
            string before = TagService.ValueOf(tag.SelectField(field)) ?? "";
            SetRef(tag, field, weapon);
            if (string.Equals(before, weapon ?? "", StringComparison.OrdinalIgnoreCase)) return;
            int loaded = 0, total = 0;
            if (!string.IsNullOrEmpty(weapon))
                using (var w = new TagFile(TagService.TagPathOf(weapon)))
                {
                    loaded = Num(w, "Block:magazines[0]/ShortInteger:rounds loaded maximum");
                    total = Num(w, "Block:magazines[0]/ShortInteger:rounds total maximum");
                }
            ((TagFieldElementInteger)tag.SelectField(e + "ShortInteger:" + which + "rounds loaded")).Data = Math.Max(0, loaded);
            ((TagFieldElementInteger)tag.SelectField(e + "ShortInteger:" + which + "rounds total")).Data = Math.Max(0, total);
        }

        private static void SetRef(TagFile tag, string field, string value)
        {
            var r = (TagFieldReference)tag.SelectField(field);
            r.Path = string.IsNullOrEmpty(value) ? null : TagService.TagPathOf(value);
        }

        /// <summary>
        /// A shader that shows one bitmap: a copy of the first template with a base_map, the bitmap in its place, shown once
        /// across the object's surface (the template's tiling removed).
        /// </summary>
        public TextureShaderResult MakeTextureShader(TextureShaderParams p)
        {
            const string ps = "Struct:render_method[0]/Block:parameters";
            foreach (string template in p.Templates ?? new List<string>())
            {
                using (var tag = new TagFile(TagService.TagPathOf(template)))
                {
                    var block = tag.SelectField(ps) as TagFieldBlock;
                    for (int i = 0; block != null && i < block.Elements.Count; i++)
                    {
                        string e = ps + "[" + i + "]/";
                        if (!string.Equals(TagService.ValueOf(tag.SelectField(e + "StringId:parameter name")), "base_map", StringComparison.OrdinalIgnoreCase)) continue;
                        ((TagFieldReference)tag.SelectField(e + "Reference:bitmap")).Path = TagService.TagPathOf(p.Bitmap);
                        if (tag.SelectField(e + "Block:animated parameters") is TagFieldBlock animated) animated.RemoveAllElements();
                        _tags.CloseIfOpen(p.NewShader);
                        tag.SaveAs(TagService.TagPathOf(p.NewShader));
                        return new TextureShaderResult { Shader = p.NewShader, Template = template };
                    }
                }
            }
            throw new InvalidOperationException("None of the materials has a color map to replace.");
        }
    }
}
