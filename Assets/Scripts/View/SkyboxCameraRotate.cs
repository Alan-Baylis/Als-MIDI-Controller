using UnityEngine;

public class SkyboxCameraRotate : MonoBehaviour
{
    [Tooltip("Rotation speed in degrees per second")]
    public float rotationSpeed = 1f;

    void Update()
    {
        // Rotate around Y-axis (up) continuously
        transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime / 20);
    }
    
    void LateUpdate()
    {
//        transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime);
    }
}
