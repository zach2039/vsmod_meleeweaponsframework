﻿using ImGuiNET;
using ProtoBuf;
using System.Numerics;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace MeleeWeaponsFramework;

public readonly struct MeleeAttackDamageId
{
    public readonly int ItemId;
    public readonly int AttackId;
    public readonly int DamageId;

    public MeleeAttackDamageId(int itemId, int attackId, int damageId)
    {
        ItemId = itemId;
        AttackId = attackId;
        DamageId = damageId;
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public struct MeleeAttackDamagePacket
{
    public MeleeAttackDamageId Id { get; set; }
    public float[] Position { get; set; }
    public long AttackerEntityId { get; set; }
    public long TargetEntityId { get; set; }
}

public class MeleeAttackDamageTypeStats
{
    public float Damage { get; set; } = 0;
    public float Knockback { get; set; } = 0;
    public float Stagger { get; set; } = 1;
    public int Tier { get; set; } = 0;
    public string DamageType { get; set; } = "PiercingAttack";
    public float[] HitWindow { get; set; } = new float[2];
    public int DurabilityDamage { get; set; } = 1;
    public float[] Collider { get; set; } = new float[6];
}

public interface IDirectionalDamage
{
    public AttackDirection Direction { get; set; }
}

public interface ILocationalDamage
{
    public Vector3 Position { get; set; }
}

public class MeleeAttackDamageSource : DamageSource, IDirectionalDamage, ILocationalDamage
{
    public AttackDirection Direction { get; set; }
    public Vector3 Position { get; set; }
}

public class MeleeAttackDamageType : IHasLineCollider
{
    public MeleeAttackDamageId Id { get; }
    public LineSegmentCollider RelativeCollider { get; set; }
    public LineSegmentCollider InWorldCollider { get; set; }

    public readonly float Damage;
    public readonly float Knockback;
    public readonly float Stagger;
    public readonly int Tier;
    public readonly EnumDamageType DamageType;
    /// <summary>
    /// In fraction of attack duration
    /// </summary>
    public readonly Vector2 HitWindow;
    public readonly int DurabilityDamage;

    public MeleeAttackDamageType(
        MeleeAttackDamageId id,
        float damage,
        EnumDamageType damageType,
        LineSegmentCollider collider,
        Vector2 hitWindow,
        int durabilityDamage = 1,
        int tier = 0,
        float knockback = 0,
        float stagger = 1.0f
        )
    {
        Damage = damage;
        DamageType = damageType;
        Tier = tier;
        Knockback = knockback;
        Stagger = stagger;
        RelativeCollider = collider;
        InWorldCollider = collider;
        Id = id;
        HitWindow = hitWindow;
        DurabilityDamage = durabilityDamage;
    }

    public MeleeAttackDamageType(MeleeAttackDamageId id, MeleeAttackDamageTypeStats stats)
    {
        Damage = stats.Damage;
        Knockback = stats.Knockback;
        Stagger = stats.Stagger;
        Tier = stats.Tier;
        DamageType = Enum.Parse<EnumDamageType>(stats.DamageType);
        RelativeCollider = new LineSegmentCollider(stats.Collider);
        InWorldCollider = new LineSegmentCollider(stats.Collider);
        Id = id;
        HitWindow = new Vector2(stats.HitWindow[0], stats.HitWindow[1]);
        DurabilityDamage = stats.DurabilityDamage;
    }

    public Vector3? TryAttack(IPlayer attacker, Entity target, AttackDirection direction)
    {
        Vector3? collisionPoint = Collide(target);

        if (collisionPoint == null) return null;

        bool received = Attack(attacker.Entity, target, direction, collisionPoint.Value);

        return received ? collisionPoint : null;
    }
    public bool Attack(Entity attacker, Entity target, AttackDirection direction, Vector3 position)
    {
        if (attacker.Api is ICoreServerAPI serverApi && attacker is EntityPlayer playerAttacker)
        {
            if (target is EntityPlayer && (!serverApi.Server.Config.AllowPvP || !playerAttacker.Player.HasPrivilege("attackplayers"))) return false;
            if (target is not EntityPlayer && !playerAttacker.Player.HasPrivilege("attackcreatures")) return false;
        }

        string attackerId = attacker.Code.ToString();
        if (attacker is EntityPlayer player)
        {
            attackerId = player.GetName();
        }
        string targetId = target.Code.ToString();
        if (target is EntityPlayer playerTarget)
        {
            targetId = playerTarget.GetName();
        }

        bool damageReceived = target.ReceiveDamage(new MeleeAttackDamageSource()
        {
            Source = attacker is EntityPlayer ? EnumDamageSource.Player : EnumDamageSource.Entity,
            SourceEntity = null,
            CauseEntity = attacker,
            Type = DamageType,
            DamageTier = Tier,
            Direction = direction,
            Position = position
        }, Damage);

        if (attacker.Api.Side == EnumAppSide.Server) LoggerUtil.Verbose(attacker.Api, this, $"Entity '{attackerId}' attacks entity '{targetId}' from direction '{direction}'. Target receives '{damageReceived}' damage.");

        bool received = damageReceived || Damage <= 0;

        if (received)
        {
            Vec3f knockback = (target.Pos.XYZFloat - attacker.Pos.XYZFloat).Normalize() * Knockback * _knockbackFactor * (1.0f - target.Properties.KnockbackResistance);
            target.SidedPos.Motion.X *= Stagger;
            target.SidedPos.Motion.Z *= Stagger;
            target.SidedPos.Motion.Add(knockback);
        }

        return received;
    }

#if DEBUG
    public void Editor(string title)
    {
        Vector3 head = RelativeCollider.Position;
        Vector3 tail = RelativeCollider.Position + RelativeCollider.Direction;

        bool headChanged = ImGui.DragFloat3($"Head##{title}", ref head);
        bool tailChanged = ImGui.DragFloat3($"Tail##{title}", ref tail);

        if (headChanged || tailChanged) RelativeCollider = new LineSegmentCollider(head, tail - head);
    }
#endif

    private const float _knockbackFactor = 0.1f;
    private Vector3? Collide(Entity target)
    {
        Cuboidf collisionBox = GetCollisionBox(target);

        if (!InWorldCollider.RoughIntersect(collisionBox)) return null;

        return InWorldCollider.IntersectCuboid(collisionBox);
    }
    private static Cuboidf GetCollisionBox(Entity entity)
    {
        Cuboidf collisionBox = entity.CollisionBox.Clone(); // @TODO: Refactor to not clone
        EntityPos position = entity.Pos;
        collisionBox.X1 += (float)position.X;
        collisionBox.Y1 += (float)position.Y;
        collisionBox.Z1 += (float)position.Z;
        collisionBox.X2 += (float)position.X;
        collisionBox.Y2 += (float)position.Y;
        collisionBox.Z2 += (float)position.Z;
        return collisionBox;
    }
}