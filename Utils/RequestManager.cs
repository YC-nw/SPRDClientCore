using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SPRDClientCore.Utils
{
    public class RequestManager
    {
        IProtocolHandler handler;
        public RequestManager(IProtocolHandler handler) { this.handler = handler; }
        public byte[] CreateSelectPartitionRequest(string partName,
                                            ulong size,
                                            IChecksum? checksum = null)
        {
            bool useMode64 = size >> 32 != 0;
            byte[] result = new byte[useMode64 ? 80 : 76];
            CreateSelectPartitionRequest(result, partName, size, checksum);
            return result;
        }
        public void CreateSelectPartitionRequest(Memory<byte> partData, string partName, ulong size, IChecksum? checksum = null)
        {
            bool useMode64 = size >> 32 != 0;
            if (partData.Length < 76) throw new ArgumentException();
            if (useMode64 && partData.Length < 80) throw new ArgumentException();
            Encoding.Unicode.GetBytes(partName).CopyTo(partData.Span.Slice(0, 72));
            BinaryPrimitives.WriteUInt32LittleEndian(partData.Span.Slice(72, 4), (uint)size);
            if (useMode64) BinaryPrimitives.WriteUInt32LittleEndian(partData.Span.Slice(76, 4), (uint)(size >> 32));
        }


    }
}
