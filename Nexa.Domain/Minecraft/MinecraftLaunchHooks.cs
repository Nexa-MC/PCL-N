using System.Text;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>Bounded command validation without token expansion or shell interpretation.</summary>
public static class MinecraftLaunchHooks
{
    public const int MaximumCommandLength = 32768;
    public const int MaximumWrapperTokens = 256;

    public static void ValidatePreLaunch(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length > MaximumCommandLength || command.Contains('\0'))
            throw new ArgumentException("The pre-launch command is too long or contains NUL.", nameof(command));
    }

    /// <summary>
    /// Splits executable-prefix syntax. Single quotes preserve literal text. Before a
    /// double quote, pairs of backslashes are literal and an odd slash escapes the quote.
    /// Backslashes elsewhere remain literal, including Windows and UNC path separators.
    /// </summary>
    public static IReadOnlyList<string> ParseWrapper(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length > MaximumCommandLength || command.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("The wrapper command is too long or contains prohibited controls.", nameof(command));
        List<string> result = [];
        StringBuilder token = new();
        char quote = '\0';
        bool started = false;
        for (int index = 0; index < command.Length; index++)
        {
            char character = command[index];
            if (character == '\\' && quote != '\'')
            {
                int slashStart = index;
                while (index + 1 < command.Length && command[index + 1] == '\\') index++;
                int slashes = index - slashStart + 1;
                if (index + 1 < command.Length && command[index + 1] == '"')
                {
                    token.Append('\\', slashes / 2);
                    index++;
                    if (slashes % 2 != 0) token.Append('"');
                    else quote = quote == '"' ? '\0' : '"';
                }
                else token.Append('\\', slashes);
                started = true; continue;
            }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else token.Append(character);
                continue;
            }
            if (character is '"' or '\'') { quote = character; started = true; continue; }
            if (char.IsWhiteSpace(character))
            {
                if (started) { Add(); token.Clear(); started = false; }
            }
            else { token.Append(character); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("The wrapper command contains an unclosed quote.", nameof(command));
        if (started) Add();
        if (result.Count > 0 && string.IsNullOrWhiteSpace(result[0]))
            throw new ArgumentException("The wrapper executable is empty.", nameof(command));
        return result.AsReadOnly();

        void Add()
        {
            if (result.Count == MaximumWrapperTokens)
                throw new ArgumentException("The wrapper command contains too many arguments.", nameof(command));
            result.Add(token.ToString());
        }
    }
}
