#if !NET
namespace System.Diagnostics.CodeAnalysis;

[Flags]
internal enum DynamicallyAccessedMemberTypes
{
    None = 0,
    PublicParameterlessConstructor = 1,
    PublicConstructors = 3,
    NonPublicConstructors = 4,
    PublicMethods = 8,
    NonPublicMethods = 16,
    PublicFields = 32,
    NonPublicFields = 64,
    PublicNestedTypes = 128,
    NonPublicNestedTypes = 256,
    PublicProperties = 512,
    NonPublicProperties = 1024,
    PublicEvents = 2048,
    NonPublicEvents = 4096,
    Interfaces = 8192,
    All = -1,
}

[AttributeUsage(
    AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter |
    AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Class |
    AttributeTargets.Interface | AttributeTargets.Struct,
    Inherited = false)]
internal sealed class DynamicallyAccessedMembersAttribute : Attribute
{
    public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes) => MemberTypes = memberTypes;

    public DynamicallyAccessedMemberTypes MemberTypes { get; }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
internal sealed class RequiresUnreferencedCodeAttribute : Attribute
{
    public RequiresUnreferencedCodeAttribute(string message) => Message = message;

    public string Message { get; }

    public string? Url { get; set; }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
internal sealed class RequiresDynamicCodeAttribute : Attribute
{
    public RequiresDynamicCodeAttribute(string message) => Message = message;

    public string Message { get; }

    public string? Url { get; set; }
}

[AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
internal sealed class UnconditionalSuppressMessageAttribute : Attribute
{
    public UnconditionalSuppressMessageAttribute(string category, string checkId)
    {
        Category = category;
        CheckId = checkId;
    }

    public string Category { get; }

    public string CheckId { get; }

    public string? Justification { get; set; }

    public string? MessageId { get; set; }

    public string? Scope { get; set; }

    public string? Target { get; set; }
}

[AttributeUsage(
    AttributeTargets.Constructor | AttributeTargets.Field | AttributeTargets.Method,
    AllowMultiple = true,
    Inherited = false)]
internal sealed class DynamicDependencyAttribute : Attribute
{
    public DynamicDependencyAttribute(string memberSignature) => MemberSignature = memberSignature;

    public DynamicDependencyAttribute(DynamicallyAccessedMemberTypes memberTypes, Type type)
    {
        MemberTypes = memberTypes;
        Type = type;
    }

    public DynamicDependencyAttribute(DynamicallyAccessedMemberTypes memberTypes, string typeName, string assemblyName)
    {
        MemberTypes = memberTypes;
        TypeName = typeName;
        AssemblyName = assemblyName;
    }

    public string? MemberSignature { get; }

    public DynamicallyAccessedMemberTypes MemberTypes { get; }

    public Type? Type { get; }

    public string? TypeName { get; }

    public string? AssemblyName { get; }

    public string? Condition { get; set; }
}
#endif
