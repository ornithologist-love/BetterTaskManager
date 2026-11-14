namespace SystemInformer.Client.Tests;

/// <summary>The written fact a test protects: a document path relative to the repository root, optionally with a heading.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
internal sealed class AnchorAttribute(string target) : Attribute
{
    public string Target { get; } = target;
}
