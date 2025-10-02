# HoloLens 2 + Unity 6 (6000) Realtime (WebSocket) Starter

This starter shows how to connect **directly** from Unity to OpenAI **Realtime (WebSocket)** using an **ephemeral key** obtained from **your own backend** (`/session`). It implements a simple **push‑to‑talk** flow and plays back the model's streamed audio.

> ⚠️ This is a minimal MVP meant to run in **Editor** and on **HoloLens 2 (UWP/ARM64)**. It favors simplicity over perfect audio buffering. For production, replace the naive JSON parsing and chunked playback with a robust event/PCM pipeline.

## Prereqs
- Unity **6000.x** (Unity 6)
- Target: **UWP / ARM64 / IL2CPP**
- OpenXR plugin (HoloLens feature group)
- Your **backend** from previous step running at `http://<host>:8787` with the `/session` endpoint.

## How to use
1. **Copy these scripts** into your Unity project:
   - `Assets/Scripts/OpenAIRealtime/OpenAIRealtimeWSClient.cs`
   - `Assets/Scripts/OpenAIRealtime/PushToTalkButton.cs`
2. Create an **empty GameObject** in a new scene, add **OpenAIRealtimeWSClient**.
3. Set **Server Base** to your backend (e.g., `http://192.168.1.10:8787`). Keep the default model unless you must change.
4. Add an **AudioSource** or let the script add one automatically.
5. (Editor test) Press **Play** and hold **Space** to talk. Release to send and listen.
6. (Optional) Add a world‑space Canvas + Button, add **PushToTalkButton** and drag the client component to its field to use hand input (requires XR UI input support or MRTK).

## Player Settings for HL2
- **Build Settings → UWP**
  - Architecture: **ARM64**
  - Scripting Backend: **IL2CPP**
- **Publishing Settings → Capabilities**: enable **InternetClient**, **Microphone** (and **SpatialPerception** if using spatial features).
- **XR Plug‑in Management**: enable **OpenXR** and the **HoloLens** feature group.

## Notes
- The script uses **Text WebSocket frames** and expects Realtime events that contain base64 **PCM16** chunks under a field named `audio` (e.g., `response.output_audio.delta`). It scans the JSON for `"audio":"<base64>"` and plays the decoded chunks.
- Audio sample rate = **16 kHz mono**.
- For better UX, consider aggregating chunks into ~500–800 ms clips before playback.