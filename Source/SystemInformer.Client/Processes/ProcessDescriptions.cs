using System.ComponentModel;
using System.Diagnostics;

namespace SystemInformer.Client.Processes;

internal sealed record ExecutableDescription(string Description, string ProductName);

/// <summary>Version resources are read once per live executable path, away from the UI thread.</summary>
internal sealed class ProcessDescriptions
{
    private static readonly ExecutableDescription Empty = new("", "");
    private readonly Dictionary<string, ExecutableDescription> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ExecutableDescription Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;
        if (_cache.TryGetValue(path, out var value)) return value;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            value = new(version.FileDescription?.Trim() ?? "", version.ProductName?.Trim() ?? "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            value = Empty;
        }
        _cache.Add(path, value);
        return value;
    }

    public void Trim(IEnumerable<string> paths)
    {
        var alive = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in _cache.Keys.Where(path => !alive.Contains(path)).ToArray()) _cache.Remove(path);
    }

    public void Clear() => _cache.Clear();
}
