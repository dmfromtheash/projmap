using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed partial class CSharpScanner
{
    private static EntryPointInfo HandlerEntry(ExpressionSyntax callback, string id, string anchor, SemanticModel model, CancellationToken ct)
    {
        var site = HandlerConstruction(callback);
        var registration = callback.Parent is AssignmentExpressionSyntax assignment ? assignment : site ?? callback;
        var list = site switch { InvocationExpressionSyntax i => i.ArgumentList, BaseObjectCreationExpressionSyntax o => o.ArgumentList, _ => null };
        var method = site == null ? null : Resolved(site, model, ct) as IMethodSymbol;
        ExpressionSyntax? caption = null;
        if (list != null)
        {
            var preceding = list.Arguments.TakeWhile(a => a.Expression != callback).ToArray();
            // Prefer parameter semantics. Legacy unbound helpers conventionally pass name, then caption.
            caption = preceding.FirstOrDefault(a => (a.NameColon?.Name.Identifier.ValueText ??
                method?.Parameters.ElementAtOrDefault(list.Arguments.IndexOf(a))?.Name) is "text" or "label" or "caption" or "content")?.Expression;
            caption ??= preceding.Where(a => (a.NameColon?.Name.Identifier.ValueText ??
                    method?.Parameters.ElementAtOrDefault(list.Arguments.IndexOf(a))?.Name) is not ("name" or "control" or "controlName" or "sliderName"))
                .Select(a => a.Expression).Where(e => e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)
                || e is InterpolatedStringExpressionSyntax || model.GetTypeInfo(e, ct).Type?.SpecialType == SpecialType.System_String).LastOrDefault();
        }
        var literal = caption is LiteralExpressionSyntax l2 && l2.IsKind(SyntaxKind.StringLiteralExpression);
        return new()
        {
            Id = "entry:" + id, Path = callback.SyntaxTree.FilePath, Line = Line(registration),
            EndLine = registration.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
            Anchor = anchor, ControlName = HandlerAnchor(callback, "", model, ct),
            Label = literal ? ((LiteralExpressionSyntax)caption!).Token.ValueText : caption?.ToString(),
            LabelLine = caption == null ? null : Line(caption),
            LabelKind = caption == null ? null : literal ? "literal" : "expression",
            Event = callback.Parent is AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax eventAccess } ? eventAccess.Name.Identifier.ValueText : null,
            Detection = callback.Parent is AssignmentExpressionSyntax ? "csharp-event" : "csharp-delegate-argument",
            HandlerIds = [id]
        };
    }

    private static ISymbol? Resolved(SyntaxNode site, SemanticModel model, CancellationToken ct)
    {
        var info = model.GetSymbolInfo(site, ct);
        return info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
    }

    private static bool LibraryOnly(ExpressionSyntax callback, SemanticModel model, CancellationToken ct)
    {
        var external = false;
        var body = callback is AnonymousFunctionExpressionSyntax lambda ? lambda.Body : callback;
        foreach (var site in body.DescendantNodesAndSelf().Where(n => n is SimpleNameSyntax or InvocationExpressionSyntax
                     or BaseObjectCreationExpressionSyntax))
        {
            var symbol = Resolved(site, model, ct);
            if (symbol is ILocalSymbol or IParameterSymbol)
            {
                // Invoking a supplied delegate is unknown behavior, not a library-only action.
                var type = symbol is ILocalSymbol local ? local.Type : ((IParameterSymbol)symbol).Type;
                if (type.TypeKind == TypeKind.Delegate || type.TypeKind == TypeKind.Error || !FromLibrary(type)) return false;
                continue;
            }
            if (symbol is INamespaceSymbol or IAliasSymbol) continue;
            if (symbol == null)
            {
                // Declarations, named argument labels and lambda parameters aren't uses.
                if (site is SimpleNameSyntax { Parent: NameColonSyntax }) continue;
                return false; // unresolved library/repository code: do not guess
            }
            if (symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke }) return false;
            if (!FromLibrary(symbol)) return false; // includes private fields and repository types
            external = true;
        }
        return external; // an empty/literal-only lambda does not call/use a library
    }

    private static SyntaxNode? HandlerConstruction(ExpressionSyntax callback) => callback.Parent switch
    {
        ArgumentSyntax { Parent: ArgumentListSyntax list } => list.Parent,
        AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.AddAssignmentExpression)
            && a.Left is MemberAccessExpressionSyntax access => access.Expression,
        _ => null,
    };

    private static ISymbol? HandlerControl(ExpressionSyntax callback, SemanticModel model, CancellationToken ct)
    {
        var site = HandlerConstruction(callback);
        if (site == null) return null;
        if (callback.Parent is AssignmentExpressionSyntax)
            return Resolved(site, model, ct);
        // A callback passed to a nested helper isn't necessarily the control constructed outside it.
        while (site.Parent is MemberAccessExpressionSyntax member && member.Expression == site
               && member.Parent is InvocationExpressionSyntax call) site = call;
        return site.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } => model.GetDeclaredSymbol(variable, ct),
            AssignmentExpressionSyntax assignment when assignment.Right == site => Resolved(assignment.Left, model, ct),
            _ => null,
        };
    }

    private static string? ConstructionName(SyntaxNode? site, SemanticModel model, CancellationToken ct)
    {
        if (site == null) return null;
        if (site is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax fluent } call
            && fluent.Expression is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)
            return fluent.Name.Identifier.ValueText == "Named" && call.ArgumentList.Arguments.FirstOrDefault() is { } named
                ? Text(named.Expression) : ConstructionName(fluent.Expression, model, ct);
        var list = site switch
        {
            InvocationExpressionSyntax i => i.ArgumentList,
            BaseObjectCreationExpressionSyntax o => o.ArgumentList,
            _ => null,
        };
        if (list != null)
        {
            var method = Resolved(site, model, ct) as IMethodSymbol;
            for (var i = 0; i < list.Arguments.Count; i++)
            {
                var arg = list.Arguments[i];
                var parameter = arg.NameColon?.Name.Identifier.ValueText ?? method?.Parameters.ElementAtOrDefault(i)?.Name;
                if (parameter is "name" or "control" or "controlName" or "sliderName")
                    return Text(arg.Expression);
            }
            // Label text is the fallback for unnamed factories/action records.
            var label = list.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
                .FirstOrDefault(e => e.IsKind(SyntaxKind.StringLiteralExpression));
            if (label != null) return label.Token.ValueText;
        }
        if (site is BaseObjectCreationExpressionSyntax creation && creation.Initializer != null)
            foreach (var a in creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                if (a.Left.ToString() == "Name") return Text(a.Right);
        return null;
        static string Text(ExpressionSyntax expression) => expression is LiteralExpressionSyntax literal
            ? literal.Token.ValueText : expression.ToString();
    }

    private static string HandlerAnchor(ExpressionSyntax callback, string label, SemanticModel model, CancellationToken ct)
    {
        var control = HandlerControl(callback, model, ct);
        // Prefer the UI's declared name over its local/field spelling, so factory and event agree.
        if (control != null)
        {
            foreach (var reference in control.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree != model.SyntaxTree) continue;
                if (reference.GetSyntax(ct) is VariableDeclaratorSyntax variable
                    && ConstructionName(variable.Initializer?.Value, model, ct) is { } name) return name;
            }
            // Fields are commonly declared as null! and constructed by a separate builder.
            var owner = callback.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax
                or IndexerDeclarationSyntax or EventDeclarationSyntax);
            var names = owner?.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && SymbolEqualityComparer.Default.Equals(Resolved(a.Left, model, ct), control))
                .Select(a => ConstructionName(a.Right, model, ct)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (names is { Length: 1 }) return names[0];
        }
        return ConstructionName(HandlerConstruction(callback), model, ct) ?? control?.Name ?? label;
    }

    private static bool IsControl(ITypeSymbol? type)
    {
        for (var t = type as INamedTypeSymbol; t != null; t = t.BaseType)
            if (t.Name is "Control" or "Visual" or "MenuItem" || t.Name.EndsWith("Button", StringComparison.Ordinal)
                || t.Name.EndsWith("Slider", StringComparison.Ordinal)) return true;
        return false;
    }

    private static void RefreshNotShown(ScanContext c, Unit unit, CancellationToken ct)
    {
        var candidates = new List<(string Id, ISymbol Control, SyntaxNode Scope)>();
        foreach (var result in unit.Results.Values)
        {
            if (result.Handlers.Count == 0) continue;
            var model = unit.Model(result.Tree, c);
            foreach (var (id, callback, declaration) in result.Handlers)
            {
                var control = HandlerControl(callback, model, ct);
                var type = control switch { ILocalSymbol l => l.Type, IFieldSymbol f => f.Type, _ => null };
                if (!IsControl(type) || control == null) continue;
                // Public fields, properties, parameters and aliased values can escape outside our scope.
                if (control is IFieldSymbol { DeclaredAccessibility: not Accessibility.Private }) continue;
                if (control.DeclaringSyntaxReferences.Length != 1) continue;
                var syntax = control.DeclaringSyntaxReferences[0].GetSyntax(ct) as VariableDeclaratorSyntax;
                var initial = syntax?.Initializer?.Value;
                if (initial is PostfixUnaryExpressionSyntax suppress && suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression)) initial = suppress.Operand;
                if (initial?.IsKind(SyntaxKind.NullLiteralExpression) == true) initial = null;
                var hadConstruction = initial != null;
                var assignments = declaration.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && SymbolEqualityComparer.Default.Equals(Resolved(a.Left, model, ct), control)).ToArray();
                if (initial == null && assignments.Length == 1) initial = assignments[0].Right;
                if (initial == null || !IsConstruction(initial, model, ct) || assignments.Length > (hadConstruction ? 0 : 1)) continue;
                candidates.Add((id, control, declaration));
            }
        }
        if (candidates.Count == 0)
        {
            foreach (var tree in unit.Trees) unit.ReleaseModel(tree);
            return;
        }
        var byName = candidates.ToLookup(x => x.Control.Name, StringComparer.Ordinal);
        var escaped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in unit.Trees)
        {
            var names = tree.GetRoot(ct).DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(n => byName.Contains(n.Identifier.ValueText)).ToArray();
            if (names.Length == 0) { unit.ReleaseModel(tree); continue; }
            var model = unit.Model(tree, c);
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var candidate in byName[name.Identifier.ValueText])
                {
                    if (escaped.Contains(candidate.Id)) continue;
                    if (candidate.Control is ILocalSymbol && (tree != candidate.Scope.SyntaxTree || !candidate.Scope.Span.Contains(name.Span))) continue;
                    if (!SymbolEqualityComparer.Default.Equals(Resolved(name, model, ct), candidate.Control)) continue;
                    if (!IsSetupUse(name)) escaped.Add(candidate.Id);
                }
            }
            unit.ReleaseModel(tree);
        }
        foreach (var candidate in candidates)
        {
            var node = c.Graph.Nodes[candidate.Id];
            var tags = node.Tags.Where(t => t != "not-shown").ToList();
            if (!escaped.Contains(candidate.Id)) tags.Add("not-shown");
            c.Graph.Nodes[node.Id] = node with { Tags = tags };
        }

    }

    private static bool IsSetupUse(IdentifierNameSyntax name)
    {
        SyntaxNode site = name;
        if (site.Parent is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access) site = access;
        // Assigning a newly built control to its field/local.
        if (site.Parent is AssignmentExpressionSyntax a && a.Left == site && a.IsKind(SyntaxKind.SimpleAssignmentExpression)) return true;
        if (site.Parent is not MemberAccessExpressionSyntax member || member.Expression != site) return false;
        // Setting a property or wiring an event configures the control; reads/calls/arguments may escape.
        return member.Parent is AssignmentExpressionSyntax set && set.Left == member
            && (set.IsKind(SyntaxKind.SimpleAssignmentExpression) || set.IsKind(SyntaxKind.AddAssignmentExpression));
    }

    private static bool IsConstruction(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    {
        if (expression is BaseObjectCreationExpressionSyntax) return true;
        if (expression is not InvocationExpressionSyntax invocation) return false;
        // A field initializer can live in another partial file of this compilation.
        if (SourceModel(expression.SyntaxTree, model) is not { } expressionModel) return false;
        model = expressionModel;
        var method = Resolved(invocation, model, ct) as IMethodSymbol;
        // An arbitrary GetControl() may return an existing/attached control. Require a source
        // factory with a fresh allocation; no guess for opaque library factories or recursion.
        if (method == null || method.DeclaringSyntaxReferences.Length != 1) return false;
        var syntax = method.DeclaringSyntaxReferences[0].GetSyntax(ct);
        var returns = syntax.DescendantNodes().OfType<ReturnStatementSyntax>().Select(r => r.Expression)
            .Concat(syntax is MethodDeclarationSyntax { ExpressionBody: { } arrow } ? [arrow.Expression] : []);
        var values = returns.OfType<ExpressionSyntax>().ToArray();
        if (values.Length == 0) return false;
        // CompilationReference symbols retain syntax references from the referenced project.
        // Requesting that tree from the caller compilation throws and aborts edge publication.
        if (SourceModel(syntax.SyntaxTree, model) is not { } factoryModel) return false;
        return values.All(value => Fresh(value, factoryModel, 0));

        bool Fresh(ExpressionSyntax value, SemanticModel current, int depth)
        {
            if (depth > 8) return false;
            if (value is BaseObjectCreationExpressionSyntax) return true;
            if (value is IdentifierNameSyntax && Resolved(value, current, ct) is ILocalSymbol local
                && local.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax(ct) is VariableDeclaratorSyntax { Initializer: { } init })
            {
                // A source factory may already attach or replace its allocated control before returning it.
                foreach (var use in syntax.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == local.Name))
                {
                    if (!SymbolEqualityComparer.Default.Equals(Resolved(use, current, ct), local)) continue;
                    if (use.Parent is ReturnStatementSyntax || IsSetupUse(use) && use.Parent is MemberAccessExpressionSyntax) continue;
                    return false;
                }
                return Fresh(init.Value, current, depth + 1);
            }
            // Fluent extension helpers are transparent only when every return is their receiver.
            if (value is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax fluent } call
                || Resolved(call, current, ct) is not IMethodSymbol { ReducedFrom: { } extension }
                || extension.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax(ct) is not MethodDeclarationSyntax helper) return false;
            var returns = helper.DescendantNodes().OfType<ReturnStatementSyntax>().Select(r => r.Expression)
                .Concat(helper.ExpressionBody is { } arrow ? [arrow.Expression] : []).ToArray();
            return returns.Length > 0 && returns.All(r => r is IdentifierNameSyntax name && name.Identifier.ValueText == extension.Parameters[0].Name)
                && Fresh(fluent.Expression, current, depth + 1);
        }
    }

    private static SemanticModel? SourceModel(SyntaxTree tree, SemanticModel current)
    {
        if (tree == current.SyntaxTree) return current;
        var pending = new Stack<Compilation>();
        var visited = new HashSet<Compilation>();
        pending.Push(current.Compilation);
        while (pending.TryPop(out var compilation))
        {
            if (!visited.Add(compilation)) continue;
            if (compilation.ContainsSyntaxTree(tree)) return compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var reference in compilation.References.OfType<CompilationReference>()) pending.Push(reference.Compilation);
        }
        return null; // No source owner: leave the visibility heuristic undecided.
    }
}
