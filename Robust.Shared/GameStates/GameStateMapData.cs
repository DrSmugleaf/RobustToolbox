using System;
using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;

namespace Robust.Shared.GameStates
{
    [Serializable, NetSerializable]
    public sealed class GameStateMapData
    {
        // Dict of the new maps
        public readonly KeyValuePair<MapId, MapCreationDatum>[]? CreatedMaps;
        public readonly KeyValuePair<GridId, GridCreationDatum>[]? CreatedGrids;
        public readonly KeyValuePair<GridId, GridDatum>[]? GridData;
        public readonly GridId[]? DeletedGrids;
        public readonly MapId[]? DeletedMaps;

        public GameStateMapData(KeyValuePair<GridId, GridDatum>[]? gridData, GridId[]? deletedGrids, MapId[]? deletedMaps, KeyValuePair<MapId, MapCreationDatum>[]? createdMaps, KeyValuePair<GridId, GridCreationDatum>[]? createdGrids)
        {
            GridData = gridData;
            DeletedGrids = deletedGrids;
            DeletedMaps = deletedMaps;
            CreatedMaps = createdMaps;
            CreatedGrids = createdGrids;
        }

        [Serializable, NetSerializable]
        public struct MapCreationDatum
        {
            public readonly EntityUid EntityUid;

            public MapCreationDatum(EntityUid entityUid)
            {
                EntityUid = entityUid;
            }
        }

        [Serializable, NetSerializable]
        public struct GridCreationDatum
        {
            public readonly ushort ChunkSize;
            public readonly float SnapSize;

            public GridCreationDatum(ushort chunkSize, float snapSize)
            {
                ChunkSize = chunkSize;
                SnapSize = snapSize;
            }
        }

        [Serializable, NetSerializable]
        public struct GridDatum
        {
            public readonly MapCoordinates Coordinates;
            public readonly ChunkDatum[] ChunkData;

            public GridDatum(ChunkDatum[] chunkData, MapCoordinates coordinates)
            {
                ChunkData = chunkData;
                Coordinates = coordinates;
            }
        }

        [Serializable, NetSerializable]
        public struct ChunkDatum
        {
            public readonly Vector2i Index;

            // Definitely wasteful to send EVERY tile.
            // Optimize away future coder.
            // Also it's stored row-major.
            public readonly Tile[] TileData;

            public ChunkDatum(Vector2i index, Tile[] tileData)
            {
                Index = index;
                TileData = tileData;
            }
        }
    }
}
