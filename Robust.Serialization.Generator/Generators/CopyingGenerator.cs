using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Robust.Serialization.Generator.CustomSerializerType;
using static Robust.Serialization.Generator.Generator;
using static Robust.Serialization.Generator.Types;

namespace Robust.Serialization.Generator.Generators;

public static class CopyingGenerator
{
    public static void GenerateCopiers(this StringBuilder builder, DataDefinition definition, SourceProductionContext sourceContext, (Compilation Left, ImmutableArray<TypeDeclarationSyntax> Right) source)
    {
        builder.AppendLine($$"""
{{GetConstructor(definition)}}

{{GetCopyMethods(definition)}}

{{GetInstantiators(definition)}}
""");
    }

     private static string GetConstructor(DataDefinition definition)
     {
         if (definition.Type.TypeKind == TypeKind.Interface)
             return string.Empty;

         var builder = new StringBuilder();

         if (NeedsEmptyConstructor(definition.Type))
         {
             builder.AppendLine($$"""
                                  // Implicit constructor
                                  #pragma warning disable CS8618
                                  public {{definition.Type.Name}}()
                                  #pragma warning restore CS8618
                                  {
                                  }
                                  """);
         }

         return builder.ToString();
     }

    private static string GetCopyMethods(DataDefinition definition)
    {
        var builder = new StringBuilder();

        var modifiers = IsVirtualClass(definition.Type) ? "virtual " : string.Empty;
        var baseCall = string.Empty;
        string baseCopy;
        var baseType = definition.Type.BaseType;

        if (baseType != null && IsDataDefinition(definition.Type.BaseType))
        {
            var baseName = baseType.ToDisplayString();
            baseCall = $"""
                        var definitionCast = ({baseName}) target;
                        base.InternalCopy(ref definitionCast, serialization, hookCtx, context);
                        target = ({definition.GenericTypeName}) definitionCast;
                        """;

             baseCopy = $$"""
                          /// <seealso cref="ISerializationManager.CopyTo"/>
                          [Obsolete("Use ISerializationManager.CopyTo instead")]
                          public override void Copy(ref {{baseName}} target, ISerializationManager serialization, SerializationHookContext hookCtx, ISerializationContext? context = null)
                          {
                              var cast = ({{definition.GenericTypeName}}) target;
                              Copy(ref cast, serialization, hookCtx, context);
                              target = cast!;
                          }

                          /// <seealso cref="ISerializationManager.CopyTo"/>
                          [Obsolete("Use ISerializationManager.CopyTo instead")]
                          public override void Copy(ref object target, ISerializationManager serialization, SerializationHookContext hookCtx, ISerializationContext? context = null)
                          {
                              var cast = ({{definition.GenericTypeName}}) target;
                              Copy(ref cast, serialization, hookCtx, context);
                              target = cast!;
                          }
                          """;
        }
        else
        {
            baseCopy = $$"""
                         /// <seealso cref="ISerializationManager.CopyTo"/>
                         [Obsolete("Use ISerializationManager.CopyTo instead")]
                         public {{modifiers}} void Copy(ref object target, ISerializationManager serialization, SerializationHookContext hookCtx, ISerializationContext? context = null)
                         {
                             var cast = ({{definition.GenericTypeName}}) target;
                             Copy(ref cast, serialization, hookCtx, context);
                             target = cast!;
                         }
                         """;
        }

        builder.AppendLine($$"""
                             /// <seealso cref="ISerializationManager.CopyTo"/>
                             [Obsolete("Use ISerializationManager.CopyTo instead")]
                             public {{modifiers}} void InternalCopy(ref {{definition.GenericTypeName}} target, ISerializationManager serialization, SerializationHookContext hookCtx, ISerializationContext? context = null)
                             {
                                {{baseCall}}
                                {{CopyDataFields(definition)}}
                             }

                             /// <seealso cref="ISerializationManager.CopyTo"/>
                             [Obsolete("Use ISerializationManager.CopyTo instead")]
                             public {{modifiers}} void Copy(ref {{definition.GenericTypeName}} target, ISerializationManager serialization, SerializationHookContext hookCtx, ISerializationContext? context = null)
                             {
                                 InternalCopy(ref target, serialization, hookCtx, context);
                             }

                             {{baseCopy}}
                             """);

        foreach (var @interface in GetImplicitDataDefinitionInterfaces(definition.Type, true))
        {
            var interfaceModifiers = baseType != null && baseType.AllInterfaces.Contains(@interface, SymbolEqualityComparer.Default)
                ? "override "
                : modifiers;
            var interfaceName = @interface.ToDisplayString();

            builder.AppendLine($$"""
                                 /// <seealso cref="ISerializationManager.CopyTo"/>
                                 [Obsolete("Use ISerializationManager.CopyTo instead")]
                                 public {{interfaceModifiers}} void InternalCopy(ref {{interfaceName}} target, ISerializationManager serialization, SerializationHookContext hookCtx, ISerializationContext? context = null)
                                 {
                                     var def = ({{definition.GenericTypeName}}) target;
                                     Copy(ref def, serialization, hookCtx, context);
                                     target = def;
                                 }

                                 /// <seealso cref="ISerializationManager.CopyTo"/>
                                 [Obsolete("Use ISerializationManager.CopyTo instead")]
                                 public {{interfaceModifiers}} void Copy(ref {{interfaceName}} target, ISerializationManager serialization, SerializationHookContext hookCtx, ISerializationContext? context = null)
                                 {
                                     InternalCopy(ref target, serialization, hookCtx, context);
                                 }
                                 """);
        }

        return builder.ToString();
    }

