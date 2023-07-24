﻿using Google.ProtocolBuffers;
using Gazillion;
using MHServerEmu.Networking;

namespace MHServerEmu.GameServer
{
    public enum GameRegion
    {
        AvengersTower,
        DangerRoom,
        Midtown
    }

    public static class RegionLoader
    {
        public static GameMessage[] GetBeginLoadingMessages(GameRegion region)
        {
            GameMessage[] messages = Array.Empty<GameMessage>(); ;

            switch (region)
            {
                case GameRegion.AvengersTower:

                    GameMessage[] loadedMessages = PacketHelper.LoadMessagesFromPacketFile("AvengersTowerBeginLoading.bin");
                    List<GameMessage> messageList = new();

                    for (int i = 0; i < loadedMessages.Length; i++)
                    {
                        if (loadedMessages[i].Id == (byte)GameServerToClientMessage.NetMessageEntityCreate)
                        {
                            // message 5 == account data?
                            // message 115 == player entity
                            // message 364 == waypoint
                            if (i == 5 || i == 115 || i == 364) messageList.Add(loadedMessages[i]);
                        }
                        else
                        {
                            messageList.Add(loadedMessages[i]);
                        }
                    }

                    messages = messageList.ToArray();
                    break;

                case GameRegion.DangerRoom:
                    messages = PacketHelper.LoadMessagesFromPacketFile("DangerRoomBeginLoading.bin");
                    break;

                case GameRegion.Midtown:
                    messages = PacketHelper.LoadMessagesFromPacketFile("MidtownBeginLoading.bin");
                    break;
            }

            return messages;
        }

        public static GameMessage[] GetFinishLoadingMessages(GameRegion region)
        {
            GameMessage[] messages = Array.Empty<GameMessage>(); ;

            switch (region)
            {
                case GameRegion.AvengersTower:

                    List<GameMessage> messageList = new();

                    byte[] blackCatEntityEnterGameWorld = {
                                0x01, 0xB2, 0xF8, 0xFD, 0x06, 0xA0, 0x21, 0xF0, 0xA3, 0x01, 0xBC, 0x40,
                                0x90, 0x2E, 0x91, 0x03, 0xBC, 0x05, 0x00, 0x00, 0x01
                            };

                    messageList.Add(new((byte)GameServerToClientMessage.NetMessageEntityEnterGameWorld,
                        NetMessageEntityEnterGameWorld.CreateBuilder().SetArchiveData(ByteString.CopyFrom(blackCatEntityEnterGameWorld)).Build().ToByteArray()));

                    byte[] waypointEntityEnterGameWorld = {
                                0x01, 0x0C, 0x02, 0x80, 0x43, 0xE0, 0x6B, 0xD8, 0x2A, 0xC8, 0x01
                            };

                    messageList.Add(new((byte)GameServerToClientMessage.NetMessageEntityEnterGameWorld,
                        NetMessageEntityEnterGameWorld.CreateBuilder().SetArchiveData(ByteString.CopyFrom(waypointEntityEnterGameWorld)).Build().ToByteArray()));

                    messageList.Add(new((byte)GameServerToClientMessage.NetMessageDequeueLoadingScreen, NetMessageDequeueLoadingScreen.DefaultInstance.ToByteArray()));

                    messages = messageList.ToArray();

                    break;

                case GameRegion.DangerRoom:
                    messages = PacketHelper.LoadMessagesFromPacketFile("DangerRoomFinishLoading.bin");
                    break;

                case GameRegion.Midtown:
                    messages = PacketHelper.LoadMessagesFromPacketFile("MidtownFinishLoading.bin");
                    break;
            }

            return messages;
        }
    }
}