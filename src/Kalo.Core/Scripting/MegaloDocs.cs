namespace Kalo.Core.Scripting;

/// <summary>
/// Plain-language help for gametype scripts, shown when hovering a word in the script editor and in autocomplete:
/// every Megalo action and condition, the keywords of Megalo and Kalo Script, and the built-in names.
/// The exact argument list comes from HREK's compiler (the catalog); this adds what the thing does.
/// </summary>
public static class MegaloDocs
{
    public sealed record Doc(string Text, string? Example = null);

    // Declared first: the doc tables below use it (static fields initialize in order).
    /// <summary>The buttons get_button_time accepts (from HREK's compiler), written without quotes.</summary>
    public static readonly string[] Buttons =
    {
        "fire_primary", "fire_secondary", "fire_tertiary", "melee_attack", "jump", "crouch", "grenade", "throw_grenade",
        "switch_weapon", "context_primary", "equipment", "scope_zoom", "night_vision", "vehicle_trick",
    };


    public static readonly IReadOnlyDictionary<string, Doc> Conditions = new Dictionary<string, Doc>(StringComparer.Ordinal)
    {
        ["if"] = new("Compares two values: numbers, timers, objects, players or teams. In Kalo Script just write the comparison.",
            "if current_player.score >= 10 then ... end"),
        ["object_in_area"] = new("True when the object is inside the boundary (shape) of the area object, e.g. a hill or a capture zone.",
            "if object_in_area(current_player, the_hill) then ... end"),
        ["player_died"] = new("True on the tick the player died. The death type filters how: enemy (killed by an enemy), suicide, betrayal, or any.",
            "if player_died(current_player, enemy) then ... end"),
        ["team_disposition"] = new("Compares how two teams relate (allies or enemies)."),
        ["timer_expired"] = new("True once the timer has reached zero (count-down timers) or its limit.",
            "if timer_expired(round_timer) then end_round() end"),
        ["object_is_type"] = new("True when the object is of the given object type (a weapon, vehicle, flag... from the object type list).",
            "if object_is_type(current_object, \"flag\") then ... end"),
        ["team_is_active"] = new("True when the team has players in this game."),
        ["object_out_of_bounds"] = new("True when the object has left the playable area (it fell or was thrown off the map)."),
        ["player_is_fire_team_leader"] = new("True when the player leads their fireteam (Firefight / Invasion style spawning)."),
        ["player_assisted_with_kill"] = new("True when the first player helped kill the second (damaged them before they died)."),
        ["object_matches_filter"] = new("True when the object matches a map object filter (a map_object declared with a label or type)."),
        ["player_is_active"] = new("True when the player is in the game and playing (not left, not waiting to join)."),
        ["equipment_is_active"] = new("True while the armor ability is being used (sprint, armor lock, active camo...)."),
        ["player_is_spartan"] = new("True when the player plays as a Spartan."),
        ["player_is_elite"] = new("True when the player plays as an Elite."),
        ["player_is_editor"] = new("True when the player is in Forge editor mode (the monitor)."),
        ["game_is_forge"] = new("True when the game is a Forge session."),
    };

