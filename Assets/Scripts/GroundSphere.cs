using UnityEngine;

[RequireComponent(typeof(SphereCollider))]
public class GroundSphere : MonoBehaviour
{
    public SphereTypeScriptableObject sphereType;
    public Vector3 basePos;
    private float depth;
    private SphereCollider sphereCollider;

    void Awake()
    {
        basePos = transform.localPosition;
        ApplyType();
    }

    public void SetType(SphereTypeScriptableObject type)
    {
        sphereType = type;
        ApplyType();
    }

    private void ApplyType()
    {
        // Awake doesn't run for objects spawned in edit mode, so fetch lazily
        if (!sphereCollider) sphereCollider = GetComponent<SphereCollider>();
        sphereCollider.sharedMaterial = sphereType.material;
    }

    // Follows a deeper target immediately; rises toward a shallower one at riseSpeed (units/sec).
    // Returns true once fully back at rest.
    public bool MoveToDepth(float targetDepth, float riseSpeed)
    {
        depth = targetDepth >= depth
            ? targetDepth
            : Mathf.MoveTowards(depth, targetDepth, riseSpeed * Time.deltaTime);

        transform.localPosition = new Vector3(basePos.x, basePos.y - depth, basePos.z);
        return depth <= 0f;
    }
}
