using System;
using Lidgren.Network;
using Robust.Shared.Serialization;

#nullable disable

namespace Robust.Shared.Network.Messages.Handshake
{
    internal sealed class MsgEncryptionResponse : NetMessage
    {
        public override string MsgName => string.Empty;

        public override MsgGroups MsgGroup => MsgGroups.Core;

        public Guid UserId;
        public byte[] SealedData;
        public byte[] LegacyHwid;

        private const int MaxFieldLength = 1024; // Starlight-edit

        public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
        {
            UserId = buffer.ReadGuid();
            var keyLength = buffer.ReadVariableInt32();
            // Starlight-start: pre-auth message, never trust the length prefix
            if (keyLength is < 0 or > MaxFieldLength)
                throw new InvalidOperationException($"Sealed data length {keyLength} out of range.");
            SealedData = buffer.ReadBytes(keyLength);
            var legacyHwidLength = buffer.ReadVariableInt32();
            if (legacyHwidLength is < 0 or > MaxFieldLength)
                throw new InvalidOperationException($"Legacy HWID length {legacyHwidLength} out of range.");
            // Starlight-end
            LegacyHwid = buffer.ReadBytes(legacyHwidLength);
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
        {
            buffer.Write(UserId);
            buffer.WriteVariableInt32(SealedData.Length);
            buffer.Write(SealedData);
            buffer.WriteVariableInt32(LegacyHwid.Length);
            buffer.Write(LegacyHwid);
        }
    }
}
