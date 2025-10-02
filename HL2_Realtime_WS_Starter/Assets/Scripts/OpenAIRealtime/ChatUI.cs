
// ChatUI.cs : Editor-friendly typed chat using /chat.
// Requires TextMeshPro. Define Scripting Symbol: TMP_PRESENT (Player Settings) after importing TMP.

#define TMP_PRESENT
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
public class ChatUI : MonoBehaviour
{
    public string serverBase = "http://127.0.0.1:8787";
    public TMP_InputField input;
    public TMP_Text output;

    [System.Serializable] class Msg { public string role; public string content; }
    [System.Serializable] class ChatReq { public Msg[] messages; }
    [System.Serializable] class ChatResp { public string text; }

    public void Send()
    {
        if (!input || string.IsNullOrEmpty(input.text)) return;
        StartCoroutine(CoSend(input.text));
    }

    IEnumerator CoSend(string user)
    {
        var reqObj = new ChatReq
        {
            messages = new[] {
            new Msg{ role="system", content="You are an MR assistant."},
            new Msg{ role="user", content=user}
        }
        };
        var json = JsonUtility.ToJson(reqObj);
        var req = new UnityWebRequest($"{serverBase}/chat", "POST");
        var body = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            if (output) output.text += $"\n[err] {req.error}";
            yield break;
        }

        string responseJson = req.downloadHandler.text;
        string onlyText = responseJson;
        try
        {
            var resp = JsonUtility.FromJson<ChatResp>(responseJson);
            if (resp != null && !string.IsNullOrEmpty(resp.text))
                onlyText = resp.text;
        }
        catch { /* 忽略解析失败，直接显示原文 */ }

        if (output) output.text +=$"\n[AI 助手] {onlyText}";

    }
}
