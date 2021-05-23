﻿using Lidgren.Network;

#nullable disable

namespace Robust.Shared.Network.Messages
{
    [NetMessage(MsgGroups.Command)]
    public class MsgConCmd : NetMessage
    {
        public string Text { get; set; }

        public override void ReadFromBuffer(NetIncomingMessage buffer)
        {
            Text = buffer.ReadString();
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer)
        {
            buffer.Write(Text);
        }
    }
}
