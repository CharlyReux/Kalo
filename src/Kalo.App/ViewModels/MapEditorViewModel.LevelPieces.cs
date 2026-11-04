using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kalo.Core;
using Kalo.Protocol;

namespace Kalo.App.ViewModels;

/// <summary>
/// Level pieces: the level's own buildings, rocks, pipes, crates (instanced geometry of its bsps). The map view lists the
/// pieces where the view is aimed and hides or shows them, one by one or a whole building at once. Hidden pieces are
/// in the map's own copy of the bsp (the original level is untouched).
/// </summary>
public partial class MapEditorViewModel
{
    public ObservableCollection<LevelPieceRow> LevelPieces { get; } = new();
    [ObservableProperty] private bool _showLevelPieces;
    [ObservableProperty] private string _levelPiecesTitle = "";
    [ObservableProperty] private bool _canShowAllPieces;

    private bool _piecesHidden; // the last list was the hidden pieces

    [RelayCommand]
    private void CloseLevelPieces() => ShowLevelPieces = false;

    /// <summary>The pieces at the point the view is aimed at (the center of the view).</summary>
    [RelayCommand]
    private async Task FindLevelPieces()
    {
        if (_main.Host == null) return;
        Vector3? aim = PlacementPointProvider?.Invoke() ?? CursorPoint;
        if (aim is not { } at) { Status = "Aim the view at a building or rock first."; return; }
        using var busy = Busy("Finding level pieces…");
        try
        {
            var r = await _main.Host.LevelPiecesAsync(new LevelPiecesParams { Path = Path, Point = new[] { at.X, at.Y, at.Z }, Radius = 1.5f, Bsps = SelectedZoneSet?.Bsps?.ToList() });
            _piecesHidden = false;
            FillPieces(r, r.Pieces.Count == 0
                ? "No level piece where the view is aimed (the ground itself can't be hidden)."
                : $"Level pieces where the view is aimed ({r.Total}{(r.Total > r.Pieces.Count ? ", nearest " + r.Pieces.Count : "")})");
            Status = $"{r.Total} level pieces near the center of the view ({r.Ms} ms).";
        }
        catch (HostException ex) { _main.AddProblem("error", "Level pieces: " + ex.Message, Path); }
    }

    /// <summary>Every hidden piece of the map, to show them again.</summary>
    [RelayCommand]
    private async Task HiddenLevelPieces()
    {
        if (_main.Host == null) return;
        using var busy = Busy("Finding hidden level pieces…");
        try
        {
            var r = await _main.Host.LevelPiecesAsync(new LevelPiecesParams { Path = Path, HiddenOnly = true, Max = 20000 });
            _piecesHidden = true;
            // A building hidden whole is one row; pieces hidden one by one are listed.
            var rows = new List<LevelPieceRow>();
            foreach (var g in r.Pieces.GroupBy(p => (p.Bsp, p.Group ?? "")))
            {
                var first = g.First();
                if (g.Key.Item2.Length > 0 && first.GroupCount > 1 && g.Count() == first.GroupCount) rows.Add(new LevelPieceRow(this, first, wholeGroup: true));
                else rows.AddRange(g.Select(p => new LevelPieceRow(this, p)));
            }
            LevelPieces.Clear();
            foreach (var row in rows.Take(300)) LevelPieces.Add(row);
            LevelPiecesTitle = r.Total == 0 ? "No hidden level piece in this map." : $"Hidden level pieces ({r.Total})";
            CanShowAllPieces = LevelPieces.Count > 1 || LevelPieces.Any(x => x.IsWholeGroup);
            ShowLevelPieces = true;
        }
        catch (HostException ex) { _main.AddProblem("error", "Level pieces: " + ex.Message, Path); }
    }

    private void FillPieces(LevelPiecesResult r, string title)
    {
        LevelPieces.Clear();
        foreach (var p in r.Pieces) LevelPieces.Add(new LevelPieceRow(this, p));
        LevelPiecesTitle = title;
        CanShowAllPieces = LevelPieces.Count(p => p.Hidden) > 1;
        ShowLevelPieces = true;
    }

    [RelayCommand]
    private Task TogglePiece(LevelPieceRow? row) =>
        row == null ? Task.CompletedTask
        : row.IsWholeGroup ? ChangePiecesAsync(row.Piece.Bsp, null, row.Piece.Group, false, row.GroupName + $" ({row.Piece.GroupCount} pieces)")
        : ChangePiecesAsync(row.Piece.Bsp, new List<int> { row.Piece.Index }, null, !row.Piece.Hidden, row.Piece.Name ?? "piece");

    [RelayCommand]
    private Task ToggleGroup(LevelPieceRow? row) =>
        row == null || string.IsNullOrEmpty(row.Piece.Group) ? Task.CompletedTask
            : ChangePiecesAsync(row.Piece.Bsp, null, row.Piece.Group, !row.Piece.Hidden, row.GroupName + $" ({row.Piece.GroupCount} pieces)");

