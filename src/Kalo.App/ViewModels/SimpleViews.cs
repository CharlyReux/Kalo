using System.Text.RegularExpressions;

namespace Kalo.App.ViewModels;

/// <summary>One field of a Simple view: matched by the end of its path (block and struct names, no indexes).</summary>
public sealed record SimpleField(string Match, string Label, string Help);

/// <summary>The fields most people change in one kind of game file, with plain names and help, and what to open next.</summary>
public sealed record SimpleView(string Intro, IReadOnlyList<SimpleField> Fields)
{
    private readonly Dictionary<string, SimpleField?> _cache = new();

    /// <summary>"Block:barrels[0]/Struct:firing[0]/RealBounds:rounds per second" → "barrels/firing/rounds per second".</summary>
    public static string Normalize(string path) =>
        string.Join("/", path.Split('/').Select(s => Regex.Replace(s.Contains(':') ? s[(s.IndexOf(':') + 1)..] : s, @"\[\d+\]$", "")));

    public SimpleField? For(string path)
    {
        if (_cache.TryGetValue(path, out var hit)) return hit;
        string n = "/" + Normalize(path);
        hit = Fields.FirstOrDefault(f => n.EndsWith("/" + f.Match, StringComparison.OrdinalIgnoreCase));
        _cache[path] = hit;
        return hit;
    }
}

/// <summary>
/// Simple views of the game files a beginner changes most: weapons, projectiles (bullets and grenades), damage, vehicles
/// and AI characters. Field names checked with kalo-cli dump on Reach's magnum, magnum_bullet, frag_grenade,
/// warthog and elite.
/// </summary>
public static class SimpleViews
{
    public static SimpleView? For(string group) => group switch
    {
        "weapon" => Weapon,
        "projectile" => Projectile,
        "damage_effect" => Damage,
        "vehicle" => Vehicle,
        "character" => Character,
        _ => null,
    };

    private static readonly SimpleView Weapon = new(
        "The weapon's most changed settings. Each shot (below) sets how much a shot hurts and how fast the bullet flies: those numbers live in the bullet and its damage file, and Save saves them with the weapon. Every weapon that fires the same bullet changes too.",
        new SimpleField[]
        {
            new("magazines/rounds loaded maximum", "Magazine size", "Rounds in a full magazine."),
            new("magazines/rounds total initial", "Spare ammo at start", "Rounds carried besides the magazine when you pick it up or spawn with it."),
            new("magazines/rounds total maximum", "Maximum spare ammo", "The most rounds you can carry besides the magazine."),
            new("magazines/rounds reloaded", "Rounds per reload", "How many rounds one reload puts in (usually the magazine size)."),
            new("barrels/firing/rounds per second", "Fire rate", "Shots per second, from first shot to full speed (0, 0 for a semi-automatic weapon: one shot per trigger pull)."),
            new("barrels/firing/fire recovery time", "Time between shots (semi-auto)", "Seconds before a semi-automatic weapon can fire again."),
            new("barrels/firing/shots per fire", "Shots per trigger pull", "1, 1 for single shots; 3, 3 for a 3-round burst."),
            new("barrels/rounds per shot", "Ammo used per shot", "Rounds each shot takes from the magazine."),
            new("barrels/projectiles per shot", "Bullets per shot", "More than 1 for a shotgun-like spread."),
            new("barrels/error angle", "Spread", "How far shots stray, in degrees: when firing slowly, and when firing as fast as possible."),
            new("barrels/projectile", "Projectile (the bullet)", "The bullet or grenade this weapon fires: its speed and the damage file. Select it and click Open this file (right) to change them."),
            new("magnification levels", "Zoom levels", "0: no zoom; 1: one zoom level; 2: two."),
            new("magnification range", "Zoom range", "Magnification of the first and last zoom level."),
            new("weapon aim assist/autoaim angle", "Aim assist angle", "How close to a target (degrees) the reticle turns red and aim is helped."),
            new("weapon aim assist/autoaim range", "Aim assist range", "How far aim assist works (world units: 1 unit is about 3 meters)."),
            new("weapon aim assist/magnetism angle", "Bullet magnetism angle", "How far (degrees) bullets bend toward a target."),
            new("ready time", "Draw time", "Seconds to take the weapon out."),
            new("melee damage parameters/melee damage", "Melee damage", "The damage file of a melee hit with this weapon."),
        });

