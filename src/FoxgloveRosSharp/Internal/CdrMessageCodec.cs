using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RosSharp.RosBridgeClient.Internal
{
    internal sealed class CdrMessageCodec
    {
        private readonly string rootType;
        private readonly IReadOnlyDictionary<string, RosMessageDefinition> definitions;

        public CdrMessageCodec(string rootType, string schema)
        {
            this.rootType = rootType;
            definitions = RosMessageDefinitionParser.Parse(rootType, schema);
        }

        public byte[] Serialize(Message message)
        {
            return SerializeObject(message);
        }

        public byte[] SerializeObject(object message)
        {
            var writer = new CdrWriter();
            writer.WriteEncapsulationHeader();
            WriteMessage(writer, rootType, message);
            return writer.ToArray();
        }

        public T Deserialize<T>(byte[] data) where T : Message
        {
            return (T)DeserializeObject(data, typeof(T));
        }

        public object DeserializeObject(byte[] data, Type messageType)
        {
            var reader = new CdrReader(data);
            object message = Activator.CreateInstance(messageType)
                ?? throw new InvalidOperationException($"Unable to create {messageType.FullName}.");
            ReadMessage(reader, rootType, message);
            return message;
        }

        private void WriteMessage(CdrWriter writer, string type, object message)
        {
            if (!definitions.TryGetValue(type, out RosMessageDefinition? definition))
                throw new InvalidOperationException($"No ROS message definition for {type}.");

            foreach (RosField field in definition.Fields)
            {
                object? value = ReflectionAccess.GetMemberValue(message, field.Name);
                if (value == null && ReflectionAccess.GetMemberType(message.GetType(), field.Name) == null)
                    value = ReflectionAccess.GetNestedMemberValue(message, field.Name);

                if (field.IsArray)
                    WriteArray(writer, field, value);
                else
                    WriteValue(writer, field.Type, value);
            }
        }

        private void WriteArray(CdrWriter writer, RosField field, object? value)
        {
            IEnumerable values = value as IEnumerable ?? Array.Empty<object>();
            if (field.FixedLength == null)
            {
                int count = value is ICollection collection ? collection.Count : Count(values);
                writer.WriteUInt32((uint)count);
            }

            foreach (object? element in values)
                WriteValue(writer, field.Type, element);
        }

        private void WriteValue(CdrWriter writer, string type, object? value)
        {
            switch (type)
            {
                case "bool": writer.WriteUInt8((bool)(value ?? false) ? (byte)1 : (byte)0); return;
                case "byte":
                case "uint8":
                case "char": writer.WriteUInt8(Convert.ToByte(value ?? 0)); return;
                case "int8": writer.WriteInt8(Convert.ToSByte(value ?? 0)); return;
                case "int16": writer.WriteInt16(Convert.ToInt16(value ?? 0)); return;
                case "uint16": writer.WriteUInt16(Convert.ToUInt16(value ?? 0)); return;
                case "int32": writer.WriteInt32(Convert.ToInt32(value ?? 0)); return;
                case "uint32": writer.WriteUInt32(Convert.ToUInt32(value ?? 0)); return;
                case "int64": writer.WriteInt64(Convert.ToInt64(value ?? 0)); return;
                case "uint64": writer.WriteUInt64(Convert.ToUInt64(value ?? 0)); return;
                case "float32": writer.WriteFloat32(Convert.ToSingle(value ?? 0)); return;
                case "float64": writer.WriteFloat64(Convert.ToDouble(value ?? 0)); return;
                case "string":
                case "wstring": writer.WriteString(Convert.ToString(value) ?? ""); return;
                default:
                    if (value == null)
                        throw new InvalidOperationException($"Cannot serialize null nested ROS message {type}.");
                    WriteMessage(writer, type, value);
                    return;
            }
        }

        private void ReadMessage(CdrReader reader, string type, object target)
        {
            if (!definitions.TryGetValue(type, out RosMessageDefinition? definition))
                throw new InvalidOperationException($"No ROS message definition for {type}.");

            Type targetType = target.GetType();
            foreach (RosField field in definition.Fields)
            {
                Type? memberType = ReflectionAccess.GetMemberType(targetType, field.Name);
                Type? nestedMemberType = memberType == null
                    ? ReflectionAccess.GetNestedMemberType(targetType, field.Name)
                    : null;

                object? value = field.IsArray
                    ? ReadArray(reader, field, memberType ?? nestedMemberType ?? typeof(object[]))
                    : ReadValue(reader, field.Type, memberType ?? nestedMemberType ?? typeof(object));

                if (memberType != null)
                    ReflectionAccess.SetMemberValue(target, field.Name, value);
                else
                    ReflectionAccess.SetNestedMemberValue(target, field.Name, value);
            }
        }

        private object ReadArray(CdrReader reader, RosField field, Type memberType)
        {
            int length = field.FixedLength ?? checked((int)reader.ReadUInt32());
            object?[] values = new object?[length];
            Type elementType = memberType.IsArray ? memberType.GetElementType() ?? typeof(object) : typeof(object);

            for (int i = 0; i < length; i++)
                values[i] = ReadValue(reader, field.Type, elementType);

            return memberType.IsArray ? ReflectionAccess.ToTypedArray(values, memberType) : values;
        }

        private object? ReadValue(CdrReader reader, string type, Type targetType)
        {
            return type switch
            {
                "bool" => reader.ReadUInt8() != 0,
                "byte" => reader.ReadUInt8(),
                "char" => reader.ReadUInt8(),
                "uint8" => reader.ReadUInt8(),
                "int8" => reader.ReadInt8(),
                "int16" => reader.ReadInt16(),
                "uint16" => reader.ReadUInt16(),
                "int32" => reader.ReadInt32(),
                "uint32" => reader.ReadUInt32(),
                "int64" => reader.ReadInt64(),
                "uint64" => reader.ReadUInt64(),
                "float32" => reader.ReadFloat32(),
                "float64" => reader.ReadFloat64(),
                "string" => reader.ReadString(),
                "wstring" => reader.ReadString(),
                _ => ReadNested(reader, type, targetType)
            };
        }

        private object ReadNested(CdrReader reader, string type, Type targetType)
        {
            Type concrete = typeof(Message).IsAssignableFrom(targetType) && targetType != typeof(Message)
                ? targetType
                : ReflectionAccess.FindMessageType(type, targetType);
            object instance = Activator.CreateInstance(concrete)
                ?? throw new InvalidOperationException($"Unable to create {concrete.FullName}.");
            ReadMessage(reader, type, instance);
            return instance;
        }

        private static int Count(IEnumerable values)
        {
            int count = 0;
            foreach (object? _ in values)
                count++;
            return count;
        }
    }

    internal sealed class CdrWriter
    {
        private readonly MemoryStream stream = new();

        public void WriteEncapsulationHeader()
        {
            stream.WriteByte(0);
            stream.WriteByte(1);
            stream.WriteByte(0);
            stream.WriteByte(0);
        }

        public byte[] ToArray() => stream.ToArray();

        public void WriteUInt8(byte value) => stream.WriteByte(value);
        public void WriteInt8(sbyte value) => stream.WriteByte(unchecked((byte)value));

        public void WriteInt16(short value) => WriteAligned(2, span => BinaryPrimitives.WriteInt16LittleEndian(span, value));
        public void WriteUInt16(ushort value) => WriteAligned(2, span => BinaryPrimitives.WriteUInt16LittleEndian(span, value));
        public void WriteInt32(int value) => WriteAligned(4, span => BinaryPrimitives.WriteInt32LittleEndian(span, value));
        public void WriteUInt32(uint value) => WriteAligned(4, span => BinaryPrimitives.WriteUInt32LittleEndian(span, value));
        public void WriteInt64(long value) => WriteAligned(8, span => BinaryPrimitives.WriteInt64LittleEndian(span, value));
        public void WriteUInt64(ulong value) => WriteAligned(8, span => BinaryPrimitives.WriteUInt64LittleEndian(span, value));
        public void WriteFloat32(float value) => WriteAligned(4, span => WriteLittleEndian(span, BitConverter.GetBytes(value)));
        public void WriteFloat64(double value) => WriteAligned(8, span => WriteLittleEndian(span, BitConverter.GetBytes(value)));

        public void WriteString(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt32((uint)bytes.Length + 1);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private void WriteAligned(int alignment, Action<byte[]> write)
        {
            Align(alignment);
            byte[] buffer = new byte[alignment];
            write(buffer);
            stream.Write(buffer, 0, buffer.Length);
        }

        private void Align(int alignment)
        {
            long padding = Padding(stream.Position, alignment);
            for (int i = 0; i < padding; i++)
                stream.WriteByte(0);
        }

        private static long Padding(long position, int alignment)
        {
            long remainder = position % alignment;
            return remainder == 0 ? 0 : alignment - remainder;
        }

        private static void WriteLittleEndian(byte[] target, byte[] bytes)
        {
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            bytes.CopyTo(target, 0);
        }
    }

    internal sealed class CdrReader
    {
        private readonly byte[] data;
        private int offset;

        public CdrReader(byte[] data)
        {
            this.data = data;
            offset = data.Length >= 4 && data[0] == 0 && (data[1] == 0 || data[1] == 1) ? 4 : 0;
        }

        public byte ReadUInt8() => data[offset++];
        public sbyte ReadInt8() => unchecked((sbyte)data[offset++]);
        public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(ReadAlignedSpan(2, 2));
        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadAlignedSpan(2, 2));
        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadAlignedSpan(4, 4));
        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadAlignedSpan(4, 4));
        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadAlignedSpan(8, 8));
        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadAlignedSpan(8, 8));
        public float ReadFloat32() => BitConverter.ToSingle(ReadLittleEndianBytes(4, 4), 0);
        public double ReadFloat64() => BitConverter.ToDouble(ReadLittleEndianBytes(8, 8), 0);

        public string ReadString()
        {
            uint length = ReadUInt32();
            if (length == 0)
                return "";
            int contentLength = checked((int)length) - 1;
            string value = Encoding.UTF8.GetString(data, offset, contentLength);
            offset += checked((int)length);
            return value;
        }

        private ReadOnlySpan<byte> ReadAlignedSpan(int alignment, int size)
        {
            Align(alignment);
            ReadOnlySpan<byte> span = data.AsSpan(offset, size);
            offset += size;
            return span;
        }

        private byte[] ReadLittleEndianBytes(int alignment, int size)
        {
            byte[] bytes = ReadAlignedSpan(alignment, size).ToArray();
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return bytes;
        }

        private void Align(int alignment)
        {
            int padding = Padding(offset, alignment);
            offset += padding;
        }

        private static int Padding(int position, int alignment)
        {
            int remainder = position % alignment;
            return remainder == 0 ? 0 : alignment - remainder;
        }
    }
}
