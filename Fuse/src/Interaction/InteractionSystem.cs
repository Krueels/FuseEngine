using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using JoltPhysicsSharp;
using Fuse.Physics;
using Fuse.Scene;

namespace Fuse.Interaction;

public static class InteractionSystem
{
    private static readonly Dictionary<string, Type> _interactableTypes;

    static InteractionSystem()
    {
        _interactableTypes = [];
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            var attr = type.GetCustomAttribute<InteractableTypeAttribute>();
            if (attr != null && typeof(IInteractable).IsAssignableFrom(type))
                _interactableTypes[attr.TypeName] = type;
        }
    }

    public static IInteractable? CreateInteractable(string typeName)
    {
        if (_interactableTypes.TryGetValue(typeName, out var type))
            return Activator.CreateInstance(type) as IInteractable;
        return null;
    }
    public static GCHandle RegisterInteractable(BodyInterface bi, BodyID bodyId, IInteractable interactable)
    {
        var gcHandle = GCHandle.Alloc(interactable);
        nint ptr = GCHandle.ToIntPtr(gcHandle);
        bi.SetUserData(bodyId, (ulong)ptr);
        return gcHandle;
    }

    public static IInteractable? GetInteractable(BodyInterface bi, BodyID bodyId, ulong userData)
    {
        if (userData == 0) return null;
        var gcHandle = GCHandle.FromIntPtr((nint)userData);
        return gcHandle.Target as IInteractable;
    }

    public static void UnregisterInteractable(BodyInterface bi, BodyID bodyId, ulong userData)
    {
        if (userData != 0)
        {
            var gcHandle = GCHandle.FromIntPtr((nint)userData);
            gcHandle.Free();
            bi.SetUserData(bodyId, 0);
        }
    }

    public static IInteractable? RaycastInteractable(
        SceneManager scene, Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (!scene.Raycast(origin, direction, maxDistance, out var hit))
            return null;

        if (!hit.BodyID.IsValid) return null;

        ulong userData = scene.Physics.BodyInterface.GetUserData(hit.BodyID);
        return GetInteractable(scene.Physics.BodyInterface, hit.BodyID, userData);
    }
}
