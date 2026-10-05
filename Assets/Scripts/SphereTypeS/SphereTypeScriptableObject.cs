using UnityEngine;


[CreateAssetMenu(fileName = "SphereTypeScriptableObject", menuName = "SphereTypeScriptableObject", order = 0)]
public class SphereTypeScriptableObject : ScriptableObject {
   [Header("Spring")]
   [Tooltip("Spring strength. Weight-only sink = mass * g / stiffness; higher = more rigid")]
   [Min(0.01f)] public float stiffness;
   [Min(0f)] public float maxSinkDepth;

   [Header("Rebound")]
   [Tooltip("Rebound speeds (m/s) below this don't launch the player; the sphere settles instead")]
   [Min(0f)] public float minReboundSpeed;
   [Tooltip("Seconds to rise from the deepest point back to the weight-only depth")]
   [Min(0.01f)] public float settleTime;
   [Tooltip("How fast spheres return to rest once the player leaves or is launched, in units/sec")]
   [Min(0.01f)] public float releaseSpeed;

   [Tooltip("bounciness = share of the landing speed given back to the player")]
    public PhysicMaterial material;
}
