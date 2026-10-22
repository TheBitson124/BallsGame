using UnityEngine;

// Anything a FunctionGrid's balls react to: they bend under it and hold it up.
// The grid needs its Rigidbody and the capsule around it (a sphere is a capsule with height <= 2 * radius),
// and tells it every physics step what the balls did.
public interface IFunctionGridBody
{
    Rigidbody Body { get; }
    CapsuleCollider Capsule { get; }

    // Whether the balls hold it up this step; read at the start of the next FixedUpdate, like collisions
    void ReportGround(bool onGround);

    // How much the balls it stands on slow it, 0 = not at all, 1 = can't move
    void ReportSlowdown(float amount);
}
