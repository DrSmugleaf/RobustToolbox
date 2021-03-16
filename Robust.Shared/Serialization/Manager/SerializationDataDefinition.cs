using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.Result;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Utility;

namespace Robust.Shared.Serialization.Manager
{
    public class SerializationDataDefinition
    {
        private delegate DeserializedFieldEntry[] DeserializeDelegate(MappingDataNode mappingDataNode,
            ISerializationManager serializationManager, ISerializationContext? context, bool skipHook);

        private delegate DeserializationResult PopulateDelegateSignature(object target, DeserializedFieldEntry[] deserializationResults, object?[] defaultValues);

        private delegate MappingDataNode SerializeDelegateSignature(object obj, ISerializationManager serializationManager,
            ISerializationContext? context, bool alwaysWrite, object?[] defaultValues);

        private delegate object CopyDelegateSignature(object source, object target,
            ISerializationManager serializationManager, ISerializationContext? context);

        public readonly Type Type;

        private readonly string[] _duplicates;
        private readonly FieldDefinition[] _baseFieldDefinitions;
        private readonly object?[] _defaultValues;

        private readonly DeserializeDelegate _deserializeDelegate;

        private readonly PopulateDelegateSignature _populateDelegate;

        private readonly SerializeDelegateSignature _serializeDelegate;

        private readonly CopyDelegateSignature _copyDelegate;

        public DeserializationResult InvokePopulateDelegate(object target, DeserializedFieldEntry[] fields) =>
            _populateDelegate(target, fields, _defaultValues);

        public DeserializationResult InvokePopulateDelegate(object target, MappingDataNode mappingDataNode, ISerializationManager serializationManager,
            ISerializationContext? context, bool skipHook)
        {
            var fields = _deserializeDelegate(mappingDataNode, serializationManager, context, skipHook);
            return _populateDelegate(target, fields, _defaultValues);
        }

        public MappingDataNode InvokeSerializeDelegate(object obj, ISerializationManager serializationManager, ISerializationContext? context, bool alwaysWrite) =>
            _serializeDelegate(obj, serializationManager, context, alwaysWrite, _defaultValues);

        public object InvokeCopyDelegate(object source, object target, ISerializationManager serializationManager, ISerializationContext? context) =>
            _copyDelegate(source, target, serializationManager, context);

        public bool CanCallWith(object obj) => Type.IsInstanceOfType(obj);

        public SerializationDataDefinition(Type type)
        {
            Type = type;
            var dummyObj = Activator.CreateInstance(type)!;

            var fieldDefs = new List<FieldDefinition>();

            foreach (var abstractFieldInfo in type.GetAllPropertiesAndFields())
            {
                var attr = abstractFieldInfo.GetCustomAttribute<DataFieldAttribute>();

                if (attr == null) continue;

                if (abstractFieldInfo is SpecificPropertyInfo propertyInfo)
                {
                    // We only want the most overriden instance of a property for the type we are working with
                    if (!propertyInfo.IsMostOverridden(type))
                    {
                        continue;
                    }

                    if (propertyInfo.PropertyInfo.GetMethod == null)
                    {
                        Logger.ErrorS(SerializationManager.LogCategory, $"Property {propertyInfo} is annotated with DataFieldAttribute but has no getter");
                        continue;
                    }
                    else if (!attr.ReadOnly && propertyInfo.PropertyInfo.SetMethod == null)
                    {
                        Logger.ErrorS(SerializationManager.LogCategory, $"Property {propertyInfo} is annotated with DataFieldAttribute as non-readonly but has no setter");
                        continue;
                    }
                }

                var inheritanceBehaviour = InheritanceBehaviour.Default;
                if (abstractFieldInfo.GetCustomAttribute<AlwaysPushInheritanceAttribute>() != null)
                {
                    inheritanceBehaviour = InheritanceBehaviour.Always;
                }
                else if (abstractFieldInfo.GetCustomAttribute<NeverPushInheritanceAttribute>() != null)
                {
                    inheritanceBehaviour = InheritanceBehaviour.Never;
                }

                fieldDefs.Add(new FieldDefinition(attr, abstractFieldInfo.GetValue(dummyObj), abstractFieldInfo, inheritanceBehaviour));
            }

            _duplicates = fieldDefs
                .Where(f =>
                    fieldDefs.Count(df => df.Attribute.Tag == f.Attribute.Tag) > 1)
                .Select(f => f.Attribute.Tag)
                .Distinct()
                .ToArray();

            var fields = fieldDefs;
            fields.Sort((a, b) => b.Attribute.Priority.CompareTo(a.Attribute.Priority));
            _baseFieldDefinitions = fields.ToArray();
            _defaultValues = fieldDefs.Select(f => f.DefaultValue).ToArray();

            _deserializeDelegate = EmitDeserializationDelegate();
            _populateDelegate = EmitPopulateDelegate();
            _serializeDelegate = EmitSerializeDelegate();
            _copyDelegate = EmitCopyDelegate();
        }