    public static readonly IReadOnlyDictionary<string, Doc> Actions = new Dictionary<string, Doc>(StringComparer.Ordinal)
    {
        ["set_score"] = new("Changes a team's or a player's score: add, subtract, set_to...",
            "set_score(add, 1, player killer)"),
        ["create_object"] = new("Spawns an object of the given type at another object (e.g. a player). Optional: store it in a variable (set x), give it a label, keep it from being cleaned up (never_garbage), offset it.",
            "create_object(\"bomb\", set new_bomb, at current_player)"),
        ["delete_object"] = new("Removes the object from the game instantly (no explosion, no effects)."),
        ["navpoint_set_visible"] = new("Shows or hides the object's waypoint (the marker over it) for no_one, everyone, allies, enemies or one player.",
            "navpoint_set_visible(the_hill, everyone)"),
        ["navpoint_set_icon"] = new("Sets the waypoint icon (flag, bomb, king, vip, skull, number...). With num, the next argument is the number shown."),
        ["navpoint_set_priority"] = new("How the waypoint is drawn: low, normal, high, or blink."),
        ["navpoint_set_timer"] = new("Shows a timer on the object's waypoint."),
        ["navpoint_set_visible_range"] = new("The waypoint only shows between these distances (in feet)."),
        ["set"] = new("Changes a variable: set_to (=), add (+=), subtract (-=), multiply (*=), divide (/=), modulo (%=), and the bit operations and, xor, not, lshift, rshift, abs. In Kalo Script, write x += 1.",
            "killer.score += 1"),
        ["set_boundary"] = new("Gives the object an area shape (sphere, cylinder, box, or none) with sizes in feet. Used for hills, capture zones, kill zones."),
        ["apply_player_traits"] = new("Applies a set of player traits (declared in game_options) to the player for this tick. Call it every tick to keep them."),
        ["set_pickup_filter"] = new("Who can pick up this object: no_one, everyone, allies, enemies or one player."),
        ["set_respawn_filter"] = new("Who can respawn at this object: no_one, everyone, allies, enemies or one player."),
        ["set_fireteam_respawn_filter"] = new("Which fireteam (0-3, all or none) can respawn at this object."),
        ["set_progress_bar"] = new("Shows a timer as a progress bar on the object's waypoint, for the chosen players."),
        ["hud_post_message"] = new("Shows a message (and plays a sound) to a team, a player, or everyone. Target: everyone, team <t>, player <p>. Sound: none or a sound from the list.",
            "hud_post_message(player killer, none, \"Killing spree!\")"),
        ["timer_set_rate"] = new("How fast the timer runs: 1 normal, 0 paused, -1 backwards, 2 twice as fast.", "timer_set_rate(round_timer, -1)"),
        ["print_variable"] = new("Writes text (and values) to the game's debug log. Handy while testing."),
        ["get_player_holding_object"] = new("Stores the player carrying the object (a flag, a bomb, a ball) in a player variable, or none."),
        ["for_each"] = new("Megalo's block: runs its contents for each player, team, object, or once (general). Kalo Script writes it as 'for each player do ... end' or 'do ... end'."),
        ["end_round"] = new("Ends the current round now (scores decide who wins)."),
        ["boundary_set_visible"] = new("Shows the object's boundary shape (the outline drawn on the ground) to no_one, everyone, allies or enemies, or to one player: player, then the player, then 1 (show) or 0 (hide).",
            "boundary_set_visible(the_hill, everyone)\nboundary_set_visible(marker, player, current_player, 1)"),
        ["object_destroy"] = new("Destroys the object as if it had been killed (with effects, and it counts as a kill unless no_statistics)."),
        ["object_set_invincibility"] = new("Makes the object (or a player's biped) take no damage, or normal damage again."),
        ["random"] = new("Picks a random whole number from 0 to count-1 and stores it.",
            "random(6, roll)"),
        ["break_into_debugger"] = new("Stops in the debugger (devkit only). Does nothing in a normal game."),
        ["object_get_orientation"] = new("Stores which way the object is turned, as a number from 1 to 6 (which face points up)."),
        ["object_get_velocity"] = new("Stores the object's speed in feet per second."),
        ["player_death_get_killing_player"] = new("Stores who killed the dead player (none for suicides and falls). Use right after player_died.",
            "player_death_get_killing_player(current_player, killer)"),
        ["player_death_get_damage_type"] = new("Stores what killed the player, as a damage type number (weapon kinds, melee, grenade, fall...)."),
        ["player_death_get_special_type"] = new("Stores how special the kill was, as a number (headshot, assassination, splatter, stick...)."),
        ["debugging_enable_tracing"] = new("Turns tracing of the script on or off in the debug log."),
        ["object_attach"] = new("Attaches an object to another one at an offset (feet). It then moves with its parent.",
            "object_attach(bomb, current_player, 0, 0, 5)"),
        ["object_detach"] = new("Detaches an object attached with object_attach."),
        ["player_get_place"] = new("Stores the player's place in the standings (1 = first)."),
        ["team_get_place"] = new("Stores the team's place in the standings (1 = first)."),
        ["player_get_killing_spree_count"] = new("Stores how many kills the player made without dying."),
        ["player_adjust_money"] = new("Changes a player's money (Invasion / Firefight requisition)."),
        ["player_enable_purchases"] = new("Lets the player buy from the requisition palette while alive, dead or both."),
        ["player_get_vehicle"] = new("Stores the vehicle the player is in, or none."),
        ["player_set_vehicle"] = new("Puts the player into the vehicle."),
        ["player_set_unit"] = new("Gives the player control of another unit (biped)."),
        ["timer_reset"] = new("Puts the timer back to its starting value."),
        ["weapon_set_pickup_priority"] = new("How a weapon is picked up: normal, special (hold the button, like a flag) or auto (walk over it)."),
        ["object_bounce"] = new("Gives the object a small hop."),
        ["hud_widget_set_text"] = new("Sets the text of a HUD widget (declared in hud_widgets). Text can include values with %n, %s..."),
        ["hud_widget_set_value"] = new("Sets the value shown by a HUD widget."),
        ["hud_widget_set_meter"] = new("Shows a meter in the HUD widget: from a number out of a maximum, from a timer, or off."),
        ["hud_widget_set_icon"] = new("Sets the icon of a HUD widget."),
        ["hud_widget_set_visibility"] = new("Shows or hides a HUD widget for one player."),
        ["play_sound"] = new("Plays a sound (announcer or effect) for a team, a player or everyone."),
        ["object_set_scale"] = new("Scales the object in game, in percent (100 = normal size). This is how to resize objects in multiplayer, where the map's own scale is ignored.",
            "object_set_scale(current_object, 300)"),
        ["navpoint_set_text"] = new("Sets text shown on the object's waypoint."),
        ["object_get_shield"] = new("Stores the object's shield in percent."),
        ["object_get_health"] = new("Stores the object's health in percent."),
        ["player_set_objective"] = new("Sets the objective text shown to the player (top of the screen at spawn)."),
        ["player_set_objective_allegiance"] = new("Sets the player's objective text with an allegiance (team) icon."),
        ["player_set_objective_allegiance_icon"] = new("Sets the icon shown next to the player's objective."),
        ["team_set_coop_spawning"] = new("Lets the team's players spawn on each other (or not)."),
        ["team_set_primary_respawn_object"] = new("Makes the team spawn at this object first."),
        ["player_set_primary_respawn_object"] = new("Makes the player spawn at this object first."),
        ["player_get_fireteam_index"] = new("Stores the player's fireteam number."),
        ["player_set_fireteam_index"] = new("Moves the player to a fireteam."),
        ["object_adjust_shield"] = new("Changes the object's current shield (percent): add, subtract, set_to..."),
        ["object_adjust_health"] = new("Changes the object's current health (percent): add, subtract, set_to..."),
        ["object_get_distance"] = new("Stores the distance between two objects, in feet."),
        ["object_adjust_maximum_shield"] = new("Changes the object's maximum shield (percent)."),
        ["object_adjust_maximum_health"] = new("Changes the object's maximum health (percent)."),
        ["player_set_requisition_palette"] = new("Sets what the player can buy (requisition palette)."),
        ["device_set_power"] = new("Powers a device (door, lift, switch) on or off, in percent."),
        ["device_get_power"] = new("Stores a device's power, in percent."),
        ["device_set_position"] = new("Moves a device (e.g. opens a door) to a position in percent of its animation."),
        ["device_get_position"] = new("Stores a device's position, in percent of its animation."),
        ["adjust_grenades"] = new("Changes how many frag or plasma grenades the player has.",
            "adjust_grenades(current_player, frag, set_to, 4)"),
        ["submit_incident"] = new("Reports a game event (incident): plays its medal, announcer line and statistics. Cause and effect are teams or players."),
        ["submit_incident_with_custom_value"] = new("Reports an incident with an extra number (e.g. which territory)."),
        ["set_loadout_palette"] = new("Sets which loadouts a team or a player can pick from when spawning."),
        ["device_set_position_track"] = new("Picks which animation a device plays, with a blend time."),
        ["device_animate_position"] = new("Animates a device to a position over time, with acceleration and deceleration."),
        ["device_set_position_immediate"] = new("Jumps a device to a position (percent) without animating."),
        ["saved_film_insert_marker"] = new("Adds a marker to the saved film (Theater)."),
        ["respawn_zone_enable"] = new("Turns a respawn zone on or off."),
        ["player_get_weapon"] = new("Stores the player's primary or secondary weapon."),
        ["player_get_equipment"] = new("Stores the player's armor ability."),
        ["object_set_never_garbage"] = new("Keeps the object from being cleaned up by the garbage collector (dropped weapons usually vanish after a while)."),
        ["player_get_target_object"] = new("Stores the object the player is aiming at."),
        ["create_tunnel"] = new("Creates an object stretched between two objects (like a beam), with a radius."),
        ["debug_force_player_view_count"] = new("Debug: forces a splitscreen player count."),
        ["player_pick_up_weapon"] = new("Makes the player pick up a weapon object."),
        ["player_set_coop_spawning"] = new("Lets players spawn on this player (or not)."),
        ["object_set_orientation"] = new("Turns the object to face like another object."),
        ["object_face_object"] = new("Turns the object to face another object."),
        ["biped_give_weapon"] = new("Gives the biped (a player's body) a weapon as primary or secondary; force replaces what it holds.",
            "biped_give_weapon(current_player, \"sniper_rifle\", force)"),
        ["biped_drop_weapon"] = new("Makes the biped drop its primary or secondary weapon (delete_on_drop removes it)."),
        ["set_scenario_interpolator_state"] = new("Turns a scenario interpolator (a map animation) on or off."),
        ["get_random_object"] = new("Stores a random object matching a map object filter, ignoring one object."),
        ["game_grief_record_custom_penalty"] = new("Adds a betrayal-style penalty to the player (matchmaking anti-griefing)."),
        ["boundary_set_player_color"] = new("Colors an object's boundary with a player's color (the player variable must belong to the object)."),
        ["begin"] = new("An inline block: its conditions only gate what is inside, and it doesn't use one of the gametype's limited triggers. Kalo Script writes it 'inline ... end'."),
        ["hs_function_call"] = new("Calls a map script (HaloScript) function by name."),
        ["get_button_time"] = new("Stores how long (milliseconds) the player has been holding a button, 0 when not pressed. The button is a word, without quotes: " + string.Join(", ", Buttons) + ".",
            "temp number held = 0\nget_button_time(current_player, fire_primary, held)\nif held > 500 then ... end"),
        ["team_set_vehicle_spawning"] = new("Lets the team spawn in vehicles (or not)."),
        ["player_set_vehicle_spawning"] = new("Lets the player spawn in vehicles (or not)."),
        ["set_player_respawn_vehicle"] = new("Makes the player respawn in this vehicle."),
        ["set_team_respawn_vehicle"] = new("Makes the team respawn in this vehicle."),
        ["hide_object"] = new("Hides or shows the object (it stays in the game)."),
    };

