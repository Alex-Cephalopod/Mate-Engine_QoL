using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(-1000)]
public class AISystemPromptBinder : MonoBehaviour
{
    [Header("References")]
    public InputField input;
    public LLMUnity.LLMCharacter target;

    [Header("Behavior")]
    public bool liveSave = true;

    void Reset()
    {
        if (!input) input = GetComponent<InputField>();
        if (!target) target = FindObjectOfType<LLMUnity.LLMCharacter>();
    }

    void Awake()
    {
        if (!input) input = GetComponent<InputField>();

        string path = GetFixedPromptPath();
        string txt = target ? target.prompt : "";

        try
        {
            if (File.Exists(path)) txt = File.ReadAllText(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, txt);
            }
        }
        catch (Exception e) { Debug.LogError("[AI Prompt] Read/Create failed: " + e); }

        // QoL: with characters loaded, this box shows and edits the active character's prompt
        bool qolCharacter = MateEngineQoL.Bridge.CharacterManager.TryGetActivePrompt(out string qolPrompt);
        if (qolCharacter) txt = qolPrompt;

        input.onValueChanged.RemoveListener(OnValueChanged);
        input.onEndEdit.RemoveListener(OnEndEdit);

        input.text = txt;
        if (!qolCharacter) ApplyToLLM(txt);

        input.onValueChanged.AddListener(OnValueChanged);
        input.onEndEdit.AddListener(OnEndEdit);
    }

    void OnDestroy()
    {
        if (input != null)
        {
            input.onValueChanged.RemoveListener(OnValueChanged);
            input.onEndEdit.RemoveListener(OnEndEdit);
        }
    }

    void OnValueChanged(string s)
    {
        if (liveSave) Save(s);
    }

    void OnEndEdit(string s)
    {
        if (!liveSave) Save(s);
    }

    void Save(string s)
    {
        if (MateEngineQoL.Bridge.CharacterManager.TrySetActivePrompt(s)) return; // QoL: saved to the active character, chat kept
        string path = GetFixedPromptPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, s);
        }
        catch (Exception e) { Debug.LogError("[AI Prompt] Write failed: " + e); }
        ApplyToLLM(s);
    }

    void ApplyToLLM(string s)
    {
        if (target != null) target.SetPrompt(s, true);
    }

    static string GetFixedPromptPath()
    {
        // QoL: was a hardcoded LocalLow\Shinymoon\MateEngineX path, which kept sharing the prompt with the
        // Steam install after the fork got its own data folder (and only worked on Windows).
        return Path.Combine(Application.persistentDataPath, "ZomeAI_prompt.txt");
    }
}
