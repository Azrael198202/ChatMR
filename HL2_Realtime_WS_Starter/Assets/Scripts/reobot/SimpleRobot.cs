// SimpleRobot.cs —— 让机器人挥手、抬头看看相机
using UnityEngine;

public class SimpleRobot : MonoBehaviour
{
    public Transform head, leftArm, rightArm;
    public float waveAmp = 25f;
    public float waveSpeed = 2f;

    void LateUpdate()
    {
        if (Camera.main && head)
        {
            var dir = Camera.main.transform.position - head.position;
            dir.y = Mathf.Max(0.1f, dir.y);     // 稍抬一点头
            head.rotation = Quaternion.Slerp(head.rotation,
                Quaternion.LookRotation(dir, Vector3.up), Time.deltaTime * 3f);
        }

        float a = Mathf.Sin(Time.time * waveSpeed) * waveAmp;
        if (leftArm)  leftArm.localRotation  = Quaternion.Euler(a, 0, 0);
        if (rightArm) rightArm.localRotation = Quaternion.Euler(-a * 0.6f, 0, 0);
    }
}