    private static readonly SimpleView Projectile = new(
        "What a bullet or grenade does once fired or thrown. How much it hurts is in its damage files (Impact damage for bullets, Explosion damage for grenades: select one, then Open this file).",
        new SimpleField[]
        {
            new("initial velocity", "Speed when fired", "World units per second (1 unit is about 3 meters)."),
            new("final velocity", "Speed at the end", "Speed it slows (or speeds) to."),
            new("air gravity scale", "Gravity", "0: flies straight; 1: falls like a thrown object."),
            new("maximum range", "Maximum range", "It disappears after this distance (world units)."),
            new("timer", "Fuse time (grenades)", "Seconds before it explodes, from when the timer starts (see Detonation timer starts)."),
            new("detonation timer starts", "Detonation timer starts", "When the fuse starts: right away, after the first bounce, when it stops..."),
            new("arming time", "Arming time", "Seconds before it can explode."),
            new("impact damage", "Impact damage", "Damage when it hits something (bullets). Open this file to change the damage."),
            new("detonation damage", "Explosion damage", "Damage of its explosion (grenades, rockets). Open this file to change it."),
            new("attached detonation damage", "Explosion damage when stuck", "When stuck to someone (plasma grenade)."),
        });

    private static readonly SimpleView Damage = new(
        "How much this hurts. Shields and health: a Spartan has about 70 shield and 45 health points (Reach's own numbers, before damage modifiers).",
        new SimpleField[]
        {
            new("damage lower bound", "Damage (minimum)", "The least damage one hit does."),
            new("damage upper bound", "Damage (maximum)", "The most damage one hit does (two values: the game picks between them)."),
            new("radius", "Explosion radius", "Inner and outer radius of the blast (0, 0 for a bullet). Full damage inside the inner one, fading to nothing at the outer one."),
            new("AOE core radius", "Core radius", "Area with full damage around the center."),
            new("category", "Kind of damage", "Bullet, grenade, explosion, melee... used for damage modifiers and medals."),
            new("instantaneous acceleration", "Push", "How hard it shoves what it hits."),
            new("stun", "Stun", "How much it slows the target."),
            new("flags", "Flags", "e.g. can cause headshots, does not hurt owner."),
        });

    private static readonly SimpleView Vehicle = new(
        "How the vehicle drives. Its health is not here: it is in its model file (Model, below: select it, then Open this file).",
        new SimpleField[]
        {
            new("engine/engine moment", "Acceleration (engine power)", "Higher: it picks up speed faster."),
            new("engine/engine max angular velocity", "Top speed (engine)", "How fast the wheels can turn: higher is faster."),
            new("wheel circumferance", "Wheel size", "With the engine's top speed, sets the vehicle's top speed."),
            new("turning control/maximum left turn", "Steering (left)", "How far the wheels turn."),
            new("turning control/maximum right turn (negative)", "Steering (right, negative)", "How far the wheels turn the other way."),
            new("turning control/turn rate", "Steering speed", "How fast the wheels turn."),
            new("havok vehicle physics/gravity scale", "Gravity", "1: normal; lower floats, higher sticks to the ground."),
            new("minimum flipping angular velocity", "Flip speed (minimum)", "How fast it rolls back on its wheels."),
            new("object/model", "Model (health is here)", "Select it and Open this file: its damage section holds the vehicle's health and shields."),
        });

    private static readonly SimpleView Character = new(
        "How an AI fights. Squads spawn characters (the Squad section of a mission's spawn points). Its body (model, size) is the Unit file.",
        new SimpleField[]
        {
            new("vitality properties/normal body vitality", "Health (normal)", "Health points on Normal (and Easy)."),
            new("vitality properties/normal shield vitality", "Shields (normal)", "Shield points on Normal (and Easy)."),
            new("vitality properties/legendary body vitality", "Health (Legendary)", "Health on Legendary (Heroic is in between)."),
            new("vitality properties/legendary shield vitality", "Shields (Legendary)", "Shields on Legendary."),
            new("vitality properties/shield recharge delay time", "Shield recharge delay", "Seconds without damage before shields recharge."),
            new("vitality properties/shield recharge time", "Shield recharge time", "Seconds for a full recharge."),
            new("weapons properties/weapon", "Weapon", "The weapon it uses when the squad does not give one."),
            new("weapons properties/normal accuracy bounds", "Accuracy (normal)", "How accurate its shots are, from first to best."),
            new("weapons properties/legendary accuracy bounds", "Accuracy (Legendary)", "The same on Legendary."),
            new("weapons properties/maximum firing range", "Maximum firing range", "It doesn't shoot beyond this (world units)."),
            new("general properties/default grenade type", "Grenade type", "The grenades it throws."),
            new("unit", "Unit (its body)", "The biped it is: model, size, speed. Open this file to see it."),
            new("parent character", "Based on", "Settings it doesn't set itself come from this character."),
        });
}
