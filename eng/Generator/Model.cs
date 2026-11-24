namespace WGpuFury.Generator;

/// <summary>A C type as written in the header, e.g. <c>WGPUBindGroupLayout const *</c>.</summary>
internal sealed record CType(string Base, int Pointers, bool PointeeConst)
{
    public bool IsPointer => Pointers > 0;

    public bool IsVoid => Base == "void" && Pointers == 0;

    public override string ToString() => (PointeeConst ? "const " : "") + Base + new string('*', Pointers);
}

internal sealed class HeaderModel
{
    public List<HandleDef> Handles { get; } = [];
    public List<EnumDef> Enums { get; } = [];
    public List<StructDef> Structs { get; } = [];
    public List<CallbackDef> Callbacks { get; } = [];
    public List<FunctionDef> Functions { get; } = [];
    public List<ConstantDef> Constants { get; } = [];
    public Dictionary<string, CType> Aliases { get; } = [];
    public Dictionary<string, InitMacro> InitMacros { get; } = [];
}

internal sealed record HandleDef(string Name, string? Doc, string Header);

internal sealed class EnumDef(string name, bool isFlags, string? doc, string header)
{
    public string Name { get; } = name;
    public bool IsFlags { get; } = isFlags;
    public string? Doc { get; } = doc;
    public string Header { get; } = header;
    public List<EnumMember> Members { get; } = [];
}

internal sealed record EnumMember(string CName, string Value, string? Doc);

internal sealed class StructDef(string name, string? doc, string header, bool isUnion = false)
{
    public string Name { get; } = name;
    public string? Doc { get; } = doc;
    public string Header { get; } = header;
    public bool IsUnion { get; } = isUnion;
    public List<FieldDef> Fields { get; } = [];
}

/// <param name="ArrayLength">For a fixed-size array member (<c>float transform[12]</c>), its length; otherwise 0.</param>
internal sealed record FieldDef(string Name, CType Type, bool Nullable, string? Doc, int ArrayLength = 0);

internal sealed record ParamDef(string Name, CType Type, bool Nullable);

internal sealed record CallbackDef(string Name, CType Return, List<ParamDef> Params, string? Doc, string Header);

internal sealed record FunctionDef(string Name, CType Return, List<ParamDef> Params, string? Doc, string Header);

internal sealed record ConstantDef(string Name, string Value, string? Doc);

/// <summary>An initializer macro, e.g. <c>WGPU_BUFFER_DESCRIPTOR_INIT</c>.</summary>
internal sealed record InitMacro(string MacroName, string StructName, InitValue.Struct Value);

internal abstract record InitValue
{
    public sealed record Scalar(string Text) : InitValue;

    public sealed record Struct(string StructName, List<KeyValuePair<string, InitValue>> Fields) : InitValue;
}
