using System.Text;
using System.Text.RegularExpressions;

namespace WGpuFury.Generator;

/// <summary>
/// A deliberately small parser for the subset of C used by <c>webgpu.h</c> and <c>wgpu.h</c>.
/// It understands exactly the declaration shapes those headers use and fails loudly on anything else,
/// so header changes that need generator work are caught instead of silently mis-bound.
/// </summary>
internal sealed partial class HeaderParser(HeaderModel model)
{
    private static readonly HashSet<string> IgnoredAttributes =
    [
        "WGPU_EXPORT", "WGPU_OBJECT_ATTRIBUTE", "WGPU_ENUM_ATTRIBUTE", "WGPU_STRUCTURE_ATTRIBUTE", "WGPU_FUNCTION_ATTRIBUTE",
    ];

    // Macros treated as defined when evaluating #if / #ifdef. WGPU_SKIP_PROCS drops the WGPUProc* typedef block.
    private readonly HashSet<string> _defined = ["WGPU_SKIP_PROCS"];

    private List<Token> _tokens = [];
    private int _pos;
    private string _header = "";

    public void Parse(string path)
    {
        _header = Path.GetFileName(path);
        string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        _tokens = Tokenize(Preprocess(text));
        _pos = 0;
        ParseTopLevel();
    }

    // ---------------------------------------------------------------- preprocessing

    private string Preprocess(string text)
    {
        var output = new StringBuilder();
        var active = new Stack<(bool Active, bool ParentActive)>();
        bool IsActive() => active.Count == 0 || active.Peek().Active;

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith('#'))
            {
                if (IsActive())
                {
                    output.Append(line).Append('\n');
                }

                continue;
            }

            // Join continuation lines of the directive.
            var directive = new StringBuilder(trimmed);
            while (directive.Length > 0 && directive[^1] == '\\' && i + 1 < lines.Length)
            {
                directive.Length--;
                directive.Append(' ').Append(lines[++i].Trim());
            }

