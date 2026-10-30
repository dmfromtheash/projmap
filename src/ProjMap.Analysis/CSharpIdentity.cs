using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ProjMap.Analysis;

internal sealed partial class CSharpScanner
{
    // Use the original source declaration for both declarations and bound references (including
    // constructed generics and cross-project compilation references). Never format parameter symbols:
    // their names/qualification change when a package becomes available after restore/build.
    private static string? MemberSignature(ISymbol symbol)
    {
        symbol = symbol.OriginalDefinition;
        if (symbol is IMethodSymbol { PartialDefinitionPart: { } definition }) symbol = definition;
        var declaration = symbol.DeclaringSyntaxReferences
            .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(r => r.Span.Start)
            .FirstOrDefault()?.GetSyntax();
        var owner = symbol.ContainingType;
        if (declaration == null || owner == null) return null;
        var prefix = symbol switch { IPropertySymbol => "P:", IEventSymbol => "E:", IFieldSymbol => "F:", _ => "M:" };
        string Explicit(ExplicitInterfaceSpecifierSyntax? specifier) => specifier == null ? "" : SignatureText(specifier.Name) + ".";
        var name = declaration switch
        {
            MethodDeclarationSyntax m => Explicit(m.ExplicitInterfaceSpecifier) + m.Identifier.ValueText
                + (m.TypeParameterList == null ? "" : "``" + m.TypeParameterList.Parameters.Count),
            ConstructorDeclarationSyntax c => c.Modifiers.Any(SyntaxKind.StaticKeyword) ? "#cctor" : "#ctor",
            DestructorDeclarationSyntax => "Finalize",
            OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => symbol.MetadataName,
            PropertyDeclarationSyntax p => Explicit(p.ExplicitInterfaceSpecifier) + p.Identifier.ValueText,
            IndexerDeclarationSyntax i => Explicit(i.ExplicitInterfaceSpecifier) + "Item",
            EventDeclarationSyntax e => Explicit(e.ExplicitInterfaceSpecifier) + e.Identifier.ValueText,
            VariableDeclaratorSyntax v => v.Identifier.ValueText,
            _ => null,
        };
        if (name == null) return null; // synthesized members/local functions have no emitted node
        var parameters = declaration switch
        {
            BaseMethodDeclarationSyntax m => m.ParameterList.Parameters,
            IndexerDeclarationSyntax i => i.ParameterList.Parameters,
            _ => default,
        };
        var signature = prefix + Fqn(owner) + "." + name;
        if (parameters.Count > 0)
            signature += "(" + string.Join(',', parameters.Select(p => SignatureText(p.Type!)
                + (p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)
                    || m.IsKind(SyntaxKind.InKeyword)) ? "@" : ""))) + ")";
        if (declaration is ConversionOperatorDeclarationSyntax conversion) signature += "~" + SignatureText(conversion.Type);
        return signature;
    }

    // Keep written qualifications and alias names: A.Context and B.Context must not collapse into
    // one overload. Strip trivia/global::; normalize C# built-in keywords without semantic binding.
    // Generic arguments, tuple names, array ranks, pointers and nullable markers retain their syntax.
    private static string SignatureText(SyntaxNode syntax)
    {
        var text = new System.Text.StringBuilder();
        foreach (var token in syntax.DescendantTokens())
        {
            var value = SignatureToken(token);
            if (text.Length > 0 && value.Length > 0 && Word(text[^1]) && Word(value[0])) text.Append(' ');
            text.Append(value);
        }
        return text.ToString().Replace("global::", "", StringComparison.Ordinal);
        static bool Word(char c) => char.IsLetterOrDigit(c) || c is '_' or '@';
    }
    private static string SignatureToken(SyntaxToken t) => t.Kind() switch
    {
        SyntaxKind.BoolKeyword => "System.Boolean", SyntaxKind.ByteKeyword => "System.Byte",
        SyntaxKind.SByteKeyword => "System.SByte", SyntaxKind.ShortKeyword => "System.Int16",
        SyntaxKind.UShortKeyword => "System.UInt16", SyntaxKind.IntKeyword => "System.Int32",
        SyntaxKind.UIntKeyword => "System.UInt32", SyntaxKind.LongKeyword => "System.Int64",
        SyntaxKind.ULongKeyword => "System.UInt64", SyntaxKind.CharKeyword => "System.Char",
        SyntaxKind.FloatKeyword => "System.Single", SyntaxKind.DoubleKeyword => "System.Double",
        SyntaxKind.DecimalKeyword => "System.Decimal", SyntaxKind.StringKeyword => "System.String",
        SyntaxKind.ObjectKeyword => "System.Object", SyntaxKind.VoidKeyword => "System.Void",
        _ => t.Text,
    };
}
