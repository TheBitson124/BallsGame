using UnityEngine;


[CreateAssetMenu(fileName = "SphereTypeScriptableObject", menuName = "SphereTypeScriptableObject", order = 0)]
public class SphereTypeScriptableObject : ScriptableObject {
   [Header("Spring")]
   [Tooltip("Spring strength per sphere. Weight-only sink = load * g / stiffness; higher = more rigid")]
   [Min(0.01f)] public float stiffness;
   [Tooltip("How far a sphere can be pushed in. 0 = rigid ground that never moves")]
   [Min(0f)] public float maxSinkDepth;
   [Tooltip("How much of a sphere's dip its neighbors follow, ring by ring. 0.5 = next ring sinks about half as deep, the one after a quarter. 0 = spheres move on their own")]
   [Range(0f, 0.9f)] public float neighborFalloff = 0.5f;

   [Header("Physics (SpringSphere)")]
   [Tooltip("Slows the spring. Low = bouncy. Rise time after being pushed in is about damping / stiffness seconds")]
   [Min(0f)] public float damping;
   [Tooltip("Mass of the moving part of one sphere. Lower = less of the landing speed lost on impact, so bouncier")]
   [Min(0.01f)] public float sphereMass;

   [Header("Bounce (SpringSphere)")]
   [Tooltip("Share of the landing speed you leave with, at least. 0 = plain spring, 1 = same height every bounce, above 1 = higher each bounce")]
   [Min(0f)] public float bounceRestitution;
   [Tooltip("Landings slower than this (m/s, into the surface) only get the plain spring, so walking on it doesn't launch you")]
   [Min(0f)] public float bounceMinLandingSpeed = 2f;

   [Header("Rebound (GroundGridManager only)")]
   [Tooltip("Rebound speeds (m/s) below this don't launch the player; the sphere settles instead")]
   [Min(0f)] public float minReboundSpeed;
   [Tooltip("Seconds to rise from the deepest point back to the weight-only depth")]
   [Min(0.01f)] public float settleTime;
   [Tooltip("How fast spheres return to rest once the player leaves or is launched, in units/sec")]
   [Min(0.01f)] public float releaseSpeed;

   [Tooltip("Friction. Keep bounciness 0 for SpringSphere: the spring does the bouncing")]
    public PhysicMaterial material;
}
