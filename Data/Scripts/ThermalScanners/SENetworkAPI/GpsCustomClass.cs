using System;
using ProtoBuf;
using VRageMath;

namespace ThermalSectorSync.Descriptions
{
    [ProtoContract]
    public class GpsCustomType
    {
        [ProtoMember(1)]
        public string Name { get; set; }

        [ProtoMember(2)]
        public string Description { get; set; }

        [ProtoMember(3)]
        public Vector3D Coords { get; set; }
    }
}