    [RelayCommand]
    private async Task ShowAllPieces()
    {
        foreach (int bsp in LevelPieces.Where(r => r.Piece.Hidden).Select(r => r.Piece.Bsp).Distinct().ToList())
            await ChangePiecesAsync(bsp, null, null, false, "every hidden piece", all: true);
    }

    private async Task ChangePiecesAsync(int bsp, List<int>? indices, string? group, bool hide, string what, bool all = false)
    {
        if (_main.Host == null) return;
        using var busy = Busy((hide ? "Hiding " : "Showing ") + what + "…");
        try
        {
            var r = await _main.Host.HideLevelPiecesAsync(new HideLevelPiecesParams { Path = Path, Bsp = bsp, Indices = indices, Group = group, Hide = hide, AllHidden = all });
            if (r.Copied)
            {
                IsDirty = true;
                if (bsp < _bsps.Count) _bsps[bsp] = r.Bsp;
                _main.Log($"{Title}: level piece {bsp} is now this map's own copy ({r.Bsp}): the original level is untouched. Save the map to keep it.");
            }
            Status = $"{(hide ? "Hid" : "Showed")} {r.Changed} level piece{(r.Changed == 1 ? "" : "s")} ({what}). The 3D view updates when the level is read again." + (r.Copied ? " Save the map to keep it." : "");
            _main.Log($"{Title}: {(hide ? "hid" : "showed")} {what}: {r.Changed} pieces in {r.Bsp} ({r.Ms} ms).");
            if (r.Changed > 0)
            {
                if (_piecesHidden) await HiddenLevelPieces();
                else foreach (var row in LevelPieces.Where(x => x.Piece.Bsp == bsp && (indices?.Contains(x.Piece.Index) == true || all || (group != null && x.Piece.Group == group))).ToList())
                    row.SetHidden(hide);
                CanShowAllPieces = LevelPieces.Count(p => p.Hidden) > 1;
                _ = LoadGeometryAsync(_bsps, SelectedZoneSet?.Bsps, reload: true);
            }
        }
        catch (HostException ex) { _main.AddProblem("error", "Level pieces: " + ex.Message, Path); }
    }
}

/// <summary>A row of the level pieces list.</summary>
public partial class LevelPieceRow : ObservableObject
{
    private readonly MapEditorViewModel _map;
    public LevelPiece Piece { get; }

    public LevelPieceRow(MapEditorViewModel map, LevelPiece piece, bool wholeGroup = false)
    {
        _map = map;
        Piece = piece;
        IsWholeGroup = wholeGroup;
    }

    /// <summary>A row for a whole building (every piece of its group hidden).</summary>
    public bool IsWholeGroup { get; }
    public string Name => IsWholeGroup ? GroupName + " (whole)" : Piece.Name ?? "";
    /// <summary>"m10_030_spadehorn_barn" → "spadehorn_barn" (the bsp's name prefix is the same for every group).</summary>
    public string GroupName
    {
        get
        {
            string g = Piece.Group ?? "";
            int at = g.IndexOf('_');
            if (at > 0 && at + 1 < g.Length && g.Length > 4 && char.IsDigit(g[at + 1]))
            {
                int second = g.IndexOf('_', at + 1);
                if (second > 0 && second + 1 < g.Length) return g[(second + 1)..];
            }
            return g;
        }
    }
    public string Detail => IsWholeGroup ? $"{Piece.GroupCount} pieces · bsp {Piece.Bsp}" : $"{(string.IsNullOrEmpty(Piece.Group) ? "" : GroupName + " · ")}bsp {Piece.Bsp} · {Piece.Size:0.#} wide" + (Piece.Distance > 0.05f ? $" · {Piece.Distance:0.#} away" : "");
    public bool Hidden => Piece.Hidden;
    public string ToggleText => Piece.Hidden ? "Show" : "Hide";
    public bool HasGroup => !IsWholeGroup && !string.IsNullOrEmpty(Piece.Group) && Piece.GroupCount > 1;
    public string GroupText => (Piece.Hidden ? "Show all " : "Hide all ") + Piece.GroupCount;
    public string GroupTip => $"{(Piece.Hidden ? "Show" : "Hide")} every piece of {Piece.Group} ({Piece.GroupCount} pieces: usually a whole building and what is in it)";
    public double Opacity => Piece.Hidden ? 0.55 : 1;

    public void SetHidden(bool hidden)
    {
        Piece.Hidden = hidden;
        OnPropertyChanged(nameof(Hidden));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(GroupText));
        OnPropertyChanged(nameof(GroupTip));
        OnPropertyChanged(nameof(Opacity));
    }
}
