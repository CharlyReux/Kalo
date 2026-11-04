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
            P("4. Squads made from a squad template spawn what the template says, whatever their cells show: change each spawn point's own character, or click Use its own cells to edit the cells instead."),
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

        yield return new("recipe-small-mission", G, "Tutorial: make a small mission", new[]
        {
            P("A mission of your own on a Reach level: walk into an area, an Elite appears, defeat it and the mission is won. About 15 minutes. It uses an empty campaign mission: the level, its zone sets, player starts and objects, with no AI, no cutscenes and one starter script."),
            H("1. Create the mission"),
            P("My mod › New map › Empty campaign mission. Name it (e.g. barn_fight), pick the level m10 (Winter Contingency) and click Create map. Kalo opens it; the Activity panel says what was kept."),
            H("2. Find a place for the fight"),
            P("In the Campaign bar, pick a zone set in Zone set: the view shows only the level pieces it loads (the mission switches zone sets as the player moves). Fly with ZQSD (WASD on QWERTY), right-drag to look, Shift for speed. The barn of m10 is a good spot: pick a zone set whose list shows bsp 2."),
            H("3. Place the enemy"),
            P("Look at the ground where the enemy should stand and click Add › Squad (Campaign bar). Kalo adds a squad named squad with one Elite and a spawn point there, and selects it."),
            P("In the Squad section (right): change the Character or Weapon of its cell, or the count. Add › Spawn point adds another place for the squad's AI. Save (Ctrl+S)."),
            H("4. Add the trigger"),
            P("Look where the player should start the fight and click Add › Volume: a 4 x 4 x 3 box. In the panel on the right, type its Name: tv_start_fight, and its size (W, L, H) if it needs to be bigger. Save."),
            H("5. Move the start"),
            P("Show the start points: in the object list, Start locations. Select the first one and move it near your volume (drag it, or type its position). Save. Play (F5) starts the mission there."),
            H("6. Write the mission script"),
            P("Campaign bar › Mission scripts › your map's script (first in the list). It opens in Kalo Script. Replace the starter with:"),
            Code(@"
# barn_fight: walk into the barn, beat the Elite, win.
startup mission_start:
    print(""mission started"")
    game_save()
    sleep_until(volume_test_players(tv_start_fight), 1)
    ai_place(squad)
    sleep_until(ai_living_count(squad) == 0, 1)
    print(""mission complete"")
    game_won()
"),
            P("startup: runs once when the mission starts. sleep_until waits for its condition (checked every tick with 1). ai_place makes the squad appear at its spawn points. game_won ends the mission as won."),
            P("Hover any word for its help; Check finds unknown names (a volume or squad that isn't in the map) and wrong values. Save writes it as HaloScript."),
            H("7. Play it"),
            P("Pick your map in the Play list (top) and press Play (F5). The mission loads (about 2 minutes for m10), you start at your start point; walk into the volume and fight. If the game can't compile the script, Problems says which line."),
            Tip("While it runs, the Live game bar helps: Spawn this squad places the selected squad again, Can't die, Move me to the camera."),
            H("Next steps"),
            P("More squads (Add › Squad, then ai_place(squad_2)), checkpoints (game_save()), a second fight after the first (another volume and sleep_until), allies (set a squad's team to human in All squad fields…)."),
            P("Change the place itself: Level pieces › Here hides a building or rock of the level (see the recipe Remove a building or rock from a level), and the Objects panel adds scenery from any mission."),
        });

        yield return new("recipe-level-pieces", G, "Remove a building or rock from a level", new[]
        {
            P("A Reach level is ground (its terrain and big walls) plus thousands of level pieces: buildings, rocks, pipes, crates, fences, each placed in the level. Kalo can hide level pieces in your map; the ground itself stays."),
            H("1. Find the piece"),
            P("Open your map (a copy of a level: My mod › New map). In the Campaign bar, pick a zone set, then aim the center of the view at the building or rock and click Level pieces › Here. A list opens on the right: the pieces there, nearest first, with the building they belong to (e.g. spadehorn_barn)."),
            H("2. Hide it"),
            P("Hide removes that one piece. Hide all N removes every piece of its building (a whole barn is about 1,500 pieces: walls, roof, shelves...). The 3D view reads the level again (a minute or two on big levels) and shows the result."),
            P("The first time, Kalo copies that part of the level (its bsp) into your map's folder: the original level and other maps keep the piece. Save the map (Ctrl+S) to keep the change."),
            H("3. Bring it back"),
            P("Level pieces › Hidden lists every hidden piece: Show brings one back, Show them all brings back all of them. Ctrl+Z does not undo hiding."),
            Tip("Hidden pieces are gone in the game (they are shrunk to nothing). Their baked shadows and lighting stay on the ground, and AI may still avoid where they were."),
            H("Pieces from other levels"),
            P("Level pieces can't be copied to another level: their shapes are stored inside their own level. To bring things from another mission, use objects instead: the Objects panel (bottom) lists every object of the game, scenery included (crates, barriers, trees, Covenant props, the Forge pieces); drag one onto the map view, or select it and click Add."),
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
