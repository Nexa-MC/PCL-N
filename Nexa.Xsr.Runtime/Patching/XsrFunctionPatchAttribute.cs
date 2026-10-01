namespace Nexa.Xsr.Runtime;

/// <summary>Opt-in marker consumed by the compile-time source rewriter.</summary>
[AttributeUsage(AttributeTargets.Method)]
[Obsolete("Function Patch methods must be rewritten before compilation.", error: true)]
public sealed class XsrFunctionPatchAttribute(string target) : Attribute
{
    public string Target { get; } = target;
}
