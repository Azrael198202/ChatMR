import 'dotenv/config';
import express from 'express';
import cors from 'cors';
import helmet from 'helmet';
import rateLimit from 'express-rate-limit';
import multer from 'multer';

const app = express();
const PORT = process.env.PORT || 8787;
const OPENAI_KEY = process.env.OPENAI_API_KEY;
if (!OPENAI_KEY) throw new Error('OPENAI_API_KEY missing');

app.use(helmet());
app.use(express.json({ limit: '2mb' }));

// CORS 白名单
const allow = (process.env.ALLOWED_ORIGINS || '').split(',').map(s => s.trim()).filter(Boolean);
app.use(cors({
  origin: (origin, cb) => {
    if (!origin || allow.length === 0 || allow.includes(origin)) return cb(null, true);
    return cb(new Error(`Origin not allowed: ${origin}`));
  },
  credentials: false
}));

// 简单限流
app.use(rateLimit({ windowMs: 60_000, max: 120 }));

// —— 工具函数 ——
async function openai(path, body, extraHeaders = {}) {
  const r = await fetch(`https://api.openai.com${path}`, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${OPENAI_KEY}`,
      'Content-Type': 'application/json',
      ...extraHeaders
    },
    body: JSON.stringify(body)
  });
  if (!r.ok) {
    const errText = await r.text();
    throw new Error(`OpenAI ${path} ${r.status}: ${errText}`);
  }
  return r;
}

// ========== 1) 文本对话（Chat Completions） ==========
// 请求体：{ messages: [{role, content}], model? }
app.post('/chat', async (req, res) => {
  try {
    const { messages, model } = req.body || {};
    if (!Array.isArray(messages)) return res.status(400).json({ error: 'messages[] required' });

    const r = await openai('/v1/chat/completions', {
      model: model || process.env.DEFAULT_MODEL || 'gpt-4o-mini',
      messages,
      temperature: 0.6
    });
    const data = await r.json();

    // 提取首条文本输出
    const text = data?.choices?.[0]?.message?.content ?? '';
    res.json({ text, raw: data });
  } catch (e) {
    res.status(500).json({ error: String(e.message || e) });
  }
});

// ========== 2) Realtime 临时会话密钥（WebRTC/WS） ==========
app.get('/session', async (req, res) => {
  try {
    const r = await openai('/v1/realtime/sessions', {
      model: process.env.REALTIME_MODEL || 'gpt-4o-realtime-preview',
      // 可选：初始语音、工具开关、系统指令等
      // voice: 'verse',
      // instructions: 'You are an MR in-app assistant ...'
    });
    const data = await r.json();
    // data.client_secret.value 即为 1 分钟有效的临时 key（交给 Unity 客户端）
    res.json(data);
  } catch (e) {
    res.status(500).json({ error: String(e.message || e) });
  }
});

// ========== 3) TTS：文字转语音（MP3） ==========
app.post('/tts', async (req, res) => {
  try {
    const { text, voice = 'verse', format = 'mp3' } = req.body || {};
    if (!text) return res.status(400).json({ error: 'text required' });

    const r = await openai('/v1/audio/speech', {
      model: process.env.TTS_MODEL || 'gpt-4o-mini-tts',
      input: text,
      voice,
      format
    });
    const arrayBuf = await r.arrayBuffer();
    const buf = Buffer.from(arrayBuf);
    res.setHeader('Content-Type', 'audio/mpeg');
    res.send(buf);
  } catch (e) {
    res.status(500).json({ error: String(e.message || e) });
  }
});

// ========== 4) STT：语音转文字（multipart 上传） ==========
const upload = multer({ limits: { fileSize: 20 * 1024 * 1024 } }); // ≤20MB
app.post('/stt', upload.single('audio'), async (req, res) => {
  try {
    if (!req.file) return res.status(400).json({ error: 'audio file required (field name: audio)' });

    // multipart+form-data：这里用原始 fetch + FormData
    const form = new FormData();
    form.append('model', process.env.STT_MODEL || 'gpt-4o-transcribe');
    form.append('file', new Blob([req.file.buffer], { type: req.file.mimetype }), req.file.originalname);

    const r = await fetch('https://api.openai.com/v1/audio/transcriptions', {
      method: 'POST',
      headers: { 'Authorization': `Bearer ${OPENAI_KEY}` },
      body: form
    });
    if (!r.ok) {
      const t = await r.text();
      throw new Error(`OpenAI STT ${r.status}: ${t}`);
    }
    const data = await r.json();
    res.json(data); // { text: "...", ... }
  } catch (e) {
    res.status(500).json({ error: String(e.message || e) });
  }
});

// —— 健康检查 ——
app.get('/health', (_, res) => res.json({ ok: true }));

app.listen(PORT, () => console.log(`MR backend running on :${PORT}`));
