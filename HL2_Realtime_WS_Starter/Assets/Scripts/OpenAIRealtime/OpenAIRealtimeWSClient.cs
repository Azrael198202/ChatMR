// OpenAIRealtimeWSClient.cs  (fixed)
// - UNITY_WSA: 用 MessageWebSocket 真连接 Realtime（HL2）
// - 非 UNITY_WSA: Editor stub（可挂组件、调 Inspector；不连网）
// - 新增：通过正则从 /session 响应里解析临时密钥；文本/音频解析都用正则；所有 JSON 字符串已正确转义

using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

#if UNITY_WSA
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
#endif

#if TMP_PRESENT
using TMPro;
#endif

[Serializable] class SessionResp { public ClientSecret client_secret; }
[Serializable] class ClientSecret { public string value; }

public class OpenAIRealtimeWSClient : MonoBehaviour
{
    [Header("Backend base (hosts /session)")]
    public string serverBase = "http://192.168.0.233:8787";

    [Header("OpenAI Realtime model")]
    public string realtimeModel = "gpt-4o-realtime-preview";

    [Header("Mic")]
    public int sampleRate = 16000;
    public string micDevice = null;
    public KeyCode pushToTalk = KeyCode.Space;

    [Header("UI (optional)")]
#if TMP_PRESENT
    public TMP_Text transcriptTarget;     // TextMeshPro
#else
    public UnityEngine.UI.Text transcriptTargetLegacy; // 如果你用的是UGUI Text
#endif

    private AudioSource speaker;
    private AudioClip micClip;
    private bool isTalking;

#if UNITY_WSA
    private MessageWebSocket ws;
    private DataWriter writer;
#endif

    void Awake()
    {
        speaker = GetComponent<AudioSource>();
        if (!speaker) speaker = gameObject.AddComponent<AudioSource>();
        speaker.playOnAwake = false;
    }

    void Update()
    {
        if (Input.GetKeyDown(pushToTalk)) StartTalk();
        if (Input.GetKeyUp(pushToTalk))   StopTalk();
    }

    // ---- 从你的后端获取 Realtime 临时密钥（ephemeral key） ----
    public async System.Threading.Tasks.Task<string> GetEphemeralKey()
    {
        using var req = UnityWebRequest.Get($"{serverBase}/session");
#if UNITY_2020_1_OR_NEWER
        await req.SendWebRequest();
#else
        var op = req.SendWebRequest(); while (!op.isDone) await System.Threading.Tasks.Task.Yield();
#endif
        if (req.result != UnityWebRequest.Result.Success)
            throw new Exception(req.error);

        string json = req.downloadHandler.text;
        // 兼容不同返回结构：匹配 "value":"<TOKEN>"
        var m = Regex.Match(json, "\"value\"\\s*:\\s*\"([^\"]+)\"");
        if (!m.Success) throw new Exception("No ephemeral key in /session response");
        return m.Groups[1].Value;
    }

#if UNITY_WSA
    // ---------------- HL2（UWP）真实 WebSocket ----------------
    public async System.Threading.Tasks.Task ConnectWS()
    {
        if (ws != null) return;
        var key = await GetEphemeralKey();
        var url = new Uri($"wss://api.openai.com/v1/realtime?model={realtimeModel}");

        ws = new MessageWebSocket();
        ws.Control.MessageType = SocketMessageType.Utf8;
        ws.SetRequestHeader("Authorization", $"Bearer {key}");
        ws.MessageReceived += OnMessage;
        await ws.ConnectAsync(url);
        writer = new DataWriter(ws.OutputStream);
        Debug.Log("[RT] Connected");
    }

