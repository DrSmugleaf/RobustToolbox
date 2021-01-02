using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using Robust.Shared.GameObjects;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.Interfaces.Reflection;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Robust.Shared.Prototypes
{
    public class ComponentDataManager : IComponentDataManager
    {
        [Dependency] private readonly IComponentFactory _componentFactory = default!;
        [Dependency] private readonly IReflectionManager _reflectionManager = default!;

        private Dictionary<Type, Type> _customDataClasses = new ();
        private Dictionary<Type, Dictionary<string, IYamlFieldDefinition>> _customYamlFields = new ();

        public void RegisterCustomDataClasses()
        {
            var iComponent = typeof(IComponent);

            foreach (var type in _reflectionManager.FindTypesWithAttribute<CustomDataClassAttribute>())
            {
                if (!iComponent.IsAssignableFrom(type))
                {
                    Logger.Error("Type {0} has CustomDataClassAttribute but does not implement IComponent.", type);
                    continue;
                }

                var attribute = (CustomDataClassAttribute?)Attribute.GetCustomAttribute(type, typeof(CustomDataClassAttribute));
                if (attribute == null)
                {
                    Logger.Error("Type {0}: CustomDataClassAttribute could not be found.", type);
                    continue;
                }

                var customDataClass = attribute.ClassName;

                var fields = new Dictionary<string, IYamlFieldDefinition>();
                foreach (var fieldInfo in customDataClass.GetFields())
                {
                    var fieldDef = GetFieldDefinition(fieldInfo, true);
                    if(fieldDef == null) continue;
                    fields.Add(fieldDef.Tag, fieldDef);
                }

                foreach (var fieldInfo in customDataClass.GetProperties())
                {
                    var fieldDef = GetFieldDefinition(fieldInfo, true, true);
                    if(fieldDef == null) continue;
                    fields.Add(fieldDef.Tag, fieldDef);
                }

                if(fields.Count != 0)
                    _customYamlFields.Add(type, fields);

                _customDataClasses.Add(type, customDataClass);
            }
        }

        private Dictionary<Type, Type> _cachedAutoGeneratedTypes = new();

        private Type GetComponentDataType(string compName)
        {
            var compType = _componentFactory.GetRegistration(compName).Type;
            return GetComponentDataType(compType);
        }

        private Type GetComponentDataType(Type? compType)
        {
            if(compType ==  null)
                throw new InvalidProgramException($"No autogenerated Dataclass found for component {compType}"); //this should never throw since Component has a dataclass

            if (_customDataClasses.TryGetValue(compType, out var customDataClass))
            {
                return customDataClass;
            }

            if (_cachedAutoGeneratedTypes.TryGetValue(compType, out var dataClass))
            {
                return dataClass;
            }

            var generatedType = _reflectionManager.GetType($"{compType.Namespace}.{compType.Name}_AUTODATA");
            if (generatedType == null) return GetComponentDataType(compType.BaseType);
            _cachedAutoGeneratedTypes.Add(compType, generatedType);
            return generatedType;
        }

        #region Populating

        private IYamlFieldDefinition GetCustomField(Type type, string tag)
        {
            if (_customYamlFields.TryGetValue(type, out var fields) && fields.TryGetValue(tag, out var field))
                return field;
            if (type.BaseType == null) throw new Exception($"Custom Yamlfield {tag} not found");
            return GetCustomField(type.BaseType, tag);
        }

        public void PopulateComponent(IComponent comp, ComponentData values)
        {
            var def = GetComponentDataDefinition(comp.Name);

            foreach (var fieldDefinition in def)
            {
                object? value;
                if (fieldDefinition.IsCustom)
                {
                    var sourceField = GetCustomField(comp.GetType(), fieldDefinition.Tag);
                    value = sourceField.GetValue(values);
                }
                else
                {
                    value = values.GetValue(fieldDefinition.Tag);
                }
                if(value == null) continue;
                fieldDefinition.SetValue(comp, value);
            }
        }


        public void PushInheritance(string compName, ComponentData source, ComponentData target)
        {
            var def = GetComponentDataDefinition(compName);

            foreach (var tag in def)
            {
                if(target.GetValue(tag.Tag) == null)
                    target.SetValue(tag.Tag, source.GetValue(tag.Tag));
            }
        }

        #endregion

        #region Parsing

        private readonly Dictionary<string, IYamlFieldDefinition[]> _dataDefinitions = new();

        public YamlMappingNode? SerializeNonDefaultComponentData(IComponent comp, YamlObjectSerializer.Context? context = null)
        {
            var mapping = new YamlMappingNode();
            var ser = YamlObjectSerializer.NewWriter(mapping, context);

            ComponentData data = GetEmptyComponentData(comp.Name);
            var def = GetComponentDataDefinition(comp.Name);
            foreach (var fieldDefinition in def)
            {
                var value = fieldDefinition.GetValue(comp);
                if(Equals(value, fieldDefinition.DefaultValue)) continue;
                if (fieldDefinition.IsCustom)
                {
                    var customField = GetCustomField(comp.GetType(), fieldDefinition.Tag);
                    customField.SetValue(data, value);
                }
                else
                {
                    data.SetValue(fieldDefinition.Tag, value);
                }
            }
            data.ExposeData(ser);

            if (mapping.Children.Count != 0)
            {
                mapping.Add("type", comp.Name);
                return mapping;
            }

            return null;
        }

        public IYamlFieldDefinition[] GetComponentDataDefinition(string compName)
        {
            if (!_dataDefinitions.TryGetValue(compName, out var dataDefinition))
            {
                dataDefinition = GenerateAndCacheDataDefinition(compName);
            }

            return dataDefinition;
        }

        public ComponentData GetEmptyComponentData(string compName)
        {
            var compData = (ComponentData?)Activator.CreateInstance(GetComponentDataType(compName));
            if (compData == null)
                throw new Exception($"Failed creating instance of dataclass of component {compName}");

            return compData;
        }

        private IYamlFieldDefinition[] GenerateAndCacheDataDefinition(string compName)
        {
            var compType = _componentFactory.GetRegistration(compName).Type;
            var dataDef = new List<IYamlFieldDefinition>();
            foreach (var fieldInfo in compType.GetAllFields())
            {
                var fieldDef = GetFieldDefinition(fieldInfo);
                if(fieldDef == null) continue;
                dataDef.Add(fieldDef);
            }

            foreach (var propertyInfo in compType.GetAllProperties())
            {
                var fieldDef = GetFieldDefinition(propertyInfo);
                if(fieldDef == null) continue;
                dataDef.Add(fieldDef);
            }

            var res = dataDef.ToArray();

            _dataDefinitions.Add(compName, res);

            return res;
        }

        private IYamlFieldDefinition? GetFieldDefinition(PropertyInfo info, bool onlyCustom = false, bool needsGet = false)
        {
            var yamlFieldAttr =
                (YamlFieldAttribute?) Attribute.GetCustomAttribute(info, typeof(YamlFieldAttribute));
            var customYamlAttr =
                (CustomYamlFieldAttribute?) Attribute.GetCustomAttribute(info,
                    typeof(CustomYamlFieldAttribute));
            if (yamlFieldAttr != null && customYamlAttr != null)
            {
                throw new Exception($"Property {info} is annotated with both YamlFieldAttribute and CustomYamlFieldAttribute");
            }
            var tag =
                yamlFieldAttr?.Tag ??
                customYamlAttr?.Tag;
            if (tag == null) return null;

            if (!info.CanWrite)
            {
                throw new Exception(
                    $"Property {info} is annotated as a YamlField but does not have a setter");
            }

            if (!info.CanRead && needsGet)
            {
                throw new Exception($"Property {info} does not have a required getter.");
            }

            return onlyCustom && customYamlAttr == null ? null : new YamlPropertyDefinition(tag, info, customYamlAttr != null);
        }

        private IYamlFieldDefinition? GetFieldDefinition(FieldInfo info, bool onlyCustom = false)
        {
            var yamlFieldAttr =
                (YamlFieldAttribute?) Attribute.GetCustomAttribute(info, typeof(YamlFieldAttribute));
            var customYamlAttr =
                (CustomYamlFieldAttribute?) Attribute.GetCustomAttribute(info,
                    typeof(CustomYamlFieldAttribute));
            if (yamlFieldAttr != null && customYamlAttr != null)
            {
                throw new Exception($"Property {info} is annotated with both YamlFieldAttribute and CustomYamlFieldAttribute");
            }
            var tag =
                yamlFieldAttr?.Tag ??
                customYamlAttr?.Tag;
            if (tag == null) return null;

            return onlyCustom && customYamlAttr == null ? null : new YamlFieldDefinition(tag, info, customYamlAttr != null);
        }

        public ComponentData ParseComponentData(string compName, YamlMappingNode mapping, YamlObjectSerializer.Context? context = null)
        {
            //var dataDefinition = GetComponentDataDefinition(compName);
            var ser = YamlObjectSerializer.NewReader(mapping, context);

            var data = GetEmptyComponentData(compName);
            data.ExposeData(ser);

            //todo if (mapping.Children.Count != 0)
            //    throw new PrototypeLoadException($"Not all values of component {compName} were consumed (Not consumed: {string.Join(',',mapping.Children.Select(n => n.Key))})");

            return data;
        }

        #endregion
    }
}