    private static string GetInstantiators(DataDefinition definition)
    {
        var builder = new StringBuilder();
        var modifiers = string.Empty;

        if (definition.Type.BaseType is { } baseType && IsDataDefinition(baseType))
            modifiers = "override ";
        else if (IsVirtualClass(definition.Type))
            modifiers = "virtual ";

        if (definition.Type.IsAbstract)
        {
            // TODO make abstract once data definitions are forced to be partial
            builder.AppendLine($$"""
                                 /// <seealso cref="ISerializationManager.CreateCopy"/>
                                 [Obsolete("Use ISerializationManager.CreateCopy instead")]
                                 public {{modifiers}} {{definition.GenericTypeName}} Instantiate()
                                 {
                                     throw new NotImplementedException();
                                 }
                                 """);
        }
        else
        {
            builder.AppendLine($$"""
                                 /// <seealso cref="ISerializationManager.CreateCopy"/>
                                 [Obsolete("Use ISerializationManager.CreateCopy instead")]
                                 public {{modifiers}} {{definition.GenericTypeName}} Instantiate()
                                 {
                                     return new {{definition.GenericTypeName}}();
                                 }
                                 """);
        }

        foreach (var @interface in GetImplicitDataDefinitionInterfaces(definition.Type, false))
        {
            var interfaceName = @interface.ToDisplayString();
            builder.AppendLine($$"""
                                 {{interfaceName}} {{interfaceName}}.Instantiate()
                                 {
                                     return Instantiate();
                                 }

                                 {{interfaceName}} ISerializationGenerated<{{interfaceName}}>.Instantiate()
                                 {
                                     return Instantiate();
                                 }
                                 """);
        }

        return builder.ToString();
    }

