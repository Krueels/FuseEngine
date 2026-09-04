using System.Numerics;
using Fuse.Input;
using Fuse.Physics;
using Fuse.Scene;

namespace Fuse.Interaction;

public class PlayerInteraction
{
    private readonly SceneManager _scene;
    private readonly Player.Player _player;
    private readonly UI.HUDImage _crosshairNode;
    private readonly Renderer.Texture _crosshairTexture;
    private readonly Renderer.Texture _crosshairInteractTexture;

    private IInteractable? _lookingAt;

    public PlayerInteraction(SceneManager scene, Player.Player player, UI.HUDImage crosshairNode, Renderer.Texture crosshairNormal, Renderer.Texture crosshairInteract)
    {
        _scene = scene;
        _player = player;
        _crosshairNode = crosshairNode;
        _crosshairTexture = crosshairNormal;
        _crosshairInteractTexture = crosshairInteract;
    }

    public IInteractable? LookingAt => _lookingAt;
    public Renderer.Entity? LookingEntity => _lookingAt?.Entity;

    public void Update()
    {
        Vector3 origin = _player.EyePosition;
        Vector3 dir = _player.Camera.Front;
        float range = 5.0f;

        var hit = InteractionSystem.RaycastInteractable(_scene, origin, dir, range);

        if (hit != _lookingAt)
        {
            _lookingAt = hit;
            if (hit != null)
                _crosshairNode.Texture = _crosshairInteractTexture;
            else
                _crosshairNode.Texture = _crosshairTexture;
        }

        if (!Input.Input.IsCursorDisabled() || InputManager.CurrentContext == InputContext.UI)
            return;

        if (Input.Input.KeyPressed(KeyCodes.E) && _lookingAt != null)
        {
            _lookingAt.OnInteract();
        }
    }
}
