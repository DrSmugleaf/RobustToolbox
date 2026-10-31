using System.Numerics;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.UnitTesting.Server;

namespace Robust.UnitTesting.Shared.Physics;

[TestFixture]
internal sealed class ClippingRecoveryTest
{
    private const float FrameTime = 1f / 30f;

    // Recovery moves the body in one step; the regular solver never moves a deadlocked body this far in a tick.
    private const float RecoveryJumpDistance = 1f;

    [TestCase(BodyType.Dynamic)]
    [TestCase(BodyType.KinematicController)]
    public void EjectsBodyFromCenterOfFiveByFiveWall(BodyType bodyType)
    {
        var (entityManager, mapUid) = CreateWorld();
        SpawnWalls(entityManager, mapUid, -2, 2, -2, 2);
        var itemUid = SpawnItem(entityManager, mapUid, Vector2.Zero, bodyType, 0.75f);

        var recoveryFrame = RunUntilRecovered(entityManager, itemUid, 600, out var finalPosition);

        Assert.That(recoveryFrame, Is.GreaterThanOrEqualTo(0), $"Expected the item to be recovered, but it stayed at {finalPosition}.");
        if (bodyType == BodyType.KinematicController)
            Assert.That(recoveryFrame, Is.GreaterThanOrEqualTo(10));

        // The walls end at 2.5 and the item is 0.75 in half-size, so a free position is at least 3.25 away on some axis.
        Assert.That(
            System.MathF.Max(System.MathF.Abs(finalPosition.X), System.MathF.Abs(finalPosition.Y)),
            Is.GreaterThan(3.2f),
            $"Expected the item to leave the wall block, but it ended at {finalPosition}.");
    }

    [TestCase(BodyType.Dynamic)]
    [TestCase(BodyType.KinematicController)]
    public void PrefersNearbyOpeningOverMoreDistantExit(BodyType bodyType)
    {
        var (entityManager, mapUid) = CreateWorld();
        SpawnWalls(entityManager, mapUid, -2, 2, -2, 2, skip: new Vector2i(2, 1));
        var itemUid = SpawnItem(entityManager, mapUid, Vector2.Zero, bodyType, 0.2f);

        var recoveryFrame = RunUntilRecovered(entityManager, itemUid, 600, out var finalPosition);

        var openingDirection = Vector2.Normalize(new Vector2(2f, 1f));
        Assert.That(recoveryFrame, Is.GreaterThanOrEqualTo(0), $"Expected the item to be recovered, but it stayed at {finalPosition}.");
        Assert.That(
            Vector2.Dot(Vector2.Normalize(finalPosition), openingDirection),
            Is.GreaterThan(0.95f),
            $"Expected recovery into the nearby opening along {openingDirection}, but it ended at {finalPosition}.");
    }

    // Only controllers (players) deadlock here; the regular solver pops a dynamic body out of this wall by itself.
    [TestCase(BodyType.KinematicController)]
    public void ReturnsBodyToSideItCameFrom(BodyType bodyType)
    {
        var (entityManager, mapUid) = CreateWorld();
        var transformSystem = entityManager.System<SharedTransformSystem>();

        // A three tile thick wall between a south and a north room. A controller stuck in its middle is deadlocked:
        // every push is cancelled by an opposite one, and a blind search would find the north side first.
        SpawnWalls(entityManager, mapUid, -3, 3, -1, 1);
        var southPosition = new Vector2(0f, -3f);
        var itemUid = SpawnItem(entityManager, mapUid, southPosition, bodyType, 0.35f);

        var physicsSystem = entityManager.System<SharedPhysicsSystem>();
        for (var i = 0; i < 5; i++)
        {
            physicsSystem.Update(FrameTime);
        }

        transformSystem.SetWorldPosition(itemUid, Vector2.Zero);

        var recoveryFrame = RunUntilRecovered(entityManager, itemUid, 600, out var finalPosition);

        Assert.That(recoveryFrame, Is.GreaterThanOrEqualTo(0), $"Expected the item to be recovered, but it stayed at {finalPosition}.");
        Assert.That(
            Vector2.Distance(finalPosition, southPosition),
            Is.LessThan(0.1f),
            $"Expected the item to return to the south room at {southPosition}, but it ended at {finalPosition}.");
    }

    // A body that left the solver may have been carried anywhere, so its old safe position must not be used.
    [TestCase(BodyType.KinematicController)]
    public void ForgetsSafePositionAfterLeavingSolver(BodyType bodyType)
    {
        var (entityManager, mapUid) = CreateWorld();
        var physicsSystem = entityManager.System<SharedPhysicsSystem>();
        var transformSystem = entityManager.System<SharedTransformSystem>();

        SpawnWalls(entityManager, mapUid, -3, 3, -1, 1);
        var southPosition = new Vector2(0f, -3f);
        var itemUid = SpawnItem(entityManager, mapUid, southPosition, bodyType, 0.35f);
        var item = entityManager.GetComponent<PhysicsComponent>(itemUid);

        for (var i = 0; i < 5; i++)
        {
            physicsSystem.Update(FrameTime);
        }

        // Like being put into a locker: the body stops being solved for a while.
        physicsSystem.SetCanCollide(itemUid, false, body: item);
        for (var i = 0; i < 5; i++)
        {
            physicsSystem.Update(FrameTime);
        }

        transformSystem.SetWorldPosition(itemUid, Vector2.Zero);
        physicsSystem.SetCanCollide(itemUid, true, body: item);
        physicsSystem.WakeBody(itemUid, body: item);

        var recoveryFrame = RunUntilRecovered(entityManager, itemUid, 600, out var finalPosition);

        Assert.That(recoveryFrame, Is.GreaterThanOrEqualTo(0), $"Expected the item to be recovered, but it stayed at {finalPosition}.");
        Assert.That(
            Vector2.Distance(finalPosition, southPosition),
            Is.GreaterThan(0.5f),
            $"Expected the stale safe position at {southPosition} to be ignored, but the item returned there.");
    }

