using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SPRDClientCore.Protocol.CheckSums;

namespace SPRDClientCore.Models
{
    public interface IEncoder
    {
        public bool UseTranscode { get; set; }
        public byte[] Encode(SprdCommand type, ReadOnlyMemory<byte> data, IChecksum checksum);
        public byte[] Encode(Packet packet);
    }
    public struct Packet
    {
        public SprdCommand Type { get; set; }
        public byte[] Data { get; set; }
        public IChecksum ChecksumStrategy { get; set; }

        public Packet(SprdCommand type, byte[]? data, IChecksum checksumStrategy)
        {
            Type = type;
            Data = data ?? Array.Empty<byte>();
            ChecksumStrategy = checksumStrategy;
        }

        public override readonly string ToString()
        {
            return $"Type : 0x{Type.ToString("X")} , Data : {BitConverter.ToString(Data)}";
        }
    }
    public interface IChecksum
    {
        ushort Compute(params ReadOnlyMemory<byte>[] datas);
    }

}
