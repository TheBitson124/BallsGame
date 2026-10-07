using UnityEngine;

[RequireComponent(typeof(SphereCollider))]
public class GroundSphere : MonoBehaviour
{
    public SphereTypeScriptableObject sphereType;
    public Vector3 basePos;
    private float depth;
    private Vector3 pushDir;
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

    // Pushed along pushDir (world space, into the surface). Follows a deeper target immediately;
    // returns toward a shallower one at riseSpeed (units/sec). Returns true once fully back at rest.
    public bool MoveToDepth(float targetDepth, Vector3 pushDir, float riseSpeed)
    {
        if (targetDepth >= depth)
        {
            depth = targetDepth;
            this.pushDir = pushDir;
        }
        else
        {
            depth = Mathf.MoveTowards(depth, targetDepth, riseSpeed * Time.deltaTime);
        }

        Vector3 offset = this.pushDir * depth;
        if (transform.parent) offset = transform.parent.InverseTransformVector(offset);
        transform.localPosition = basePos + offset;
        return depth <= 0f;
    }
}
