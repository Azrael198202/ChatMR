using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public class ChatUIVoice : MonoBehaviour
{
    [Header("Backend base (hosts /chat , /stt , /tts)")]
    public string serverBase = "http://127.0.0.1:8787";

    [Header("UI Refs")]
    public TMP_InputField input;
    public TMP_Text output;
    public ScrollRect scrollRect;
    public TMP_Text hint;
    public AudioSource ttsPlayer;

    [Header("Push-To-Talk")]
    public KeyCode pushToTalkKey = KeyCode.LeftShift; // 按住 Shift 录音
    public int sampleRate = 16000;
    public int maxRecordSeconds = 60;

    [System.Serializable] class Msg { public string role; public string content; }
    [System.Serializable] class ChatReq { public Msg[] messages; }
    [System.Serializable] class ChatResp { public string text; }

    private bool sendRequested = false;
    private string sendBuffer = null;

    // 发送去重
    private bool sending = false;
    private static int _sendGuardFrame = -1;
    private static string _lastMsg = null;
    private static float _lastTime = 0f;

    // 录音
    private bool isRecording = false;
    private AudioClip recordClip;
    private string micDevice = null;

    void Awake()
    {
        if (input) input.lineType = TMP_InputField.LineType.MultiLineNewline; // Shift+Enter 换行
        if (!ttsPlayer) ttsPlayer = gameObject.GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
    }

    void OnEnable()
    {
        if (input != null)
        {
            input.onValidateInput += ValidateChar;
            Debug.Log($"[ChatUI] onValidateInput hooked. PTT={KeyName(pushToTalkKey)}");
        }
    }
    void OnDisable()
    {
        if (input != null) input.onValidateInput -= ValidateChar;
    }

    void Update()
    {
        // Shift 按下开始录音；松开停止转写（不自动发送）
        if (PTTDown()) StartRecord();
        if (PTTUp()) StopRecordAndTranscribe();

        // 回车发送的延迟执行
        if (sendRequested)
        {
            sendRequested = false;
            TrySend(sendBuffer ?? (input ? input.text : null));
        }
    }

    public void OnSendButton() => TrySend(input ? input.text : null);

    private void TrySend(string raw)
    {
        var msg = (raw ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(msg)) return;

        if (Time.frameCount == _sendGuardFrame) return;
        if (_lastMsg == msg && (Time.time - _lastTime) < 0.3f) return;

        _sendGuardFrame = Time.frameCount;
        _lastMsg = msg;
        _lastTime = Time.time;

        if (!sending) StartCoroutine(CoSend(msg));
    }

    // Enter=发送（无Shift），Shift+Enter=换行；录音时屏蔽空格
    private char ValidateChar(string text, int index, char c)
    {
        if (input == null) return c;
        if (!string.IsNullOrEmpty(Input.compositionString)) return c;
        if (isRecording && c == ' ') return '\0';

        if (c == '\n' || c == '\r')
        {
            bool withShift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!withShift)
            {
                sendBuffer = input.text;
                sendRequested = true;
                return '\0';
            }
        }
        return c;
    }

    // ==== Push-To-Talk ====
    private bool PTTDown()
    {
        if (pushToTalkKey == KeyCode.LeftShift || pushToTalkKey == KeyCode.RightShift)
            return Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift);
        return Input.GetKeyDown(pushToTalkKey);
    }
    private bool PTTUp()
    {
        if (pushToTalkKey == KeyCode.LeftShift || pushToTalkKey == KeyCode.RightShift)
            return Input.GetKeyUp(KeyCode.LeftShift) || Input.GetKeyUp(KeyCode.RightShift);
        return Input.GetKeyUp(pushToTalkKey);
    }
    private string KeyName(KeyCode kc) => (kc == KeyCode.LeftShift || kc == KeyCode.RightShift) ? "Shift" : kc.ToString();

    private void StartRecord()
    {
        if (isRecording) return;
        if (Microphone.devices == null || Microphone.devices.Length == 0)
        { AppendOutput("\n[err] No microphone devices."); return; }

        micDevice = null; // 默认设备
        recordClip = Microphone.Start(micDevice, false, Mathf.Clamp(maxRecordSeconds, 1, 300), sampleRate);
        isRecording = true;
        if (hint) hint.text = $"Listening... (hold {KeyName(pushToTalkKey)})";
    }

    private void StopRecordAndTranscribe()
    {
        if (!isRecording) return;
        int pos = Microphone.GetPosition(micDevice);
        Microphone.End(micDevice);
        isRecording = false;
        if (hint) hint.text = "";

        if (recordClip == null || pos <= 0) return;

        int channels = recordClip.channels;
        float[] data = new float[pos * channels];
        recordClip.GetData(data, 0);

        byte[] wav = EncodeToWav(data, channels, sampleRate);
        StartCoroutine(CoTranscribe(wav));
    }

    // 只转写 → 填入输入框（不自动发送）
    private IEnumerator CoTranscribe(byte[] wav)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("audio", wav, "speech.wav", "audio/wav");

        using (UnityWebRequest req = UnityWebRequest.Post($"{serverBase}/stt", form))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            { AppendOutput($"\n[err] STT: {req.error}"); yield break; }

            string text = req.downloadHandler.text;
            try { var resp = JsonUtility.FromJson<ChatResp>(text); if (!string.IsNullOrEmpty(resp?.text)) text = resp.text; } catch { }
            text = text.Trim();
            if (string.IsNullOrEmpty(text)) { AppendOutput("\n[err] STT returned empty text."); yield break; }
            if (input) { input.text = text; input.caretPosition = text.Length; input.ActivateInputField(); }
        }
    }

    // ==== /chat + /tts ====
    private IEnumerator CoSend(string user)
    {
        if (sending) yield break;
        sending = true;
        try
        {
            // 先把“我问了什么”显示出来
            AppendOutput($"\n[我] {user}");

            var reqObj = new ChatReq
            {
                messages = new[] {
                    new Msg{ role="system", content="You are an MR assistant." },
                    new Msg{ role="user",   content=user }
                }
            };
            var json = JsonUtility.ToJson(reqObj);

            var req = new UnityWebRequest($"{serverBase}/chat", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            { AppendOutput($"\n[err] {req.error}"); yield break; }

            var responseJson = req.downloadHandler.text;
            var onlyText = responseJson;
            try { var resp = JsonUtility.FromJson<ChatResp>(responseJson); if (!string.IsNullOrEmpty(resp?.text)) onlyText = resp.text; } catch { }

            AppendOutput($"\n[AI 助手] {onlyText}");

            // 清空输入
            if (input)
            {
                input.text = string.Empty;
                input.ActivateInputField();
                input.caretPosition = 0;
            }

            // 语音播报
            StartCoroutine(CoTTS(onlyText));
        }
        finally { sending = false; }
    }

    // /tts：优先 mp3，wav 保留兜底
    private IEnumerator CoTTS(string text)
    {
        var payload = "{\"text\":" + JsonEscape(text) + "}";
        var req = new UnityWebRequest($"{serverBase}/tts", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        yield return req.SendWebRequest();

        string ct = req.GetResponseHeader("Content-Type") ?? "";
        byte[] data = req.downloadHandler.data;

        if (req.result != UnityWebRequest.Result.Success)
        {
            AppendOutput($"\n[err] TTS: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
            yield break;
        }
        if (data == null || data.Length < 8)
        {
            AppendOutput("\n[err] TTS: empty audio");
            yield break;
        }

        // ---------- 嗅探真实格式 ----------
        // WAV: "RIFF....WAVE"
        bool looksWav = data.Length >= 12 &&
                        data[0] == (byte)'R' && data[1] == (byte)'I' &&
                        data[2] == (byte)'F' && data[3] == (byte)'F' &&
                        data[8] == (byte)'W' && data[9] == (byte)'A' &&
                        data[10] == (byte)'V' && data[11] == (byte)'E';

        // MP3: 以 "ID3" 开头，或帧同步 0xFFEx（高 11 位全 1）
        bool looksId3 = data.Length >= 3 &&
                        data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3';
        bool looksMp3Frame = (data[0] == 0xFF) && ((data[1] & 0xE0) == 0xE0);
        bool looksMp3 = looksId3 || looksMp3Frame;

        AudioClip clip = null;

        if (looksMp3 || ct.Contains("mpeg") || ct.Contains("mp3"))
        {
            // —— MP3 解码（最稳）——
            var path = System.IO.Path.Combine(Application.temporaryCachePath, "tts_tmp.mp3");
            System.IO.File.WriteAllBytes(path, data);
            using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success)
                    clip = DownloadHandlerAudioClip.GetContent(www);
                else
                    AppendOutput($"\n[err] TTS mp3 decode: {www.responseCode} {www.error}");
            }
        }
        else if (looksWav || ct.Contains("wav"))
        {
            // —— WAV: 先自解析，失败再用 Unity 兜底 —— 
            clip = WavToClip(data);
            if (clip == null)
            {
                var path = System.IO.Path.Combine(Application.temporaryCachePath, "tts_tmp.wav");
                System.IO.File.WriteAllBytes(path, data);
                using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
                {
                    yield return www.SendWebRequest();
                    if (www.result == UnityWebRequest.Result.Success)
                        clip = DownloadHandlerAudioClip.GetContent(www);
                    else
                        AppendOutput($"\n[err] TTS wav decode: {www.responseCode} {www.error}");
                }
            }
        }
        else
        {
            // 未知/错标格式，优先按 MP3 再按 WAV 各试一次（尽量把声音放出来）
            var p1 = System.IO.Path.Combine(Application.temporaryCachePath, "tts_try.mp3");
            System.IO.File.WriteAllBytes(p1, data);
            using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + p1, AudioType.MPEG))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success)
                    clip = DownloadHandlerAudioClip.GetContent(www);
            }
            if (clip == null)
            {
                var p2 = System.IO.Path.Combine(Application.temporaryCachePath, "tts_try.wav");
                System.IO.File.WriteAllBytes(p2, data);
                using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + p2, AudioType.WAV))
                {
                    yield return www.SendWebRequest();
                    if (www.result == UnityWebRequest.Result.Success)
                        clip = DownloadHandlerAudioClip.GetContent(www);
                }
            }
            if (clip == null)
                AppendOutput($"\n[err] TTS: unknown audio format. ctype={ct}, bytes={data.Length}");
        }

        if (clip == null) yield break;

        if (!ttsPlayer) ttsPlayer = gameObject.GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
        ttsPlayer.clip = clip;
        ttsPlayer.Play();
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

    // ===== WAV 编解码（保持你现有的增强版即可） =====
    private static byte[] EncodeToWav(float[] samples, int channels, int sampleRate)
    {
        int sampleCount = samples.Length;
        int byteCount = sampleCount * 2;
        int headerSize = 44;
        byte[] wav = new byte[headerSize + byteCount];

        System.Buffer.BlockCopy(Encoding.ASCII.GetBytes("RIFF"), 0, wav, 0, 4);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(headerSize + byteCount - 8), 0, wav, 4, 4);
        System.Buffer.BlockCopy(Encoding.ASCII.GetBytes("WAVE"), 0, wav, 8, 4);
        System.Buffer.BlockCopy(Encoding.ASCII.GetBytes("fmt "), 0, wav, 12, 4);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(16), 0, wav, 16, 4);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes((short)1), 0, wav, 20, 2);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes((short)channels), 0, wav, 22, 2);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(sampleRate), 0, wav, 24, 4);
        int byteRate = sampleRate * channels * 2;
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(byteRate), 0, wav, 28, 4);
        short blockAlign = (short)(channels * 2);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(blockAlign), 0, wav, 32, 2);
        short bitsPerSample = 16;
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(bitsPerSample), 0, wav, 34, 2);
        System.Buffer.BlockCopy(Encoding.ASCII.GetBytes("data"), 0, wav, 36, 4);
        System.Buffer.BlockCopy(System.BitConverter.GetBytes(byteCount), 0, wav, 40, 4);

        int offset = headerSize;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), short.MinValue, short.MaxValue);
            wav[offset++] = (byte)(s & 0xff);
            wav[offset++] = (byte)((s >> 8) & 0xff);
        }
        return wav;
    }

    // （保留你之前增强版的 WavToClip，或使用我们上一条给你的 Extensible+padding 版本）
    private static AudioClip WavToClip(byte[] wav)
    {
        if (wav == null || wav.Length < 44) return null;

        // 先验
        string riff = Encoding.ASCII.GetString(wav, 0, 4);
        string wave = Encoding.ASCII.GetString(wav, 8, 4);
        if (riff != "RIFF" || wave != "WAVE") return null;

        int pos = 12; // 跳过 RIFF/WAVE
        int channels = 1, sampleRate = 16000, bits = 16;
        int dataPos = -1, dataSize = -1;
        int audioFormat = 1;          // 1=PCM, 3=IEEE Float, 0xFFFE=Extensible
        bool isFloatByExtensible = false;

        // 扫描所有 chunk（注意偶数字节对齐：chunkSize 为奇数时需要 +1）
        while (pos + 8 <= wav.Length)
        {
            string chunkId = Encoding.ASCII.GetString(wav, pos, 4); pos += 4;
            int chunkSize = System.BitConverter.ToInt32(wav, pos); pos += 4;
            if (chunkSize < 0 || pos + chunkSize > wav.Length) return null;

            if (chunkId == "fmt ")
            {
                audioFormat = System.BitConverter.ToInt16(wav, pos + 0);
                channels = System.BitConverter.ToInt16(wav, pos + 2);
                sampleRate = System.BitConverter.ToInt32(wav, pos + 4);
                bits = System.BitConverter.ToInt16(wav, pos + 14);

                // 处理 Extensible：fmt chunk size >= 40，subFormat 在偏移 24（GUID）
                if (audioFormat == 0xFFFE && chunkSize >= 40)
                {
                    // subFormat GUID 前两个字节 = wFormatTag（PCM=0x0001，Float=0x0003）
                    int subFmt = System.BitConverter.ToInt16(wav, pos + 24);
                    if (subFmt == 0x0003) isFloatByExtensible = true;
                }
            }
            else if (chunkId == "data")
            {
                dataPos = pos;
                dataSize = chunkSize;
                break;
            }

            // pad 到偶数边界
            pos += chunkSize + (chunkSize & 1);
        }

        if (dataPos < 0 || dataSize <= 0) return null;

        int bytesPerSample = bits / 8;
        if (bytesPerSample <= 0) return null;

        int totalSamples = dataSize / bytesPerSample;
        float[] samples = new float[totalSamples];

        // 读取样本
        bool asFloat = (audioFormat == 3) || (audioFormat == 0xFFFE && isFloatByExtensible);

        if (asFloat) // IEEE Float32
        {
            if (bits != 32) return null;
            int si = 0;
            for (int i = 0; i < dataSize; i += 4)
            {
                float f = System.BitConverter.ToSingle(wav, dataPos + i);
                samples[si++] = Mathf.Clamp(f, -1f, 1f);
            }
        }
        else // 整型 PCM
        {
            if (bits == 16)
            {
                int si = 0;
                for (int i = 0; i < dataSize; i += 2)
                {
                    short s = (short)(wav[dataPos + i] | (wav[dataPos + i + 1] << 8));
                    samples[si++] = s / 32768f;
                }
            }
            else if (bits == 24)
            {
                int si = 0;
                for (int i = 0; i < dataSize; i += 3)
                {
                    int v = wav[dataPos + i] | (wav[dataPos + i + 1] << 8) | (wav[dataPos + i + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    samples[si++] = Mathf.Clamp(v / 8388608f, -1f, 1f);
                }
            }
            else if (bits == 32)
            {
                // 少见的 32-bit 整型 PCM（不是 float）
                int si = 0;
                for (int i = 0; i < dataSize; i += 4)
                {
                    int v = System.BitConverter.ToInt32(wav, dataPos + i);
                    samples[si++] = Mathf.Clamp(v / 2147483648f, -1f, 1f);
                }
            }
            else return null;
        }

        int frameCount = totalSamples / channels;
        if (frameCount <= 0) return null;

        var clip = AudioClip.Create("tts", frameCount, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static string JsonEscape(string s)
    {
        s = s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        s = s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        return "\"" + s + "\"";
    }
}

