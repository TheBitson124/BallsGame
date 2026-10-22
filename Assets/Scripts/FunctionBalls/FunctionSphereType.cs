using UnityEngine;
using UnityEngine.Serialization;
// One kind of ball in a FunctionGrid. Every type defines a round deformation under the body:
//   depth(dx, dz) = holeDepth · (1 − (dx² + dz²) / (holeWidth / 2)²), and 0 outside the hole
// holeDepth moves over time: it settles at restDepth while you stand on it, a landing dips it toward
// landingDepth, and then it comes back up to restDepth, throwing you back out if the type bounces.
// Depths below 0 are above rest height: a negative restDepth grows a mound that lifts you while you stand on it.
// Ball heights are direct samples of this function; the balls themselves have no physics, colliders or GameObjects.
[CreateAssetMenu(fileName = "FunctionSphereType", menuName = "FunctionBalls/Sphere Type", order = 0)]
public class FunctionSphereType : ScriptableObject
{
    [Header("Hole")]
    [Tooltip("How deep you sink standing on it. 0 = hard ground. Negative = the balls rise under you instead, lifting you onto a mound this high")]
    [FormerlySerializedAs("maxDepth")]
    public float restDepth = 0.5f;
    [Tooltip("Width of the deformation around the body, edge to edge")]
    [Min(0.01f)] public float holeWidth = 2f;
    [Tooltip("How fast the balls move from flat toward restDepth while you stand on it, in units/s: sinking, or rising for a negative restDepth. Low = you slowly sink in (or are slowly lifted)")]
    [Min(0.01f)] public float sinkSpeed = 1f;
    [Tooltip("How fast the balls go back: to restDepth after a landing dip, or flat once you leave it. Units/s")]
    [Min(0.01f)] public float riseSpeed = 1f;
    [Tooltip("On: jumping keeps the balls where they are; they only go back to flat once the body is over another type or off the grid. Off: they start going back the moment the body leaves them")]
    public bool holdWhileAirborne = false;

    [Header("Landing")]
    [Tooltip("How deep a hard landing pushes the hole before it comes back up to restDepth. Negative = above rest height, for a mound that a landing only flattens part way. At or below restDepth = landings don't dip")]
    public float landingDepth = 0.75f;
    [Tooltip("Landing speed (units/s) that dips all the way to landingDepth. Slower landings dip proportionally less")]
    [Min(0.01f)] public float fullLandingSpeed = 10f;
    [Tooltip("Landings slower than this (units/s) don't dip, so stepping off a ledge doesn't bounce you")]
    [Min(0f)] public float minLandingSpeed = 2f;
    [Tooltip("Share of the landing speed you're thrown back out with on the first bounce. 0 = you settle back to restDepth (mud), 1 = back up as high as you fell from. Above 1, later bounces fade it back to 1 over maxBounceCount bounces, so bouncing climbs and then holds its height until the body leaves the grid")]
    [FormerlySerializedAs("bounceBoost")]
    [Min(0f)] public float bounce = 0f;
    [Tooltip("Bounces it takes for a bounce above 1 to fade back to 1 (each one cuts the same share of what's left above 1). After that the body bounces back exactly as high every time, and jumping doesn't add to it")]
    [Min(1)] public int maxBounceCount = 5;
    [Tooltip("Landings slower than this (units/s) don't count toward maxBounceCount: they bounce at the current value without fading it, so small hops can't use the climb up. 0 = every bounce counts")]
    [Min(0f)] public float minBounceSpeed = 4f;

    // Each bounce keeps this share of the bounce above 1; under BounceDone above 1 it counts as 1.
    // Worked out from bounce and maxBounceCount so the fade reaches BounceDone exactly at the last counted bounce.
    public const float BounceDone = 0.01f;
    public float BounceFade { get; private set; }

    void OnEnable() => UpdateBounceFade();
    void OnValidate() => UpdateBounceFade();

    private void UpdateBounceFade()
    {
        float excess = bounce - 1f;
        BounceFade = excess > BounceDone ? Mathf.Pow(BounceDone / excess, 1f / Mathf.Max(1, maxBounceCount)) : 0f;
    }

    // The bounce for the landing after one that used this bounce
    public float NextBounce(float current)
    {
        if (current <= 1f) return current;
        float excess = (current - 1f) * BounceFade;
        return excess < BounceDone * 1.001f ? 1f : 1f + excess;
    }

    [Header("Body")]
    [Tooltip("Slows whatever stands on it. 0 = full speed, 1 = can't move")]
    [FormerlySerializedAs("playerSlowdown")]
    [Range(0f, 1f)] public float slowdown = 0f;

    [Header("Look")]
    [Tooltip("Color of balls of this type. Painting a ball this color turns it into this type (FunctionGrid.paintTypes)")]
    public Color color = Color.white;

    // How far this type's balls ever go below and above rest. Balls of a milder type next to a deep hole
    // or a high mound stop here, so a solid ball never rises with a neighboring mound or sinks with a pit.
    public float DeepestDepth => Mathf.Max(0f, restDepth, landingDepth);
    public float HighestRise => Mathf.Max(0f, -Mathf.Min(restDepth, landingDepth));
    public float ClampDepth(float depth) => Mathf.Clamp(depth, -HighestRise, DeepestDepth);

    // 1 right under the body, falling to 0 at the hole's edge
    public float Falloff(float dx, float dz)
    {
        float radius = holeWidth * 0.5f;
        float k = (dx * dx + dz * dz) / (radius * radius);
        return k >= 1f ? 0f : 1f - k;
    }
}
