namespace AxisTool;

/// <summary>Minimal parser: positional words, "--name value" options (repeatable), and "--flag" switches.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public Args(IEnumerable<string> raw, IReadOnlySet<string> flagNames)
    {
        var list = raw.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item.StartsWith("--", StringComparison.Ordinal) && item.Length > 2)
            {
                var name = item[2..];
                if (flagNames.Contains(name))
                {
                    _flags.Add(name);
                }
                else if (i + 1 < list.Count)
                {
                    if (!_options.TryGetValue(name, out var values))
                    {
                        _options[name] = values = [];
                    }

                    values.Add(list[++i]);
                }
                else
                {
                    throw new UsageException($"选项 --{name} 缺少值。");
                }
            }
            else
            {
                Positional.Add(item);
            }
        }
    }

    public List<string> Positional { get; } = [];

    public bool Flag(string name) => _flags.Contains(name);

    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v[^1] : null;

    public string Require(string name) => Get(name) ?? throw new UsageException($"缺少必需选项 --{name}。");

    public IReadOnlyList<string> All(string name) => _options.TryGetValue(name, out var v) ? v : [];

    public double Double(string name, double fallback) =>
        Get(name) is { } text
            ? double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new UsageException($"--{name} 需要数字：{text}")
            : fallback;
}

internal sealed class UsageException(string message) : Exception(message);
