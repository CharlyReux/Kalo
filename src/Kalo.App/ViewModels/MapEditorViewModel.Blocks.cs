using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// Block building (the tool strip on the left of the 3D view): Forge pieces placed in front of the camera, snapped
/// to a grid, and painted with any level material. Painting makes a copy of the piece whose render model uses the
/// chosen shader, saved in the mod (levels\mods\&lt;mod&gt;\blocks\...), and swaps the placement to it.
/// Real level geometry (a baked BSP) comes later; blocks are objects, so they are instant and playable now.
/// </summary>
public partial class MapEditorViewModel
{
    public static readonly BlockTool[] BlockTools =
    {
        new("floor", "Floor", "", "4 × 4 flat platform", "ff_plat_4x4_flat"),
        new("block", "Block", "", "2 × 2 platform block", "ff_plat_2x2"),
        new("wall", "Wall", "", "2 × 2 wall", "ff_wall_2x2"),
        new("ramp", "Ramp", "", "2 × 2 ramp", "ff_ramp_2x2"),
        new("stairs", "Stairs", "", "Staircase", "ff_stair_case"),
        new("column", "Column", "", "Column", "ff_column"),
        new("bridge", "Bridge", "", "2 × 2 bridge", "ff_bridge_2x2"),
        new("cover", "Cover", "", "Low cover", "ff_cover_2"),
        new("respawn", "Respawn", "\uE77B", "Respawn point: where players come back after dying", @"objects\multi\spawning\respawn_point.scenery"),
        new("start", "Start", "\uE768", "Start location: where players appear when the game starts", StartLocations.Category),
    };

    [ObservableProperty] private bool _isPaintOpen;
    [ObservableProperty] private bool _snapToGrid = true;
    public IReadOnlyList<BlockTool> Tools => BlockTools;

    /// <summary>The object tag of a Forge piece (crate, scenery or machine, whichever the workspace has).</summary>
    private string? BlockTag(BlockTool tool)
    {
        if (tool.Piece.Contains('\\')) return _main.TagIndex.FirstOrDefault(t => t.Equals(tool.Piece, StringComparison.OrdinalIgnoreCase));
        string folder = $@"objects\levels\forge\{tool.Piece}\{tool.Piece}.";
        return _main.TagIndex.FirstOrDefault(t => t.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && CategoryFor(t) != null);
    }

    [RelayCommand]
    private async Task PlaceBlock(BlockTool? tool)
    {
        using var edit = await EditAsync("place block");
        if (tool == null) return;
        if (tool.Piece == StartLocations.Category)
        {
            Vector3 spot = PlacementPointProvider?.Invoke() ?? Selected?.Position ?? Vector3.Zero;
            await RunOpAsync(new PlacementOpParams { Path = Path, Category = StartLocations.Category, Op = PlacementOps.Add, Position = new[] { spot.X, spot.Y, spot.Z + 0.1f } }, StartLocations.Category);
            Status = "Added a start location. Players appear at one of these when the game starts; respawn points are used after dying.";
            return;
        }
        string? tag = BlockTag(tool);
        if (tag == null) { Status = $"The {tool.Name} piece ({tool.Piece}) is not in the workspace."; return; }
        Vector3 at = PlacementPointProvider?.Invoke() ?? Selected?.Position ?? Vector3.Zero;
        if (SnapToGrid) at = new Vector3(MathF.Round(at.X * 2) / 2, MathF.Round(at.Y * 2) / 2, MathF.Round(at.Z * 4) / 4);
        var item = await AddTagAtAsync(tag, at);
        if (item != null) Status = $"Placed a {tool.Name.ToLowerInvariant()}. Drag it (Ctrl snaps), R turns it, the brush paints it.";
    }

    [RelayCommand]
    private async Task TogglePaint()
    {
        IsPaintOpen = !IsPaintOpen;
        if (IsPaintOpen) await _main.EnsureMaterialsAsync();
    }