    /// <summary>Kalo Script words.</summary>
    public static readonly IReadOnlyDictionary<string, Doc> KaloKeywords = new Dictionary<string, Doc>(StringComparer.Ordinal)
    {
        ["if"] = new("Runs what follows when the conditions are true. Conditions: comparisons (== != < > <= >=) and condition calls, joined with and / or (no precedence: a or b and c means (a or b) and c).",
            "if current_player.score > 5 and not timer_expired(t) then ... elseif ... then ... else ... end"),
        ["then"] = new("Ends the condition of an if."),
        ["else"] = new("What runs when no condition of the if was true."),
        ["elseif"] = new("Another condition, tried when the ones above were false."),
        ["end"] = new("Closes a block: if, for each, do, on, inline."),
        ["for"] = new("'for each X do ... end' runs its contents for each player, team, object, random player, or each object of a map object (with label \"x\" / with type \"x\"). At the top level it is a trigger; inside one, a loop.",
            "for each player do ... end"),
        ["each"] = new("Part of 'for each'."),
        ["do"] = new("At the top level, 'do ... end' is a trigger that runs every tick (trigger general). Inside a trigger, a block whose conditions only gate what is inside."),
        ["inline"] = new("'inline ... end': a block like do, but it doesn't use one of the gametype's limited triggers (Megalo's begin)."),
        ["on"] = new("'on init do ... end' runs once at the start; also on local, on host_migration, on double_migration, on pregame."),
        ["init"] = new("'on init do': runs once when the round starts (trigger initialization)."),
        ["and"] = new("Both conditions must be true. Mixed with or, parentheses are required.", "if a == 1 or (b == 2 and timer_expired(t)) then"),
        ["or"] = new("Either condition may be true. Mixed with and, parentheses are required: (a or b) and c.", "if (a == 1 or b == 2) and c == 3 then"),
        ["not"] = new("Inverts the condition (or the parenthesized group) that follows.", "if not (a == 1 or b == 2) then"),
        ["function"] = new("Defines a function: a group of statements with parameters, used like an action. Each call is a copy of the body " +
            "(it uses the budget each time); no return value, no recursion. Define it at the top level, call it on its own line.",
            "function award(p, points)\n  p.score += points\nend\n...\naward(killer, 2)"),
        ["enum"] = new("A number whose values have names: declare player.enum{placer, player, spectator} role = spectator, then role == placer, role = player.",
            "declare player.enum{placer, player, spectator} role = spectator"),
        ["temp"] = new("A temporary variable that only lives during this tick: temp number|player|object|team name = value.",
            "temp player killer = none"),
        ["declare"] = new("A variable kept between ticks: declare [networked|local] global|player|team|object.number|timer|player|object|team name = value. Player, team and object variables exist once per player/team/object (current_player.streak).",
            "declare player.number streak = 0"),
        ["alias"] = new("Another name for a variable or value: alias name = path.",
            "alias carrier = global.player_0"),
        ["setting"] = new("A base setting of the gametype (score to win, time limit, teams...): setting NAME = value, or setting base_player_traits ... end. The Settings panel edits them.",
            "setting score_to_win_round = 50"),
        ["option"] = new("A lobby option: option NAME \"Title\" \"Description\" = default range MIN MAX, or with value lines. Use it in triggers like a number.",
            "option kill_points \"Kill Points\" \"Points per kill\" = 1 range -10 10"),
        ["value"] = new("One choice of a lobby option: value NUMBER \"Label\"."),
        ["range"] = new("The minimum and maximum of a number option."),
        ["traits"] = new("A named set of player traits (speed, damage resistance, weapons...): traits NAME \"Title\" \"Description\" + name = value lines + end. Apply with apply_player_traits(player, NAME).",
            "traits fast \"Fast\" \"\"\n  speed = 150\nend"),
        ["hidden"] = new("Before option: the option isn't shown in the lobby."),
        ["locked"] = new("Before setting / option: players can't change it in the lobby."),
        ["objects"] = new("A set of map objects picked by Megalo label or type; loop over them with 'for each NAME do'.",
            "objects hill with label \"koth_hill\""),
        ["constant"] = new("A number with a name.", "constant k_max_rounds = 5"),
        ["widget"] = new("A HUD widget and where it sits on screen (top_left, bottom_center...); fill it with hud_widget_set_text.", "widget timer at bottom_center"),
        ["stat"] = new("A game statistic shown in the post-game carnage report."),
        ["engine"] = new("The gametype's menu name, description, icon and category: engine + name = value lines + end."),
        ["teams"] = new("Team setup: teams + team blocks (designator, model, color...) + end."),
        ["loadout"] = new("A spawn loadout: loadout NAME + primary_weapon / backpack_weapon / equipment / grenades lines + end."),
        ["megalo"] = new("'megalo [[ ... ]]' keeps Megalo declarations as they are: game_options, player traits, map objects, HUD widgets, teams, loadouts..."),
        ["include"] = new("Adds another Megalo file (relative to data\\multiplayer\\megalo), e.g. Bungie's shared includes."),
        ["with"] = new("'for each object with label \"x\"' (or with type \"x\"): the objects of the map that have that Megalo label or type."),
        ["networked"] = new("The variable is sent to every player (needed for anything shown on screen). Default for declare."),
        ["local"] = new("The variable stays on the host only (cheaper, not shown to players)."),
        ["random"] = new("'for each random player do': one random player."),
    };

