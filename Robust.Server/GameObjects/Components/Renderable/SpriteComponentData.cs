using System.Collections.Generic;
using Robust.Shared.GameObjects.Components.Renderable;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Robust.Server.GameObjects
{
    public partial class SpriteComponentData
    {
        [CustomYamlField("layers")]
        private List<SharedSpriteComponent.PrototypeLayerData> Layers = new();

        public override void ExposeData(ObjectSerializer serializer)
        {
            base.ExposeData(serializer);

            // TODO: Writing?
            if (!serializer.Reading)
            {
                return;
            }

            var layerData =
                serializer.ReadDataField<List<SharedSpriteComponent.PrototypeLayerData>>("layers", new List<SharedSpriteComponent.PrototypeLayerData>());

            if(layerData.Count == 0){
                var baseState = serializer.ReadDataField<string?>("state", null);
                var texturePath = serializer.ReadDataField<string?>("texture", null);

                if (baseState != null || texturePath != null)
                {
                    var layerZeroData = SharedSpriteComponent.PrototypeLayerData.New();
                    if (!string.IsNullOrWhiteSpace(baseState))
                    {
                        layerZeroData.State = baseState;
                    }

                    if (!string.IsNullOrWhiteSpace(texturePath))
                    {
                        layerZeroData.TexturePath = texturePath;
                    }

                    layerData.Insert(0, layerZeroData);
                }
            }

            Layers = layerData;
        }
    }
}
