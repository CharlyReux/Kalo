namespace Kalo.Core.Scripting.Docs;

/// <summary>
/// Help › Recipes: short step-by-step pages for what modders most often do, using the names on screen. Grouped by what
/// they are for: getting started, weapons and objects, multiplayer, campaign, testing and sharing. The language itself
/// is in the other book (KaloScriptGuide).
/// </summary>
public static class KaloRecipes
{
    private static GuideBlock H(string text) => new(GuideKind.Heading, text);
    private static GuideBlock P(string text) => new(GuideKind.Text, text);
    private static GuideBlock Tip(string text) => new(GuideKind.Note, text);
    private static GuideBlock Code(string code) => new(GuideKind.Code, code.Trim('\n'));
    /// <summary>A complete gametype script (compiled by kalo-cli kscript-guide), shown in the Python style.</summary>
    private static GuideBlock Prog(string code)
    {
        string classic = code.Trim('\n');
        return new(GuideKind.Program, KaloScriptStyle.FromClassic(classic) ?? classic);
    }

    private const string Start = "Getting started", Items = "Weapons and objects", Mp = "Multiplayer", Campaign = "Campaign", Test = "Testing and sharing";

    public static IEnumerable<GuideSection> All()
    {
        // ---- Getting started ----

        yield return new("recipe-start", Start, "Start a mod", new[]
        {
            P("A mod holds only what you make or change; the game's own files are never touched."),
            P("1. Start page (or My mod, left): type a name, Create a mod."),
            P("2. Open something: a map, a gametype, or a game file (All game files, Ctrl+P)."),
            P("3. Save (Ctrl+S). Changes (second icon, left) lists what your mod changed; right-click a file to compare it or take it out."),
            P("4. Play (F5). Files you save while the game runs reload in it."),
            Tip("Activity (bottom) says what happened, Problems what went wrong, Log has every raw line."),
        });

        // ---- Weapons and objects ----

        yield return new("recipe-weapon", Items, "Change a weapon", new[]
        {
            P("1. All game files: open the weapon's .weapon file (search \"magnum weapon\"). It opens in the Simple view."),
            P("2. Change Magazine size, Fire rate, Spread, Zoom... Each shot (top) sets damage per hit, bullet speed and, for explosives, blast radius, and says how many hits kill a Spartan."),
            P("3. Save, then Play. In a running game, pick the weapon up again."),
            Tip("Each shot edits the bullet's files: every weapon firing the same bullet changes too. To change only one weapon, use New weapon from this… first."),
        });

        yield return new("recipe-new-weapon", Items, "Make a new weapon or object", new[]
        {
            P("1. Open a weapon, change what you like, then New weapon from this…: a copy in your mod with its own bullet and damage files."),
            P("2. Look › Paint… paints every surface with a material (what the player holds too). Look › Looks like… takes another object's model."),
            P("3. Use it › Place in map puts it in front of the camera of the map tab you opened last. Add to map palette makes it available without placing it (in a mission, squads can then carry it)."),
            P("4. For gametypes: Use in scripts… gives it a name for create_object and biped_give_weapon."),
            Tip("Paint and Looks like work on vehicles, crates and scenery too. Painting a game file makes a painted copy in your mod."),
        });

        // ---- Multiplayer ----

        yield return new("recipe-place", Mp, "Build or remix a map", new[]
        {
            P("1. My mod › New map: Remix (a Reach map with its objects), Empty level (spawn points only) or Barebone."),
            P("2. Objects (bottom): search, then Add to map or drag it in. Blocks (left of the view) place Forge pieces; Prefabs place saved groups."),
            P("3. Drag to move (Shift: up/down, Ctrl: snap), R turns, Ctrl+D duplicates."),
            P("4. Save, then Play here (Ctrl+F5) from the camera or Play (F5)."),
        });

        yield return new("recipe-gametype", Mp, "Make a gametype", new[]
        {
            P("1. My mod › New gametype: a name and a template. Slayer, CTF, KotH... or a ready variant: SWAT, Snipers, Fiesta, Gun Game."),
            P("2. Settings (script toolbar): score, time, options, player traits, loadouts, without code."),
            P("3. Code for anything else, in Kalo Script (Help › Kalo Script reference). F7 checks it."),
            P("4. Play (F5) with it picked at the top. Export .bin makes an MCC game type file."),
            H("Starting weapons and loadouts"),
            P("Settings › Player traits › base player traits sets what everyone spawns with. Settings › Loadouts sets the menu players pick from: None, My loadouts (+ Loadout), or one of Bungie's lists."),
            Tip("Bungie's loadout lists use all 32 loadout slots: use yours or Bungie's, not both."),
        });

        yield return new("recipe-zones", Mp, "Zones: kill zones, teleporters, pickups", new[]
        {
            P("1. Select an object (flag stand, Forge block, spawn point...). Inspector › Zone: Sphere, Cylinder or Box, and its size."),
            P("2. When a player enters: message, kill, give a weapon, points, speed boost or teleport. Fill in the box under it."),
            P("3. Add to gametype: Kalo labels the object and writes the code into the gametype picked for Play (or a new one from Slayer)."),
            P("4. Save the map and the gametype, then Play."),
            Tip("Teleport: give the exit object a Megalo label (inspector › Properties) and type it in the box."),
        });

        yield return new("recipe-gametype-snippets", Mp, "Gametype script snippets", new[]
        {
            P("Small pieces of Kalo Script to copy into a gametype."),
            H("Give everyone a weapon when they spawn"),
            Prog(@"
declare player.number armed = 0

for each player do
  temp object body = current_player
  if current_player.armed == 0 and not body == none then
    biped_give_weapon(body, ""sniper_rifle"", force)
    current_player.armed = 1
  end
  if player_died(current_player, any) then
    current_player.armed = 0
  end
end
"),
            H("First to 10 kills ends the round"),
            Prog(@"
for each player do
  if current_player.score >= 10 then
    end_round()
  end
end
"),
            H("Make objects bigger in game"),
            P("Reach ignores a map object's scale in multiplayer; a script can set it (percent)."),
            Prog(@"
for each object with label ""big"" do
  object_set_scale(current_object, 300)
end
"),
            H("A waypoint over an object"),
            Prog(@"
objects goals with label ""goal""
for each goals do
  navpoint_set_visible(current_object, everyone)
  navpoint_set_icon(current_object, destination)
  navpoint_set_text(current_object, ""Goal"")
end
"),
            H("A waypoint over the leader"),
            Prog(@"
declare global.player leader = none
for each player do
  if current_player.score >= 10 then
    leader = current_player
  end
end
do
  if leader != none then
    navpoint_set_visible(leader, everyone)
    navpoint_set_icon(leader, vip)
  end
end
"),
            H("A countdown on the screen"),
            Prog(@"
widget clock at top_right
declare global.timer countdown = 60

on init do
  timer_set_rate(countdown, 1)
end

for each player do
  hud_widget_set_visibility(clock, current_player, 1)
end

do
  hud_widget_set_text(clock, ""Time left: %s"", countdown)
  if timer_expired(countdown) then
    end_round()
  end
end
"),
            H("A message"),
            Prog(@"
for each player do
  if current_player.score == 5 then
    hud_post_message(everyone, none, ""Someone is halfway there!"")
  end
end
"),
            Tip("hud_post_message's second argument is a sound (none for silence). Rest the mouse on a sound in the suggestions to hear it."),
        });

        // ---- Campaign ----

        yield return new("recipe-mission-ai", Campaign, "Change a mission's enemies", new[]
        {
            P("1. My mod › New map › Campaign mission: a full copy of a Reach mission and its scripts."),
            P("2. Campaign bar › AI shows the squads' spawn points. Select one: the Squad section (right) shows what spawns."),
            P("3. Change the spawn point's Character and Weapon, or the cells (characters, weapons, count, vehicle). Save."),
            Tip("Squads made from a squad template spawn what the template says: change each spawn point's own character, or Use its own cells."),
            Tip("Vehicle: a cell's AI spawn in it. To offer a vehicle, open it and click Use it › Add to map palette."),
        });

        yield return new("recipe-volumes", Campaign, "Trigger volumes: spawn, checkpoint, teleport, win", new[]
        {
            P("1. Campaign bar › Add › Volume, and size it (W, L, H in the inspector)."),
            P("2. When the player enters: spawn a squad, checkpoint, teleport to a flag, kill zone, or win the mission."),
            P("3. Add to mission script: the code goes into the mission's own script, which opens. Save the map and the script, then Play."),
            Tip("Teleport needs a flag where the player arrives: Add › Flag, then name it. Volumes named zone_set:... belong to the engine: add one of your own."),
        });

        yield return new("recipe-patrol", Campaign, "Make a squad patrol", new[]
        {
            P("1. Select one of the squad's spawn points: the Patrol panel (right) picks that squad."),
            P("2. Aim where the path starts, New path here; then Add point here for each next point."),
            P("3. Add patrol to mission script: the squad walks the points in a loop until it sees an enemy."),
        });

        yield return new("recipe-mission-script", Campaign, "Edit a mission script", new[]
        {
            P("1. Campaign bar › Mission scripts › the map's own script (first). It opens in Kalo Script and is saved as HaloScript."),
            P("2. Hover a function for its help; suggestions list the game's functions and the map's squads, volumes and flags. Ctrl+click a name shows it in the map."),
            P("3. Check finds unknown names and wrong values. Save, then Play: script errors from the game show in Problems."),
            Tip("The language is described in Help › Kalo Script reference › Mission scripts."),
        });

        yield return new("recipe-small-mission", Campaign, "Tutorial: a small mission", new[]
        {
            P("Walk into the barn, an Elite appears, beat it, win. About 15 minutes."),
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
            H("Steps"),
            P("1. My mod › New map › Empty campaign mission, level m10. It keeps the level, zone sets, starts and objects, without AI or cutscenes."),
            P("2. Campaign bar › Zone set: one listing bsp 2 (the barn). Fly there (ZQSD/WASD, right-drag to look)."),
            P("3. Look at the ground, Add › Squad: one Elite. Change it in the Squad section if you like."),
            P("4. Look where the fight should start, Add › Volume, and name it tv_start_fight."),
            P("5. Object list › Start locations: move the first start next to the volume."),
            P("6. Mission scripts › your map's script: replace the starter with the script above. Save everything."),
            P("7. Play (F5): about 2 minutes to load m10, then walk into the volume."),
            Tip("Next: more squads and volumes, checkpoints with game_save(), Level pieces › Here to remove a building."),
        });

        yield return new("recipe-camera-cutscene", Campaign, "A simple camera cutscene", new[]
        {
            P("The camera holds on one view, glides to another, then gives control back. Durations are in ticks: 30 is one second."),
            Code(@"
# intro_shot: two camera points, shot_a and shot_b.
startup intro_shot:
    player_enable_input(false)
    chud_show(false)
    camera_control(true)
    camera_set(shot_a, 0)
    sleep(60)
    camera_set(shot_b, 150)
    sleep(180)
    camera_control(false)
    chud_show(true)
    player_enable_input(true)
"),
            H("Steps"),
            P("1. In a copy of a mission, turn on Cutscenes in the Campaign bar and fly to the first view."),
            P("2. Add › Camera saves that view. Name it shot_a. Fly to the second view, Add › Camera, name it shot_b."),
            P("3. With a camera selected, Look through shows its view and Set to view moves it to where you are looking."),
            P("4. Mission scripts › your map's script: add the script above. Save everything, then Play (F5)."),
            Tip("To check a shot without the script: save, Play here, select the camera and click Show this camera in the Live game bar; View back on player ends it."),
        });

        yield return new("recipe-level-pieces", Campaign, "Remove a building or rock from a level", new[]
        {
            P("1. In a copy of a level, pick a zone set, aim at the building and click Level pieces › Here: the pieces there, nearest first."),
            P("2. Hide removes one piece; Hide all N removes its whole building. Save the map."),
            P("3. Level pieces › Hidden brings pieces back."),
            Tip("The first time, Kalo copies that part of the level into your map's folder: other maps keep the piece. Baked shadows stay on the ground."),
        });

        // ---- Testing and sharing ----

        yield return new("recipe-live", Test, "Test while the game runs", new[]
        {
            P("Play here (Ctrl+F5) starts on the ground under the camera; in a mission it loads the zone set picked in the Campaign bar, without the mission scripts."),
            P("The Live game bar acts on the running game: Move me to the camera, Spawn this squad, Remove all AI, Load this zone set, Can't die, Infinite ammo."),
            Tip("Saved game files reload by themselves; map changes (objects, squads) need Play again."),
            Tip("Without the mission scripts, Bungie's squads and copies of them can hold still and only shoot. To see them behave, use Play. A squad made with Add › Squad chases you as far as it can walk."),
        });

        yield return new("recipe-mcc", Test, "Play your mod in MCC", new[]
        {
            P("Play › Play in MCC builds the map into a .map (a few minutes) and adds it and your gametypes to a local MCC mod. Start MCC with mods (anti-cheat off), then Custom Games."),
            P("A map made from a Reach Forge map can also be exported as a Forge variant: it works in MCC without mods."),
        });

        yield return new("recipe-trouble", Test, "When something goes wrong", new[]
        {
            P("Problems lists errors with where they come from; Log has every line from the game."),
            P("Undo (Ctrl+Z) in maps; Revert to original in the field editor; Changes › take a file out of the mod."),
        });
    }
}
