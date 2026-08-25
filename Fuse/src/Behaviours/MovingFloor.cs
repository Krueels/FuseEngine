using System.Numerics;
using Fuse.Core;
using Fuse.Interaction;

namespace Fuse.Behaviours;

[Behaviour("MovingFloor")]
public sealed class MovingFloor : IBehaviour
{
    public Renderer.Entity? Entity { get; set; }
    public Physics.PhysicsWorld? World { get; set; }

    [Export] public float DistanceZ { get; set; } = -4f;
    [Export] public float Speed { get; set; } = 2f;

    private Vector3 _startPos;
    private Vector3 _endPos;
    private float _t;
    private bool _forward = true;
    private bool _initialized;
    private float _totalDistance;

    public void Update(float dt)
    {
        if (Entity?.Body == null || !Entity.Body.IsBuilt || World == null)
            return;

        if (!_initialized)
        {
            _startPos = Entity.Body.Position(World);
            _endPos = _startPos + new Vector3(0f, 0f, DistanceZ);
            _totalDistance = Vector3.Distance(_startPos, _endPos);
            if (_totalDistance < 0.001f) return;
            _initialized = true;
        }

        // Advance t based on time — completely frame-rate independent
        _t += (Speed / _totalDistance) * dt;

        if (_t >= 1.0f)
        {
            _t -= 1.0f;
            _forward = !_forward;
        }

        // Lerp between the two endpoints
        Vector3 newPos = _forward
            ? Vector3.Lerp(_startPos, _endPos, _t)
            : Vector3.Lerp(_endPos, _startPos, _t);

        World.BodyInterface.SetPosition(Entity.Body.Native, newPos, JoltPhysicsSharp.Activation.Activate);
    }
}
