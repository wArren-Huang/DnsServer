
using System;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore.Dns.SmartResolver
{
    public struct ResponseResult
    {
        public static readonly string PrintableHeader = 
            $"                   Dns|      IP Address|  Method|    Time|                    Result|  Thread|    Certificate" +
            $"{Environment.NewLine}" +
            $"----------------------+----------------+--------+--------+--------------------------+--------+---------------------";
        public string DnsServer { get; set; }
        public DnsDatagram DnsResponse { get; set; }
        public string IpAddress { get; set; }
        public long Time { get; set; }
        public SpeedTestResult Result { get; set; }
        public SpeedTestMethod Method { get; set; }
        public int ThreadId { get; set; }
        public string Certificate { get; set; }

        public override string ToString()
        {
            return $"{DnsServer,22}|{IpAddress,16}|{Method,8}|{Time,8}|{Result,26}|{ThreadId,8}|  {Certificate}";
        }
    }
}