        public int DataFieldCount => _baseFieldDefinitions.Length;

        public bool TryGetDuplicates([NotNullWhen(true)] out string[] duplicates)
        {
            duplicates = _duplicates;
            return duplicates.Length > 0;
        }

        public ValidationNode Validate(ISerializationManager serializationManager, MappingDataNode node, ISerializationContext? context)
        {
            var validatedMapping = new Dictionary<ValidationNode, ValidationNode>();

            foreach (var (key, val) in node.Children)
            {
                if (key is not ValueDataNode valueDataNode)
                {
                    validatedMapping.Add(new ErrorNode(key, "Key not ValueDataNode."), new InconclusiveNode(val));
                    continue;
                }

                var field = _baseFieldDefinitions.FirstOrDefault(f => f.Attribute.Tag == valueDataNode.Value);
                if (field == null)
                {
                    var error = new ErrorNode(
                        key,
                        $"Field \"{valueDataNode.Value}\" not found in \"{Type}\".",
                        false);

                    validatedMapping.Add(error, new InconclusiveNode(val));
                    continue;
                }

                var keyValidated = serializationManager.ValidateNode(typeof(string), key, context);
                ValidationNode valValidated = field.Attribute.CustomTypeSerializer != null
                    ? serializationManager.ValidateNodeWithCustomTypeSerializer(field.FieldType,
                        field.Attribute.CustomTypeSerializer, val, context)
                    : serializationManager.ValidateNode(field.FieldType, val, context);

                validatedMapping.Add(keyValidated, valValidated);
            }

            return new ValidatedMappingNode(validatedMapping);
        }

        private DeserializeDelegate EmitDeserializationDelegate()
        {
            DeserializedFieldEntry[] DeserializationDelegate(MappingDataNode mappingDataNode,
                ISerializationManager serializationManager, ISerializationContext? serializationContext, bool skipHook)
            {
                var mappedInfo = new DeserializedFieldEntry[_baseFieldDefinitions.Length];

                for (var i = 0; i < _baseFieldDefinitions.Length; i++)
                {
                    var fieldDefinition = _baseFieldDefinitions[i];

                    if (fieldDefinition.Attribute.ServerOnly && !IoCManager.Resolve<INetManager>().IsServer)
                    {
                        mappedInfo[i] = new DeserializedFieldEntry(false, fieldDefinition.InheritanceBehaviour);
                        continue;
                    }

                    var mapped = mappingDataNode.HasNode(fieldDefinition.Attribute.Tag);

                    if (!mapped)
                    {
                        mappedInfo[i] = new DeserializedFieldEntry(mapped, fieldDefinition.InheritanceBehaviour);
                        continue;
                    }

                    var type = fieldDefinition.FieldType;
                    var node = mappingDataNode.GetNode(fieldDefinition.Attribute.Tag);
                    var result = fieldDefinition.Attribute.CustomTypeSerializer != null
                        ? serializationManager.ReadWithTypeSerializer(type,
                            fieldDefinition.Attribute.CustomTypeSerializer, node, serializationContext,
                            skipHook)
                        : serializationManager.Read(type, node, serializationContext, skipHook);

                    var entry = new DeserializedFieldEntry(mapped, fieldDefinition.InheritanceBehaviour, result);
                    mappedInfo[i] = entry;
                }

                return mappedInfo;
            }

            return DeserializationDelegate;
        }

