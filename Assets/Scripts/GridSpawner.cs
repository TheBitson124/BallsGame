using UnityEngine;

public class GridSpawner : MonoBehaviour
{
    public GameObject spherePrefab;
    public int rows = 40;
    public int columns = 40;
    public float spacing = 0.5f;

    public SphereTypeScriptableObject sphereType;
    [ContextMenu("Generate Grid")]
    public void GenerateGrid()
    {
        ClearGrid();

        Vector3 offset = new Vector3((columns - 1) * spacing * 0.5f, 0, (rows - 1) * spacing * 0.5f);

        for (int x = 0; x < columns; x++)
        {
            for (int z = 0; z < rows; z++)
            {
                Vector3 spawnPos = new Vector3(x * spacing, 0, z * spacing) - offset;
                GameObject obj = Instantiate(spherePrefab, transform);
                obj.transform.localPosition = spawnPos;
                obj.name = $"Sphere_{x}_{z}";
                obj.GetComponent<GroundSphere>().SetType(sphereType);
            }
        }
    }

    [ContextMenu("Clear Grid")]
    public void ClearGrid()
    {
        while (transform.childCount > 0)
        {
            DestroyImmediate(transform.GetChild(0).gameObject);
        }
    }
}