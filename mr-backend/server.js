import 'dotenv/config';
import express from 'express';
import cors from 'cors';
import helmet from 'helmet';
import rateLimit from 'express-rate-limit';
import multer from 'multer';

import speech from '@google-cloud/speech';

const speechClient = new speech.SpeechClient();

const app = express();
const PORT = process.env.PORT || 8787;
const OPENAI_KEY = process.env.OPENAI_API_KEY;
const STT_MODEL = 'whisper-1' || 'gpt-4o-transcribe'; // 或 gpt-4o-transcribe，按你实际
const TTS_MODEL = process.env.TTS_MODEL || 'gpt-4o-mini-tts'; // 或 tts-1 / tts-1-hd，按你实际
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
    const { text, voice = 'alloy' } = req.body || {};
    if (!text) return res.status(400).json({ error: 'text required' });

    const model = process.env.TTS_MODEL || 'gpt-4o-mini-tts';
    const accept = 'audio/mpeg';                      // ← 统一回 MP3
    const r = await fetch('https://api.openai.com/v1/audio/speech', {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${OPENAI_KEY}`,
        'Content-Type': 'application/json',
        'Accept': accept,
      },
      body: JSON.stringify({ model, input: text, voice, format: 'mp3' }),
    });

    const ab = await r.arrayBuffer();
    const buf = Buffer.from(ab);

    console.log('[TTS] upstream status:', r.status, 'len:', buf.length, 'ctype:', r.headers.get('content-type'));

    if (!r.ok) {
      // 把上游错误原样抛给前端，便于排查
      return res.status(r.status).type('text/plain').send(buf);
    }
    res.setHeader('Content-Type', accept);
    res.send(buf);
  } catch (e) {
    console.error('[TTS] error', e);
    res.status(500).json({ error: String(e.message || e) });
  }
});

// ========== 4) STT：语音转文字（multipart 上传） ==========
const upload = multer({
  storage: multer.memoryStorage(),           // ← 用内存存储，才能读 req.file.buffer
  limits: { fileSize: 20 * 1024 * 1024 }
});

app.post('/stt', upload.any(), async (req, res) => {
  try {
    const f = (req.files || []).find(x =>
      x.fieldname === 'audio' || x.fieldname === 'file' || x.fieldname === 'audio_file'
    );
    if (!f) return res.status(400).json({ error: 'audio file required (field name: audio or file)' });

    const form = new FormData();
    form.append('model', 'gpt-4o-transcribe'); // 或 gpt-4o-transcribe，按你实际
    form.append('file', new Blob([f.buffer], { type: f.mimetype }), f.originalname);
    //form.append('language', 'auto');

    const r = await fetch('https://api.openai.com/v1/audio/transcriptions', {
      method: 'POST',
      headers: { Authorization: `Bearer ${OPENAI_KEY}` },
      body: form
    });
    if (!r.ok)
      return res.status(r.status).send(await r.text());

    const data = await r.json(); // { text: "..." }
    res.json(data);
  } catch (e) {
    res.status(500).json({ error: String(e.message || e) });
  }
});

// app.post('/stt', upload.any(), async (req, res) => {
//   try {
//     const f = (req.files || []).find(x =>
//       x.fieldname === 'audio' || x.fieldname === 'file' || x.fieldname === 'audio_file'
//     );
//     if (!f) return res.status(400).json({ error: 'audio file required' });

//     const audioBytes = f.buffer.toString('base64');

//     const request = {
//       audio: {
//         content: audioBytes,
//       },
//       config: {
//         encoding: 'WEBM_OPUS', // 根据实际格式调整
//         sampleRateHertz: 48000,
//         languageCode: 'zh-CN',
//         alternativeLanguageCodes: ['en-US', 'ja-JP'], // 支持中文、英文、日文
//         model: 'default',
//         enableAutomaticPunctuation: true,
//       },
//     };

//     const [response] = await speechClient.recognize(request);
//     const transcription = response.results
//       .map(result => result.alternatives[0].transcript)
//       .join('\n');

//     res.json({ text: transcription });
//   } catch (e) {
//     console.error('Google STT error:', e);
//     res.status(500).json({ error: String(e.message || e) });
//   }
// });

// —— 健康检查 ——
app.get('/health', (_, res) => res.json({ ok: true }));

app.listen(PORT, () => console.log(`MR backend running on :${PORT}`));