    /// <summary>Megalo text words.</summary>
    public static readonly IReadOnlyDictionary<string, Doc> MegaloKeywords = new Dictionary<string, Doc>(StringComparer.Ordinal)
    {
        ["trigger"] = new("A block that runs every tick: trigger general (once), player / team / object (once for each), initialization, local, host_migration, pregame, or a map object's name."),
        ["action"] = new("Does something: action <name> <arguments>."),
        ["condition"] = new("Gates everything after it in the block: the rest only runs when it is true. 'or' at the end of the line joins it with the next condition."),
        ["temporary"] = new("A variable that only lives during this tick: temporary <type> <name> <value>."),
        ["variables"] = new("Declares variables kept between ticks, for global, player, team or object."),
        ["game_options"] = new("Declares the options players can change in the lobby, overrides of base settings, and player traits."),
        ["map_object"] = new("A named set of map objects, picked by Megalo label or type; then 'trigger <name>' / 'for_each <name>' loops over them."),
        ["include"] = new("Adds another Megalo file."),
        ["end"] = new("Closes a block."),
        ["begin"] = new("An inline block (no trigger used): its conditions only gate what is inside."),
        ["networked"] = new("The variable is sent to every player."),
        ["local"] = new("The variable stays on the host only."),
        ["override"] = new("Changes one of the base game settings (score to win, round time, traits...)."),
        ["option"] = new("A lobby option with a list of values."),
        ["ranged_option"] = new("A lobby option with a number: default, minimum, maximum."),
        ["player_traits"] = new("A named set of player traits (damage, speed, weapons...) to apply with apply_player_traits."),
        ["engine_data"] = new("The gametype's name, description, icon and category in the menus."),
    };

