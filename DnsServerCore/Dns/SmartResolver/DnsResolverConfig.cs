using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Proxy;

namespace DnsServerCore.Dns.SmartResolver
{
    public struct DnsResolverConfig
    {
        public LogManager Log { get; private set; }
        public NameServerAddress[] Forwarders { get; private set; }
        public NetProxy Proxy { get; private set; }
        public bool PreferIPv6 { get; private set; }
        public DnsTransportProtocol ForwarderProtocol { get; private set; }
        public int Retries { get; private set; }
        public int Timeout { get; private set; }
        
        public DnsResolverConfig(LogManager log, NameServerAddress[] forwarders, NetProxy proxy, bool preferIPv6, 
            DnsTransportProtocol forwarderProtocol, int retries, int timeout)
        {
            Log = log;
            Forwarders = forwarders;
            Proxy = proxy;
            PreferIPv6 = preferIPv6;
            ForwarderProtocol = forwarderProtocol;
            Retries = retries;
            Timeout = timeout;
        }
    }
}