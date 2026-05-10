using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Meziantou.Polyfill.Generator;

internal static class StringToReadOnlySpanCharOverloadGenerator
{
    private static readonly SymbolDisplayFormat SignatureTypeDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.ExpandNullable);

    public static string[] GetDeclaredCSharpSignatureKeys(Compilation compilation, SemanticModel semanticModel, CompilationUnitSyntax root)
    {
        var result = new List<string>();
        foreach (var classDeclaration in root.Members.OfType<ClassDeclarationSyntax>().Where(IsPolyfillExtensionsClass))
        {
            foreach (var method in classDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol)
                    result.Add(CreateCSharpSignatureKey(compilation, classDeclaration.Identifier.ValueText, methodSymbol, selectedStringParameterIndexes: new HashSet<int>()));
            }

            foreach (var extensionBlock in classDeclaration.Members.OfType<ExtensionBlockDeclarationSyntax>())
            {
                foreach (var method in extensionBlock.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol)
                        result.Add(CreateCSharpSignatureKey(compilation, classDeclaration.Identifier.ValueText, methodSymbol, selectedStringParameterIndexes: new HashSet<int>()));
                }
            }
        }

        return [.. result];
    }

    public static StringToReadOnlySpanCharOverload[] Generate(Compilation compilation, SemanticModel semanticModel, CompilationUnitSyntax root, string documentationDeclarationId)
    {
        var readOnlySpanType = compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
        if (readOnlySpanType is null)
            return [];

        var docParameterTypes = GetDocumentationParameterTypes(documentationDeclarationId);
        if (docParameterTypes is null)
            return [];

        var result = new List<StringToReadOnlySpanCharOverload>();
        var generatedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var classDeclaration in root.Members.OfType<ClassDeclarationSyntax>().Where(IsPolyfillExtensionsClass))
        {
            foreach (var method in classDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                AddOverloads(result, generatedIds, compilation, semanticModel, classDeclaration, extensionBlock: null, method, documentationDeclarationId, docParameterTypes);
            }

            foreach (var extensionBlock in classDeclaration.Members.OfType<ExtensionBlockDeclarationSyntax>())
            {
                foreach (var method in extensionBlock.Members.OfType<MethodDeclarationSyntax>())
                {
                    AddOverloads(result, generatedIds, compilation, semanticModel, classDeclaration, extensionBlock, method, documentationDeclarationId, docParameterTypes);
                }
            }
        }

        return [.. result];
    }

    private static void AddOverloads(List<StringToReadOnlySpanCharOverload> result, HashSet<string> generatedIds, Compilation compilation, SemanticModel semanticModel, ClassDeclarationSyntax classDeclaration, ExtensionBlockDeclarationSyntax? extensionBlock, MethodDeclarationSyntax method, string documentationDeclarationId, string[] docParameterTypes)
    {
        if (semanticModel.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol)
            return;

        if (methodSymbol.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal)
            return;

        List<int>? readOnlySpanCharParameterIndexes = null;
        for (var i = 0; i < methodSymbol.Parameters.Length; i++)
        {
            var parameter = methodSymbol.Parameters[i];
            if (parameter.RefKind == RefKind.None && IsReadOnlySpanOfChar(compilation, parameter.Type))
            {
                readOnlySpanCharParameterIndexes ??= [];
                readOnlySpanCharParameterIndexes.Add(i);
            }
        }

        if (readOnlySpanCharParameterIndexes is null)
            return;

        var docParameterOffset = GetDocumentationParameterOffset(methodSymbol.Parameters.Length, docParameterTypes.Length);
        if (docParameterOffset is null)
            return;

        for (var mask = 1; mask < (1 << readOnlySpanCharParameterIndexes.Count); mask++)
        {
            var selectedParameterIndexes = new HashSet<int>();
            for (var i = 0; i < readOnlySpanCharParameterIndexes.Count; i++)
            {
                if ((mask & (1 << i)) != 0)
                    selectedParameterIndexes.Add(readOnlySpanCharParameterIndexes[i]);
            }

            var overloadDocumentationId = CreateDocumentationId(documentationDeclarationId, docParameterTypes, selectedParameterIndexes, docParameterOffset.Value);
            if (overloadDocumentationId is null || !generatedIds.Add(overloadDocumentationId))
                continue;

            result.Add(new StringToReadOnlySpanCharOverload(
                overloadDocumentationId,
                CreateCSharpSignatureKey(compilation, classDeclaration.Identifier.ValueText, methodSymbol, selectedParameterIndexes),
                CreateContent(classDeclaration, extensionBlock, method, methodSymbol, selectedParameterIndexes)));
        }
    }

    private static int? GetDocumentationParameterOffset(int methodParameterCount, int documentationParameterCount)
    {
        if (documentationParameterCount == methodParameterCount)
            return 0;

        if (documentationParameterCount == methodParameterCount - 1)
            return 1;

        return null;
    }

    private static string? CreateDocumentationId(string documentationDeclarationId, string[] docParameterTypes, HashSet<int> selectedParameterIndexes, int docParameterOffset)
    {
        var updatedParameterTypes = (string[])docParameterTypes.Clone();
        foreach (var parameterIndex in selectedParameterIndexes)
        {
            var docParameterIndex = parameterIndex - docParameterOffset;
            if (docParameterIndex < 0 || docParameterIndex >= updatedParameterTypes.Length)
                return null;

            if (!string.Equals(updatedParameterTypes[docParameterIndex], "System.ReadOnlySpan{System.Char}", StringComparison.Ordinal))
                return null;

            updatedParameterTypes[docParameterIndex] = "System.String";
        }

        var openParenIndex = documentationDeclarationId.IndexOf('(', StringComparison.Ordinal);
        if (openParenIndex < 0)
            return documentationDeclarationId + "(" + string.Join(",", updatedParameterTypes) + ")";

        var closeParenIndex = documentationDeclarationId.LastIndexOf(')');
        if (closeParenIndex < openParenIndex)
            return null;

        return documentationDeclarationId[..(openParenIndex + 1)] + string.Join(",", updatedParameterTypes) + documentationDeclarationId[closeParenIndex..];
    }

    private static string CreateContent(ClassDeclarationSyntax classDeclaration, ExtensionBlockDeclarationSyntax? extensionBlock, MethodDeclarationSyntax method, IMethodSymbol methodSymbol, HashSet<int> selectedParameterIndexes)
    {
        var methodIndent = extensionBlock is null ? "    " : "        ";
        var sb = new StringBuilder();

        sb.Append(CreateClassHeader(classDeclaration)).AppendLine();
        sb.AppendLine("{");
        if (extensionBlock is not null)
        {
            sb.Append("    extension").Append(extensionBlock.ParameterList!.ToString()).AppendLine();
            sb.AppendLine("    {");
        }

        sb.Append(methodIndent)
            .Append(CreateMethod(method, methodSymbol, selectedParameterIndexes))
            .AppendLine();

        if (extensionBlock is not null)
        {
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string CreateClassHeader(ClassDeclarationSyntax classDeclaration)
    {
        var modifiers = classDeclaration.Modifiers.ToString();
        if (modifiers.Length == 0)
            return "class " + classDeclaration.Identifier;

        return modifiers + " class " + classDeclaration.Identifier;
    }

    private static string CreateCSharpSignatureKey(Compilation compilation, string className, IMethodSymbol methodSymbol, HashSet<int> selectedStringParameterIndexes)
    {
        var stringType = compilation.GetSpecialType(SpecialType.System_String);
        var parameters = methodSymbol.Parameters
            .Select((parameter, index) =>
            {
                var type = selectedStringParameterIndexes.Contains(index) ? stringType : parameter.Type;
                return parameter.RefKind + ":" + type.ToDisplayString(SignatureTypeDisplayFormat);
            });

        return className + "|" + methodSymbol.Name + "|" + methodSymbol.Arity.ToString(CultureInfo.InvariantCulture) + "|" + string.Join("|", parameters);
    }

    private static string CreateMethod(MethodDeclarationSyntax method, IMethodSymbol methodSymbol, HashSet<int> selectedParameterIndexes)
    {
        var modifiers = method.Modifiers.ToString();
        var typeParameters = method.TypeParameterList?.ToString() ?? "";
        var constraints = method.ConstraintClauses.ToString();
        var parameters = string.Join(", ", method.ParameterList.Parameters.Select((parameter, index) => CreateParameter(parameter, selectedParameterIndexes.Contains(index))));
        var arguments = string.Join(", ", method.ParameterList.Parameters.Select((parameter, index) => CreateArgument(parameter, methodSymbol.Parameters[index], selectedParameterIndexes.Contains(index))));
        var typeArguments = method.TypeParameterList is null ? "" : "<" + string.Join(", ", method.TypeParameterList.Parameters.Select(parameter => parameter.Identifier.ToString())) + ">";

        return $"{modifiers} {method.ReturnType} {method.Identifier}{typeParameters}({parameters}){constraints} => {method.Identifier}{typeArguments}({arguments});";
    }

    private static string CreateParameter(ParameterSyntax parameter, bool useString)
    {
        if (!useString)
            return parameter.ToString();

        var sb = new StringBuilder();
        foreach (var attributeList in parameter.AttributeLists)
        {
            sb.Append(attributeList).Append(' ');
        }

        foreach (var modifier in parameter.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.ThisKeyword))
                sb.Append(modifier).Append(' ');
        }

        sb.Append("string? ").Append(parameter.Identifier);
        if (parameter.Default is not null)
            sb.Append(parameter.Default);

        return sb.ToString();
    }

    private static string CreateArgument(ParameterSyntax parameter, IParameterSymbol parameterSymbol, bool useString)
    {
        var name = parameter.Identifier.ToString();
        if (useString)
            return "global::System.MemoryExtensions.AsSpan(" + name + ")";

        return parameterSymbol.RefKind switch
        {
            RefKind.In => "in " + name,
            RefKind.RefReadOnlyParameter => "in " + name,
            RefKind.Out => "out " + name,
            RefKind.Ref => "ref " + name,
            _ => name,
        };
    }

    private static bool IsPolyfillExtensionsClass(ClassDeclarationSyntax classDeclaration)
    {
        return classDeclaration.Identifier.ValueText.StartsWith("PolyfillExtensions", StringComparison.Ordinal);
    }

    private static bool IsReadOnlySpanOfChar(Compilation compilation, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol namedType || namedType.TypeArguments.Length != 1)
            return false;

        var readOnlySpanType = compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
        return readOnlySpanType is not null &&
               SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, readOnlySpanType) &&
               namedType.TypeArguments[0].SpecialType == SpecialType.System_Char;
    }

    private static string[]? GetDocumentationParameterTypes(string documentationDeclarationId)
    {
        var openParenIndex = documentationDeclarationId.IndexOf('(', StringComparison.Ordinal);
        if (openParenIndex < 0)
            return [];

        var closeParenIndex = documentationDeclarationId.LastIndexOf(')');
        if (closeParenIndex < openParenIndex)
            return null;

        var parameters = documentationDeclarationId.Substring(openParenIndex + 1, closeParenIndex - openParenIndex - 1);
        if (parameters.Length == 0)
            return [];

        var result = new List<string>();
        var startIndex = 0;
        var depth = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            switch (parameters[i])
            {
                case '{':
                case '(':
                    depth++;
                    break;
                case '}':
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(parameters[startIndex..i]);
                    startIndex = i + 1;
                    break;
            }
        }

        result.Add(parameters[startIndex..]);
        return [.. result];
    }
}
