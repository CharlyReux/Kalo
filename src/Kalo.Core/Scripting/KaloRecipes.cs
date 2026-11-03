namespace Kalo.Core.Scripting;

/// <summary>
/// Help › Recipes: short, step-by-step pages for the things people most often want to do with Kalo, in Kalo's own
/// words (buttons and panels as they are named on screen). Shown first in the help window.
/// </summary>
public static class KaloRecipes
{
    private static GuideBlock H(string text) => new(GuideKind.Heading, text);
    private static GuideBlock P(string text) => new(GuideKind.Text, text);
    private static GuideBlock Tip(string text) => new(GuideKind.Note, text);
    private static GuideBlock Code(string code) => new(GuideKind.Code, code.Trim('\n'));
    private const string G = "Recipes";

    public static IEnumerable<GuideSection> All()
    {
        yield return new("recipe-start", G, "Start a mod", new[]
        {
            P("A mod holds only what you make or change; the game's own files are never touched. Everything you save goes into your mod."),
            H("Steps"),
            P("1. On the start page (or in My mod, left), type a name and click Create a mod."),
            P("2. Open something to change: a map (Play list or Project panel), a gametype (Gametypes panel) or a game file (All game files)."),
            P("3. Save (Ctrl+S). The Changes panel lists what your mod changed; right-click a file there to compare it with the original or take it out of the mod."),
            P("4. Play (F5) starts the game with your changes. While it runs, saved files reload in it by themselves."),
            Tip("Activity (bottom panel) says in plain words what Kalo and the game did. Problems lists what went wrong. Log has every raw line, for troubleshooting."),
        });

        yield return new("recipe-weapon", G, "Change a weapon (magazine, fire rate, damage)", new[]
        {
            P("Weapons open in a Simple view: the settings most people change, with plain names. Click a field to read what it does (right panel)."),
            H("Steps"),
            P("1. Project panel › All game files: search the weapon (e.g. \"magnum weapon\" or \"assault rifle\") and open its .weapon file."),
            P("2. Change Magazine size, Fire rate, Spread, Zoom... and Save."),
            P("3. Damage is not in the weapon: select Projectile (the bullet), click Open this file (right panel). In the bullet, select Impact damage (or Explosion damage) and Open this file again: change Damage (minimum / maximum) and Save."),
            P("4. Play (F5). If the game is already running, it reloads the saved files: pick the weapon up again."),
            Tip("All fields shows everything (Raw: the engine's own names, for guides written for Guerilla). Compare lists what you changed; Revert to original puts the game's version back."),
            Tip("A Spartan has about 70 shield and 45 health points: a 17.5 damage magnum bullet takes 4 shots to break the shields."),
        });

        yield return new("recipe-place", G, "Place objects in a multiplayer map", new[]
        {
            H("Steps"),
            P("1. New map in My mod (Remix: a copy of a Reach map with its objects, or Barebone / Empty level), or open one of yours."),
            P("2. Objects (bottom panel): search an object (file name, Forge name such as \"Flag Stand\", or script name), select it, Add to map. It appears in front of the camera."),
            P("3. Move it: drag it in the view (Shift: up and down, Ctrl: snap), R turns it; or type its position on the right. Ctrl+D duplicates."),
            P("4. Save, then Play here (Ctrl+F5) starts on the ground under the camera, or Play (F5) from the map's start."),
            Tip("Blocks (left of the view): floors, walls, ramps... built from Forge pieces. Prefabs: save a group of objects and place it again anywhere."),
        });

        yield return new("recipe-gametype", G, "Make a gametype", new[]
        {
            P("Gametypes are written in Kalo Script: Python-like code that Kalo turns into Megalo, the game's own gametype language."),
            H("Steps"),
            P("1. My mod › New gametype: a name, a template (Slayer, Juggernaut...), keep Write it in Kalo Script ticked."),
            P("2. Settings (top of the script) changes score to win, time limit, player traits and options without code."),
            P("3. Code: for example 2 points per kill:"),
            Code(@"
for each player:
    if player_died(current_player, enemy):
        killer: player = none
        player_death_get_killing_player(current_player, killer)
        killer.score += 2
"),
            P("4. F7 checks it (errors are underlined on their line). Play (F5) with the gametype picked at the top. Export .bin makes an MCC game type file."),
            Tip("Hover a word for its help, F1 for its page. Help › Kalo Script documentation has the whole language."),
        });

        yield return new("recipe-mission-ai", G, "Change which enemies a mission spawns", new[]
        {
            P("Missions place their enemies in squads. Each squad has spawn points (red markers in the map view, AI in the Campaign bar)."),
            H("Steps"),
            P("1. My mod › New map › Campaign mission: a full copy of a Reach mission with its own copy of the mission scripts. Open it."),
            P("2. Turn on AI in the Campaign bar. In the object list, AI squads holds the squads by group; select a squad's spawn point."),
            P("3. The Squad section (right) says what spawns: each spawn point's own Character and Weapon, and the squad's cells. Pick an Elite in Character, for example. Save."),
            P("4. Squads made from a squad template spawn what the template says, whatever their cells show: change each spawn point's own character instead."),
            P("5. Play here (Ctrl+F5) near the squad, or Play (F5) for the whole mission. While the game runs, Spawn this squad (Live game bar) places it right away."),
            Tip("Used by scripts (right panel) lists the script lines that name the squad: when it is placed (ai_place) and what it does."),
        });

        yield return new("recipe-mission-script", G, "Edit a mission script", new[]
        {
            P("Mission scripts are Bungie's HaloScript. Kalo shows them in Kalo Script (Python-like) and saves them back as HaloScript."),
            H("Steps"),
            P("1. Open your campaign map, then Mission scripts (Campaign bar) › the map's own script (first in the list)."),
            P("2. Read and change it. Hover a function for its help; type to get suggestions (game functions, and the map's squads, volumes, flags...). Ctrl+click a squad or volume name shows it in the map."),
            Code(@"
dormant barn_skirm_init_start:
    ai_place(barn_skirm_init)
    sleep_until(volume_test_players(tv_barn01_end) or volume_test_players(vol_barn_courtyard), 1)
"),
            P("3. Check finds unknown names and wrong values; Save writes the HaloScript. Show HaloScript shows the original form."),
            P("4. Play (F5). If the game can't compile a script, the error appears in Problems and on its line."),
            Tip("Add a trigger volume (Add › Volume in the Campaign bar), rename it, then wait for players in it: sleep_until(volume_test_players(my_volume))."),
        });

        yield return new("recipe-live", G, "Test things while the game runs", new[]
        {
            P("While the game runs, the map tab shows a Live game bar: it acts on the running game, no restart."),
            P("Move me to the camera: your player jumps to the ground under the map view's camera."),
            P("Spawn this squad: places the selected spawn point's squad. Remove all AI clears them."),
            P("Load this zone set: switches the mission to the zone set picked in the Campaign bar."),
            P("Can't die and Infinite ammo: cheats for testing."),
            Tip("Saved game files (weapons, objects...) reload in the running game by themselves. Map changes (objects, squads, spawn points) need a restart: Play again."),
        });

        yield return new("recipe-play-here", G, "Start playing from anywhere (Play here)", new[]
        {
            P("Play here (Ctrl+F5, map toolbar) starts the game on the ground under the camera, facing where it looks. The map's own start is unchanged."),
            P("Kalo checks the spot first: with no floor under the camera (you would fall) or no room to stand, it says so; press Play here again to play anyway."),
            P("In a mission, Play here loads the zone set picked in the Campaign bar, without the mission scripts (no cutscenes, no AI). Use the Live game bar to spawn squads."),
            Tip("If a mission keeps restarting from its checkpoint, the player dies as soon as they appear: Kalo warns in Problems. Start over a floor, or use Play (F5)."),
        });

        yield return new("recipe-mcc", G, "Play your mod in MCC", new[]
        {
            P("Play in MCC (Play menu) builds the map into a real .map file (a few minutes), adds it to a local MCC mod and puts your gametype in MCC's game types."),
            P("Start MCC from Steam with mods (anti-cheat off), then Custom Games › Halo: Reach and pick your map and gametype."),
            P("A map made from one of Reach's own Forge maps can also be exported as a Forge variant (Play menu): it works in MCC without mods."),
        });

        yield return new("recipe-trouble", G, "When something goes wrong", new[]
        {
            P("Problems (bottom) lists errors and warnings with where they come from; Activity says what happened, in order."),
            P("A change you don't want: Revert to original (field editor), or right-click the file in Changes › take it out of the mod."),
            P("The game doesn't start or shows nothing: Log (bottom) has every line from the game; search it."),
            P("Undo (Ctrl+Z) works in maps; in the field editor, Compare shows what changed before you save."),
        });
    }
}