    /// <summary>Names the engine provides.</summary>
    public static readonly IReadOnlyDictionary<string, Doc> Builtins = new Dictionary<string, Doc>(StringComparer.Ordinal)
    {
        ["current_player"] = new("The player this 'for each player' (or trigger player) is running for."),
        ["current_team"] = new("The team this 'for each team' is running for."),
        ["current_object"] = new("The object this 'for each object' (or a map object loop) is running for."),
        ["score"] = new("The score of a player or team (player.score, team.score)."),
        ["team"] = new("The team of a player or object (current_player.team)."),
        ["none"] = new("No object, player or team."),
        ["true"] = new("1"),
        ["false"] = new("0"),
        ["everyone"] = new("Every player (message and waypoint targets)."),
        ["no_one"] = new("Nobody (waypoint and pickup filters)."),
        ["allies"] = new("The players on the object's team."),
        ["enemies"] = new("The players not on the object's team."),
        ["enemy"] = new("player_died death type: killed by an enemy."),
        ["suicide"] = new("player_died death type: killed themselves."),
        ["betrayal"] = new("player_died death type: killed by a teammate."),
        ["neutral"] = new("No team."),
        ["defenders"] = new("The defending team (team designator)."),
        ["attackers"] = new("The attacking team (team designator)."),
    };

