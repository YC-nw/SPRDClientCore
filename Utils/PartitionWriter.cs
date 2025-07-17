using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SPRDClientCore.Utils
{
    public class PartitionWriter
    {
        private static long nowBytes = 0;
        public PartitionWriter(PartitionManagerSettings pms,RequestManager rm)
        {
            RequestManager requestManager = rm;
            PartitionManagerSettings settings = pms;
            IProtocolHandler handler = pms.Handler;
        }
    }
}
