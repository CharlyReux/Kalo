using Kalo.Protocol;

namespace Kalo.Core.Scripting;

/// <summary>One piece of a documentation page.</summary>
public enum GuideKind
{
    /// <summary>A sub-heading.</summary>
    Heading,
    /// <summary>A paragraph of text.</summary>
    Text,
    /// <summary>A code fragment (may not compile on its own).</summary>
    Code,
    /// <summary>A complete Kalo Script that compiles (checked by the tests).</summary>
    Program,
    /// <summary>A tip or warning.</summary>
    Note,
    /// <summary>One action or condition: Title = name, Text = what it does, Signature, Example.</summary>
    Entry,
}

public sealed record GuideBlock(GuideKind Kind, string Text, string? Title = null, string? Signature = null, string? Megalo = null, string? Example = null);

public sealed record GuideSection(string Id, string Group, string Title, IReadOnlyList<GuideBlock> Blocks)
{
    /// <summary>Everything searchable in the page.</summary>
    public string SearchText { get; } = Title + "\n" + string.Join("\n", Blocks.Select(b => $"{b.Title} {b.Text} {b.Signature} {b.Example}"));
}

/// <summary>
/// The Kalo Script documentation shown in Help › Kalo Script documentation: a guide to the language, then a reference
/// of every action and condition (with the compiler's argument lists when the catalog is loaded) and the value lists.
/// </summary>
public static class KaloScriptGuide
{
    private static GuideBlock H(string text) => new(GuideKind.Heading, text);
    private static GuideBlock P(string text) => new(GuideKind.Text, text);
    private static GuideBlock C(string code) => new(GuideKind.Code, code.Trim('\n'));
    private static GuideBlock Prog(string code) => new(GuideKind.Program, code.Trim('\n'));
    private static GuideBlock Tip(string text) => new(GuideKind.Note, text);

    public static IReadOnlyList<GuideSection> Build(MegaloCatalog? catalog)
    {
        var list = new List<GuideSection>(Guide());
        list.AddRange(Reference(catalog));
        return list;
    }

