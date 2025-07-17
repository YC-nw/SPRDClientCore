using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SPRDClientCore.Utils
{
    public class PartitionManager(PartitionManagerSettings pms,RequestManager rm)
    {
        private readonly IProtocolHandler handler = pms.Handler;
        private readonly PartitionManagerSettings settings = pms;
        private readonly RequestManager requestManager = rm;
        public PartitionWriter Writer { get;private set; } = new PartitionWriter(pms,rm);
        public PartitionReader Reader { get;private set; } = new PartitionReader(pms,rm);
    }
}