            string d = directive.ToString();
            Match m = DirectiveRegex().Match(d);
            string kind = m.Groups[1].Value;
            string rest = m.Groups[2].Value.Trim();
            switch (kind)
            {
                case "ifndef":
                    Push(!_defined.Contains(rest));
                    break;
                case "ifdef":
                    Push(_defined.Contains(rest));
                    break;
                case "if":
                    Push(IsActive() && EvaluateCondition(rest));
                    break;
                case "else":
                {
                    var (wasActive, parent) = active.Pop();
                    active.Push((parent && !wasActive, parent));
                    break;
                }

                case "endif":
                    active.Pop();
                    break;
                case "define":
                    if (IsActive())
                    {
                        _defined.Add(DefineNameRegex().Match(rest).Groups[1].Value);
                        output.Append(d).Append('\n');
                    }

                    break;
                case "include":
                case "pragma":
                    break;
                default:
                    throw new FormatException($"{_header}: unsupported directive '{d}'");
            }
        }

        if (active.Count != 0)
        {
            throw new FormatException($"{_header}: unbalanced #if/#endif");
        }

        return output.ToString();

        void Push(bool condition)
        {
            bool parent = IsActive();
            active.Push((parent && condition, parent));
        }
    }

    private bool EvaluateCondition(string expression)
    {
        expression = expression.Split("//")[0].Trim();

        // Compiler-version checks only select between spellings of internal _wgpu_* helper macros,
        // which the generator interprets itself; we parse as "neither C++ nor a specific C standard".
        if (expression.Contains("__STDC_VERSION__", StringComparison.Ordinal) || expression.Contains("__cplusplus", StringComparison.Ordinal))
        {
            return false;
        }

        Match m = ConditionRegex().Match(expression);
        if (!m.Success)
        {
            throw new FormatException($"{_header}: unsupported #if expression '{expression}'");
        }

        bool value = _defined.Contains(m.Groups[2].Value);
        return m.Groups[1].Value == "!" ? !value : value;
    }

    // ---------------------------------------------------------------- tokenizing

    private enum TokenKind { Ident, Number, Punct, Doc, Define, String }

    private readonly record struct Token(TokenKind Kind, string Text)
    {
        public override string ToString() => $"{Kind}:{Text}";
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '#')
            {
                int end = s.IndexOf('\n', i);
                end = end < 0 ? s.Length : end;
                tokens.Add(new Token(TokenKind.Define, s[i..end]));
                i = end;
            }
            else if (s.AsSpan(i).StartsWith("/**") && !s.AsSpan(i).StartsWith("/**/"))
            {
                int end = s.IndexOf("*/", i + 3, StringComparison.Ordinal);
                tokens.Add(new Token(TokenKind.Doc, s[i..(end + 2)]));
                i = end + 2;
            }
            else if (s.AsSpan(i).StartsWith("/*"))
            {
                i = s.IndexOf("*/", i + 2, StringComparison.Ordinal) + 2;
            }
            else if (s.AsSpan(i).StartsWith("//"))
            {
                int end = s.IndexOf('\n', i);
                i = end < 0 ? s.Length : end;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Ident, s[start..i]));
            }
            else if (char.IsDigit(c))
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, s[start..i]));
            }
            else if (c == '"')
            {
                int end = s.IndexOf('"', i + 1);
                tokens.Add(new Token(TokenKind.String, s[i..(end + 1)]));
                i = end + 1;
            }
            else if (s.AsSpan(i).StartsWith("<<"))
            {
                tokens.Add(new Token(TokenKind.Punct, "<<"));
                i += 2;
            }
            else
            {
                tokens.Add(new Token(TokenKind.Punct, c.ToString()));
                i++;
            }
        }

        return tokens;
    }

    // ---------------------------------------------------------------- parsing

    private bool Eof => _pos >= _tokens.Count;

    private Token Peek(int offset = 0) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : new Token(TokenKind.Punct, "<eof>");

    private Token Next() => _tokens[_pos++];

    private bool IsPunct(string p, int offset = 0) => Peek(offset) is { Kind: TokenKind.Punct } t && t.Text == p;

    private bool IsIdent(string p, int offset = 0) => Peek(offset) is { Kind: TokenKind.Ident } t && t.Text == p;

    private void Expect(string text)
    {
        Token t = Next();
        if (t.Text != text)
        {
            throw Error($"expected '{text}' but found '{t.Text}'");
        }
    }

    private string ExpectIdent()
    {
        Token t = Next();
        return t.Kind == TokenKind.Ident ? t.Text : throw Error($"expected identifier but found '{t.Text}'");
    }

    private FormatException Error(string message)
    {
        string context = string.Join(' ', _tokens.Skip(Math.Max(0, _pos - 8)).Take(16).Select(t => t.Kind == TokenKind.Doc ? "/**...*/" : t.Text));
        return new FormatException($"{_header}: {message} near: {context}");
    }

    private void SkipAttributes()
    {
        while (Peek().Kind == TokenKind.Ident && IgnoredAttributes.Contains(Peek().Text))
        {
            _pos++;
        }
    }

    private void ParseTopLevel()
    {
        string? doc = null;
        while (!Eof)
        {
            Token t = Peek();
            switch (t.Kind)
            {
                case TokenKind.Doc:
                    doc = DocText.Clean(Next().Text);
                    continue;
                case TokenKind.Define:
                    ParseDefine(Next().Text, doc);
                    break;
                case TokenKind.Ident when t.Text == "typedef":
                    ParseTypedef(doc);
                    break;
                case TokenKind.Ident when t.Text == "struct":
                    // Forward declaration: struct WGPUFoo;
                    _pos++;
                    ExpectIdent();
                    Expect(";");
                    break;
                case TokenKind.Ident when t.Text == "static":
                    ParseStaticConst(doc);
                    break;
                case TokenKind.Ident:
                    ParseFunction(doc);
                    break;
                default:
                    throw Error($"unexpected token '{t.Text}'");
            }

            doc = null;
        }
    }

    private void ParseDefine(string text, string? doc)
    {
        Match m = DefineRegex().Match(text);
        if (!m.Success)
        {
            return;
        }

        string name = m.Groups[1].Value;
        bool functionLike = m.Groups[2].Value == "(";
        string body = m.Groups[3].Value.Trim();
        if (functionLike || name.StartsWith('_') || body.Length == 0)
        {
            return;
        }

        if (name.EndsWith("_INIT", StringComparison.Ordinal) && body.StartsWith("_wgpu_MAKE_INIT_STRUCT(", StringComparison.Ordinal))
        {
            int pos = 0;
            InitValue.Struct value = ParseInitStruct(body, ref pos);
            model.InitMacros[name] = new InitMacro(name, value.StructName, value);
            return;
        }

        if (name.StartsWith("WGPU_", StringComparison.Ordinal) && body.StartsWith('(') && body.EndsWith(')'))
        {
            model.Constants.Add(new ConstantDef(name, body[1..^1].Trim(), doc));
        }
    }

    private InitValue.Struct ParseInitStruct(string s, ref int pos)
    {
        const string Make = "_wgpu_MAKE_INIT_STRUCT(";
        const string Comma = "_wgpu_COMMA";
        ExpectAt(s, ref pos, Make);
        int typeEnd = s.IndexOf(',', pos);
        string type = s[pos..typeEnd].Trim();
        pos = typeEnd + 1;
        SkipWs(s, ref pos);
        ExpectAt(s, ref pos, "{");
        var fields = new List<KeyValuePair<string, InitValue>>();
        while (true)
        {
            SkipWs(s, ref pos);
            if (s[pos] == '}')
            {
                pos++;
                break;
            }

            ExpectAt(s, ref pos, "/*.");
            int nameEnd = s.IndexOf("=*/", pos, StringComparison.Ordinal);
            string field = s[pos..nameEnd];
            pos = nameEnd + 3;
            SkipWs(s, ref pos);
            InitValue value;
            if (s.AsSpan(pos).StartsWith(Make))
            {
                value = ParseInitStruct(s, ref pos);
            }
            else
            {
                int depth = 0;
                int start = pos;
                while (!(depth == 0 && s.AsSpan(pos).StartsWith(Comma)))
                {
                    depth += s[pos] switch { '(' or '{' => 1, ')' or '}' => -1, _ => 0 };
                    pos++;
                }

                value = new InitValue.Scalar(s[start..pos].Trim());
            }

            fields.Add(new(field, value));
            SkipWs(s, ref pos);
            ExpectAt(s, ref pos, Comma);
        }

        SkipWs(s, ref pos);
        ExpectAt(s, ref pos, ")");
        return new InitValue.Struct(type, fields);

        static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            {
                pos++;
            }
        }

        void ExpectAt(string s, ref int pos, string expected)
        {
            if (!s.AsSpan(pos).StartsWith(expected))
            {
                throw new FormatException($"{_header}: malformed INIT macro, expected '{expected}' at '{s[pos..Math.Min(s.Length, pos + 40)]}'");
            }

            pos += expected.Length;
        }
    }

    private void ParseTypedef(string? doc)
    {
        Expect("typedef");
        if (IsIdent("struct"))
        {
            _pos++;
            string tag = ExpectIdent();
            if (IsPunct("*"))
            {
                // typedef struct WGPUAdapterImpl* WGPUAdapter WGPU_OBJECT_ATTRIBUTE;
                _pos++;
                string name = ExpectIdent();
                SkipAttributes();
                Expect(";");
                model.Handles.Add(new HandleDef(name, doc, _header));
                return;
            }

            var def = new StructDef(tag, doc, _header);
            ParseStructBody(def);
            string typedefName = ExpectIdent();
            if (typedefName != tag)
            {
                throw Error($"struct tag '{tag}' differs from typedef name '{typedefName}'");
            }

            SkipAttributes();
            Expect(";");
            model.Structs.Add(def);
            return;
        }

        if (IsIdent("enum"))
        {
            _pos++;
            string tag = ExpectIdent();
            var def = new EnumDef(tag, isFlags: false, doc, _header);
            Expect("{");
            string? memberDoc = null;
            while (!IsPunct("}"))
            {
                if (Peek().Kind == TokenKind.Doc)
                {
                    memberDoc = DocText.Clean(Next().Text);
                    continue;
                }

                string member = ExpectIdent();
                Expect("=");
                var value = new List<string>();
                while (!IsPunct(",") && !IsPunct("}"))
                {
                    value.Add(Next().Text);
                }

                if (IsPunct(","))
                {
                    _pos++;
                }

                def.Members.Add(new EnumMember(member, string.Join(' ', value), memberDoc));
                memberDoc = null;
            }

            Expect("}");
            string typedefName = ExpectIdent();
            if (typedefName != tag)
            {
                throw Error($"enum tag '{tag}' differs from typedef name '{typedefName}'");
            }

            SkipAttributes();
            Expect(";");
            model.Enums.Add(def);
            return;
        }

        // Either a callback typedef or a plain alias.
        var typeTokens = new List<string>();
        while (!IsPunct("(") && !IsPunct(";"))
        {
            typeTokens.Add(Next().Text);
        }

        if (IsPunct("("))
        {
            // typedef void (*WGPUBufferMapCallback)(params) WGPU_FUNCTION_ATTRIBUTE;
            Expect("(");
            Expect("*");
            string name = ExpectIdent();
            Expect(")");
            List<ParamDef> ps = ParseParams();
            SkipAttributes();
            Expect(";");
            model.Callbacks.Add(new CallbackDef(name, ParseType(typeTokens, out _), ps, doc, _header));
            return;
        }

        Expect(";");
        string aliasName = typeTokens[^1];
        CType aliased = ParseType(typeTokens[..^1], out _);
        if (aliased is { Base: "WGPUFlags", Pointers: 0 })
        {
            model.Enums.Add(new EnumDef(aliasName, isFlags: true, doc, _header));
        }
        else
        {
            model.Aliases[aliasName] = aliased;
        }
    }

    private void ParseStructBody(StructDef def)
    {
        Expect("{");
        string? doc = null;
        while (!IsPunct("}"))
        {
            if (Peek().Kind == TokenKind.Doc)
            {
                doc = DocText.Clean(Next().Text);
                continue;
            }

            if (IsIdent("union"))
            {
                _pos++;
                var union = new StructDef("", doc, _header, isUnion: true);
                ParseStructBody(union);
                string fieldName = ExpectIdent();
                Expect(";");
                var named = new StructDef(def.Name + Naming.Pascal(fieldName), union.Doc, _header, isUnion: true);
                named.Fields.AddRange(union.Fields);
                model.Structs.Add(named);
                def.Fields.Add(new FieldDef(fieldName, new CType(named.Name, 0, false), false, doc));
                doc = null;
                continue;
            }

            var tokens = new List<string>();
            while (!IsPunct(";"))
            {
                tokens.Add(Next().Text);
            }

            Expect(";");
            if (tokens.Contains("["))
            {
                // Only the `T name[N]` form, with a literal N.
                if (tokens.Count < 5 || tokens[^3] != "[" || tokens[^1] != "]" || !int.TryParse(tokens[^2], out int length) || length <= 0)
                {
                    throw Error("unsupported array member");
                }

                CType elementType = ParseType(tokens[..^4], out bool elementNullable);
                def.Fields.Add(new FieldDef(tokens[^4], elementType, elementNullable, doc, length));
                doc = null;
                continue;
            }

            CType type = ParseType(tokens[..^1], out bool nullable);
            def.Fields.Add(new FieldDef(tokens[^1], type, nullable, doc));
            doc = null;
        }

        Expect("}");
    }

    private List<ParamDef> ParseParams()
    {
        Expect("(");
        var result = new List<ParamDef>();
        var current = new List<string>();
        while (true)
        {
            Token t = Next();
            if (t.Text is "," or ")")
            {
                if (current is ["void"])
                {
                    current.Clear();
                }

                if (current.Count > 0)
                {
                    CType type = ParseType(current[..^1], out bool nullable);
                    result.Add(new ParamDef(current[^1], type, nullable));
                    current.Clear();
                }

                if (t.Text == ")")
                {
                    return result;
                }

                continue;
            }

            current.Add(t.Text);
        }
    }

    private void ParseStaticConst(string? doc)
    {
        // static const WGPUBufferUsage WGPUBufferUsage_MapRead = 0x0000000000000001;
        Expect("static");
        Expect("const");
        string type = ExpectIdent();
        string name = ExpectIdent();
        Expect("=");
        var value = new List<string>();
        while (!IsPunct(";"))
        {
            value.Add(Next().Text);
        }

        Expect(";");
        EnumDef def = model.Enums.LastOrDefault(e => e.Name == type && e.IsFlags)
            ?? throw Error($"static const of unknown flags type '{type}'");
        def.Members.Add(new EnumMember(name, string.Join(' ', value), doc));
    }

    private void ParseFunction(string? doc)
    {
        SkipAttributes();
        var tokens = new List<string>();
        while (!IsPunct("("))
        {
            tokens.Add(Next().Text);
        }

        string name = tokens[^1];
        CType ret = ParseType(tokens[..^1], out _);
        List<ParamDef> ps = ParseParams();
        SkipAttributes();
        Expect(";");
        if (!name.StartsWith("wgpu", StringComparison.Ordinal))
        {
            throw Error($"unexpected function name '{name}'");
        }

        model.Functions.Add(new FunctionDef(name, ret, ps, doc, _header));
    }

    private CType ParseType(IReadOnlyList<string> tokens, out bool nullable)
    {
        nullable = tokens.Contains("WGPU_NULLABLE");
        int pointers = 0;
        bool pointeeConst = false;
        var baseParts = new List<string>();
        foreach (string t in tokens)
        {
            switch (t)
            {
                case "WGPU_NULLABLE" or "struct":
                    break;
                case "*":
                    pointers++;
                    break;
                case "const":
                    if (pointers == 0)
                    {
                        pointeeConst = true;
                    }

                    break;
                default:
                    if (!IdentRegex().IsMatch(t))
                    {
                        throw Error($"unexpected token '{t}' in type '{string.Join(' ', tokens)}'");
                    }

                    baseParts.Add(t);
                    break;
            }
        }

        if (baseParts.Count == 0)
        {
            throw Error($"type without a base: '{string.Join(' ', tokens)}'");
        }

        return new CType(string.Join(' ', baseParts), pointers, pointeeConst);
    }

    [GeneratedRegex(@"^#\s*(\w+)\s*(.*)$")]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"^(\w+)")]
    private static partial Regex DefineNameRegex();

    [GeneratedRegex(@"^#define\s+(\w+)(\(?)(.*)$")]
    private static partial Regex DefineRegex();

    [GeneratedRegex(@"^(!?)\s*defined\s*\(?\s*(\w+)\s*\)?$")]
    private static partial Regex ConditionRegex();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex IdentRegex();
}
