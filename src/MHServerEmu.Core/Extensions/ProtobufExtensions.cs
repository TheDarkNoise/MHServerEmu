﻿using System.Text;
using Google.ProtocolBuffers;

namespace MHServerEmu.Core.Extensions
{
    public static class ProtobufExtensions
    {
        #region CodedInputStream

        public static int ReadRawInt32(this CodedInputStream stream)
        {
            return CodedInputStream.DecodeZigZag32(stream.ReadRawVarint32());
        }

        public static long ReadRawInt64(this CodedInputStream stream)
        {
            return CodedInputStream.DecodeZigZag64(stream.ReadRawVarint64());
        }

        public static uint ReadRawUInt32(this CodedInputStream stream)
        {
            return BitConverter.ToUInt32(stream.ReadRawBytes(4));
        }

        public static float ReadRawFloat(this CodedInputStream stream)
        {
            return BitConverter.UInt32BitsToSingle(stream.ReadRawVarint32());
        }

        public static float ReadRawZigZagFloat(this CodedInputStream stream, int precision)
        {
            int intValue = CodedInputStream.DecodeZigZag32(stream.ReadRawVarint32());
            return (float)intValue / (1 << precision);
        }

        public static string ReadRawString(this CodedInputStream stream)
        {
            int length = (int)stream.ReadRawVarint32();
            return Encoding.UTF8.GetString(stream.ReadRawBytes(length));
        }

        #endregion

        #region CodedOutputStream

        public static void WriteRawInt32(this CodedOutputStream stream, int value)
        {
            stream.WriteRawVarint32(CodedOutputStream.EncodeZigZag32(value));
        }

        public static void WriteRawInt64(this CodedOutputStream stream, long value)
        {
            stream.WriteRawVarint64(CodedOutputStream.EncodeZigZag64(value));
        }

        public static void WriteRawUInt32(this CodedOutputStream stream, uint value)
        {
            stream.WriteRawBytes(BitConverter.GetBytes(value));
        }

        public static void WriteRawFloat(this CodedOutputStream stream, float value)
        {
            stream.WriteRawVarint32(BitConverter.SingleToUInt32Bits(value));
        }

        public static void WriteRawZigZagFloat(this CodedOutputStream stream, float value, int precision)
        {
            int intValue = (int)(value * (1 << precision));
            stream.WriteRawVarint32(CodedOutputStream.EncodeZigZag32(intValue));
        }

        public static void WriteRawString(this CodedOutputStream stream, string value)
        {
            byte[] rawBytes = Encoding.UTF8.GetBytes(value);
            stream.WriteRawVarint64((ulong)rawBytes.Length);
            stream.WriteRawBytes(rawBytes);
        }

        #endregion
    }
}