    // TODO serveronly? do we care? who knows!!
    private static StringBuilder CopyDataFields(DataDefinition definition)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"""
if (serialization.TryCustomCopy(this, ref target, hookCtx, {definition.HasHooks.ToString().ToLower()}, context))
    return;
""");

        var structCopier = new StringBuilder();
        foreach (var field in definition.Fields)
        {
            var type = field.Type;
            var typeName = type.ToDisplayString();
            if (IsMultidimensionalArray(type))
            {
                typeName = typeName.Replace("*", "");
            }

            var isNullableValueType = IsNullableValueType(type);
            var nonNullableTypeName = type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();
            if (isNullableValueType)
            {
                nonNullableTypeName = typeName.Substring(0, typeName.Length - 1);
            }

            var isClass = type.IsReferenceType || type.SpecialType == SpecialType.System_String;
            var isNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
            var nullableOverride = isClass && !isNullable ? ", true" : string.Empty;
            var name = field.Symbol.Name;
            var tempVarName = $"{name}Temp";
            var nullableValue = isNullableValueType ? ".Value" : string.Empty;
            var nullNotAllowed = isClass && !isNullable;

            if (field.CustomSerializer is { Serializer: var serializer, Type: var serializerType })
            {
                if (nullNotAllowed)
                {
                    builder.AppendLine($$"""
                                         if ({{name}} == null)
                                         {
                                             throw new NullNotAllowedException();
                                         }
                                         """);
                }

                builder.AppendLine($$"""
                                     {{typeName}} {{tempVarName}} = default!;
                                     """);

                if (isNullable || isNullableValueType)
                {
                    builder.AppendLine($$"""
                                         if ({{name}} == null)
                                         {
                                             {{tempVarName}} = null!;
                                         }
                                         else
                                         {
                                         """);
                }

                var serializerName = serializer.ToDisplayString();
                switch (serializerType)
                {
                    case Copier:
                        CopyToCustom(
                            builder,
                            nonNullableTypeName,
                            serializerName,
                            tempVarName,
                            name,
                            isNullable,
                            isClass,
                            isNullableValueType
                        );
                        break;
                    case CopyCreator:
                        CreateCopyCustom(
                            builder,
                            name,
                            tempVarName,
                            nonNullableTypeName,
                            serializerName,
                            nullableValue,
                            nullableOverride
                        );
                        break;
                }

                if (isNullable || isNullableValueType)
                {
                    builder.AppendLine("}");
                }

                if (definition.Type.IsValueType)
                {
                    structCopier.AppendLine($"{name} = {tempVarName}!,");
                }
                else
                {
                    builder.AppendLine($"target.{name} = {tempVarName}!;");
                }
            }
            else
            {
                builder.AppendLine($$"""
                                     {{typeName}} {{tempVarName}} = default!;
                                     """);

                if (nullNotAllowed)
                {
                    builder.AppendLine($$"""
                                         if ({{name}} == null)
                                         {
                                             throw new NullNotAllowedException();
                                         }
                                         """);
                }

                var hasHooks = ImplementsInterface(type, SerializationHooksNamespace) || !type.IsSealed;
                builder.AppendLine($$"""
                                     if (!serialization.TryCustomCopy(this.{{name}}, ref {{tempVarName}}, hookCtx, {{hasHooks.ToString().ToLower()}}, context))
                                     {
                                     """);

                if (CanBeCopiedByValue(field.Symbol, field.Type))
                {
                    builder.AppendLine($"{tempVarName} = {name};");
                }
                else if (IsDataDefinition(type) && !type.IsAbstract &&
                         type is not INamedTypeSymbol { TypeKind: TypeKind.Interface })
                {
                    var nullable = !type.IsValueType || IsNullableType(type);

                    if (nullable)
                    {
                        builder.AppendLine($$"""
                                           if ({{name}} == null)
                                           {
                                               {{tempVarName}} = null!;
                                           }
                                           else
                                           {
                                           """);
                    }

                    builder.AppendLine($$"""
                                         serialization.CopyTo({{name}}, ref {{tempVarName}}, hookCtx, context{{nullableOverride}});
                                         """);

                    if (nullable)
                    {
                        builder.AppendLine("}");
                    }
                }
                else
                {
                    builder.AppendLine($"{tempVarName} = serialization.CreateCopy({name}, hookCtx, context);");
                }

                builder.AppendLine("}");

                if (definition.Type.IsValueType)
                {
                    structCopier.AppendLine($"{name} = {tempVarName}!,");
                }
                else
                {
                    builder.AppendLine($"target.{name} = {tempVarName}!;");
                }
            }
        }

        if (definition.Type.IsValueType)
        {
            builder.AppendLine($$"""
                                target = target with
                                {
                                    {{structCopier}}
                                };
                                """);
        }

        return builder;
    }

    private static void CopyToCustom(
        StringBuilder builder,
        string typeName,
        string serializerName,
        string tempVarName,
        string varName,
        bool isNullable,
        bool isClass,
        bool isNullableValueType)
    {
        var newTemp = isNullable && isClass ? $"{tempVarName} ??= new();" : string.Empty;
        var nullableOverride = isClass ? ", true" : string.Empty;
        var nullableValue = isNullableValueType ? ".Value" : string.Empty;
        var nonNullableTypeName = typeName.EndsWith("?") ? typeName.Substring(0, typeName.Length - 1) : typeName;

        builder.AppendLine($$"""
                             {{nonNullableTypeName}} {{tempVarName}}CopyTo = default!;
                             {{newTemp}}
                             serialization.CopyTo<{{typeName}}, {{serializerName}}>(this.{{varName}}{{nullableValue}}, ref {{tempVarName}}CopyTo, hookCtx, context{{nullableOverride}});
                             {{tempVarName}} = {{tempVarName}}CopyTo;
                             """);
    }

    private static void CreateCopyCustom(
        StringBuilder builder,
        string varName,
        string tempVarName,
        string nonNullableTypeName,
        string serializerName,
        string nullableValue,
        string nullableOverride)
    {
        builder.AppendLine($$"""
                             {{tempVarName}} = serialization.CreateCopy<{{nonNullableTypeName}}, {{serializerName}}>(this.{{varName}}{{nullableValue}}, hookCtx, context{{nullableOverride}});
                             """);
    }
}
