// Assets/Scripts/OpenAIRealtime/PushToTalkButton.cs
// Optional: attach to a UI Button to call StartTalk/StopTalk on pointer events.
// Requires an EventSystem and a proper XR UI Input Module or MRTK input to receive hand-ray clicks on HL2.

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class PushToTalkButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public OpenAIRealtimeWSClient client;
    public Text label;

    public void OnPointerDown(PointerEventData eventData)
    {
        if (client) client.StartTalk();
        if (label) label.text = "Recording...";
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (client) client.StopTalk();
        if (label) label.text = "Hold to Talk";
    }
}