    [Test]
    public void WaitsBeforeRetryingFailedSearch()
    {
        var (entityManager, mapUid) = CreateWorld();
        var physicsSystem = entityManager.System<SharedPhysicsSystem>();

        // Deeper than the maximum search distance in every direction, like an item buried in asteroid rock.
        SpawnWalls(entityManager, mapUid, -7, 7, -7, 7);
        var itemUid = SpawnItem(entityManager, mapUid, Vector2.Zero, BodyType.Dynamic, 0.75f);
        var item = entityManager.GetComponent<PhysicsComponent>(itemUid);

        // Three seconds, shorter than the retry delay.
        for (var i = 0; i < 90; i++)
        {
            physicsSystem.Update(FrameTime);
        }

        Assert.That(
            physicsSystem.ClippingRecoveryFailedSearches,
            Is.EqualTo(1),
            "Expected one failed search followed by a cooldown, not a search every tick.");
        Assert.That(item.ClippingRecoveryTime, Is.LessThan(0f));
    }

    private static (IEntityManager EntityManager, EntityUid MapUid) CreateWorld()
    {
        var simulation = RobustServerSimulation.NewSimulation().InitializeInstance();
        var entityManager = simulation.Resolve<IEntityManager>();
        var (mapUid, _) = simulation.CreateMap();
        entityManager.System<SharedPhysicsSystem>().SetGravity(Vector2.Zero);
        return (entityManager, mapUid);
    }

    private static void SpawnWalls(
        IEntityManager entityManager,
        EntityUid mapUid,
        int minX,
        int maxX,
        int minY,
        int maxY,
        Vector2i? skip = null)
    {
        var fixtureSystem = entityManager.System<FixtureSystem>();
        var physicsSystem = entityManager.System<SharedPhysicsSystem>();

        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                if (skip == new Vector2i(x, y))
                    continue;

                var wallUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, new Vector2(x, y)));
                var wall = entityManager.AddComponent<PhysicsComponent>(wallUid);
                var manager = entityManager.EnsureComponent<FixturesComponent>(wallUid);
                physicsSystem.SetBodyType(wallUid, BodyType.Static, manager: manager, body: wall);

                var wallShape = new PolygonShape(0.001f);
                wallShape.SetAsBox(0.5f, 0.5f);
                fixtureSystem.CreateFixture(
                    wallUid,
                    "wall",
                    new Fixture(wallShape, 1, 1, true),
                    manager: manager,
                    body: wall);
                physicsSystem.SetCanCollide(wallUid, true, manager: manager, body: wall);
            }
        }
    }

    private static EntityUid SpawnItem(
        IEntityManager entityManager,
        EntityUid mapUid,
        Vector2 position,
        BodyType bodyType,
        float halfSize)
    {
        var fixtureSystem = entityManager.System<FixtureSystem>();
        var physicsSystem = entityManager.System<SharedPhysicsSystem>();

        var itemUid = entityManager.SpawnEntity(null, new EntityCoordinates(mapUid, position));
        var item = entityManager.AddComponent<PhysicsComponent>(itemUid);
        var itemManager = entityManager.EnsureComponent<FixturesComponent>(itemUid);
        physicsSystem.SetBodyType(itemUid, bodyType, manager: itemManager, body: item);

        var itemShape = new PolygonShape(0.001f);
        itemShape.SetAsBox(halfSize, halfSize);
        fixtureSystem.CreateFixture(
            itemUid,
            "item",
            new Fixture(itemShape, 1, 1, true),
            manager: itemManager,
            body: item);

        // Like a player, a controller never sleeps.
        if (bodyType == BodyType.KinematicController)
            physicsSystem.SetSleepingAllowed(itemUid, item, false);

        physicsSystem.WakeBody(itemUid, manager: itemManager, body: item);
        return itemUid;
    }

    /// <summary>
    /// Steps physics and returns the frame on which the body jumped out of the walls, or -1 if it never did.
    /// </summary>
    private static int RunUntilRecovered(IEntityManager entityManager, EntityUid itemUid, int frames, out Vector2 finalPosition)
    {
        var physicsSystem = entityManager.System<SharedPhysicsSystem>();
        var transformSystem = entityManager.System<SharedTransformSystem>();
        var recoveryFrame = -1;
        var lastPosition = transformSystem.GetWorldPosition(itemUid);

        for (var i = 0; i < frames; i++)
        {
            physicsSystem.Update(FrameTime);

            var position = transformSystem.GetWorldPosition(itemUid);
            if (recoveryFrame < 0 && Vector2.Distance(position, lastPosition) > RecoveryJumpDistance)
                recoveryFrame = i;

            lastPosition = position;
        }

        finalPosition = lastPosition;
        return recoveryFrame;
    }
}
