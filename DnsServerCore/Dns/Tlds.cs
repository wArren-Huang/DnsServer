using System;
using System.Collections.Generic;
using System.Linq;

namespace DnsServerCore.Dns
{
    public static class Tlds
    {
        
        public static bool IsTld(string domain)
        {
            return !domain.Contains('.');
        }
    }
}