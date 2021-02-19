using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Robust.Shared.Log;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Utility;

namespace Robust.Shared.Serialization.Manager
{
    public class SerializationDataDefinition
    {
        private delegate object PopulateDelegateSignature(object target, MappingDataNode mappingDataNode, IServ3Manager serv3Manager,
            ISerializationContext? context, object?[] defaultValues);

        private delegate MappingDataNode SerializeDelegateSignature(object obj, IServ3Manager serv3Manager,
            ISerializationContext? context, bool alwaysWrite, object?[] defaultValues);
        private delegate object PushInheritanceDelegateSignature(object source, object target,
            IServ3Manager serv3Manager, object?[] defaultValues);
        public delegate object CopyDelegateSignature(object source, object target,
            IServ3Manager serv3Manager);

        public readonly Type Type;

        private readonly FieldDefinition[] _baseFieldDefinitions;
        private readonly object?[] _defaultValues;

        private readonly PopulateDelegateSignature _populateDelegate;

        private readonly SerializeDelegateSignature _serializeDelegate;

        private readonly PushInheritanceDelegateSignature _pushInheritanceDelegate;

        public readonly CopyDelegateSignature _copyDelegate;

        public object InvokePopulateDelegate(object target, MappingDataNode mappingDataNode, IServ3Manager serv3Manager,
            ISerializationContext? context) =>
            _populateDelegate(target, mappingDataNode, serv3Manager, context, _defaultValues);

        public MappingDataNode InvokeSerializeDelegate(object obj, IServ3Manager serv3Manager, ISerializationContext? context, bool alwaysWrite) =>
            _serializeDelegate(obj, serv3Manager, context, alwaysWrite, _defaultValues);

        public object InvokePushInheritanceDelegate(object source, object target,
            IServ3Manager serv3Manager) =>
            _pushInheritanceDelegate(source, target, serv3Manager, _defaultValues);

        public object InvokeCopyDelegate(object source, object target, IServ3Manager serv3Manager) =>
            _copyDelegate(source, target, serv3Manager);


        public bool CanCallWith(object obj) => Type.IsInstanceOfType(obj);

        public SerializationDataDefinition(Type type)
        {
            Type = type;
            var dummyObj = Activator.CreateInstance(type)!;

            var fieldDefs = new List<FieldDefinition>();
            foreach (var abstractFieldInfo in type.GetAllPropertiesAndFields())
            {
                if(abstractFieldInfo.DeclaringType != type) continue;
                var attr = abstractFieldInfo.GetCustomAttribute<DataFieldAttribute>();
                if(attr == null) continue;
                if (abstractFieldInfo is SpecificPropertyInfo propertyInfo)
                {
                    if (propertyInfo.PropertyInfo.GetMethod == null)
                    {
                        Logger.ErrorS("SerV3", $"Property {propertyInfo} is annotated with DataFieldAttribute but has no getter");
                        continue;
                    }else if (!attr.ReadOnly && propertyInfo.PropertyInfo.SetMethod == null)
                    {
                        Logger.ErrorS("SerV3", $"Property {propertyInfo} is annotated with DataFieldAttribute as non-readonly but has no setter");
                        continue;
                    }
                }
                fieldDefs.Add(new FieldDefinition(attr, abstractFieldInfo.GetValue(dummyObj), abstractFieldInfo));
            }

            var duplicates = fieldDefs.Where(f =>
                fieldDefs.Count(df => df.Attribute.Tag == f.Attribute.Tag) > 1).Select(f => f.Attribute.Tag).ToList();
            if (duplicates.Count > 0)
                throw new ArgumentException($"Duplicate Datafield-Tags found in {Type}: {string.Join(",", duplicates)}");


            var fields = fieldDefs;
            //todo paul write a test for this
            fields.Sort((a, b) => a.Attribute.Priority.CompareTo(a.Attribute.Priority));
            _baseFieldDefinitions = fields.ToArray();
            _defaultValues = fieldDefs.Select(f => f.DefaultValue).ToArray();

            _populateDelegate = EmitPopulateDelegate();
            _serializeDelegate = EmitSerializeDelegate();
            _pushInheritanceDelegate = EmitPushInheritanceDelegate();
            _copyDelegate = EmitCopyDelegate();
        }

