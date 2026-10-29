using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ProjMap.Core;

namespace ProjMap.Analysis;

internal sealed class XamlScanner : IRepositoryScanner
{
    public string Name => "xaml";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace X2009 = "http://schemas.microsoft.com/winfx/2009/xaml";
    private static readonly XNamespace Design = "http://schemas.microsoft.com/expression/blend/2008";
    private static bool XamlNamespace(string uri) => uri == X.NamespaceName || uri == X2009.NamespaceName;
    private static XAttribute? XamlAttribute(XElement element, string name) => element.Attribute(X + name) ?? element.Attribute(X2009 + name);
    // Deliberately bounded: arbitrary identifier-valued properties are not event subscriptions.
    // Framework event metadata may be unavailable when scanning an unrestored repository.
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "Click", "Clicked", "DoubleClick", "Tapped", "DoubleTapped", "Holding", "RightTapped",
        "PointerPressed", "PointerReleased", "PointerMoved", "PointerEntered", "PointerExited",
        "PointerCaptureLost", "PointerWheelChanged", "PointerCanceled", "Pressed", "Released",
        "KeyDown", "KeyUp", "PreviewKeyDown", "PreviewKeyUp", "TextInput", "PreviewTextInput",
        "GotFocus", "LostFocus", "Loaded", "Unloaded", "Initialized", "Opened", "Closed", "Closing",
        "Activated", "Deactivated", "AttachedToVisualTree", "DetachedFromVisualTree", "SizeChanged",
        "LayoutUpdated", "SelectionChanged", "SelectedIndexChanged", "TextChanged", "ValueChanged",
        "Checked", "Unchecked", "Indeterminate", "Toggled", "CheckStateChanged",
        "MouseDown", "MouseUp", "MouseMove", "MouseEnter", "MouseLeave", "MouseWheel",
        "MouseLeftButtonDown", "MouseLeftButtonUp", "MouseRightButtonDown", "MouseRightButtonUp",
        "PreviewMouseDown", "PreviewMouseUp", "PreviewMouseMove", "PreviewMouseWheel",
        "DragEnter", "DragLeave", "DragOver", "Drop", "ScrollChanged", "RowDoubleClick",
        "Appearing", "Disappearing", "Completed", "NavigatedTo", "NavigatedFrom"
    };
    private static bool Identifier(string value) => Regex.IsMatch(value, @"^[_\p{L}][_\p{L}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}]*$");

    public Task ScanAsync(ScanContext c, CancellationToken ct)
    {
        var types = c.Graph.Nodes.Values.Where(n => n.Kind == NodeKinds.Type && n.Language == "C#")
            .OrderBy(n => n.Id, StringComparer.Ordinal).ToArray();
        var members = c.Graph.Nodes.Values.Where(n => n.Kind == NodeKinds.Member && n.Language == "C#")
            .ToLookup(n => n.ParentId, StringComparer.Ordinal);
        foreach (var file in c.Files.Values.Where(f => f.Language == "XAML").OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Text(c) is not { } text) continue;
            try
            {
                // Prohibit DTDs/external entities; parsing never reads other files or the network.
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
                if (document.Root is not { } root) continue;
                var module = c.ModuleFor(file.Path)?.Id ?? "system:root";
                var fileId = ScanContext.FileId(file.Path);
                Node? FindType(string? fullName)
                {
                    if (string.IsNullOrWhiteSpace(fullName)) return null;
                    var localId = "type:" + module + ":" + fullName.Trim();
                    var candidates = types.Where(n => n.Id.EndsWith(":" + fullName.Trim(), StringComparison.Ordinal)).ToArray();
                    return candidates.FirstOrDefault(n => n.Id == localId) ?? (candidates.Length == 1 ? candidates[0] : null);
                }
                Node? MarkupType(XElement site, string? value)
                {
                    if (value == null) return null;
                    value = value.Trim();
                    // x:DataType may itself use {x:Type vm:Type}; prefix aliases are namespace-aware.
                    if (Extension(site, value) is { Local: "Type", Namespace: var ns, Arguments: var argument } && XamlNamespace(ns))
                        value = argument.Trim();
                    var colon = value.IndexOf(':');
                    var prefix = colon < 0 ? "" : value[..colon];
                    var name = colon < 0 ? value : value[(colon + 1)..];
                    if (!Identifier(name)) return null;
                    var uri = prefix.Length == 0 ? site.GetDefaultNamespace().NamespaceName : site.GetNamespaceOfPrefix(prefix)?.NamespaceName;
                    var clrNamespace = uri?.StartsWith("using:", StringComparison.Ordinal) == true ? uri[6..]
                        : uri?.StartsWith("clr-namespace:", StringComparison.Ordinal) == true ? uri[14..].Split(';')[0] : null;
                    return clrNamespace == null ? null : FindType(clrNamespace.Length == 0 ? name : clrNamespace + "." + name);
                }
                Node? ContextType(XElement site)
                {
                    foreach (var ancestor in site.AncestorsAndSelf())
                    {
                        if (XamlAttribute(ancestor, "DataType") is { } dataType) return MarkupType(ancestor, dataType.Value);
                        if (ancestor.Attribute(Design + "DataContext") is { } context)
                        {
                            var extension = Extension(ancestor, context.Value);
                            if (extension is { Local: "Type", Namespace: var ns } && XamlNamespace(ns))
                                return MarkupType(ancestor, context.Value);
                            if (extension is { Local: "DesignInstance", Namespace: var designNs, Arguments: var args } && designNs == Design.NamespaceName)
                            {
                                var parts = args.Split(',').Select(p => Regex.Replace(p.Trim(), @"\s*=\s*", "=")).ToArray();
                                var named = parts.Where(p => p.StartsWith("Type=", StringComparison.Ordinal)).ToArray();
                                var type = named.Length == 1 ? named[0][5..] : named.Length == 0 ? parts[0] : "";
                                return MarkupType(ancestor, type);
                            }
                            return null;
                        }
                        // Template contents have their own runtime scope unless explicitly typed.
                        if (ancestor.Name.LocalName is "DataTemplate" or "HierarchicalDataTemplate" or "TreeDataTemplate" or "ControlTemplate")
                            return null;
                        // A runtime DataContext change blocks inference from a more distant ancestor.
                        if (ancestor.Attribute("DataContext") != null || ancestor.Elements().Any(e => e.Name.LocalName.EndsWith(".DataContext", StringComparison.Ordinal)))
                            return null;
                    }
                    return null;
                }
                Node? Member(Node? owner, string name, bool method)
                {
                    if (owner == null) return null;
                    var candidates = members[owner.Id].Where(n => n.Name == name &&
                        (method ? n.SubKind == "method" : n.SubKind is "property" or "field")).ToArray();
                    // Without an event signature, overload selection would be a guess.
                    return candidates.Length == 1 ? candidates[0] : null;
                }
                SourceLocation Evidence(XAttribute attribute)
                {
                    var element = attribute.Parent!;
                    var name = XamlAttribute(element, "Name")?.Value ?? element.Attribute("Name")?.Value;
                    var snippet = element.Name.LocalName + (name == null ? "" : " #" + name) + " " + attribute;
                    return new(file.Path, ((IXmlLineInfo)attribute).LineNumber, snippet);
                }
                void Entry(XElement element, XAttribute attribute, Node target)
                {
                    var caption = element.Attribute("Content") ?? element.Attribute("Text") ?? element.Attribute("Header");
                    var name = XamlAttribute(element, "Name")?.Value ?? element.Attribute("Name")?.Value;
                    var line = ((IXmlLineInfo)attribute).LineNumber;
                    c.EntryPoints.Add(new()
                    {
                        Id = $"entry:{file.Path}:{line}:{attribute.Name.LocalName}:{target.Id}",
                        Path = file.Path, Line = line, ControlName = name, Label = caption?.Value,
                        LabelLine = caption == null ? null : ((IXmlLineInfo)caption).LineNumber,
                        LabelKind = caption == null ? null : caption.Value.TrimStart().StartsWith('{') ? "expression" : "literal",
                        Event = attribute.Name.LocalName, Detection = "xaml-attribute", HandlerIds = [target.Id]
                    });
                }
                var classAttribute = XamlAttribute(root, "Class");
                var codeBehind = FindType(classAttribute?.Value);
                if (codeBehind != null) c.Edge(EdgeKinds.Reference, fileId, codeBehind.Id, Evidence(classAttribute!));
                foreach (var element in root.DescendantsAndSelf())
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                    {
                        var value = attribute.Value.Trim();
                        if (attribute.Name.Namespace == XNamespace.None && Events.Contains(attribute.Name.LocalName) && Identifier(value) &&
                            Member(codeBehind, value, true) is { } handler)
                        {
                            c.Edge(EdgeKinds.XamlEvent, fileId, handler.Id, Evidence(attribute));
                            Entry(element, attribute, handler);
                            var current = c.Graph.Nodes[handler.Id];
                            c.Graph.Nodes[handler.Id] = current with
                            { Tags = current.Tags.Concat(["callback", "xaml-handler"]).Distinct().Order(StringComparer.Ordinal).ToList() };
                        }
                        if (attribute.Name.Namespace == XNamespace.None && attribute.Name.LocalName == "Command" &&
                            BindingPath(element, value) is { } path && Member(ContextType(element), path, false) is { } command)
                        {
                            c.Edge(EdgeKinds.Binding, fileId, command.Id, Evidence(attribute));
                            Entry(element, attribute, command);
                        }
                    }
                }
            }
            catch (XmlException ex) { c.Warn(Name, "Invalid XAML: " + ex.Message, file.Path); }
        }
        return Task.CompletedTask;
    }

    private sealed record MarkupExtension(string Namespace, string Local, string Arguments);
    private static MarkupExtension? Extension(XElement site, string value)
    {
        if (!value.StartsWith('{') || !value.EndsWith('}')) return null;
        var body = value[1..^1].Trim();
        var separator = body.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator < 0) return null;
        var name = body[..separator];
        var colon = name.IndexOf(':');
        var uri = colon < 0 ? "" : site.GetNamespaceOfPrefix(name[..colon])?.NamespaceName;
        if (uri == null) return null;
        return new(uri, colon < 0 ? name : name[(colon + 1)..], body[(separator + 1)..].Trim());
    }
    private static string? BindingPath(XElement site, string value)
    {
        var extension = Extension(site, value);
        if (extension == null || !(extension.Namespace == "" && extension.Local is "Binding" or "CompiledBinding" ||
            XamlNamespace(extension.Namespace) && extension.Local == "Bind")) return null;
        var parts = extension.Arguments.Split(',').Select(p => Regex.Replace(p.Trim(), @"\s*=\s*", "=")).ToArray();
        // Explicit sources and nested extensions can change the binding owner; do not infer it.
        if (parts.Any(p => p.Contains('{') || p.Contains('}') || p.StartsWith("Source=", StringComparison.Ordinal) ||
            p.StartsWith("RelativeSource=", StringComparison.Ordinal) || p.StartsWith("ElementName=", StringComparison.Ordinal))) return null;
        var paths = parts.Where(p => p.StartsWith("Path=", StringComparison.Ordinal)).Select(p => p[5..].Trim()).ToArray();
        var path = paths.Length == 1 ? paths[0] : paths.Length == 0 ? parts[0] : "";
        return Identifier(path) ? path : null;
    }
}
