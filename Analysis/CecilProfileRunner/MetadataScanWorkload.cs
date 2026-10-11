using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Rocks;

namespace CecilProfileRunner;

public record MetadataScanStats(
    int TypesScanned,
    int MethodsScanned,
    int FieldsScanned,
    int PropertiesScanned,
    int CustomAttributesScanned,
    int AttributeArgsUnpacked
);

public static class MetadataScanWorkload
{
    public static MetadataScanStats Execute(string assemblyPath)
    {
        var readerParams = AssemblyResolverHelper.CreateReaderParameters(assemblyPath);

        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);

        int typesScanned = 0;
        int methodsScanned = 0;
        int fieldsScanned = 0;
        int propertiesScanned = 0;
        int customAttributesScanned = 0;
        int attributeArgsUnpacked = 0;

        foreach (var type in assembly.MainModule.GetAllTypes())
        {
            typesScanned++;

            // Scan type-level attributes
            if (type.HasCustomAttributes)
            {
                foreach (var attr in type.CustomAttributes)
                {
                    customAttributesScanned++;
                    attributeArgsUnpacked += UnpackAttributeArgs(attr);
                }
            }

            // Scan fields
            if (type.HasFields)
            {
                foreach (var field in type.Fields)
                {
                    fieldsScanned++;
                    if (field.HasCustomAttributes)
                    {
                        foreach (var attr in field.CustomAttributes)
                        {
                            customAttributesScanned++;
                            attributeArgsUnpacked += UnpackAttributeArgs(attr);
                        }
                    }
                }
            }

            // Scan properties
            if (type.HasProperties)
            {
                foreach (var prop in type.Properties)
                {
                    propertiesScanned++;
                    if (prop.HasCustomAttributes)
                    {
                        foreach (var attr in prop.CustomAttributes)
                        {
                            customAttributesScanned++;
                            attributeArgsUnpacked += UnpackAttributeArgs(attr);
                        }
                    }
                }
            }

            // Scan methods & parameters
            if (type.HasMethods)
            {
                foreach (var method in type.Methods)
                {
                    methodsScanned++;

                    if (method.HasCustomAttributes)
                    {
                        foreach (var attr in method.CustomAttributes)
                        {
                            customAttributesScanned++;
                            attributeArgsUnpacked += UnpackAttributeArgs(attr);
                        }
                    }

                    if (method.HasParameters)
                    {
                        foreach (var param in method.Parameters)
                        {
                            if (param.HasCustomAttributes)
                            {
                                foreach (var attr in param.CustomAttributes)
                                {
                                    customAttributesScanned++;
                                    attributeArgsUnpacked += UnpackAttributeArgs(attr);
                                }
                            }
                        }
                    }
                }
            }
        }

        return new MetadataScanStats(
            typesScanned,
            methodsScanned,
            fieldsScanned,
            propertiesScanned,
            customAttributesScanned,
            attributeArgsUnpacked
        );
    }

    private static int UnpackAttributeArgs(CustomAttribute attr)
    {
        int count = 0;
        if (attr.HasConstructorArguments)
        {
            foreach (var arg in attr.ConstructorArguments)
            {
                count += CountArgument(arg);
            }
        }

        if (attr.HasProperties)
        {
            foreach (var namedArg in attr.Properties)
            {
                count += CountArgument(namedArg.Argument);
            }
        }

        if (attr.HasFields)
        {
            foreach (var namedArg in attr.Fields)
            {
                count += CountArgument(namedArg.Argument);
            }
        }

        return count;
    }

    private static int CountArgument(CustomAttributeArgument arg)
    {
        int count = 1;
        if (arg.Value is CustomAttributeArgument[] array)
        {
            foreach (var sub in array)
            {
                count += CountArgument(sub);
            }
        }
        return count;
    }
}