    /// <summary>The help for a word, by where it is: (title, text, example) or null.</summary>
    public static (string Title, Doc Doc)? Find(string word, bool kaloScript, bool afterDot)
    {
        if (!afterDot)
        {
            if (kaloScript && KaloKeywords.TryGetValue(word, out var k)) return ("keyword " + word, k);
            if (!kaloScript && MegaloKeywords.TryGetValue(word, out var m)) return ("keyword " + word, m);
            if (Conditions.TryGetValue(word, out var c)) return ("condition " + word, c);
            if (Actions.TryGetValue(word, out var a)) return ("action " + word, a);
        }
        else if (Actions.TryGetValue(word, out var method)) return ("action " + word, method);
        else if (Conditions.TryGetValue(word, out var cm)) return ("condition " + word, cm);
        if (Builtins.TryGetValue(word, out var b)) return (word, b);
        return null;
    }

    /// <summary>
    /// The compiler's argument list as a Kalo Script call: "&lt;a&gt; {x|y} [opt]" → name(&lt;a&gt;, {x|y}, [opt]).
    /// </summary>
    /// <summary>
    /// Signatures where HREK's compiler describes an action wrongly: boundary_set_visible says "&lt;object&gt; &lt;boolean&gt;"
    /// but takes the same player filter as navpoint_set_visible (Bungie's scripts write no_one / everyone / player P 1).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SignatureFixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["boundary_set_visible"] = "<object> {no_one|everyone|allies|enemies|player <player_reference> <boolean>}",
        ["get_button_time"] = "<player> <button> <milliseconds_out>",
    };

    /// <summary>Corrects the compiler's catalog in place (signatures above, the button list). Safe to call twice.</summary>
    public static void Fix(Kalo.Protocol.MegaloCatalog? catalog)
    {
        if (catalog == null) return;
        foreach (var a in catalog.Actions ?? new())
            if (a.Name != null && SignatureFixes.TryGetValue(a.Name, out var sig)) a.Description = sig;
        catalog.Lists ??= new();
        catalog.Lists["buttons"] = Buttons.ToList();
    }

    public static string KaloSignature(string name, string? megaloSignature) =>
        string.IsNullOrWhiteSpace(megaloSignature) ? name + "()" : $"{name}({string.Join(", ", KaloArguments(megaloSignature))})";

    /// <summary>The compiler's argument list as Kalo Script arguments: "&lt;a&gt; at &lt;b&gt; [x]" → "&lt;a&gt;", "at &lt;b&gt;", "[x]".</summary>
    public static List<string> KaloArguments(string? megaloSignature)
    {
        if (string.IsNullOrWhiteSpace(megaloSignature)) return new List<string>();
        var parts = new List<string>();
        int depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (char ch in megaloSignature.Trim())
        {
            if (ch is '<' or '{' or '[' or '(') depth++;
            if (ch is '>' or '}' or ']' or ')') depth = Math.Max(0, depth - 1);
            if (ch == ' ' && depth == 0)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        // Notes like "(feet)" stay with the argument before them; words like create_object's "at" start the next one.
        var merged = new List<string>();
        string prefix = "";
        foreach (string p in parts)
        {
            if (p.StartsWith('(') && merged.Count > 0) { merged[^1] += " " + p; continue; }
            bool keyword = !(p.StartsWith('<') || p.StartsWith('{') || p.StartsWith('['));
            if (keyword) { prefix += p + " "; continue; }
            merged.Add(prefix + p);
            prefix = "";
        }
        if (prefix.Length > 0) merged.Add(prefix.Trim());
        return merged;
    }
}
