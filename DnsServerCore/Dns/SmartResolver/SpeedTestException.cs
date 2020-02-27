using System;

namespace DnsServerCore.Dns.SmartResolver
{
    public class SpeedTestException : Exception
    {
        public SpeedTestException(string message, Exception innerException) :
            base(message, innerException)
        {
            
        }
    }
}