    private void OnMessage(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
    {
        using var rd = args.GetDataReader();
        rd.UnicodeEncoding = UnicodeEncoding.Utf8;
        string msg = rd.ReadString(rd.UnconsumedBufferLength);
        HandleServerEvent(msg);
    }

    private async System.Threading.Tasks.Task SendJson(string json)
    {
        writer.WriteString(json);
        await writer.StoreAsync();
        await writer.FlushAsync();
    }
#else
    // ---------------- Editor/非UWP：Stub，仅用于能编译和加组件 ----------------
    public System.Threading.Tasks.Task ConnectWS() { Debug.Log("[RT] Editor stub (no WS). Build to HL2."); return System.Threading.Tasks.Task.CompletedTask; }
    private System.Threading.Tasks.Task SendJson(string json) { Debug.Log("[RT=>] " + json); return System.Threading.Tasks.Task.CompletedTask; }
#endif

    // ---------------- 录音/发送 ----------------
    public async void StartTalk()
    {
        isTalking = true;
        if (micClip == null) micClip = Microphone.Start(micDevice, true, 10, sampleRate);
        else Microphone.Start(micDevice, true, 10, sampleRate);

        await ConnectWS();
        // 注意：JSON 字符串要正确转义
        await SendJson("{\"type\":\"session.update\",\"session\":{\"input_audio_format\":\"pcm16\"}}");
        StartCoroutine(StreamMic());
        AppendText("\n[You] ...");
    }

    public void StopTalk()
    {
        if (!isTalking) return;
        isTalking = false;
        Microphone.End(micDevice);
        _ = SendJson("{\"type\":\"input_audio_buffer.commit\"}");
        _ = SendJson("{\"type\":\"response.create\"}");
    }

    IEnumerator StreamMic()
    {
        var buf = new float[sampleRate / 10]; // 0.1s
        int lastPos = 0;
        while (isTalking)
        {
            yield return new WaitForSeconds(0.1f);
            if (!micClip) continue;

            int pos = Microphone.GetPosition(micDevice);
            int len = pos - lastPos; if (len < 0) len += micClip.samples;
            if (len <= 0 || len > buf.Length) continue;

            micClip.GetData(buf, lastPos);
            lastPos = pos;

            byte[] pcm = FloatsToPCM16(buf, len);
            string b64 = Convert.ToBase64String(pcm);
            string json = $"{{\"type\":\"input_audio_buffer.append\",\"audio\":\"{b64}\"}}";
            _ = SendJson(json);
        }
    }

    // ---------------- 处理服务端事件（文本+音频，朴素解析） ----------------
    private void HandleServerEvent(string json)
    {
        // 1) 文本：匹配所有 "text":"..."
        foreach (Match m in Regex.Matches(json, "\"text\"\\s*:\\s*\"([^\"]*)\""))
        {
            string t = m.Groups[1].Value.Replace("\\n", "\n").Replace("\\\"", "\"");
            AppendText(t);
        }

        // 2) 音频：匹配所有 "audio":"<base64>"
        foreach (Match m in Regex.Matches(json, "\"audio\"\\s*:\\s*\"([^\"]+)\""))
        {
            try
            {
                byte[] pcm = Convert.FromBase64String(m.Groups[1].Value);
                float[] f = PCM16ToFloats(pcm);
                var clip = AudioClip.Create("rt", f.Length, 1, sampleRate, false);
                clip.SetData(f, 0);
                speaker.PlayOneShot(clip);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RT] audio decode failed: " + e.Message);
            }
        }
    }

    private void AppendText(string t)
    {
#if TMP_PRESENT
        if (transcriptTarget) transcriptTarget.text += t;
#else
        if (transcriptTargetLegacy) transcriptTargetLegacy.text += t;
#endif
        Debug.Log("[RT txt] " + t);
    }

    // ---------------- 工具：PCM 与 float 转换 ----------------
    private static byte[] FloatsToPCM16(float[] f, int count)
    {
        var bytes = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            short s = (short)Mathf.Clamp(Mathf.RoundToInt(f[i] * 32767f), -32768, 32767);
            bytes[2 * i]     = (byte)(s & 0xff);
            bytes[2 * i + 1] = (byte)((s >> 8) & 0xff);
        }
        return bytes;
    }
    private static float[] PCM16ToFloats(byte[] pcm)
    {
        int n = pcm.Length / 2;
        var f = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
            f[i] = Mathf.Clamp(s / 32767f, -1f, 1f);
        }
        return f;
    }

    void OnDestroy()
    {
#if UNITY_WSA
        try { writer?.Dispose(); ws?.Dispose(); } catch {}
#endif
    }
}
