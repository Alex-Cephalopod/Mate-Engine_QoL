namespace MateEngineQoL.Characters
{
    public enum ChatCommandKind
    {
        /// <summary>"/char &lt;name&gt;": switch character.</summary>
        SwitchCharacter,
        /// <summary>"/chars" or "/char" alone: list characters.</summary>
        ListCharacters,
        /// <summary>"/new": start a new session with the current character.</summary>
        NewSession,
    }

    /// <summary>
    /// Slash commands typed into the chat box. Only these exact command words are handled; anything else,
    /// including other text starting with "/", is sent to the model as usual.
    /// </summary>
    public sealed class ChatCommand
    {
        public ChatCommandKind Kind;
        public string Argument = "";

        public static bool TryParse(string text, out ChatCommand command)
        {
            command = null;
            text = (text ?? "").Trim();
            if (text.Length < 2 || text[0] != '/') return false;

            int space = text.IndexOfAny(new[] { ' ', '\t', '\n' });
            string word = (space < 0 ? text.Substring(1) : text.Substring(1, space - 1)).ToLowerInvariant();
            string arg = space < 0 ? "" : text.Substring(space + 1).Trim();

            switch (word)
            {
                case "char":
                case "character":
                    command = new ChatCommand
                    {
                        Kind = arg.Length > 0 ? ChatCommandKind.SwitchCharacter : ChatCommandKind.ListCharacters,
                        Argument = arg,
                    };
                    return true;
                case "chars":
                case "characters":
                    command = new ChatCommand { Kind = ChatCommandKind.ListCharacters };
                    return true;
                case "new":
                    command = new ChatCommand { Kind = ChatCommandKind.NewSession };
                    return true;
                default:
                    return false;
            }
        }
    }
}
