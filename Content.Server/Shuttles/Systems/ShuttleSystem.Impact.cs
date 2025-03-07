using System.Numerics;
using Content.Server.Shuttles.Components;
using Robust.Server.GameObjects;
using Content.Shared.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Physics.Events;
using Robust.Shared.Map.Components;
using Content.Shared.Damage;
using Content.Shared.Buckle.Components;
using Content.Shared.Mobs.Components;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    [Dependency] private readonly MapSystem _mapSys = default!;
    [Dependency] private readonly DamageableSystem _damageSys = default!;

    /// <summary>
    /// Minimum velocity difference between 2 bodies for a shuttle "impact" to occur.
    /// </summary>
    private const int MinimumImpactVelocity = 10;

    /// <summary>
    /// Kinetic energy required to dismantle a single tile
    /// </summary>
    private const float TileBreakEnergy = 5000;

    /// <summary>
    /// Kinetic energy required to spawn sparks
    /// </summary>
    private const float SparkEnergy = 7000;

    private readonly SoundCollectionSpecifier _shuttleImpactSound = new("ShuttleImpactSound");

    private void InitializeImpact()
    {
        SubscribeLocalEvent<ShuttleComponent, StartCollideEvent>(OnShuttleCollide);
    }

    private void OnShuttleCollide(EntityUid uid, ShuttleComponent component, ref StartCollideEvent args)
    {
        if (!TryComp<MapGridComponent>(uid, out var ourGrid) ||
            !TryComp<MapGridComponent>(args.OtherEntity, out var otherGrid))
            return;

        var ourBody = args.OurBody;
        var otherBody = args.OtherBody;

        // TODO: Would also be nice to have a continuous sound for scraping.
        var ourXform = Transform(uid);

        if (ourXform.MapUid == null)
            return;

        var otherXform = Transform(args.OtherEntity);

        var ourPoint = Vector2.Transform(args.WorldPoint, _transform.GetInvWorldMatrix(ourXform));
        var otherPoint = Vector2.Transform(args.WorldPoint, _transform.GetInvWorldMatrix(otherXform));

        var ourVelocity = _physics.GetLinearVelocity(uid, ourPoint, ourBody, ourXform);
        var otherVelocity = _physics.GetLinearVelocity(args.OtherEntity, otherPoint, otherBody, otherXform);
        var jungleDiff = (ourVelocity - otherVelocity).Length();

        if (jungleDiff < MinimumImpactVelocity)
            return;

        var energy = ourBody.Mass * Math.Pow(jungleDiff, 2) / 2;
        var dir = (ourVelocity.Length() > otherVelocity.Length() ? ourVelocity : -otherVelocity).Normalized();
        
        // Convert the collision point directly to tile indices without creating intermediate EntityCoordinates
        // ourPoint and otherPoint are already in local space of their respective entities
        var ourTile = new Vector2i((int)Math.Floor(ourPoint.X / ourGrid.TileSize), (int)Math.Floor(ourPoint.Y / ourGrid.TileSize));
        var otherTile = new Vector2i((int)Math.Floor(otherPoint.X / otherGrid.TileSize), (int)Math.Floor(otherPoint.Y / otherGrid.TileSize));
        
        ProcessTile(uid, ourGrid, ourTile, (float) energy, -dir);
        ProcessTile(args.OtherEntity, otherGrid, otherTile, (float) energy, dir);

        var coordinates = new EntityCoordinates(ourXform.MapUid.Value, args.WorldPoint);
        var volume = MathF.Min(10f, 1f * MathF.Pow(jungleDiff, 0.5f) - 5f);
        var audioParams = AudioParams.Default.WithVariation(SharedContentAudioSystem.DefaultVariation).WithVolume(volume);
        _audio.PlayPvs(_shuttleImpactSound, coordinates, audioParams);
        
        // Knockdown unbuckled entities on both grids
        KnockdownEntitiesOnGrid(uid);
        KnockdownEntitiesOnGrid(args.OtherEntity);
    }

    /// <summary>
    /// Knocks down all unbuckled entities on the specified grid.
    /// </summary>
    private void KnockdownEntitiesOnGrid(EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return;
            
        // Find all entities on the grid
        var buckleQuery = GetEntityQuery<BuckleComponent>();
        var knockdownTime = TimeSpan.FromSeconds(5);
        
        // Get all entities with MobState component on the grid
        var query = EntityQueryEnumerator<MobStateComponent, TransformComponent>();
        
        while (query.MoveNext(out var uid, out var mobState, out var xform))
        {
            // Skip entities not on this grid
            if (xform.GridUid != gridUid)
                continue;
                
            // If entity has a buckle component and is buckled, skip it
            if (buckleQuery.TryGetComponent(uid, out var buckle) && buckle.Buckled)
                continue;
                
            // Apply knockdown to unbuckled entities
            _stuns.TryKnockdown(uid, knockdownTime, true);
        }
    }

    private void ProcessTile(EntityUid uid, MapGridComponent grid, Vector2i tile, float energy, Vector2 dir)
    {
        DamageSpecifier damage = new();
        damage.DamageDict = new() { { "Blunt", energy } };

        foreach (EntityUid localUid in _lookup.GetLocalEntitiesIntersecting(uid, tile, gridComp: grid))
        {
            _damageSys.TryChangeDamage(localUid, damage);

            TransformComponent form = Transform(localUid);
            if (!form.Anchored)
                _transform.Unanchor(localUid, form);
            _throwing.TryThrow(localUid, dir);
        }

        if (energy > TileBreakEnergy)
            _mapSys.SetTile(new Entity<MapGridComponent>(uid, grid), tile, Tile.Empty);

        if (energy > SparkEnergy)
            Spawn("EffectSparks", new EntityCoordinates(uid, tile));
    }
}
