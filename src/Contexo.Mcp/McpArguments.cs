namespace Contexo.Mcp;

/// <summary>Command-line options of Contexo.Mcp. Unknown arguments are ignored.</summary>
/// <param name="DatabasePath">--db: SQLite file to read. Defaults to the shared Contexo database.</param>
/// <param name="ModelsDirectory">--models: folder with embedding models. Defaults to the installed or per-user models folder.</param>
internal sealed record McpArguments(string? DatabasePath, string? ModelsDirectory)
{
    public static McpArguments Parse(IReadOnlyList<string> args)
    {
        string? db = null;
        string? models = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (TryRead(arg, "--db", args, ref i, out var dbValue))
            {
                db = dbValue;
            }
            else if (TryRead(arg, "--models", args, ref i, out var modelsValue))
            {
                models = modelsValue;
            }
        }

        return new McpArguments(db, models);
    }

    // Accepts "--name value" and "--name=value".
    private static bool TryRead(string arg, string name, IReadOnlyList<string> args, ref int index, out string? value)
    {
        value = null;
        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 < args.Count)
            {
                value = args[++index];
            }

            return true;
        }

        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(name.Length + 1)..];
            return true;
        }

        return false;
    }
}