    /// <summary>Paints every selected object with a material (one copy of each piece per material, reused).</summary>
    [RelayCommand]
    private async Task Paint(MaterialChoice? material)
    {
        using var edit = await EditAsync("paint");
        if (material == null || _main.Host == null) return;
        if (_selection.Count == 0) { Status = "Select the blocks to paint first."; return; }
        string mod = _main.ModSlugForContent;
        string shaderName = MainViewModel.Slug(System.IO.Path.GetFileNameWithoutExtension(material.Shader));
        int painted = 0;
        var keys = _selection.Select(i => (i.Category, i.Index)).ToHashSet();
        foreach (var key in _selection.Select(i => (i.Category, i.Index, i.Tag)).ToList())
        {
            string ext = System.IO.Path.GetExtension(key.Tag);
            string baseName = System.IO.Path.GetFileNameWithoutExtension(key.Tag);
            int cut = baseName.IndexOf("__", StringComparison.Ordinal);
            if (cut > 0) baseName = baseName[..cut]; // repainting a painted copy: start from the original piece
            string source = cut > 0 ? OriginalOf(key.Tag) ?? key.Tag : key.Tag;
            string name = $"{baseName}__{shaderName}";
            string newObject = $@"levels\mods\{mod}\blocks\{name}\{name}{ext}";
            try
            {
                if (!System.IO.File.Exists(System.IO.Path.Combine(_main.Paths!.WorkspaceTags, newObject)))
                {
                    var r = await _main.Host.MakeVariantAsync(new MakeVariantParams { Object = source, Shader = material.Shader, NewObject = newObject });
                    _main.Log($"Painted copy {newObject}: {r.Materials} material(s) → {material.Name}.");
                }
                _main.ModProjects?.UpdateActive(m => m.PaintedBlocks[newObject] = new PaintedBlock(source, material.Shader));
                var pal = await _main.Host.PlacementOpAsync(new PlacementOpParams { Path = Path, Category = key.Category, Op = PlacementOps.AddPalette, Tag = newObject });
                var set = await _main.Host.PlacementOpAsync(new PlacementOpParams { Path = Path, Category = key.Category, Op = PlacementOps.SetType, Index = key.Index, Type = pal.SelectIndex });
                if (set.Category != null) ReplaceCategory(set.Category);
                painted++;
            }
            catch (HostException ex)
            {
                _main.AddProblem("error", $"Paint {baseName}: {ex.Message}", Path);
            }
        }
        IsDirty = true;
        RefreshPalette();
        // The new copies need their models (and the material's textures) in the view.
        var models = Items.Select(i => i.RenderModel).OfType<string>().Distinct().ToList();
        await _main.Models.LoadAsync(_main.Host, models);
        foreach (var i in Items) i.Model = _main.Models.Get(i.RenderModel);
        SetSelection(Items.Where(i => keys.Contains((i.Category, i.Index))).ToList());
        _ = EnsureTexturesAsync();
        SceneChanged?.Invoke();
        Status = painted == 0 ? Status : $"Painted {painted} object{(painted == 1 ? "" : "s")} with {material.Name}. The copies are part of your mod.";
        _main.RefreshChangesAfterEdit();
    }

    /// <summary>For a painted copy levels\mods\m\blocks\ff_x__mat\ff_x__mat.crate: the Forge piece it came from.</summary>
    private string? OriginalOf(string tag)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(tag);
        int cut = name.IndexOf("__", StringComparison.Ordinal);
        if (cut <= 0) return null;
        string piece = name[..cut];
        string prefix = $@"objects\levels\forge\{piece}\{piece}{System.IO.Path.GetExtension(tag)}";
        return _main.TagIndex.FirstOrDefault(t => t.Equals(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

public partial class MapEditorViewModel
{
    /// <summary>
    /// Painted copies this map uses whose files are gone (removed from the mod, or lost): rebuilt from the record the
    /// mod keeps (or, for older paints, from the piece and shader names in the copy's path). Returns how many.
    /// </summary>
    private async Task<int> RebuildMissingBlocksAsync(ScenarioPlacements data)
    {
        if (_main.Host == null || _main.Paths == null) return 0;
        var records = _main.ModProjects?.Active?.Manifest.PaintedBlocks ?? new Dictionary<string, PaintedBlock>();
        int rebuilt = 0;
        foreach (var entry in (data.Categories ?? new()).SelectMany(c => c.Palette ?? new()))
        {
            string? tag = entry.Tag;
            if (string.IsNullOrEmpty(tag) || !tag.Contains(@"\blocks\", StringComparison.OrdinalIgnoreCase)) continue;
            if (System.IO.File.Exists(System.IO.Path.Combine(_main.Paths.WorkspaceTags, tag))) continue;
            PaintedBlock? how = records.TryGetValue(tag, out var r) ? r : Guess(tag);
            if (how == null) { _main.AddProblem("warning", $"{Title} uses {tag}, which is missing (shown as a box).", Path); continue; }
            try
            {
                await _main.Host.MakeVariantAsync(new MakeVariantParams { Object = how.Source, Shader = how.Shader, NewObject = tag });
                _main.ModProjects?.UpdateActive(m => m.PaintedBlocks[tag] = how);
                rebuilt++;
            }
            catch (HostException ex)
            {
                _main.AddProblem("warning", $"Could not rebuild the painted block {tag}: {ex.Message}", Path);
            }
        }
        if (rebuilt > 0) _main.Log($"{Title}: rebuilt {rebuilt} painted block(s) whose files were missing.");
        return rebuilt;
    }

    /// <summary>levels\mods\m\blocks\ff_plat__shader_slug\...: the Forge piece, and a shader whose name slugs the same.</summary>
    private PaintedBlock? Guess(string tag)
    {
        string? source = OriginalOf(tag);
        string name = System.IO.Path.GetFileNameWithoutExtension(tag);
        int cut = name.IndexOf("__", StringComparison.Ordinal);
        if (source == null || cut <= 0) return null;
        string slug = name[(cut + 2)..];
        string? shader = _main.TagIndex.FirstOrDefault(t => t.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) &&
                                                            MainViewModel.Slug(System.IO.Path.GetFileNameWithoutExtension(t)) == slug);
        return shader == null ? null : new PaintedBlock(source, shader);
    }
}

/// <summary>A button of the block tool strip: a Forge piece.</summary>
public sealed record BlockTool(string Key, string Name, string Icon, string Tip, string Piece);
