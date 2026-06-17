using UnityEngine;
using TMPro;

public class ListenText : MonoBehaviour
{
    private TextMeshProUGUI textMesh;

    void Start()
    {
        textMesh = GetComponent<TextMeshProUGUI>();
        Hide();
    }

    public void Show(string responseMapping)
    {
        textMesh.text =  "Listen\n\n<size=50%>" + responseMapping + "</size>";
        gameObject.SetActive(true);
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }
}