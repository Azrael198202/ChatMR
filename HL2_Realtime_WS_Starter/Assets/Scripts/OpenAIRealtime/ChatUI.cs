using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using UnityEngine.UI;
using Debug = UnityEngine.Debug; // 避免与 System.Diagnostics.Debug 冲突

public class ChatUI : MonoBehaviour
{
    [Header("Backend base (hosts /chat)")]
    public string serverBase = "http://127.0.0.1:8787";

    [Header("UI Refs")]
    public TMP_InputField input;
    public TMP_Text output;
    public ScrollRect scrollRect;

    [System.Serializable] class Msg { public string role; public string content; }
    [System.Serializable] class ChatReq { public Msg[] messages; }
    [System.Serializable] class ChatResp { public string text; }

    private bool sendRequested = false;
    private string sendBuffer = null;

    void Awake()
    {
        if (input)
            input.lineType = TMP_InputField.LineType.MultiLineNewline; // 允许 Shift+Enter 换行
    }

    void OnEnable()
    {
        if (input != null)
        {
            input.onValidateInput += ValidateChar; // 在字符写入前拦截回车
            Debug.Log("[ChatUI] onValidateInput hooked.");
        }
        else
        {
            Debug.LogError("[ChatUI] input ref is null. Drag TMP_InputField to ChatUI.");
        }
    }

    void OnDisable()
    {
        if (input != null)
            input.onValidateInput -= ValidateChar;
    }

    void Update()
    {
        // 在安全时机触发发送（避免在 onValidateInput 内直接起协程）
        if (sendRequested)
        {
            sendRequested = false;
            var msg = (sendBuffer ?? input.text).Trim();
            if (!string.IsNullOrEmpty(msg))
            {
                Debug.Log("[ChatUI] Send via Enter.");
                StartCoroutine(CoSend(msg));
            }
        }
    }

    // 供按钮 OnClick 绑定
    public void OnSendButton()
    {
        if (!input) return;
        var msg = input.text.Trim();
        if (!string.IsNullOrEmpty(msg))
        {
            Debug.Log("[ChatUI] Send via Button.");
            StartCoroutine(CoSend(msg));
        }
    }

    // 回车拦截：Enter=发送，Shift+Enter=换行；IME 组合中不触发
    private char ValidateChar(string text, int index, char c)
    {
        if (input == null) return c;

        // ✅ 使用全局的 IME 组合字符串判断是否在候选组合中
        if (!string.IsNullOrEmpty(Input.compositionString))
            return c;

        if (c == '\n' || c == '\r')
        {
            bool withShift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!withShift)
            {
                sendBuffer = input.text;  // 当前内容（不含这次换行）
                sendRequested = true;
                Debug.Log("[ChatUI] Enter intercepted -> send.");
                return '\0';              // 阻止把换行写进输入框
            }
            // Shift+Enter 保留换行
        }
        return c;
    }

    IEnumerator CoSend(string user)
    {
        var reqObj = new ChatReq
        {
            messages = new[]
            {
                new Msg { role="system", content="You are an MR assistant." },
                new Msg { role="user",   content=user }
            }
        };
        var json = JsonUtility.ToJson(reqObj);

        var req = new UnityWebRequest($"{serverBase}/chat", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            AppendOutput($"\n[err] {req.error}");
            yield break;
        }

        var responseJson = req.downloadHandler.text;
        var onlyText = responseJson;
        try
        {
            var resp = JsonUtility.FromJson<ChatResp>(responseJson);
            if (resp != null && !string.IsNullOrEmpty(resp.text))
                onlyText = resp.text;
        }
        catch { /* ignore parse error */ }

        AppendOutput($"\n[AI 助手] {onlyText}");

        // 清空并保留焦点
        if (input)
        {
            input.text = string.Empty;
            input.ActivateInputField();
            input.caretPosition = 0;
        }
    }

    private void AppendOutput(string s)
    {
        if (output) output.text += s + "\n";
        if (scrollRect)
        {
            Canvas.ForceUpdateCanvases();
            scrollRect.verticalNormalizedPosition = 0f;
            Canvas.ForceUpdateCanvases();
        }
    }
}