    /// <summary>The guide pages (language), without the reference.</summary>
    public static IEnumerable<GuideSection> Guide()
    {
        yield return new("start", "Guide", "Getting started", new[]
        {
            P("Kalo Script is the language Kalo uses for gametypes. It reads like ReachVariantTool's scripts: if / then / else, " +
              "for each player do, function calls like f(a, b), and x += 1. Kalo turns it into Megalo, the language of Bungie's " +
              "gametype scripts, and compiles that with HREK's own compiler: everything Megalo can do, Kalo Script can do."),
            H("Your first gametype"),
            P("Make a mod (start page), then New gametype in My mod. Leave \"Write it in Kalo Script\" ticked. Kalo creates a " +
              ".kscript file in data\\multiplayer\\megalo\\mods\\<your mod> from a template and opens it."),
            Prog(@"
-- title: Double points
-- 2 points per kill instead of 1.
for each player do
  if player_died(current_player, enemy) then
    temp player killer = none
    player_death_get_killing_player(current_player, killer)
    killer.score += 2
  end
end
"),
            H("Compile, look, play"),
            P("F7 (Compile) checks the script. Errors point at your line; the Problems panel lists them. The Megalo button shows " +
              "the text Kalo hands to the compiler, which is useful to understand what a line does."),
            P("Play (F5, top right) saves, compiles, loads the gametype into the game and starts the chosen map with it. " +
              "Export .bin writes an MCC game type file (Kalo\\exports\\gametypes) that you can use in Custom Games."),
            H("While you type"),
            P("Hover any word for its help. Autocomplete (Ctrl+Space, or just type) offers actions, conditions, keywords, " +
              "the game's value lists (weapons, vehicles, sounds...) and your variables. The panel on the right lists every action " +
              "and condition; double-click inserts one."),
            Tip("A line starting with -- is a comment. A \"-- title: Name\" line near the top names the exported game type."),
        });

        yield return new("ticks", "Guide", "How a gametype runs", new[]
        {
            P("A gametype script is a list of triggers. The game runs every trigger about 60 times a second (each \"tick\"), from top " +
              "to bottom. There is no waiting: a trigger checks what is true now and acts. To make something happen later, use a timer."),
            C(@"
do                      -- runs once per tick
  ...
end

for each player do      -- runs once per tick for every player (current_player)
  ...
end

on init do              -- runs once, when the round starts
  ...
end
"),
            P("Events like a death are seen as conditions that are true for one tick: player_died(current_player, enemy) is true on " +
              "the tick the player was killed."),
        });

        yield return new("triggers", "Guide", "Triggers: for each, on, do", new[]
        {
            P("At the top level of the file, each of these is a trigger:"),
            C(@"
do ... end                                  -- every tick, once
for each player do ... end                  -- every tick, for each player: current_player
for each team do ... end                    -- every tick, for each team: current_team
for each object do ... end                  -- every tick, for each object: current_object (expensive)
for each random player do ... end           -- every tick, one random player
for each object with label ""koth_hill"" do ... end   -- objects whose Megalo label is koth_hill
for each object with type ""flag"" do ... end         -- objects of that object type
for each hill do ... end                    -- objects declared with: objects hill with label ""koth_hill""
on init do ... end                          -- once at the start of the round
on local do ... end                         -- on every machine, for things only shown locally
on host_migration do ... end                -- after the host changed
on double_migration do ... end
on pregame do ... end                       -- before the game starts
"),
            P("Inside a trigger, the same words make loops and blocks: \"for each player do\" loops over the players, \"do ... end\" " +
              "is a block (its conditions only gate what is inside), \"inline ... end\" is a block that doesn't use up a trigger."),
            Tip("Objects you place in the map editor get their Megalo label in the inspector (Properties, megalo label). That's how " +
                "a script finds \"the hill\" or \"the bomb spawn\" on your map."),
        });

        yield return new("if", "Guide", "Conditions: if, and, or, not", new[]
        {
            P("if runs what follows when its conditions are true. A condition is a comparison or a condition call."),
            C(@"
if current_player.score >= 10 then
  end_round()
end

if timer_expired(round_timer) and not current_player.team == none then
  ...
elseif current_player.score == 0 then
  ...
else
  ...
end
"),
            P("Comparisons: == != < > <= >=. Use == to compare (= changes a value). Conditions join with and / or, " +
              "and parentheses group them. not inverts a condition or a group."),
            C(@"
if end_playing == 1 or (timer_expired(playtime_timer) and playing_time == 1) then
  ...
end

if not (current_player.score > 5 or current_player.team == none) then
  ...
end
"),
            Tip("When and and or are both used, parentheses are required, so it is always clear which you mean: write " +
                "(a or b) and c, or a or (b and c)."),
            P("How it compiles: Megalo can only AND groups of ORed conditions. Kalo rewrites the expression in that form: " +
              "a or (b and c) becomes (a or b) and (a or c), which costs one more condition. Very large expressions are refused; " +
              "split them into nested ifs."),
            P("How it compiles: an if becomes a block whose conditions gate its contents. An if that is the last thing in its block " +
              "needs no block of its own, so it costs nothing extra. else / elseif add a temporary number that remembers whether a " +
              "branch already ran."),
            H("Conditions"),
            P(string.Join(", ", MegaloDocs.Conditions.Keys.Where(k => k != "if")) + ". See Reference › Conditions."),
        });

        yield return new("calls", "Guide", "Actions and calls", new[]
        {
            P("Actions do things. Call them like functions: name(arguments). The first argument can also go before a dot."),
            C(@"
navpoint_set_visible(the_hill, everyone)
the_hill.navpoint_set_visible(everyone)     -- the same
"),
            P("Megalo arguments can be several words; write them together between commas, as the compiler expects them:"),
            C(@"
hud_post_message(player killer, none, ""Killing spree!"")    -- target: everyone, team <t> or player <p>
create_object(""bomb"", set new_bomb, at current_player)     -- spawn a bomb on the player, keep it in new_bomb
set_score(add, 1, team current_team)
adjust_grenades(current_player, frag, set_to, 4)
"),
            P("Values: numbers (1, -5), \"text\" in quotes, names of variables, and the game's own names (weapons, vehicles, " +
              "sounds, incidents, icons...). Calls can't be nested: store a result in a temp first."),
            Tip("Every action's arguments are listed in Reference › Actions, with the exact form the compiler accepts."),
        });

        yield return new("variables", "Guide", "Variables: declare, temp, members", new[]
        {
            P("declare makes a variable that keeps its value between ticks. It belongs to the game (global), or there is one for " +
              "each player, team or object."),
            C(@"
declare global.number round_count = 0
declare player.number streak = 0            -- one per player: current_player.streak, killer.streak
declare team.timer capture_time = 30
declare global.object the_flag = none
declare local global.number host_only = 0   -- local: not sent to players (use it for values nobody sees)
"),
            P("Types: number, timer, object, player, team. Variables are networked (sent to every player) unless declared local. " +
              "Each kind of variable is limited in number; the compiler says when there are too many."),
            P("temp makes a temporary variable that only lives during the current tick. Use temps to hold a result (the killer, " +
              "a distance) before using it."),
            C(@"
temp player killer = none
temp number distance = 0
"),
            P("Members: player.score, team.score, player.team, and your own player / team / object variables " +
              "(current_player.streak). A player can be used where an object is expected: it means the player's body."),
            P("enum gives names to the values of a number: compare and set it with the names (stored as 0, 1, 2...)."),
            C(@"
declare player.enum{placer, player, spectator} role = spectator

if current_player.role == placer then
  current_player.role = player
end
"),
            P("alias gives a short name to something long, or to a text used in messages:"),
            C(@"
alias leader = global.player_0
alias goal_message = ""{current_player} reached the goal!""
"),
            H("Values in messages"),
            P("Put a player, team, number or timer in braces inside a text: it is shown in the message (a player as their name). " +
              "At most two per text. Megalo has no text variables: name a text with alias instead."),
            C(@"
hud_post_message(everyone, none, ""{current_player} reached the goal! ({current_player.score} points)"")
hud_post_message(everyone, ball_taken, goal_message)
"),
        });

        yield return new("functions", "Guide", "Functions", new[]
        {
            P("A function names a group of statements so you can use them in several places. Define it at the top level " +
              "(outside triggers) and call it on its own line, like an action."),
            C(@"
function award(p, points)
  p.score += points
  hud_post_message(player p, none, ""+{points}"")
end

for each player do
  if player_died(current_player, enemy) then
    temp player killer = none
    player_death_get_killing_player(current_player, killer)
    award(killer, 2)
  end
end
"),
            P("Parameters are replaced by what you pass (also inside {} in texts). temp variables declared in a function belong " +
              "to each call. To get a result back, pass a variable and set it in the function."),
            Tip("Megalo can't call a block from several places, so each call is a copy of the function: it counts against the " +
                "action and condition budget every time. A function can call other functions, but not itself."),
        });

        yield return new("math", "Guide", "Changing values", new[]
        {
            C(@"
x = 5
x += 1        -- add
x -= 1        -- subtract
x *= 2        -- multiply
x /= 2        -- divide (whole numbers)
x %= 3        -- remainder
x = a + b     -- becomes x = a then x += b
flags |= 4    -- bit or; also &= ^= <<= >>=
"),
            P("Megalo changes one value at a time, so x = a + b works but x = a - x doesn't (store it in a temp first). For the " +
              "operations without a symbol (not, abs) call set directly: set(x, abs, y)."),
            P("Timers: declare them with a starting value, then timer_set_rate(t, -1) to count down, timer_expired(t) to test, " +
              "timer_reset(t) to start again."),
            Prog(@"
declare global.timer bonus_timer = 30

do
  timer_set_rate(bonus_timer, -1)
  if timer_expired(bonus_timer) then
    timer_reset(bonus_timer)
    for each player do
      current_player.score += 1
    end
  end
end
"),
        });

        yield return new("declarations", "Guide", "Settings, options, traits, objects", new[]
        {
            P("Everything a gametype declares besides its triggers. The Settings button in the script toolbar shows most of " +
              "these as a form (like ReachVariantTool); what you change there is written here, and the other way round."),
            H("Base settings"),
            C(@"
setting score_to_win_round = 50        -- points to win a round
setting round_time_limit = 10          -- minutes
setting teams_enabled = 1
locked setting teams_enabled = 1       -- players can't change it in the lobby
setting base_player_traits             -- what every player starts with
  initial_primary_weapon = dmr
  initial_grenades = 2 frag
end
"),
            H("Lobby options"),
            P("Options players pick in the lobby. Use them in triggers like a number: killer.score += kill_points."),
            C(@"
option kill_points ""Kill Points"" ""Points per kill"" = 1 range -10 10
option hill_time ""Hill Movement Time"" ""How often the hill moves"" = 30
  value 0 ""Never""
  value 30 ""30 seconds""
  value 60 ""1 minute""
end
hidden option ...                      -- not shown in the lobby
option ""Sides"" = k_side_option_one_sided   -- sets an option declared in an include
"),
            H("Player traits"),
            P("A named set of traits; apply_player_traits(player, name) applies it for a tick (do it every tick to keep it)."),
            C(@"
traits hill_traits ""In Hill Traits"" ""While inside the hill""
  damage_resistance = 150
  speed = 110
  waypoint = all
end
"),
            H("Map objects, constants, widgets"),
            C(@"
objects hill with label ""koth_hill""           -- then: for each hill do ... end
objects hammers with type ""gravity_hammer""
objects goals with label ""as_goal"" min 1
constant k_state_normal = 0                   -- a fixed number with a name
widget hill_timer at bottom_center            -- a HUD widget (hud_widget_set_text...)
stat stat_time_in_hill = timer stat_time_in_hill team 0
"),
            H("Other declarations"),
            P("engine (menu name, description, icon), teams, loadout, loadout_palette, map_permissions and player_rating use " +
              "the same form: the block name, name = value lines, nested blocks, end."),
            C(@"
engine
  name = koth_title
  icon = k_engine_icon_king
  category = koth
end
teams
  team
    designator = defenders
  end
end
"),
            Prog(@"
setting score_to_win_round = 30
option kill_points ""Kill Points"" ""Points for each kill"" = 1 range -10 10
traits leader_traits ""Leader Traits"" ""Traits of the player in the lead""
  damage_resistance = 150
end
objects hill with label ""koth_hill""

for each player do
  if player_died(current_player, enemy) then
    temp player killer = none
    player_death_get_killing_player(current_player, killer)
    killer.score += kill_points
  end
end
"),
            P("include \"file.txt\" adds one of Bungie's shared Megalo files (paths relative to data\\multiplayer\\megalo), " +
              "for example the standard Slayer rules:"),
            C(@"
include ""slayer_rating_no_sort.txt""
include ""slayer_gameplay_triggers.txt""
"),
            P("megalo [[ ... ]] still takes raw Megalo text, for anything not covered here."),
            Tip("Literal text in options and messages compiles with a localization warning: that's fine for a mod."),
        });

        yield return new("recipes", "Guide", "Recipes", new[]
        {
            H("Give everyone a weapon when they spawn"),
            Prog(@"
declare player.number armed = 0

for each player do
  temp object body = current_player       -- a player used as an object is its body (none while dead)
  if current_player.armed == 0 and not body == none then
    biped_give_weapon(body, ""sniper_rifle"", force)
    current_player.armed = 1
  end
  if player_died(current_player, any) then
    current_player.armed = 0
  end
end
"),
            H("Make objects bigger in game"),
            P("Reach ignores the scale set on a map object in multiplayer; a script can scale it instead (percent)."),
            Prog(@"
for each object with label ""big"" do
  object_set_scale(current_object, 300)
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
            H("Show a message to one player"),
            C(@"
hud_post_message(player current_player, none, ""Welcome!"")
"),
        });

        yield return new("convert", "Guide", "Converting Megalo scripts", new[]
        {
            P("Open any Megalo script (Gametypes panel) and press To Kalo Script: Kalo writes a .kscript copy in your mod and " +
              "opens it; the original stays as it is. Every Bungie script that compiles converts and compiles back to the same " +
              "gametype."),
            H("Megalo → Kalo Script"),
            C(@"
trigger general                   do ... end
trigger player                    for each player do ... end
trigger initialization            on init do ... end
action for_each player            for each player do ... end (inside a trigger)
action for_each general           do ... end (inside a trigger)
begin ... end                     inline ... end
condition if a equal_to b         if a == b then ... end
condition timer_expired t or      timer_expired(t) or ...
condition not if a == b           not a == b
action set x add 1                x += 1
action hud_post_message a b ""c""   hud_post_message(a, b, ""c"")
temporary player p none           temp player p = none
networked number n 0              declare networked global.number n = 0   (in variables global)
override score_to_win_round 50    setting score_to_win_round = 50
ranged_option p t d 1 -10 10 end  option p t d = 1 range -10 10
player_traits t a b ... end       traits t a b ... end   (lines: key = value)
map_object hill label ""x"" end     objects hill with label ""x""
constants number k 0 end          constant k = 0
; comment                         -- comment
"),
            P("In Megalo, a condition gates everything after it in its block; Kalo Script makes that an if around the rest of the block."),
        });
    }

    /// <summary>Reference pages: conditions, actions by topic, values.</summary>
    public static IEnumerable<GuideSection> Reference(MegaloCatalog? catalog)
    {
        var sig = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in catalog?.Conditions ?? new()) if (e.Name != null) sig["c:" + e.Name] = e.Description ?? "";
        foreach (var e in catalog?.Actions ?? new()) if (e.Name != null) sig["a:" + e.Name] = e.Description ?? "";

        GuideBlock Entry(string name, MegaloDocs.Doc doc, bool condition)
        {
            sig.TryGetValue((condition ? "c:" : "a:") + name, out string? megalo);
            string kalo = name == "if" ? "a == b   (also != < > <= >=)" : MegaloDocs.KaloSignature(name, megalo);
            return new GuideBlock(GuideKind.Entry, doc.Text, name, kalo, megalo, doc.Example);
        }

        yield return new("ref-conditions", "Reference", "Conditions", new[]
        {
            P("Used after if (and joined with and / or). Signatures in Megalo form are shown under each one."),
        }.Concat(MegaloDocs.Conditions.Select(kv => Entry(kv.Key, kv.Value, true))).ToList());

        foreach (var (title, names) in ActionTopics)
        {
            var entries = names.Where(MegaloDocs.Actions.ContainsKey).Select(n => Entry(n, MegaloDocs.Actions[n], false)).ToList();
            yield return new("ref-" + title.ToLowerInvariant().Replace(' ', '-'), "Reference", "Actions: " + title, entries);
        }
        var listed = ActionTopics.SelectMany(t => t.Names).ToHashSet();
        var rest = MegaloDocs.Actions.Where(kv => !listed.Contains(kv.Key)).Select(kv => Entry(kv.Key, kv.Value, false)).ToList();
        if (rest.Count > 0) yield return new("ref-other", "Reference", "Actions: other", rest);

        var builtins = new List<GuideBlock> { P("Names the engine provides, usable anywhere a value goes.") };
        builtins.AddRange(MegaloDocs.Builtins.Select(kv => new GuideBlock(GuideKind.Entry, kv.Value.Text, kv.Key)));
        yield return new("ref-builtins", "Values", "Built-in names", builtins);

        var labels = new Dictionary<string, string>
        {
            ["weapons"] = "Weapons", ["vehicles"] = "Vehicles", ["equipment"] = "Armor abilities", ["grenades"] = "Grenades",
            ["objectTypes"] = "Object types", ["weaponSets"] = "Weapon sets", ["vehicleSets"] = "Vehicle sets", ["sounds"] = "Sounds",
            ["incidents"] = "Incidents (medals, announcer, stats)", ["loadouts"] = "Loadouts", ["loadoutPalettes"] = "Loadout palettes",
            ["hudWidgetIcons"] = "HUD widget icons", ["gameOptions"] = "Overridable game options",
        };
        if (catalog?.Lists is { Count: > 0 } lists)
        {
            var blocks = new List<GuideBlock> { P("The names the compiler knows, from HREK's object lists. Text values usually go in quotes when they are object type names (create_object(\"bomb\", ...)).") };
            foreach (var (key, values) in lists.OrderBy(kv => labels.Keys.ToList().IndexOf(kv.Key)))
            {
                blocks.Add(H($"{(labels.TryGetValue(key, out var l) ? l : key)} ({values.Count})"));
                blocks.Add(new GuideBlock(GuideKind.Code, string.Join("  ", values)));
            }
            yield return new("ref-values", "Values", "Value lists", blocks);
        }
        else
        {
            yield return new("ref-values", "Values", "Value lists", new[] { P("The value lists appear here once the gametype compiler has started (open or compile a gametype script).") });
        }
    }

    public static readonly (string Title, string[] Names)[] ActionTopics =
    {
        ("Variables and math", new[] { "set", "random", "timer_set_rate", "timer_reset", "print_variable", "for_each", "begin" }),
        ("Score and round", new[] { "set_score", "end_round", "submit_incident", "submit_incident_with_custom_value", "player_get_place", "team_get_place", "player_get_killing_spree_count", "saved_film_insert_marker", "game_grief_record_custom_penalty" }),
        ("Players", new[] { "player_death_get_killing_player", "player_death_get_damage_type", "player_death_get_special_type", "apply_player_traits", "player_get_weapon", "player_get_equipment", "player_pick_up_weapon", "biped_give_weapon", "biped_drop_weapon", "adjust_grenades", "player_get_vehicle", "player_set_vehicle", "player_set_unit", "player_get_target_object", "get_button_time", "player_set_objective", "player_set_objective_allegiance", "player_set_objective_allegiance_icon", "player_adjust_money", "player_enable_purchases", "player_set_requisition_palette", "get_player_holding_object" }),
        ("Spawning and loadouts", new[] { "set_loadout_palette", "set_respawn_filter", "set_fireteam_respawn_filter", "respawn_zone_enable", "team_set_coop_spawning", "player_set_coop_spawning", "team_set_primary_respawn_object", "player_set_primary_respawn_object", "player_get_fireteam_index", "player_set_fireteam_index", "team_set_vehicle_spawning", "player_set_vehicle_spawning", "set_player_respawn_vehicle", "set_team_respawn_vehicle" }),
        ("Objects", new[] { "create_object", "delete_object", "object_destroy", "object_set_invincibility", "object_set_scale", "object_attach", "object_detach", "object_get_distance", "object_get_velocity", "object_get_orientation", "object_set_orientation", "object_face_object", "object_bounce", "object_get_health", "object_get_shield", "object_adjust_health", "object_adjust_shield", "object_adjust_maximum_health", "object_adjust_maximum_shield", "object_set_never_garbage", "hide_object", "set_pickup_filter", "weapon_set_pickup_priority", "get_random_object", "create_tunnel" }),
        ("Areas and boundaries", new[] { "set_boundary", "boundary_set_visible", "boundary_set_player_color" }),
        ("Waypoints and HUD", new[] { "navpoint_set_visible", "navpoint_set_icon", "navpoint_set_priority", "navpoint_set_timer", "navpoint_set_text", "navpoint_set_visible_range", "set_progress_bar", "hud_post_message", "play_sound", "hud_widget_set_text", "hud_widget_set_value", "hud_widget_set_meter", "hud_widget_set_icon", "hud_widget_set_visibility" }),
        ("Devices and map", new[] { "device_set_power", "device_get_power", "device_set_position", "device_get_position", "device_set_position_track", "device_animate_position", "device_set_position_immediate", "set_scenario_interpolator_state", "hs_function_call" }),
        ("Debugging", new[] { "break_into_debugger", "debugging_enable_tracing", "debug_force_player_view_count" }),
    };
}