        private PopulateDelegateSignature EmitPopulateDelegate()
        {
            var dynamicMethod = new DynamicMethod(
                $"_populateDelegate<>{Type}",
                typeof(object),
                new[] {typeof(object), typeof(MappingDataNode), typeof(IServ3Manager), typeof(ISerializationContext), typeof(object?[])},
                Type,
                true);
            dynamicMethod.DefineParameter(1, ParameterAttributes.In, "obj");
            dynamicMethod.DefineParameter(2, ParameterAttributes.In, "mapping");
            dynamicMethod.DefineParameter(3, ParameterAttributes.In, "serializationManager");
            dynamicMethod.DefineParameter(4, ParameterAttributes.In, "serializationContext");
            dynamicMethod.DefineParameter(5, ParameterAttributes.In, "defaultValues");
            var generator = dynamicMethod.GetRobustGen();

            for (var i = 0; i < _baseFieldDefinitions.Length; i++)
            {
                var fieldDefinition = _baseFieldDefinitions[i];
                var idc = generator.DeclareLocal(fieldDefinition.FieldType).LocalIndex;
                generator.EmitPopulateField(fieldDefinition, idc, i);
            }

            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ret);

            return dynamicMethod.CreateDelegate<PopulateDelegateSignature>();
        }

        private SerializeDelegateSignature EmitSerializeDelegate()
        {
            var dynamicMethod = new DynamicMethod(
                $"_serializeDelegate<>{Type}",
                typeof(MappingDataNode),
                new[] {typeof(object), typeof(IServ3Manager), typeof(ISerializationContext), typeof(bool), typeof(object?[])},
                Type,
                true);
            dynamicMethod.DefineParameter(1, ParameterAttributes.In, "obj");
            dynamicMethod.DefineParameter(2, ParameterAttributes.In, "serializationManager");
            dynamicMethod.DefineParameter(3, ParameterAttributes.In, "serializationContext");
            dynamicMethod.DefineParameter(4, ParameterAttributes.In, "alwaysWrite");
            dynamicMethod.DefineParameter(5, ParameterAttributes.In, "defaultValues");
            var generator = dynamicMethod.GetRobustGen();

            var loc = generator.DeclareLocal(typeof(MappingDataNode));
            Debug.Assert(loc.LocalIndex == 0);
            generator.Emit(OpCodes.Newobj, typeof(MappingDataNode).GetConstructor(new Type[0])!);
            generator.Emit(OpCodes.Stloc_0);

            for (var i = _baseFieldDefinitions.Length-1; i >= 0; i--)
            {
                var fieldDefinition = _baseFieldDefinitions[i];
                generator.EmitSerializeField(fieldDefinition, i);
            }

            generator.Emit(OpCodes.Ldloc_0);
            generator.Emit(OpCodes.Ret);

            return dynamicMethod.CreateDelegate<SerializeDelegateSignature>();
        }

        private PushInheritanceDelegateSignature EmitPushInheritanceDelegate()
        {
            var dynamicMethod = new DynamicMethod(
                $"_serializeDelegate<>{Type}",
                typeof(object),
                new[] {typeof(object), typeof(object), typeof(IServ3Manager), typeof(object?[])},
                Type,
                true);
            dynamicMethod.DefineParameter(1, ParameterAttributes.In, "source");
            dynamicMethod.DefineParameter(2, ParameterAttributes.In, "target");
            dynamicMethod.DefineParameter(3, ParameterAttributes.In, "serializationManager");
            dynamicMethod.DefineParameter(4, ParameterAttributes.In, "defaultValues");
            var generator = dynamicMethod.GetRobustGen();

            for (var i = 0; i < _baseFieldDefinitions.Length; i++)
            {
                var fieldDefinition = _baseFieldDefinitions[i];
                generator.EmitPushInheritanceField(fieldDefinition, i);
            }

            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ret);

            return dynamicMethod.CreateDelegate<PushInheritanceDelegateSignature>();
        }

        private CopyDelegateSignature EmitCopyDelegate()
        {
            var dynamicMethod = new DynamicMethod(
                $"_populateDelegate<>{Type}",
                typeof(object),
                new[] {typeof(object), typeof(object), typeof(IServ3Manager)},
                Type,
                true);
            dynamicMethod.DefineParameter(1, ParameterAttributes.In, "source");
            dynamicMethod.DefineParameter(2, ParameterAttributes.In, "target");
            dynamicMethod.DefineParameter(3, ParameterAttributes.In, "serializationManager");
            var generator = dynamicMethod.GetRobustGen();

            foreach (var fieldDefinition in _baseFieldDefinitions)
            {
                generator.EmitCopy(0, fieldDefinition.FieldInfo, 1, fieldDefinition.FieldInfo, 2);
            }

            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ret);

            return dynamicMethod.CreateDelegate<CopyDelegateSignature>();
        }

        public class FieldDefinition
        {
            public readonly DataFieldAttribute Attribute;
            public readonly object? DefaultValue;
            public readonly AbstractFieldInfo FieldInfo;

            public FieldDefinition(DataFieldAttribute attr, object? defaultValue, AbstractFieldInfo fieldInfo)
            {
                Attribute = attr;
                DefaultValue = defaultValue;
                FieldInfo = fieldInfo;
            }

            public Type FieldType => FieldInfo.FieldType;
        }
    }
}
