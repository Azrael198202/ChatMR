// SlantedBoard.cs
using UnityEngine;

[ExecuteAlways]
public class SlantedBoard : MonoBehaviour
{
    public Camera cam;
    public float distance = 2.2f;  // 离相机前方距离（米）
    public float left = 1.2f;      // 向左偏移（米）
    public float down = 0.3f;      // 向下偏移（米）
    public float yawDegrees = -20; // 左右内折
    public float pitchDegrees = 0;
    public float rollDegrees = 0;

    void LateUpdate()
    {
        if (!cam) cam = Camera.main;
        if (!cam) return;

        var pos = cam.transform.position
                + cam.transform.forward * distance
                - cam.transform.right   * left
                - cam.transform.up      * down;
        transform.position = pos;

        var look = Quaternion.LookRotation(transform.position - cam.transform.position, cam.transform.up);
        transform.rotation = look;
        transform.Rotate(Vector3.up, yawDegrees, Space.Self);
        transform.Rotate(Vector3.right, pitchDegrees, Space.Self);
        transform.Rotate(Vector3.forward, rollDegrees, Space.Self);
    }
}
