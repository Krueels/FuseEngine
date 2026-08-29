using System;
using System.Collections.Generic;
using System.Numerics;

namespace Fuse.Enemy;

public enum SpiderRagdollShapeType
{
    Capsule,
    Sphere,
    Box,
    ConvexHull
}

public sealed class SpiderRagdollPartDefinition
{
    public required string Id { get; init; }

    public required string BoneName { get; init; }

    public SpiderRagdollShapeType ShapeType { get; init; } =
        SpiderRagdollShapeType.Capsule;

    // Offset do centro do corpo físico em relação ao osso.
    public Vector3 LocalOffset { get; init; } =
        Vector3.Zero;

    public Quaternion LocalRotation { get; init; } =
        Quaternion.Identity;

    // Dimensões para cápsulas.
    public float Radius { get; init; } = 0.1f;
    public float Height { get; init; } = 0.25f;

    // Dimensões para caixas.
    public Vector3 BoxHalfExtents { get; init; } =
        new(0.1f);

    // Vértices opcionais para convex hull.
    public Vector3[]? ConvexHullVertices { get; init; }

    public float Mass { get; init; } = 0.1f;
    public float Friction { get; init; } = 0.5f;
    public float Restitution { get; init; } = 0.1f;

    public bool CollidesWithWorld { get; init; } = true;

    // Desligado inicialmente para evitar instabilidade entre segmentos.
    public bool CollidesWithOtherParts { get; init; } = false;
}

public sealed class SpiderRagdollJointDefinition
{
    public required string Id { get; init; }

    public required string ParentPartId { get; init; }

    public required string ChildPartId { get; init; }

    // Âncoras em espaço local de cada corpo físico.
    public Vector3 ParentAnchor { get; init; } =
        Vector3.Zero;

    public Vector3 ChildAnchor { get; init; } =
        Vector3.Zero;

    // Limites no espaço local da junta.
    public float TwistMinRadians { get; init; } =
        -0.75f;

    public float TwistMaxRadians { get; init; } =
        0.75f;

    public float SwingLimitRadians { get; init; } =
        0.9f;

    // Inicialmente as partes da mesma pata não colidem entre si.
    public bool DisableCollision { get; init; } = true;
}

public sealed class SpiderRagdollDefinition
{
    public required string Name { get; init; }

    // Tempo que o ragdoll permanecerá na cena.
    public float LifetimeSeconds { get; set; } = 8f;

    public float RootRadius { get; set; } = 0.6f;

    public float RootHeight { get; set; } = 0.3f;
    
    public float RootMass { get; set; } = 3f;

    public float DefaultDensity { get; set; } = 1f;

    public bool SelfCollisionEnabled { get; set; } = false;

    public List<SpiderRagdollPartDefinition> Parts { get; } = new();

    public List<SpiderRagdollJointDefinition> Joints { get; } = new();

    public bool TryGetPart(
        string id,
        out SpiderRagdollPartDefinition part)
    {
        for (int i = 0; i < Parts.Count; i++)
        {
            if (!string.Equals(
                    Parts[i].Id,
                    id,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            part = Parts[i];
            return true;
        }

        part = null!;
        return false;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException(
                "Spider ragdoll definition requires a name.");

        var partIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (SpiderRagdollPartDefinition part in Parts)
        {
            if (string.IsNullOrWhiteSpace(part.Id))
                throw new InvalidOperationException(
                    "Spider ragdoll part requires an id.");

            if (string.IsNullOrWhiteSpace(part.BoneName))
                throw new InvalidOperationException(
                    $"Spider ragdoll part '{part.Id}' requires a bone name.");

            if (!partIds.Add(part.Id))
                throw new InvalidOperationException(
                    $"Duplicated spider ragdoll part id '{part.Id}'.");

            if (!float.IsFinite(part.Mass) || part.Mass < 0f)
                throw new InvalidOperationException(
                    $"Invalid mass on spider ragdoll part '{part.Id}'.");

            if (!float.IsFinite(part.Radius) || part.Radius <= 0f)
                throw new InvalidOperationException(
                    $"Invalid radius on spider ragdoll part '{part.Id}'.");

            if (!float.IsFinite(part.Height) || part.Height <= 0f)
                throw new InvalidOperationException(
                    $"Invalid height on spider ragdoll part '{part.Id}'.");
        }

        var jointIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (SpiderRagdollJointDefinition joint in Joints)
        {
            if (string.IsNullOrWhiteSpace(joint.Id))
                throw new InvalidOperationException(
                    "Spider ragdoll joint requires an id.");

            if (!jointIds.Add(joint.Id))
                throw new InvalidOperationException(
                    $"Duplicated spider ragdoll joint id '{joint.Id}'.");

            if (!partIds.Contains(joint.ParentPartId))
                throw new InvalidOperationException(
                    $"Joint '{joint.Id}' references missing parent part " +
                    $"'{joint.ParentPartId}'.");

            if (!partIds.Contains(joint.ChildPartId))
                throw new InvalidOperationException(
                    $"Joint '{joint.Id}' references missing child part " +
                    $"'{joint.ChildPartId}'.");
        }
    }
}