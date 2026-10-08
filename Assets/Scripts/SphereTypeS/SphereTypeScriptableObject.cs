using UnityEngine;


[CreateAssetMenu(fileName = "SphereTypeScriptableObject", menuName = "SphereTypeScriptableObject", order = 0)]
public class SphereTypeScriptableObject : ScriptableObject {
   [Header("Spring")]
   [Tooltip("Spring strength per sphere. Weight-only sink = load * g / stiffness; higher = more rigid")]
   [Min(0.01f)] public float stiffness;
   [Tooltip("How far a sphere can be pushed in, in sphere diameters (GridSpawner.sphereSize). 0 = rigid ground that never moves")]
   [Min(0f)] public float maxSinkDepth;
   [Tooltip("Max height difference between neighboring spheres, in sphere diameters. Push one further and it pulls its neighbors along, leaving a cone-shaped pit. Too large and the next sphere is a wall the player can't walk out over. 0 = spheres move on their own")]
   [Min(0f)] public float maxNeighborOffset = 0.3f;
   [Tooltip("How hard a sphere past maxNeighborOffset pulls its neighbors. 1 = hard limit, the pull reaches as far as the pit needs. Lower = the pull fades ring by ring so fewer spheres get dragged, but the step next to the pushed sphere can grow past maxNeighborOffset")]
   [Range(0.01f, 1f)] public float neighborPull = 0.5f;

   [Header("Physics (SpringSphere)")]
   [Tooltip("Slows the spring. Low = bouncy. Rise time after being pushed in is about damping / stiffness seconds")]
   [Min(0f)] public float damping;
   [Tooltip("Scales how fast spheres sink and rise. 2 = twice as fast, 0.5 = half. Works by dividing damping, so it also speeds up the settle after a bounce")]
   [Min(0.01f)] public float sinkSpeedMultiplier = 1f;
   [Tooltip("Mass of the moving part of one sphere. Lower = less of the landing speed lost on impact, so bouncier")]
   [Min(0.01f)] public float sphereMass;

   [Header("Bounce (SpringSphere)")]
   [Tooltip("Share of the landing speed you leave with, at least. 0 = plain spring, 1 = same height every bounce, above 1 = higher each bounce")]
   [Min(0f)] public float bounceRestitution;
   [Tooltip("Landings slower than this (m/s, into the surface) only get the plain spring, so walking on it doesn't launch you")]
   [Min(0f)] public float bounceMinLandingSpeed = 2f;

   [Header("Side push (SpringSphere)")]
   [Tooltip("Share of a sideways shove (walking into the side of a raised sphere) that pushes it in instead, so you can wade from a pit into its neighbors. 0 = the side is a wall")]
   [Min(0f)] public float sidePushSink = 0f;

   [Tooltip("Friction. Keep bounciness 0 for SpringSphere: the spring does the bouncing")]
    public PhysicMaterial material;

   [Header("Visuals")]
   [Tooltip("Look of the sphere only, no effect on physics. Empty = keep the prefab's material")]
   public Material visualMaterial;
}
