﻿using System.Linq;
using NUnit.Framework;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Manager.DataDefinition;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Utility;

// ReSharper disable UnassignedGetOnlyAutoProperty
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AccessToStaticMemberViaDerivedType

namespace Robust.UnitTesting.Shared.Serialization
{
    public class PropertyAndFieldDefinitionTest : SerializationTest
    {
        private const string GetOnlyPropertyName = "GetOnlyProperty";
        private const string GetOnlyPropertyFieldTargetedName = "GetOnlyPropertyFieldTargeted";
        private const string GetAndSetPropertyName = "GetAndSetProperty";
        private const string FieldName = "Field";
        private const string GetOnlyPropertyWithOtherAttributeFieldTargetedName =
            "GetOnlyPropertyWithOtherAttributeFieldTargeted";
        private const string GetOnlyPropertyFieldTargetedAndOtherAttributeName =
            "GetOnlyPropertyFieldTargetedAndOtherAttribute";

        [Test]
        public void ParityTest()
        {
            var mapping = new MappingDataNode();
            mapping.AddNode(GetOnlyPropertyName, new ValueDataNode("5"));
            mapping.AddNode(GetOnlyPropertyFieldTargetedName, new ValueDataNode("10"));
            mapping.AddNode(GetAndSetPropertyName, new ValueDataNode("15"));
            mapping.AddNode(FieldName, new ValueDataNode("20"));
            mapping.AddNode(GetOnlyPropertyWithOtherAttributeFieldTargetedName, new ValueDataNode("25"));
            mapping.AddNode(GetOnlyPropertyFieldTargetedAndOtherAttributeName, new ValueDataNode("30"));

            var definition = Serialization.ReadValue<PropertyAndFieldDefinitionTestDefinition>(mapping);

            Assert.NotNull(definition);

            // Set only property with backing field, property targeted
            Assert.That(definition!.GetOnlyProperty, Is.EqualTo(5));

            var backingField = definition.GetType().GetBackingField(GetOnlyPropertyName);
            Assert.NotNull(backingField);

            var backingFieldValue = backingField!.GetValue(definition);
            Assert.That(backingFieldValue, Is.EqualTo(5));

            // Set only property with backing field, field targeted
            Assert.That(definition.GetOnlyPropertyFieldTargeted, Is.EqualTo(10));

            // Get and set property with backing field, property targeted
            Assert.That(definition.GetAndSetProperty, Is.EqualTo(15));

            // Field
            Assert.That(definition.Field, Is.EqualTo(20));

            // Get only property with other attribute field targeted
            Assert.That(definition.GetOnlyPropertyWithOtherAttributeFieldTargeted, Is.EqualTo(25));

            var property = definition.GetType().GetProperty(GetOnlyPropertyWithOtherAttributeFieldTargetedName);
            Assert.NotNull(property);

            var propertyInfo = new SpecificPropertyInfo(property!);
            Assert.NotNull(propertyInfo.GetCustomAttribute<DataFieldAttribute>());
            Assert.NotNull(propertyInfo.GetCustomAttribute<AlwaysPushInheritanceAttribute>());

            // We check for the property info properly finding field targeted attributes as
            // well, otherwise we run the risk of the data field being targeted to the
            // property but an additional attribute like AlwaysPushInheritance being targeted
            // to the field, as was the case in EntityPrototype.
            // And I don't want to debug that ever again.
            Assert.NotNull(propertyInfo.DeclaringType);

            var dataDefinition = ((SerializationManager) Serialization).GetDataDefinition(propertyInfo.DeclaringType!);
            Assert.NotNull(dataDefinition);

            var alwaysPushDataField = propertyInfo.GetCustomAttribute<DataFieldAttribute>();
            var propertyDefinition =
                dataDefinition!.BaseFieldDefinitions.Single(e => e.Attribute.Equals(alwaysPushDataField));
            var inheritanceBehaviour = propertyDefinition.InheritanceBehaviour;
            Assert.That(inheritanceBehaviour, Is.EqualTo(InheritanceBehaviour.Always));

            // Get only property with backing field and another attribute targeted to the property
            Assert.That(definition.GetOnlyPropertyFieldTargetedAndOtherAttribute, Is.EqualTo(30));

            property = definition.GetType().GetProperty(GetOnlyPropertyFieldTargetedAndOtherAttributeName);
            Assert.NotNull(property);

            propertyInfo = new SpecificPropertyInfo(property!);
            Assert.NotNull(propertyInfo.GetCustomAttribute<DataFieldAttribute>());
            Assert.NotNull(propertyInfo.GetCustomAttribute<NeverPushInheritanceAttribute>());

            var neverPushDataField = new SpecificPropertyInfo(property!).GetCustomAttribute<DataFieldAttribute>();
            propertyDefinition =
                dataDefinition!.BaseFieldDefinitions.Single(e => e.Attribute.Equals(neverPushDataField));
            inheritanceBehaviour = propertyDefinition.InheritanceBehaviour;
            dataDefinition = ((SerializationManager) Serialization).GetDataDefinition(property!.DeclaringType!);
            Assert.NotNull(dataDefinition);
            Assert.That(inheritanceBehaviour, Is.EqualTo(InheritanceBehaviour.Never));
        }

        [Robust.Shared.Serialization.Manager.Attributes.DataDefinition]
        public class PropertyAndFieldDefinitionTestDefinition
        {
            [DataField(GetOnlyPropertyName)]
            public int GetOnlyProperty { get; }

            [field: DataField(GetOnlyPropertyFieldTargetedName)]
            public int GetOnlyPropertyFieldTargeted { get; }

            [DataField(GetAndSetPropertyName)]
            public int GetAndSetProperty { get; set; }

            [DataField(FieldName)]
            // ReSharper disable once UnassignedField.Global
            public int Field;

            [DataField(GetOnlyPropertyWithOtherAttributeFieldTargetedName)]
            [field: AlwaysPushInheritance]
            public int GetOnlyPropertyWithOtherAttributeFieldTargeted { get; }

            [field: DataField(GetOnlyPropertyFieldTargetedAndOtherAttributeName)]
            [NeverPushInheritance]
            public int GetOnlyPropertyFieldTargetedAndOtherAttribute { get; }
        }
    }
}
