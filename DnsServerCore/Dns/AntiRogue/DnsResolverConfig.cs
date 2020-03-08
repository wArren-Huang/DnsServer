using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Proxy;

namespace DnsServerCore.Dns.AntiRogue
{
    public struct DnsResolverConfig
    {
        public LogManager Log { get; }
        public NetProxy Proxy { get; }
        public bool PreferIPv6 { get; }
        public DnsTransportProtocol ForwarderProtocol { get; }
        public int Retries { get; }
        public int Timeout { get; }

        public DnsResolverConfig(NetProxy proxy, bool preferIPv6, DnsTransportProtocol forwarderProtocol, int retries,
            int timeout, LogManager log)
        {
            Proxy = proxy;
            PreferIPv6 = preferIPv6;
            ForwarderProtocol = forwarderProtocol;
            Retries = retries;
            Timeout = timeout;
            Log = log;
        }
    }
}