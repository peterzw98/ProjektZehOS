using System;
using System.Collections.Generic;
using System.Text;

namespace ProjektZeh.Shell;

/// <summary>
/// Zerlegt Befehlszeilen in Namen und Argumente.
/// </summary>
internal static class CommandLineParser
{
    /// <summary>
    /// Liest Argumente mit optionalen doppelten Anfuehrungszeichen.
    /// </summary>
    /// <param name="line"></param>
    /// <returns></returns>
    public static ResultObject<string[]> Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new ResultObject<string[]>(Array.Empty<string>());
        }

        var tokens = new CommandTokens();
        var token = new StringBuilder();
        bool insideQuotes = false;
        bool tokenStarted = false;

        foreach (char character in line)
        {
            switch (character)
            {
                case '"':
                    insideQuotes = !insideQuotes;
                    tokenStarted = true;
                    continue;
            }

            if (insideQuotes)
            {
                token.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (tokenStarted)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    tokenStarted = false;
                }
                continue;
            }

            token.Append(character);
            tokenStarted = true;
        }

        if (insideQuotes)
        {
            return new ResultObject<string[]>(Array.Empty<string>(), EnmErrCode.InvalidInput,
                "cause='InvalidInput' message='Doppelte Anfuehrungszeichen sind nicht geschlossen.'");
        }

        if (tokenStarted)
        {
            tokens.Add(token.ToString());
        }

        return new ResultObject<string[]>(tokens.ToArray());
    }

    private sealed class CommandTokens : List<string>
    {
    }
}
