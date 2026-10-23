namespace Builder.Cli;

/// <summary>
/// 간단한 인자 파서. 형식: &lt;명령&gt; [위치 인자...] [--옵션 값 | --옵션=값 | --플래그]
/// 같은 옵션을 여러 번 줄 수 있다 (예: --set a=1 --set b=2).
/// </summary>
public sealed class CommandLine
{
    private static readonly HashSet<string> Flags =
        ["accept-eula", "no-java-download", "all", "help", "no-mods", "allow-unstable-mods", "update", "confirm", "no-metrics"];

    private readonly Dictionary<string, List<string>> options = new(StringComparer.OrdinalIgnoreCase);

    public CommandLine(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            string value;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (Flags.Contains(name))
            {
                value = "true";
            }
            else if (i + 1 < args.Length)
            {
                value = args[++i];
            }
            else
            {
                throw new ArgumentException($"--{name} 옵션에 값이 필요합니다.");
            }

            if (!options.TryGetValue(name, out var list))
                options[name] = list = [];
            list.Add(value);
        }

        Command = positional.Count > 0 ? positional[0].ToLowerInvariant() : "help";
        Positional = positional.Skip(1).ToList();
    }

    public string Command { get; }
    public IReadOnlyList<string> Positional { get; }

    public string? Option(string name) => options.TryGetValue(name, out var v) ? v[^1] : null;

    public IReadOnlyList<string> Options(string name) => options.TryGetValue(name, out var v) ? v : [];

    public bool Flag(string name) => Option(name) is "true" or "1" or "yes";

    public string RequirePositional(int index, string description) =>
        index < Positional.Count ? Positional[index] : throw new ArgumentException($"{description}을(를) 지정하세요.");

    /// <summary>--set key=value 목록을 사전으로 만든다.</summary>
    public Dictionary<string, string> KeyValues(string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Options(name))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                throw new ArgumentException($"--{name} 값은 키=값 형식이어야 합니다: {pair}");
            result[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
        return result;
    }
}
