using System;

namespace DnsServerCore.Dns.AntiRogue
{
    public class Expiration
    {
        public DateTime ExpiresAt { get; set; }

        public bool IsExpired => DateTime.Now.CompareTo(ExpiresAt) > 0;
        public bool IsNotExpired => DateTime.Now.CompareTo(ExpiresAt) <= 0;
    }
}