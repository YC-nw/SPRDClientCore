using SPRDClientCore.Models;
using SPRDClientCore.Protocol.CheckSums;

namespace SPRDClientCore.Protocol.Encoders
{
    public class HdlcEncoder  : IEncoder
    {
        private const byte HDLC_HEADER = 0x7E;
        private const byte HDLC_ESCAPE = 0x7D;

        private byte[] buffer = new byte[0xffff];
        private byte[] headerBuffer = new byte[4];
        private byte[] footerBuffer = new byte[2];
        private ReadOnlyMemory<byte>[] buffers = new ReadOnlyMemory<byte>[3];

        public bool UseTranscode { get; set; } = true;

        public HdlcEncoder()
        {
            buffers[0] = headerBuffer;
            buffers[2] = footerBuffer;
        }

        public byte[] Encode(SprdCommand type, ReadOnlyMemory<byte> data, IChecksum checksum)
        {
            if (type == (SprdCommand)HDLC_HEADER)
            {
                return new byte[] { HDLC_HEADER };
            }
            buffers[1] = data;
            int nowPosition = 0;
            headerBuffer[0] = (byte)((ushort)type >> 8);
            headerBuffer[1] = (byte)((ushort)type & 0xFF);
            headerBuffer[2] = (byte)(data.Length >> 8);
            headerBuffer[3] = (byte)(data.Length & 0xFF);

            ushort sum = checksum.Compute(headerBuffer, data);

            footerBuffer[0] = (byte)(sum >> 8);
            footerBuffer[1] = (byte)(sum & 0xFF);

            buffer[nowPosition++] = HDLC_HEADER;

            if (UseTranscode)
                foreach (var buf in buffers)
                {
                    var payload = buf.Span;
                    for (int i = 0; i < payload.Length; i++)
                    {
                        switch (payload[i])
                        {
                            default:
                                buffer[nowPosition++] = payload[i];
                                break;
                            case HDLC_ESCAPE or HDLC_HEADER:
                                buffer[nowPosition++] = HDLC_ESCAPE;
                                buffer[nowPosition++] = (byte)(payload[i] ^ 0x20);
                                break;
                        }
                    }
                }
            else
            {
                headerBuffer.CopyTo(buffer.AsSpan(1, 4));
                data.Span.CopyTo(buffer.AsSpan(5));
                footerBuffer.CopyTo(buffer.AsSpan(data.Length + 5));
                nowPosition = 1 + 4 + data.Length + 2;
            }
            buffer[nowPosition++] = HDLC_HEADER;

            byte[] result = new byte[nowPosition];
            Buffer.BlockCopy(buffer, 0, result, 0, nowPosition);
            return result;
        }

        public byte[] Encode(Packet packet)
            => Encode(packet.Type, packet.Data, packet.ChecksumStrategy);

    }
}