        // TODO PAUL SERV3: Turn this back into IL once it is fixed
        private PopulateDelegateSignature EmitPopulateDelegate()
        {
            DeserializationResult PopulateDelegate(
                object target,
                DeserializedFieldEntry[] deserializedFields,
                object?[] defaultValues)
            {
                for (var i = 0; i < _baseFieldDefinitions.Length; i++)
                {
                    var res = deserializedFields[i];
                    if (!res.Mapped) continue;

                    var fieldDefinition = _baseFieldDefinitions[i];

                    var defValue = defaultValues[i];

                    if (Equals(res.Result?.RawValue, defValue))
                    {
                        continue;
                    }

                    fieldDefinition.FieldInfo.SetValue(target, res.Result?.RawValue);
                }

                return DeserializationResult.Definition(target, deserializedFields);
            }

            return PopulateDelegate;
        }

        // TODO PAUL SERV3: Turn this back into IL once it is fixed
        private SerializeDelegateSignature EmitSerializeDelegate()
        {
            MappingDataNode SerializeDelegate(
                object obj,
                ISerializationManager manager,
                ISerializationContext? context,
                bool alwaysWrite,
                object?[] defaultValues)
            {
                var mapping = new MappingDataNode();

                for (var i = _baseFieldDefinitions.Length - 1; i >= 0; i--)
                {
                    var fieldDefinition = _baseFieldDefinitions[i];

                    if (fieldDefinition.Attribute.ReadOnly)
                    {
                        continue;
                    }

                    if (fieldDefinition.Attribute.ServerOnly &&
                        !IoCManager.Resolve<INetManager>().IsServer)
                    {
                        continue;
                    }

                    var info = fieldDefinition.FieldInfo;
                    var value = info.GetValue(obj);

                    if (value == null)
                    {
                        continue;
                    }

                    if (!fieldDefinition.Attribute.Required &&
                        !alwaysWrite &&
                        Equals(value, defaultValues[i]))
                    {
                        continue;
                    }

                    var type = fieldDefinition.FieldType;
                    var node = fieldDefinition.Attribute.CustomTypeSerializer != null
                        ? manager.WriteWithTypeSerializer(type, fieldDefinition.Attribute.CustomTypeSerializer,
                            value, alwaysWrite, context)
                        : manager.WriteValue(type, value, alwaysWrite, context);

                    mapping[fieldDefinition.Attribute.Tag] = node;
                }

                return mapping;
            }

            return SerializeDelegate;
        }

        // TODO PAUL SERV3: Turn this back into IL once it is fixed
        // todo paul add skiphooks
        private CopyDelegateSignature EmitCopyDelegate()
        {
            object PopulateDelegate(
                object source,
                object target,
                ISerializationManager manager,
                ISerializationContext? context)
            {
                foreach (var field in _baseFieldDefinitions)
                {
                    var info = field.FieldInfo;
                    var sourceValue = info.GetValue(source);
                    var targetValue = info.GetValue(target);

                    object? copy;
                    if (sourceValue != null && targetValue != null && TypeHelpers.SelectCommonType(sourceValue.GetType(), targetValue.GetType()) == null)
                    {
                        copy = manager.CreateCopy(sourceValue, context);
                    }else
                    {
                        copy = field.Attribute.CustomTypeSerializer != null
                            ? manager.CopyWithTypeSerializer(field.Attribute.CustomTypeSerializer, sourceValue, targetValue,
                                context)
                            : manager.Copy(sourceValue, targetValue, context);
                    }

                    info.SetValue(target, copy);
                }

                return target;
            }

            return PopulateDelegate;
        }

        public class FieldDefinition
        {
            public readonly DataFieldAttribute Attribute;
            public readonly object? DefaultValue;
            public readonly AbstractFieldInfo FieldInfo;
            public readonly InheritanceBehaviour InheritanceBehaviour;

            public FieldDefinition(DataFieldAttribute attr, object? defaultValue, AbstractFieldInfo fieldInfo, InheritanceBehaviour inheritanceBehaviour)
            {
                Attribute = attr;
                DefaultValue = defaultValue;
                FieldInfo = fieldInfo;
                InheritanceBehaviour = inheritanceBehaviour;
            }

            public Type FieldType => FieldInfo.FieldType;
        }

        public enum InheritanceBehaviour : byte
        {
            Default,
            Always,
            Never
        }
    }
}
