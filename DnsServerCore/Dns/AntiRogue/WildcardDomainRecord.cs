using System.Globalization;

namespace DnsServerCore.Dns.AntiRogue
{
    public class WildcardDomainRecord
    {
        public string WildcardDomainName { get; }

        public WildcardDomainRecord(string domainName)
        {
            WildcardDomainName = domainName.ToLower(CultureInfo.InvariantCulture);
        }

        public bool Dominates(string subDomainName)
        {
            return
                subDomainName.ToLower(CultureInfo.InvariantCulture).Equals(WildcardDomainName) || 
                subDomainName.ToLower(CultureInfo.InvariantCulture).EndsWith($".{WildcardDomainName}");
        }
    }
}